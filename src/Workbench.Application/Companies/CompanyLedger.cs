using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Accounting;
using Workbench.Domain.Companies;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Companies;

/// <summary>
/// The general ledger's company rules, applied by <see cref="Accounting.GeneralLedger"/> to every entry from every module:
/// <list type="bullet">
/// <item>A line posts to its project's company, or else to the entry's company. The entry's company is set by the module
/// that posts it (the timesheet's employee, the bill's or invoice's company), or else is the first project line's company,
/// or else the default company.</item>
/// <item>An account limited to one company takes postings only there.</item>
/// <item>When the lines span companies, each other company's net is moved to intercompany accounts: the company that
/// received the debit owes the entry's company (due to), which is owed (due from). Every company's ledger balances on its own.</item>
/// <item>Inactive companies and companies that closed the period refuse postings.</item>
/// </list>
/// </summary>
internal static class CompanyLedger
{
    /// <summary>Fills in the entry's and the lines' companies and checks company-limited accounts.</summary>
    public static async Task ResolveAsync(IWorkbenchDbContext db, JournalEntry entry, IReadOnlyDictionary<Guid, Account> accounts, CancellationToken ct)
    {
        var projectIds = entry.Lines.Where(l => l.ProjectId is not null && l.CompanyId == Guid.Empty).Select(l => l.ProjectId!.Value).Distinct().ToList();
        if (entry.CompanyId == Guid.Empty && projectIds.Count == 0)
        {
            projectIds = entry.Lines.Where(l => l.ProjectId is not null).Select(l => l.ProjectId!.Value).Distinct().ToList();
        }

        var projectCompanies = projectIds.Count == 0
            ? []
            : await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.CompanyId, ct);

        if (entry.CompanyId == Guid.Empty)
        {
            var first = entry.Lines.OrderBy(l => l.LineNumber).FirstOrDefault(l => l.ProjectId is { } p && projectCompanies.ContainsKey(p));
            entry.CompanyId = first is null ? Company.DefaultId : projectCompanies[first.ProjectId!.Value];
        }

        foreach (var line in entry.Lines)
        {
            if (line.CompanyId == Guid.Empty)
            {
                line.CompanyId = line.ProjectId is { } pid && projectCompanies.TryGetValue(pid, out var company) ? company : entry.CompanyId;
            }

            if (accounts.TryGetValue(line.AccountId, out var account) && account.CompanyId is { } only && only != line.CompanyId)
            {
                throw new ArgumentException($"Line {line.LineNumber}: account {account.Number} {account.Name} belongs to another company.");
            }
        }
    }

    /// <summary>
    /// Refuses inactive or closed companies and adds the intercompany lines that make every company balance. Runs after
    /// <see cref="ResolveAsync"/>, on posting.
    /// </summary>
    public static async Task BalanceAsync(IWorkbenchDbContext db, JournalEntry entry, CancellationToken ct)
    {
        var companyIds = entry.Lines.Select(l => l.CompanyId).Append(entry.CompanyId).Distinct().ToList();
        var companies = await db.Set<Company>().AsNoTracking().Where(c => companyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        string Code(Guid id) => companies.TryGetValue(id, out var c) ? c.Code : id == Company.DefaultId ? "MAIN" : id.ToString();
        foreach (var id in companyIds)
        {
            // The default company always exists in a migrated database; an unmigrated test database may not have its row.
            if (!companies.TryGetValue(id, out var company))
            {
                if (id != Company.DefaultId)
                {
                    throw new ArgumentException("The entry names a company that doesn't exist.");
                }

                continue;
            }

            if (!company.IsActive)
            {
                throw new ArgumentException($"Company {company.Code} is inactive, so it can't take new postings.");
            }
        }

        var closed = await db.Set<CompanyPeriodClose>().AsNoTracking()
            .Where(c => c.FiscalPeriodId == entry.FiscalPeriodId && c.IsClosed && companyIds.Contains(c.CompanyId))
            .Select(c => c.CompanyId).ToListAsync(ct);
        if (closed.Count > 0)
        {
            var name = await db.Set<FiscalPeriod>().AsNoTracking().Where(p => p.Id == entry.FiscalPeriodId).Select(p => p.Name).FirstOrDefaultAsync(ct);
            throw new ArgumentException($"Fiscal period {name} is closed for {string.Join(", ", closed.Select(Code).Order(StringComparer.Ordinal))}; post into an open period.");
        }

        var home = entry.CompanyId;
        var nets = entry.Lines.GroupBy(l => l.CompanyId).Where(g => g.Key != home)
            .Select(g => (Company: g.Key, Net: g.Sum(l => l.Debit - l.Credit)))
            .Where(x => x.Net != 0)
            .OrderBy(x => Code(x.Company), StringComparer.Ordinal)
            .ToList();
        if (nets.Count == 0)
        {
            return;
        }

        var others = nets.Select(n => n.Company).ToList();
        var setups = await db.Set<IntercompanyAccount>().AsNoTracking()
            .Where(a => (a.CompanyId == home && others.Contains(a.CounterpartyCompanyId)) || (others.Contains(a.CompanyId) && a.CounterpartyCompanyId == home))
            .ToDictionaryAsync(a => (a.CompanyId, a.CounterpartyCompanyId), ct);
        foreach (var (other, net) in nets)
        {
            if (!setups.TryGetValue((home, other), out var homeSide) || !setups.TryGetValue((other, home), out var otherSide))
            {
                throw new ArgumentException(
                    $"This entry crosses companies {Code(home)} and {Code(other)}. Set up their intercompany accounts under Setup › Companies first.");
            }

            // net > 0: the other company received a net debit (cost charged to its project), so it owes the home company.
            var amount = Math.Abs(net);
            var otherOwes = net > 0;
            AddLine(db, entry, other, home, otherOwes ? otherSide.DueToAccountId : otherSide.DueFromAccountId,
                debit: otherOwes ? 0 : amount, credit: otherOwes ? amount : 0,
                memo: otherOwes ? $"Intercompany: due to {Code(home)}" : $"Intercompany: due from {Code(home)}");
            AddLine(db, entry, home, other, otherOwes ? homeSide.DueFromAccountId : homeSide.DueToAccountId,
                debit: otherOwes ? amount : 0, credit: otherOwes ? 0 : amount,
                memo: otherOwes ? $"Intercompany: due from {Code(other)}" : $"Intercompany: due to {Code(other)}");
        }

        var unbalanced = entry.Lines.GroupBy(l => l.CompanyId).FirstOrDefault(g => g.Sum(l => l.Debit - l.Credit) != 0);
        if (unbalanced is not null)
        {
            throw new InvalidOperationException($"The entry doesn't balance in company {Code(unbalanced.Key)}.");
        }
    }

    private static void AddLine(IWorkbenchDbContext db, JournalEntry entry, Guid company, Guid counterparty, Guid account, decimal debit, decimal credit, string memo)
    {
        var line = new JournalLine
        {
            JournalEntryId = entry.Id,
            CompanyId = company,
            IntercompanyCompanyId = counterparty,
            LineNumber = entry.Lines.Max(l => l.LineNumber) + 1,
            AccountId = account,
            Debit = debit,
            Credit = credit,
            Memo = memo,
        };
        entry.Lines.Add(line);

        // Lines have client-generated keys: add them explicitly so a tracked (loaded) draft inserts them.
        db.Set<JournalLine>().Add(line);
    }
}
