using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Companies;
using Workbench.Domain.Accounting;
using Workbench.Domain.Companies;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Companies;

public sealed class IntercompanyService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : IIntercompanyService
{
    public async Task<IReadOnlyList<IntercompanyAccountDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var companies = (await db.Set<Company>().AsNoTracking().Where(c => c.IsActive).ToListAsync(cancellationToken))
            .OrderBy(c => c.Code, StringComparer.Ordinal).ToList();
        var setups = await db.Set<IntercompanyAccount>().AsNoTracking().ToDictionaryAsync(a => (a.CompanyId, a.CounterpartyCompanyId), cancellationToken);
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, a => $"{a.Number} {a.Name}", cancellationToken);
        return (from c in companies
                from other in companies
                where c.Id != other.Id
                let s = setups.GetValueOrDefault((c.Id, other.Id))
                select new IntercompanyAccountDto(c.Id, c.Code, other.Id, other.Code,
                    s?.DueFromAccountId, s is null ? null : accounts.GetValueOrDefault(s.DueFromAccountId),
                    s?.DueToAccountId, s is null ? null : accounts.GetValueOrDefault(s.DueToAccountId)))
            .ToList();
    }

    public async Task<IntercompanyAccountDto> SaveAsync(SaveIntercompanyAccountRequest request, CancellationToken cancellationToken = default)
    {
        if (request.CompanyId == request.CounterpartyCompanyId)
        {
            throw new ArgumentException("Choose two different companies.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var companies = await db.Set<Company>().AsNoTracking().Where(c => c.Id == request.CompanyId || c.Id == request.CounterpartyCompanyId)
            .ToDictionaryAsync(c => c.Id, cancellationToken);
        var company = companies.GetValueOrDefault(request.CompanyId) ?? throw WorkbenchNotFoundException.For("Company", request.CompanyId);
        var other = companies.GetValueOrDefault(request.CounterpartyCompanyId) ?? throw WorkbenchNotFoundException.For("Company", request.CounterpartyCompanyId);
        var dueFrom = await RequireAccountAsync(db, request.DueFromAccountId, AccountType.Asset, company, "due from", cancellationToken);
        var dueTo = await RequireAccountAsync(db, request.DueToAccountId, AccountType.Liability, company, "due to", cancellationToken);

        var setup = await db.Set<IntercompanyAccount>().FirstOrDefaultAsync(a => a.CompanyId == company.Id && a.CounterpartyCompanyId == other.Id, cancellationToken);
        var before = setup is null ? null : new { setup.DueFromAccountId, setup.DueToAccountId };
        if (setup is null)
        {
            setup = new IntercompanyAccount { CompanyId = company.Id, CounterpartyCompanyId = other.Id };
            db.Set<IntercompanyAccount>().Add(setup);
        }

        setup.DueFromAccountId = dueFrom.Id;
        setup.DueToAccountId = dueTo.Id;
        setup.UpdatedBy = audit.Actor;
        setup.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, nameof(IntercompanyAccount), setup.Id, before is null ? "Created" : "Updated", before,
            new { Company = company.Code, Counterparty = other.Code, DueFrom = dueFrom.Number, DueTo = dueTo.Number });
        await db.SaveChangesAsync(cancellationToken);
        return new IntercompanyAccountDto(company.Id, company.Code, other.Id, other.Code, dueFrom.Id, $"{dueFrom.Number} {dueFrom.Name}", dueTo.Id, $"{dueTo.Number} {dueTo.Name}");
    }

    private static async Task<Account> RequireAccountAsync(IWorkbenchDbContext db, Guid id, AccountType type, Company company, string what, CancellationToken ct)
    {
        var account = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new ArgumentException($"Choose the {what} account.");
        if (account.Type != type)
        {
            throw new ArgumentException($"The {what} account must be {(type == AccountType.Asset ? "an asset" : "a liability")} account; {account.Number} is {account.Type.ToString().ToLowerInvariant()}.");
        }

        if (!account.IsActive || await db.Set<Account>().AnyAsync(a => a.ParentId == account.Id, ct))
        {
            throw new ArgumentException($"Account {account.Number} must be an active detail account.");
        }

        if (account.CompanyId is { } only && only != company.Id)
        {
            throw new ArgumentException($"Account {account.Number} belongs to another company.");
        }

        return account;
    }

    public async Task<IReadOnlyList<IntercompanyBalanceDto>> ListBalancesAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == fiscalPeriodId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Fiscal period", fiscalPeriodId);
        var balances = await IntercompanyBalancesAsync(db, period.End, null, cancellationToken);
        var codes = await db.Set<Company>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken);
        var pairs = balances.Keys.Select(k => k.Company.CompareTo(k.Counterparty) < 0 ? (k.Company, k.Counterparty) : (k.Counterparty, k.Company)).Distinct();
        return pairs
            .Select(p => new IntercompanyBalanceDto(p.Item1, codes.GetValueOrDefault(p.Item1, "?"), p.Item2, codes.GetValueOrDefault(p.Item2, "?"),
                balances.GetValueOrDefault((p.Item1, p.Item2)), balances.GetValueOrDefault((p.Item2, p.Item1))))
            .Where(b => b.Balance != 0 || b.CounterpartyBalance != 0)
            .OrderBy(b => b.CompanyCode, StringComparer.Ordinal).ThenBy(b => b.CounterpartyCode, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Each company's balance with each other company (debit-positive) on balance-sheet accounts through
    /// <paramref name="through"/>, from the lines tagged with a counterparty.
    /// </summary>
    internal static async Task<Dictionary<(Guid Company, Guid Counterparty), decimal>> IntercompanyBalancesAsync(
        IWorkbenchDbContext db, DateOnly through, IReadOnlyCollection<Guid>? companies, CancellationToken ct)
    {
        var q = from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                join a in db.Set<Account>().AsNoTracking() on l.AccountId equals a.Id
                where e.Status != JournalStatus.Draft && e.Date <= through && l.IntercompanyCompanyId != null
                    && (a.Type == AccountType.Asset || a.Type == AccountType.Liability || a.Type == AccountType.Equity)
                select l;
        if (companies is not null)
        {
            var ids = companies.ToList();
            q = q.Where(l => ids.Contains(l.CompanyId));
        }

        var sums = await q.GroupBy(l => new { l.CompanyId, Counterparty = l.IntercompanyCompanyId!.Value })
            .Select(g => new { g.Key.CompanyId, g.Key.Counterparty, Debit = g.Sum(l => l.Debit), Credit = g.Sum(l => l.Credit) })
            .ToListAsync(ct);
        return sums.ToDictionary(s => (s.CompanyId, s.Counterparty), s => s.Debit - s.Credit);
    }
}
