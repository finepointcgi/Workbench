using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Companies;
using Workbench.Domain.Accounting;
using Workbench.Domain.Assets;
using Workbench.Domain.Billing;
using Workbench.Domain.Cash;
using Workbench.Domain.Companies;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Companies;

public sealed partial class CompanyService(IWorkbenchDbContextFactory dbFactory, CompanyScope scope, AuditRecorder audit, TimeProvider clock) : ICompanyService
{
    public const string AuditEntityType = nameof(Company);

    /// <summary>The default chart's intercompany accounts, used for new company pairs when they're in the chart.</summary>
    public const string DefaultDueFromAccount = "1180";

    public const string DefaultDueToAccount = "2180";

    // ---- The signed-in user's companies ----

    public async Task<CompanyContextDto> GetContextAsync(CancellationToken cancellationToken = default)
    {
        var view = await scope.GetAsync(cancellationToken);
        var usable = view.Companies.Where(c => c.IsActive && view.CanUse(c.Id)).Select(ToDto).ToList();
        return new CompanyContextDto(usable, view.Selected, usable.Count > 1);
    }

    public async Task<CompanyContextDto> SelectAsync(SelectCompanyRequest request, CancellationToken cancellationToken = default)
    {
        var view = await scope.GetAsync(cancellationToken);
        if (request.CompanyId is { } id && (!view.Companies.Any(c => c.Id == id && c.IsActive) || !view.CanUse(id)))
        {
            throw WorkbenchNotFoundException.For("Company", id);
        }

        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var userId = await db.Set<AppUser>().Where(u => u.Email == audit.Actor).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("Sign in again to choose a company.");
            var selection = await db.Set<UserCompanySelection>().FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
            if (selection is null)
            {
                selection = new UserCompanySelection { UserId = userId };
                db.Set<UserCompanySelection>().Add(selection);
            }

            selection.CompanyId = request.CompanyId;
            selection.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetContextAsync(cancellationToken);
    }

    // ---- Companies ----

