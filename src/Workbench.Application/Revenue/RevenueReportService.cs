using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Revenue;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Revenue;

namespace Workbench.Application.Revenue;

/// <summary>
/// Revenue reports read from the general ledger (revenue accounts 4010–4040 with a project segment), so they include
/// revenue from every source: invoices, accruals and revenue runs.
/// </summary>
public sealed class RevenueReportService(IWorkbenchDbContextFactory dbFactory) : IRevenueReportService
{
    public async Task<RevenueByPeriodDto> GetRevenueByPeriodAsync(RevenueByPeriodQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var first = await RevenueSupport.FindPeriodAsync(db, query.FromPeriodId, cancellationToken);
        var last = await RevenueSupport.FindPeriodAsync(db, query.ToPeriodId, cancellationToken);
        if (last.Start < first.Start)
        {
            throw new ArgumentException("The last period is before the first.");
        }

        var periods = (await db.Set<FiscalPeriod>().AsNoTracking().Where(p => p.Start >= first.Start && p.Start <= last.Start).ToListAsync(cancellationToken))
            .OrderBy(p => p.Start).ToList();
        if (periods.Count > 36)
        {
            throw new ArgumentException("Choose at most 36 periods.");
        }

        var accounts = RevenueEngine.RevenueAccounts.ToList();
        var lines = await (
                from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                join a in db.Set<Account>().AsNoTracking() on l.AccountId equals a.Id
                where l.ProjectId != null && e.Status != JournalStatus.Draft && e.Date >= first.Start && e.Date <= last.End && accounts.Contains(a.Number)
                select new { ProjectId = l.ProjectId!.Value, e.Date, Amount = l.Credit - l.Debit })
            .ToListAsync(cancellationToken);
        var names = await RevenueSupport.Names.LoadAsync(db, [], cancellationToken);
        var methods = await ProjectMethodsAsync(db, cancellationToken);
        int Index(DateOnly date) => periods.FindIndex(p => p.Start <= date && p.End >= date);

        var keyed = lines.Select(l => (Key: query.GroupBy == RevenueGrouping.Contract ? names.Projects.GetValueOrDefault(l.ProjectId)?.ContractId ?? Guid.Empty : l.ProjectId, l.Date, l.Amount))
            .Where(l => l.Key != Guid.Empty && Index(l.Date) >= 0).ToList();
        var rows = keyed.GroupBy(l => l.Key).Select(g =>
            {
                var amounts = periods.Select((_, i) => g.Where(l => Index(l.Date) == i).Sum(l => l.Amount)).ToList();
                var (code, name, method) = query.GroupBy == RevenueGrouping.Contract
                    ? (names.Contracts[g.Key].ContractNumber, names.Contracts[g.Key].Name, (string?)null)
                    : (names.Projects[g.Key].Code, names.Projects[g.Key].Name, methods.GetValueOrDefault(g.Key));
                return new RevenueByPeriodRowDto(g.Key, code, name, method, amounts, amounts.Sum());
            })
            .OrderBy(r => r.Code, StringComparer.Ordinal).ToList();
        return Totals(periods.Select(p => p.Name).ToList(), rows);
    }

    /// <summary>The report with column and grand totals over <paramref name="rows"/> (also used after filtering rows).</summary>
    public static RevenueByPeriodDto Totals(IReadOnlyList<string> periods, IReadOnlyList<RevenueByPeriodRowDto> rows)
    {
        var totals = periods.Select((_, i) => rows.Sum(r => r.Amounts[i])).ToList();
        return new RevenueByPeriodDto(periods, rows, totals, totals.Sum());
    }

    public async Task<RevenueVsBillingsDto> GetRevenueVsBillingsAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await RevenueSupport.FindPeriodAsync(db, fiscalPeriodId, cancellationToken);
        var end = period.End;
        var accounts = RevenueEngine.RevenueAccounts.ToList();
        var lines = await (
                from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                join a in db.Set<Account>().AsNoTracking() on l.AccountId equals a.Id
                where l.ProjectId != null && e.Status != JournalStatus.Draft && e.Date <= end && accounts.Contains(a.Number)
                select new { ProjectId = l.ProjectId!.Value, e.Source, e.SourceId, e.ReversalOfId, Amount = l.Credit - l.Debit })
            .ToListAsync(cancellationToken);