    public async Task<IReadOnlyList<CompanyDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureDefaultAsync(db, cancellationToken);
        var companies = await db.Set<Company>().AsNoTracking().Where(c => includeInactive || c.IsActive).ToListAsync(cancellationToken);
        return companies.OrderBy(c => c.Code, StringComparer.Ordinal).Select(ToDto).ToList();
    }

    public async Task<CompanyDto> CreateAsync(SaveCompanyRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureDefaultAsync(db, cancellationToken);
        var company = new Company { Code = "", LegalName = "", CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
        await ApplyAsync(db, company, request, cancellationToken);
        db.Set<Company>().Add(company);
        audit.Record(db, AuditEntityType, company.Id, "Created", after: Snapshot(company));
        await AddDefaultIntercompanyAccountsAsync(db, company, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(company);
    }

    public async Task<CompanyDto> UpdateAsync(Guid companyId, SaveCompanyRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureDefaultAsync(db, cancellationToken);
        var company = await db.Set<Company>().FirstOrDefaultAsync(c => c.Id == companyId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Company", companyId);
        var before = Snapshot(company);
        await ApplyAsync(db, company, request, cancellationToken);
        if (company.IsDefault && !company.IsActive)
        {
            throw new ArgumentException("The default company can't be deactivated.");
        }

        company.UpdatedBy = audit.Actor;
        company.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, AuditEntityType, company.Id, "Updated", before, Snapshot(company));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(company);
    }

    private static async Task ApplyAsync(IWorkbenchDbContext db, Company company, SaveCompanyRequest request, CancellationToken ct)
    {
        var code = (request.Code ?? "").Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(code))
        {
            throw new ArgumentException("Enter a company code of up to 16 letters, digits or dashes.");
        }

        if (await db.Set<Company>().AnyAsync(c => c.Code == code && c.Id != company.Id, ct))
        {
            throw new ArgumentException($"Another company already uses code {code}.");
        }

        var name = (request.LegalName ?? "").Trim();
        if (name.Length == 0 || name.Length > 200)
        {
            throw new ArgumentException("Enter the company's legal name (up to 200 characters).");
        }

        var ein = string.IsNullOrWhiteSpace(request.Ein) ? null : request.Ein.Trim();
        if (ein is not null)
        {
            var digits = new string(ein.Where(char.IsDigit).ToArray());
            if (digits.Length != 9 || ein.Any(ch => !char.IsDigit(ch) && ch != '-'))
            {
                throw new ArgumentException("An EIN has nine digits, like 12-3456789.");
            }

            ein = $"{digits[..2]}-{digits[2..]}";
        }

        // Companies share the tenant's fiscal calendar, so consolidated periods line up.
        var calendarStart = await db.Set<FiscalYear>().AsNoTracking().OrderBy(y => y.Start).Select(y => (int?)y.Start.Month).FirstOrDefaultAsync(ct);
        var month = request.FiscalYearStartMonth ?? calendarStart ?? company.FiscalYearStartMonth;
        if (month is < 1 or > 12)
        {
            throw new ArgumentException("The fiscal year starts in a month from 1 to 12.");
        }

        if (calendarStart is { } start && month != start)
        {
            throw new ArgumentException(
                $"Companies share the fiscal calendar (Setup › Fiscal calendar), whose years start in {new DateOnly(2000, start, 1):MMMM}.");
        }

        company.Code = code;
        company.LegalName = name;
        company.Ein = ein;
        company.FiscalYearStartMonth = month;
        company.HomeCurrencyCode = "USD";
        company.IsActive = request.IsActive;
    }

    /// <summary>
    /// Links a new company to every other one through the default chart's intercompany accounts (1180 due from, 2180 due
    /// to), when those exist; otherwise the pairs wait for Setup › Companies › Intercompany.
    /// </summary>
    private async Task AddDefaultIntercompanyAccountsAsync(IWorkbenchDbContext db, Company company, CancellationToken ct)
    {
        var accounts = await db.Set<Account>().AsNoTracking()
            .Where(a => (a.Number == DefaultDueFromAccount || a.Number == DefaultDueToAccount) && a.IsActive && a.CompanyId == null)
            .ToDictionaryAsync(a => a.Number, a => a.Id, ct);
        if (!accounts.TryGetValue(DefaultDueFromAccount, out var dueFrom) || !accounts.TryGetValue(DefaultDueToAccount, out var dueTo))
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var other in await db.Set<Company>().AsNoTracking().Where(c => c.Id != company.Id).Select(c => c.Id).ToListAsync(ct))
        {
            db.Set<IntercompanyAccount>().AddRange(
                new IntercompanyAccount { CompanyId = company.Id, CounterpartyCompanyId = other, DueFromAccountId = dueFrom, DueToAccountId = dueTo, UpdatedBy = audit.Actor, UpdatedAtUtc = now },
                new IntercompanyAccount { CompanyId = other, CounterpartyCompanyId = company.Id, DueFromAccountId = dueFrom, DueToAccountId = dueTo, UpdatedBy = audit.Actor, UpdatedAtUtc = now });
        }
    }

    /// <summary>
    /// Adds the default company's row when it's missing. Migrations seed it; an unmigrated database (the in-memory test
    /// database) doesn't have it until a company is first listed or added.
    /// </summary>
    internal static async Task EnsureDefaultAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        if (!await db.Set<Company>().AnyAsync(c => c.Id == Company.DefaultId, ct))
        {
            db.Set<Company>().Add(new Company { Id = Company.DefaultId, Code = "MAIN", LegalName = "Main company", CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            await db.SaveChangesAsync(ct);
        }
    }

    // ---- Who may use which company ----

    public async Task<IReadOnlyList<UserCompanyAccessDto>> ListUserAccessAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var users = await db.Set<AppUser>().AsNoTracking().Where(u => u.IsActive).Select(u => new { u.Id, u.Email, u.DisplayName }).ToListAsync(cancellationToken);
        var access = (await db.Set<UserCompanyAccess>().AsNoTracking().ToListAsync(cancellationToken)).ToLookup(a => a.UserId, a => a.CompanyId);
        return users.OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.Email, StringComparer.Ordinal)
            .Select(u => new UserCompanyAccessDto(u.Id, u.Email, u.DisplayName, access[u.Id].ToList()))
            .ToList();
    }

    public async Task<UserCompanyAccessDto> SetUserAccessAsync(Guid userId, SetUserCompanyAccessRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var user = await db.Set<AppUser>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("User", userId);
        var wanted = (request.CompanyIds ?? []).Distinct().ToList();
        var known = await db.Set<Company>().AsNoTracking().Where(c => wanted.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken);
        if (known.Count != wanted.Count)
        {
            throw new ArgumentException("Choose companies from the list.");
        }

        var current = await db.Set<UserCompanyAccess>().Where(a => a.UserId == userId).ToListAsync(cancellationToken);
        var before = current.Select(a => a.CompanyId).Order().ToList();
        db.Set<UserCompanyAccess>().RemoveRange(current.Where(a => !wanted.Contains(a.CompanyId)));
        db.Set<UserCompanyAccess>().AddRange(wanted.Where(id => current.All(a => a.CompanyId != id)).Select(id => new UserCompanyAccess { UserId = userId, CompanyId = id }));
        audit.Record(db, "UserCompanyAccess", userId, "Changed", new { User = user.Email, Companies = before }, new { User = user.Email, Companies = wanted.Order().ToList() });
        await db.SaveChangesAsync(cancellationToken);
        return new UserCompanyAccessDto(user.Id, user.Email, user.DisplayName, wanted);
    }

    // ---- Moving records between companies ----

    public async Task<IReadOnlyList<CompanyRecordDto>> ListRecordsAsync(CompanyRecordKind kind, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var codes = await db.Set<Company>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken);
        string Code(Guid id) => codes.GetValueOrDefault(id, "MAIN");
        var rows = await RecordsAsync(db, kind, null, cancellationToken);
        return rows.Select(r => r with { CompanyCode = Code(r.CompanyId) }).OrderBy(r => r.Code, StringComparer.Ordinal).ToList();
    }

    public async Task<CompanyRecordDto> AssignAsync(AssignCompanyRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var company = await db.Set<Company>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CompanyId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Company", request.CompanyId);
        if (!company.IsActive)
        {
            throw new ArgumentException($"Company {company.Code} is inactive.");
        }

        var record = (await RecordsAsync(db, request.Kind, request.RecordId, cancellationToken)).FirstOrDefault()
            ?? throw WorkbenchNotFoundException.For(request.Kind.ToString(), request.RecordId);
        if (record.CompanyId == company.Id)
        {
            return record with { CompanyCode = company.Code };
        }

        if (record.Locked is { } reason)
        {
            throw new ArgumentException(reason);
        }

        var id = request.RecordId;
        switch (request.Kind)
        {
            case CompanyRecordKind.Contract:
                (await db.Set<Contract>().FirstAsync(c => c.Id == id, cancellationToken)).CompanyId = company.Id;
                foreach (var project in await db.Set<Project>().Where(p => p.ContractId == id).ToListAsync(cancellationToken))
                {
                    project.CompanyId = company.Id;
                }

                break;
            case CompanyRecordKind.Project:
                (await db.Set<Project>().FirstAsync(p => p.Id == id, cancellationToken)).CompanyId = company.Id;
                break;
            case CompanyRecordKind.Employee:
                (await db.Set<Employee>().FirstAsync(e => e.Id == id, cancellationToken)).CompanyId = company.Id;
                break;
            case CompanyRecordKind.BankAccount:
                (await db.Set<BankAccount>().FirstAsync(b => b.Id == id, cancellationToken)).CompanyId = company.Id;
                break;
            case CompanyRecordKind.FixedAsset:
                (await db.Set<FixedAsset>().FirstAsync(a => a.Id == id, cancellationToken)).CompanyId = company.Id;
                break;
            default:
                throw new ArgumentException("Choose what to move.");
        }

        audit.Record(db, request.Kind.ToString(), id, "CompanyChanged", new { Company = record.CompanyId }, new { Company = company.Id, company.Code });
        await db.SaveChangesAsync(cancellationToken);
        return record with { CompanyId = company.Id, CompanyCode = company.Code };
    }

    /// <summary>One kind of record with its company and, when it can't move, why. Codes are filled in by the caller.</summary>
    private static async Task<List<CompanyRecordDto>> RecordsAsync(IWorkbenchDbContext db, CompanyRecordKind kind, Guid? only, CancellationToken ct)
    {
        const string Posted = "It has posted ledger activity, which stays in its company. Add a new record in the other company instead.";
        switch (kind)
        {
            case CompanyRecordKind.Contract:
            {
                var contracts = await db.Set<Contract>().AsNoTracking().Where(c => only == null || c.Id == only).ToListAsync(ct);
                var ids = contracts.Select(c => c.Id).ToList();
                var invoiced = (await db.Set<Invoice>().AsNoTracking().Where(i => ids.Contains(i.ContractId)).Select(i => i.ContractId).Distinct().ToListAsync(ct)).ToHashSet();
                var posted = (await (from p in db.Set<Project>().AsNoTracking()
                        where p.ContractId != null && ids.Contains(p.ContractId.Value) && db.Set<JournalLine>().Any(l => l.ProjectId == p.Id)
                        select p.ContractId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
                return contracts.Select(c => new CompanyRecordDto(kind, c.Id, c.ContractNumber, c.Name, c.CompanyId, "",
                    posted.Contains(c.Id) || invoiced.Contains(c.Id) ? Posted : null)).ToList();
            }

            case CompanyRecordKind.Project:
            {
                var projects = await db.Set<Project>().AsNoTracking().Where(p => only == null || p.Id == only).ToListAsync(ct);
                var ids = projects.Select(p => p.Id).ToList();
                var posted = (await db.Set<JournalLine>().AsNoTracking().Where(l => l.ProjectId != null && ids.Contains(l.ProjectId.Value))
                    .Select(l => l.ProjectId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
                return projects.Select(p => new CompanyRecordDto(kind, p.Id, p.Code, p.Name, p.CompanyId, "",
                    p.ContractId is not null ? "It belongs to its contract's company; move the contract instead."
                    : posted.Contains(p.Id) ? Posted : null)).ToList();
            }

            case CompanyRecordKind.Employee:
                // History stays where it posted; only later labor follows the new home company.
                return (await db.Set<Employee>().AsNoTracking().Where(e => only == null || e.Id == only).ToListAsync(ct))
                    .Select(e => new CompanyRecordDto(kind, e.Id, e.EmployeeNumber, e.DisplayName, e.CompanyId, "", null)).ToList();

            case CompanyRecordKind.BankAccount:
            {
                var accounts = await db.Set<BankAccount>().AsNoTracking().Where(b => only == null || b.Id == only).ToListAsync(ct);
                var ids = accounts.Select(a => a.Id).ToList();
                var used = (await db.Set<BankTransaction>().AsNoTracking().Where(t => ids.Contains(t.BankAccountId)).Select(t => t.BankAccountId).Distinct().ToListAsync(ct)).ToHashSet();
                return accounts.Select(b => new CompanyRecordDto(kind, b.Id, $"····{b.AccountNumberLast4}", b.Name, b.CompanyId, "",
                    used.Contains(b.Id) ? "It has imported bank statements, which stay in its company." : null)).ToList();
            }

            case CompanyRecordKind.FixedAsset:
            {
                var assets = await db.Set<FixedAsset>().AsNoTracking().Where(a => only == null || a.Id == only).ToListAsync(ct);
                var ids = assets.Select(a => a.Id).ToList();
                var posted = (await db.Set<JournalEntry>().AsNoTracking().Where(e => e.Source == JournalSource.Asset && e.SourceId != null && ids.Contains(e.SourceId.Value))
                        .Select(e => e.SourceId!.Value).ToListAsync(ct))
                    .Concat(await db.Set<AssetDepreciation>().AsNoTracking().Where(d => ids.Contains(d.AssetId)).Select(d => d.AssetId).ToListAsync(ct))
                    .ToHashSet();
                return assets.Select(a => new CompanyRecordDto(kind, a.Id, a.TagNumber, a.Description, a.CompanyId, "", posted.Contains(a.Id) ? Posted : null)).ToList();
            }

            default:
                throw new ArgumentException("Choose what to list.");
        }
    }

    // ---- Period close per company ----

    public async Task<IReadOnlyList<CompanyPeriodDto>> ListPeriodsAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureDefaultAsync(db, cancellationToken);
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == fiscalPeriodId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Fiscal period", fiscalPeriodId);
        var closes = await db.Set<CompanyPeriodClose>().AsNoTracking().Where(c => c.FiscalPeriodId == period.Id).ToDictionaryAsync(c => c.CompanyId, cancellationToken);
        var companies = await db.Set<Company>().AsNoTracking().ToListAsync(cancellationToken);
        return companies.OrderBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => PeriodDto(c, period, closes.GetValueOrDefault(c.Id)))
            .ToList();
    }

    public async Task<CompanyPeriodDto> SetPeriodClosedAsync(Guid companyId, Guid fiscalPeriodId, SetCompanyPeriodRequest request, CancellationToken cancellationToken = default)
    {
        var reason = (request.Reason ?? "").Trim();
        if (reason.Length == 0)
        {
            throw new ArgumentException(request.Closed ? "Give a reason for closing the period." : "Give a reason for reopening the period.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureDefaultAsync(db, cancellationToken);
        var company = await db.Set<Company>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Company", companyId);
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == fiscalPeriodId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Fiscal period", fiscalPeriodId);
        if (!request.Closed && period.Status == PeriodStatus.Closed)
        {
            throw new ArgumentException($"Fiscal period {period.Name} is closed for every company. Reopen it under Accounting › Period close first.");
        }

        var close = await db.Set<CompanyPeriodClose>().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.FiscalPeriodId == fiscalPeriodId, cancellationToken);
        if (close is null)
        {
            close = new CompanyPeriodClose { CompanyId = companyId, FiscalPeriodId = fiscalPeriodId, ChangedBy = audit.Actor };
            db.Set<CompanyPeriodClose>().Add(close);
        }

        var wasClosed = close.IsClosed;
        close.IsClosed = request.Closed;
        close.ChangedBy = audit.Actor;
        close.ChangedAtUtc = clock.GetUtcNow().UtcDateTime;
        close.Reason = reason.Length > 1000 ? reason[..1000] : reason;
        audit.Record(db, AuditEntityType, companyId, request.Closed ? "PeriodClosed" : "PeriodReopened",
            new { Period = period.Name, Closed = wasClosed }, new { Period = period.Name, Closed = request.Closed }, close.Reason);
        await db.SaveChangesAsync(cancellationToken);
        return PeriodDto(company, period, close);
    }

    private static CompanyPeriodDto PeriodDto(Company c, FiscalPeriod p, CompanyPeriodClose? close) =>
        new(c.Id, c.Code, c.LegalName, p.Id, p.Name, p.Status == PeriodStatus.Closed || close?.IsClosed == true, p.Status == PeriodStatus.Closed,
            close?.ChangedBy, close?.ChangedAtUtc, close?.Reason);

    internal static CompanyDto ToDto(Company c) => new(c.Id, c.Code, c.LegalName, c.Ein, c.FiscalYearStartMonth, c.HomeCurrencyCode, c.IsActive, c.IsDefault);

    private static object Snapshot(Company c) => new { c.Code, c.LegalName, c.Ein, c.FiscalYearStartMonth, c.HomeCurrencyCode, c.IsActive };

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,15}$")]
    private static partial Regex CodePattern();
}