        // Invoiced = revenue posted by billing for invoices and credit memos (and their voids), not its unbilled accruals.
        var accrualIds = (await db.Set<UnbilledAccrual>().AsNoTracking().Select(a => a.Id).ToListAsync(cancellationToken)).ToHashSet();
        var reversedIds = lines.Where(l => l.ReversalOfId != null).Select(l => l.ReversalOfId!.Value).Distinct().ToList();
        var reversed = await db.Set<JournalEntry>().AsNoTracking().Where(e => reversedIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => new { e.Source, e.SourceId }, cancellationToken);
        bool IsInvoice(JournalSource source, Guid? sourceId) => source == JournalSource.Billing && !(sourceId is { } s && accrualIds.Contains(s));
        bool Invoiced(JournalSource source, Guid? sourceId, Guid? reversalOf) =>
            IsInvoice(source, sourceId) || (reversalOf is { } r && reversed.TryGetValue(r, out var o) && IsInvoice(o.Source, o.SourceId));

        var names = await RevenueSupport.Names.LoadAsync(db, [], cancellationToken);
        var methods = await ProjectMethodsAsync(db, cancellationToken);
        var lastRuns = (await (from r in db.Set<RevenueRun>().AsNoTracking()
                               join p in db.Set<FiscalPeriod>().AsNoTracking() on r.FiscalPeriodId equals p.Id
                               where r.Status == RevenueRunStatus.Posted && p.End <= end
                               select new { r.ProjectId, p.End }).ToListAsync(cancellationToken))
            .GroupBy(r => r.ProjectId).ToDictionary(g => g.Key, g => g.Max(r => r.End));

        var rows = new List<RevenueVsBillingsRowDto>();
        foreach (var project in names.Projects.Values.Where(p => p.ContractId != null).OrderBy(p => p.Code, StringComparer.Ordinal))
        {
            var mine = lines.Where(l => l.ProjectId == project.Id).ToList();
            var recognized = mine.Sum(l => l.Amount);
            var billed = mine.Where(l => Invoiced(l.Source, l.SourceId, l.ReversalOfId)).Sum(l => l.Amount);
            var provision = (await RevenueEngine.LedgerAsync(db, project.Id, end, cancellationToken)).LossProvision;
            if (recognized == 0 && billed == 0 && provision == 0 && !lastRuns.ContainsKey(project.Id))
            {
                continue;
            }

            var (asset, liability) = RevenueCalculator.Position(recognized, billed);
            rows.Add(new RevenueVsBillingsRowDto(project.Id, project.Code, project.Name, names.ContractNumber(project.Id),
                methods.GetValueOrDefault(project.Id) ?? RevenueSupport.Label(RevenueMethod.Billings), recognized, billed, asset, liability, provision,
                lastRuns.TryGetValue(project.Id, out var last) ? last : null));
        }

        return new RevenueVsBillingsDto(period.Id, period.Name, end, rows);
    }

    public async Task<BacklogDto> GetBacklogAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await RevenueSupport.FindPeriodAsync(db, fiscalPeriodId, cancellationToken);
        var names = await RevenueSupport.Names.LoadAsync(db, [], cancellationToken);
        var customers = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var revenue = await RevenueEngine.RevenueByProjectAsync(db, null, period.End, cancellationToken);
        var rows = new List<BacklogRowDto>();
        foreach (var contract in names.Contracts.Values.Where(c => c.Status != ContractStatus.Closed).OrderBy(c => c.ContractNumber, StringComparer.Ordinal))
        {
            var clins = names.Clins.Values.Where(c => c.ContractId == contract.Id).ToList();
            var (value, funded) = RevenueEngine.ContractValue(contract, clins);
            var recognized = names.Projects.Values.Where(p => p.ContractId == contract.Id).Sum(p => revenue.GetValueOrDefault(p.Id));
            var fundedBacklog = Math.Max(0, funded - recognized);
            var unfundedBacklog = Math.Max(0, value - Math.Max(funded, recognized));
            rows.Add(new BacklogRowDto(contract.Id, contract.ContractNumber, contract.Name, customers.GetValueOrDefault(contract.CustomerId) ?? "",
                value, funded, recognized, fundedBacklog, unfundedBacklog));
        }

        return new BacklogDto(period.End, rows);
    }

    /// <summary>Each project's revenue method label (its project-level setup; CLIN setups noted).</summary>
    private static async Task<Dictionary<Guid, string>> ProjectMethodsAsync(IWorkbenchDbContext db, CancellationToken ct) =>
        (await db.Set<RevenueSetup>().AsNoTracking().ToListAsync(ct))
            .GroupBy(s => s.ProjectId)
            .ToDictionary(g => g.Key, g =>
            {
                var main = g.FirstOrDefault(s => s.ClinId is null)?.Method ?? RevenueMethod.Billings;
                var clins = g.Count(s => s.ClinId is not null);
                return RevenueSupport.Label(main) + (clins > 0 ? $" (+{clins} CLIN setup{(clins == 1 ? "" : "s")})" : "");
            });
}
