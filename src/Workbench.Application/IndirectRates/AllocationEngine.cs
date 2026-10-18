using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.IndirectRates;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.IndirectRates;

/// <summary>
/// Allocation runs, the year-end true-up and the rate calculation, on a caller's context (the service, the period-close
/// check and the development seed share it). Unchecked: <see cref="IIndirectRateService"/> is the Finance-only face.
/// <para>
/// The allocation base is approved direct labor and allowable direct ODCs dated in the period (the same figures the GL
/// posts and the project financials show), burdened with the fiscal year's provisional rates in cascade order. Each run
/// posts one Allocation journal entry (source id = the period, revision = the run): debit the pool's applied-burden
/// account with project/WBS segments, credit its applied (contra) account. Re-running reverses the posted run first.
/// </para>
/// </summary>
public sealed class AllocationEngine(GeneralLedger ledger, AuditRecorder audit)
{
    public const string AuditEntityType = "IndirectAllocation";

    private sealed record Planned(Guid ProjectId, string ProjectCode, Guid WbsId, string WbsCode, Guid? ClinId, decimal Labor, decimal Odc, Burden Burden);

    // ---- Allocation runs ----

    public async Task<AllocationPreviewDto> PreviewAsync(IWorkbenchDbContext db, Guid periodId, CancellationToken ct)
    {
        var period = await FindPeriodAsync(db, periodId, ct);
        var book = await RateBook.LoadAsync(db, ct);
        return await PreviewAsync(db, book, period, ct);
    }

    private async Task<AllocationPreviewDto> PreviewAsync(IWorkbenchDbContext db, RateBook book, FiscalPeriod period, CancellationToken ct)
    {
        var (set, lines, notes) = await PlanPeriodAsync(db, book, period, ct);
        var posted = await PostedAsync(db, period.Id, ct);
        var expected = Expected(book, lines);
        var isCurrent = posted.Count == 0 ? expected.Count == 0 : posted.Count == 1 && SameAmounts(expected, posted[0]);
        return new AllocationPreviewDto(period.Id, period.Name, set.FiscalYear, period.Status == PeriodStatus.Closed, set.ToContract(),
            lines.Select(l => new AllocationLineDto(l.ProjectId, l.ProjectCode, l.WbsId, l.WbsCode, l.Labor, l.Odc, l.Burden.Fringe, l.Burden.Overhead, l.Burden.GeneralAndAdministrative)).ToList(),
            lines.Sum(l => l.Burden.Total), posted.Count == 0 ? null : ToPosted(posted[^1]), isCurrent, notes);
    }

    public async Task<IReadOnlyList<AllocationRunSummaryDto>> ListRunsAsync(IWorkbenchDbContext db, int fiscalYear, CancellationToken ct)
    {
        var book = await RateBook.LoadAsync(db, ct);
        var year = book.Years.FirstOrDefault(y => y.Year == fiscalYear) ?? throw new WorkbenchNotFoundException($"Fiscal year {fiscalYear} doesn't exist.");
        var periods = await db.Set<FiscalPeriod>().AsNoTracking().Where(p => p.FiscalYearId == year.Id).OrderBy(p => p.Start).ToListAsync(ct);
        var result = new List<AllocationRunSummaryDto>();
        foreach (var period in periods)
        {
            var preview = await PreviewAsync(db, book, period, ct);
            result.Add(new AllocationRunSummaryDto(period.Id, period.Name, period.Start, period.End, preview.PeriodClosed, preview.Total, preview.Posted, preview.IsCurrent));
        }

        return result;
    }

    /// <summary>Posts (or re-posts) a period's allocation and saves. <see cref="InvalidOperationException"/> when the period is closed.</summary>
    public async Task<AllocationPreviewDto> PostAsync(IWorkbenchDbContext db, Guid periodId, CancellationToken ct)
    {
        var period = await FindPeriodAsync(db, periodId, ct);
        EnsureOpen(period, "run the allocation");
        var book = await RateBook.LoadAsync(db, ct);
        if (book.Steps.Count == 0)
        {
            throw new InvalidOperationException("Set up the indirect pools before running an allocation.");
        }

        var (set, lines, _) = await PlanPeriodAsync(db, book, period, ct);
        var reversed = await ReverseActiveAsync(db, period.Id, _ => period.End, "Allocation re-run", ct);
        var revision = await NextRevisionAsync(db, period.Id, ct);
        var entry = BuildEntry(book, lines.Select(l => (l.ProjectId, l.WbsId, l.WbsCode, l.Burden)), period.End, period.Id, period.Id, revision,
            $"Indirect cost allocation {period.Name} (FY{set.FiscalYear} {set.Basis.ToString().ToLowerInvariant()} rates)" + (revision > 1 ? $", run {revision}" : ""));
        if (entry is not null)
        {
            db.Set<JournalEntry>().Add(entry);
            await ledger.PostAsync(db, entry, ct);
        }

        audit.Record(db, AuditEntityType, period.Id, "Posted", after: new
        {
            Period = period.Name,
            Revision = revision,
            Entry = entry?.Number,
            Burden = lines.Sum(l => l.Burden.Total),
            Rates = set.ToContract(),
            Reversed = reversed,
        });
        await db.SaveChangesAsync(ct);
        return await PreviewAsync(db, book, period, ct);
    }

    public async Task<AllocationPreviewDto> ReverseAsync(IWorkbenchDbContext db, Guid periodId, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to reverse an allocation.");
        }

        var period = await FindPeriodAsync(db, periodId, ct);
        EnsureOpen(period, "reverse the allocation");
        var reversed = await ReverseActiveAsync(db, period.Id, _ => period.End, reason.Trim(), ct);
        if (reversed == 0)
        {
            throw new InvalidOperationException($"No allocation is posted for {period.Name}.");
        }

        audit.Record(db, AuditEntityType, period.Id, "Reversed", after: new { Period = period.Name, Reversed = reversed }, reason: reason.Trim());
        await db.SaveChangesAsync(ct);
        return await PreviewAsync(db, period.Id, ct);
    }

    private async Task<(RateSet Set, List<Planned> Lines, List<string> Notes)> PlanPeriodAsync(IWorkbenchDbContext db, RateBook book, FiscalPeriod period, CancellationToken ct)
    {
        var notes = new List<string>();
        var year = book.Years.FirstOrDefault(y => y.Id == period.FiscalYearId) ?? book.YearOf(period.Start)
            ?? throw new InvalidOperationException($"{period.Name} isn't in a fiscal year.");
        var set = book.For(year.Year, IndirectRateBasis.Provisional);
        if (book.Steps.Count == 0)
        {
            notes.Add("No indirect pools are set up, so there's nothing to allocate.");
        }
        else if (set.Missing.Count > 0)
        {
            notes.Add(RateBook.MissingNote(set));
        }

        var costs = await DirectCosts.LoadAsync(db, null, period.Start, period.End, notes, ct);
        var lines = await PlanLinesAsync(db, costs, (labor, odc) => book.Apply(set, labor, odc), ct);
        var unallowable = costs.Sum(c => c.Unallowable);
        if (unallowable != 0)
        {
            notes.Add($"{unallowable:C} of unallowable cost is excluded from the base and not burdened.");
        }

        return (set, lines, notes);
    }

    private static async Task<List<Planned>> PlanLinesAsync(IWorkbenchDbContext db, IReadOnlyList<DirectCost> costs, Func<decimal, decimal, Burden> burden, CancellationToken ct)
    {
        var wbsIds = costs.Select(c => c.WbsElementId).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var projectIds = wbs.Values.Select(w => w.ProjectId).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        return costs.Where(c => c.Labor != 0 || c.Odc != 0)
            .GroupBy(c => c.WbsElementId)
            .Select(g =>
            {
                var w = wbs[g.Key];
                var (labor, odc) = (g.Sum(c => c.Labor), g.Sum(c => c.Odc));
                return new Planned(w.ProjectId, projects.GetValueOrDefault(w.ProjectId) ?? "?", w.Id, w.Code, w.ClinId, labor, odc, burden(labor, odc));
            })
            .OrderBy(l => l.WbsCode, StringComparer.Ordinal)
            .ToList();
    }

    // ---- Year-end true-up ----

    public async Task<TrueUpDto> TrueUpAsync(IWorkbenchDbContext db, int fiscalYear, CancellationToken ct)
    {
        var book = await RateBook.LoadAsync(db, ct);
        var (year, provisional, actual, lines, notes) = await PlanTrueUpAsync(db, book, fiscalYear, ct);
        var projectIds = lines.Select(l => l.ProjectId).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var contractIds = projects.Values.Where(p => p.ContractId is not null).Select(p => p.ContractId!.Value).Distinct().ToList();
        var contracts = await db.Set<Contract>().AsNoTracking().Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

        var rows = lines.Select(l =>
        {
            var contract = projects[l.ProjectId].ContractId is { } cid ? contracts.GetValueOrDefault(cid) : null;
            var diff = l.Actual.Minus(l.Provisional);
            return new TrueUpLineDto(l.ProjectId, l.ProjectCode, contract?.Id, contract?.ContractNumber, l.WbsId, l.WbsCode, l.ClinId, l.Labor, l.Odc,
                l.Provisional.Total, l.Actual.Total, diff.Fringe, diff.Overhead, diff.GeneralAndAdministrative);
        }).ToList();
        var byProject = rows.GroupBy(r => r.ProjectId)
            .Select(g => new TrueUpTotalDto(g.Key, projects[g.Key].Code, projects[g.Key].Name, g.Sum(r => r.DirectLabor + r.DirectOdc), g.Sum(r => r.ProvisionalBurden), g.Sum(r => r.ActualBurden)))
            .OrderBy(t => t.Code, StringComparer.Ordinal).ToList();
        var byContract = rows.Where(r => r.ContractId is not null).GroupBy(r => r.ContractId!.Value)
            .Select(g => new TrueUpTotalDto(g.Key, contracts[g.Key].ContractNumber, contracts[g.Key].Name, g.Sum(r => r.DirectLabor + r.DirectOdc), g.Sum(r => r.ProvisionalBurden), g.Sum(r => r.ActualBurden)))
            .OrderBy(t => t.Code, StringComparer.Ordinal).ToList();
        var posted = await PostedAsync(db, year.Id, ct);
        return new TrueUpDto(fiscalYear, actual.Basis == IndirectRateBasis.Actual, provisional.ToContract(), actual.ToContract(), rows, byProject, byContract,
            posted.Count == 0 ? null : ToPosted(posted[^1]), notes);
    }

    /// <summary>Posts the true-up adjustment (reversing an earlier one) and saves.</summary>
    public async Task<TrueUpDto> PostTrueUpAsync(IWorkbenchDbContext db, int fiscalYear, DateOnly? date, CancellationToken ct)
    {
        var book = await RateBook.LoadAsync(db, ct);
        var (year, _, actual, lines, _) = await PlanTrueUpAsync(db, book, fiscalYear, ct);
        if (actual.Basis != IndirectRateBasis.Actual)
        {
            throw new InvalidOperationException($"Enter actual rates for every pool for FY{fiscalYear} before posting the true-up.");
        }

        var on = date ?? year.End;
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Start <= on && p.End >= on, ct)
            ?? throw new ArgumentException($"No fiscal period covers {on:yyyy-MM-dd}.");
        EnsureOpen(period, "post the true-up");

        var open = (await db.Set<FiscalPeriod>().AsNoTracking().Where(p => p.Status == PeriodStatus.Open).ToListAsync(ct));
        DateOnly ReversalDate(JournalEntry e) => open.Any(p => p.Start <= e.Date && e.Date <= p.End) ? e.Date : on;
        var reversed = await ReverseActiveAsync(db, year.Id, ReversalDate, "True-up re-posted", ct);
        var revision = await NextRevisionAsync(db, year.Id, ct);
        var entry = BuildEntry(book, lines.Select(l => (l.ProjectId, l.WbsId, l.WbsCode, l.Actual.Minus(l.Provisional))), on, period.Id, year.Id, revision,
            $"Indirect rate true-up FY{fiscalYear}: actual vs provisional rates" + (revision > 1 ? $", run {revision}" : ""));
        if (entry is not null)
        {
            db.Set<JournalEntry>().Add(entry);
            await ledger.PostAsync(db, entry, ct);
        }

        audit.Record(db, AuditEntityType, year.Id, "TrueUpPosted", after: new
        {
            FiscalYear = fiscalYear,
            Revision = revision,
            Entry = entry?.Number,
            Difference = lines.Sum(l => l.Actual.Total - l.Provisional.Total),
            Reversed = reversed,
        });
        await db.SaveChangesAsync(ct);
        return await TrueUpAsync(db, fiscalYear, ct);
    }

    private sealed record TrueUpPlan(Guid ProjectId, string ProjectCode, Guid WbsId, string WbsCode, Guid? ClinId, decimal Labor, decimal Odc, Burden Provisional, Burden Actual);

    private static async Task<(FiscalYear Year, RateSet Provisional, RateSet Actual, List<TrueUpPlan> Lines, List<string> Notes)> PlanTrueUpAsync(
        IWorkbenchDbContext db, RateBook book, int fiscalYear, CancellationToken ct)
    {
        var year = book.Years.FirstOrDefault(y => y.Year == fiscalYear) ?? throw new WorkbenchNotFoundException($"Fiscal year {fiscalYear} doesn't exist.");
        var notes = new List<string>();
        var provisional = book.For(fiscalYear, IndirectRateBasis.Provisional);
        var actual = book.For(fiscalYear, IndirectRateBasis.Actual);
        if (provisional.Missing.Count > 0)
        {
            notes.Add(RateBook.MissingNote(provisional));
        }

        if (actual.Basis != IndirectRateBasis.Actual)
        {
            notes.Add($"FY{fiscalYear} doesn't have an actual rate for every pool yet, so actual cost equals provisional cost.");
        }

        var costs = await DirectCosts.LoadAsync(db, null, year.Start, year.End, notes, ct);
        var planned = await PlanLinesAsync(db, costs, (labor, odc) => book.Apply(provisional, labor, odc), ct);
        var lines = planned.Select(p => new TrueUpPlan(p.ProjectId, p.ProjectCode, p.WbsId, p.WbsCode, p.ClinId, p.Labor, p.Odc, p.Burden, book.Apply(actual, p.Labor, p.Odc))).ToList();
        return (year, provisional, actual, lines, notes);
    }

    // ---- Rate calculation from GL balances ----

    public async Task<RateCalculationDto> CalculateRatesAsync(IWorkbenchDbContext db, Guid fromPeriodId, Guid toPeriodId, CancellationToken ct)
    {
        var periods = (await db.Set<FiscalPeriod>().AsNoTracking().ToListAsync(ct)).OrderBy(p => p.Start).ToList();
        var from = periods.FirstOrDefault(p => p.Id == fromPeriodId) ?? throw WorkbenchNotFoundException.For("Fiscal period", fromPeriodId);
        var to = periods.FirstOrDefault(p => p.Id == toPeriodId) ?? throw WorkbenchNotFoundException.For("Fiscal period", toPeriodId);
        if (to.Start < from.Start)
        {
            throw new ArgumentException("The range ends before it starts.");
        }

        var book = await RateBook.LoadAsync(db, ct);
        var map = await PoolAccountMap.LoadAsync(db, book.Setups, ct);
        var years = book.Years.ToDictionary(y => y.Id, y => y.Year);
        int YearOf(FiscalPeriod p) => years.GetValueOrDefault(p.FiscalYearId);

        // Load from the start of the first period's fiscal year so the year-to-date trend is complete.
        var loadFrom = periods.Where(p => p.FiscalYearId == from.FiscalYearId).Min(p => p.Start);
        var loaded = periods.Where(p => p.Start >= loadFrom && p.Start <= to.Start).ToList();
        var loadedIds = loaded.Select(p => p.Id).ToList();
        var lines = await (
                from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                where e.Status != JournalStatus.Draft && loadedIds.Contains(e.FiscalPeriodId)
                select new { e.FiscalPeriodId, l.AccountId, l.Debit, l.Credit })
            .ToListAsync(ct);
        var byPeriod = lines.GroupBy(l => l.FiscalPeriodId).ToDictionary(g => g.Key, g => g.GroupBy(l => l.AccountId).ToDictionary(a => a.Key, a => a.Sum(l => l.Debit - l.Credit)));

        Figures FiguresOf(IEnumerable<FiscalPeriod> ps)
        {
            var balances = new Dictionary<Guid, decimal>();
            foreach (var p in ps)
            {
                foreach (var (account, net) in byPeriod.GetValueOrDefault(p.Id) ?? [])
                {
                    balances[account] = balances.GetValueOrDefault(account) + net;
                }
            }

            decimal Sum(IEnumerable<Guid> ids) => ids.Sum(id => balances.GetValueOrDefault(id));
            return new Figures(
                Sum(map.DirectLabor), Sum(map.DirectOdc),
                book.Setups.ToDictionary(s => s.Pool, s => Sum(map.Pools.GetValueOrDefault(s.Pool) ?? [])),
                book.Setups.ToDictionary(s => s.Pool, s => -balances.GetValueOrDefault(s.AbsorbedAccountId)));
        }

        var notes = new List<string>();
        if (book.Steps.Count == 0)
        {
            notes.Add("No indirect pools are set up.");
        }

        var range = loaded.Where(p => p.Start >= from.Start).ToList();
        var fiscalYear = YearOf(to);
        var total = FiguresOf(range);
        var pools = new List<PoolRateDto>();
        foreach (var (pool, b, rate) in BurdenCascade.ActualRates(book.Steps, total.Labor, total.Odc, p => total.Pool.GetValueOrDefault(p)))
        {
            var setup = book.Setup(pool)!;
            var provisional = book.Rate(pool, fiscalYear, IndirectRateBasis.Provisional);
            var cost = total.Pool.GetValueOrDefault(pool);
            var applied = total.Applied.GetValueOrDefault(pool);
            pools.Add(new PoolRateDto(pool.ToContract(), setup.Name, (AllocationBaseKind)(int)setup.Base, cost, b, rate, provisional,
                rate is { } r && provisional is { } pr ? r - pr : null, applied, cost - applied));
        }

        if (range.Select(YearOf).Distinct().Count() > 1)
        {
            notes.Add($"The range spans fiscal years; the provisional rates shown are FY{fiscalYear}'s.");
        }

        var trend = new List<RateTrendPointDto>();
        foreach (var period in range)
        {
            var month = FiguresOf([period]);
            var ytd = FiguresOf(loaded.Where(p => p.FiscalYearId == period.FiscalYearId && p.Start <= period.Start));
            var monthRates = BurdenCascade.ActualRates(book.Steps, month.Labor, month.Odc, p => month.Pool.GetValueOrDefault(p)).ToDictionary(r => r.Pool);
            var ytdRates = BurdenCascade.ActualRates(book.Steps, ytd.Labor, ytd.Odc, p => ytd.Pool.GetValueOrDefault(p)).ToDictionary(r => r.Pool);
            foreach (var step in book.Steps)
            {
                trend.Add(new RateTrendPointDto(period.Id, period.Name, YearOf(period), step.Pool.ToContract(),
                    month.Pool.GetValueOrDefault(step.Pool), monthRates[step.Pool].Base, monthRates[step.Pool].Rate,
                    ytd.Pool.GetValueOrDefault(step.Pool), ytdRates[step.Pool].Base, ytdRates[step.Pool].Rate,
                    book.Rate(step.Pool, YearOf(period), IndirectRateBasis.Provisional)));
            }
        }

        return new RateCalculationDto(from.Id, to.Id, from.Name, to.Name, fiscalYear, pools, trend, notes);
    }

    private sealed record Figures(decimal Labor, decimal Odc, Dictionary<IndirectPool, decimal> Pool, Dictionary<IndirectPool, decimal> Applied);

    // ---- Journal helpers ----

    private JournalEntry? BuildEntry(RateBook book, IEnumerable<(Guid Project, Guid Wbs, string Code, Burden Burden)> lines,
        DateOnly date, Guid periodId, Guid sourceId, int revision, string description)
    {
        var entry = new JournalEntry
        {
            Date = date,
            FiscalPeriodId = periodId,
            Source = JournalSource.Allocation,
            SourceId = sourceId,
            SourceRevision = revision,
            Description = description,
            CreatedBy = ledger.Actor,
            CreatedAtUtc = ledger.UtcNow,
        };
        var credits = new Dictionary<IndirectPool, decimal>();
        foreach (var line in lines.OrderBy(l => l.Code, StringComparer.Ordinal))
        {
            foreach (var step in book.Steps)
            {
                var amount = line.Burden[step.Pool];
                AddLine(entry, book.Setup(step.Pool)!.AppliedAccountId, amount, line.Project, line.Wbs, $"{RateBook.Label(step.Pool)} on {line.Code}");
                credits[step.Pool] = credits.GetValueOrDefault(step.Pool) + amount;
            }
        }

        foreach (var step in book.Steps)
        {
            AddLine(entry, book.Setup(step.Pool)!.AbsorbedAccountId, -credits.GetValueOrDefault(step.Pool), null, null, $"{RateBook.Label(step.Pool)} applied");
        }

        return entry.Lines.Count < 2 ? null : entry;
    }

    private static void AddLine(JournalEntry entry, Guid account, decimal signedDebit, Guid? project, Guid? wbs, string memo)
    {
        if (signedDebit == 0)
        {
            return;
        }

        entry.Lines.Add(new JournalLine
        {
            JournalEntryId = entry.Id,
            LineNumber = entry.Lines.Count + 1,
            AccountId = account,
            ProjectId = project,
            WbsElementId = wbs,
            Debit = signedDebit > 0 ? signedDebit : 0,
            Credit = signedDebit < 0 ? -signedDebit : 0,
            Memo = memo,
        });
    }

    private static Task<List<JournalEntry>> PostedAsync(IWorkbenchDbContext db, Guid sourceId, CancellationToken ct) =>
        db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
            .Where(e => e.Source == JournalSource.Allocation && e.SourceId == sourceId && e.Status == JournalStatus.Posted)
            .OrderBy(e => e.Sequence).ToListAsync(ct);

    private async Task<int> ReverseActiveAsync(IWorkbenchDbContext db, Guid sourceId, Func<JournalEntry, DateOnly> date, string reason, CancellationToken ct)
    {
        var active = await db.Set<JournalEntry>().Include(e => e.Lines)
            .Where(e => e.Source == JournalSource.Allocation && e.SourceId == sourceId && e.Status == JournalStatus.Posted)
            .ToListAsync(ct);
        foreach (var entry in active)
        {
            await ledger.ReverseAsync(db, entry, date(entry), reason, ct);
        }

        return active.Count;
    }

    private static async Task<int> NextRevisionAsync(IWorkbenchDbContext db, Guid sourceId, CancellationToken ct) =>
        (await db.Set<JournalEntry>().Where(e => e.Source == JournalSource.Allocation && e.SourceId == sourceId).MaxAsync(e => (int?)e.SourceRevision, ct) ?? 0) + 1;

    private static Dictionary<(Guid Wbs, Guid Account), decimal> Expected(RateBook book, IEnumerable<Planned> lines)
    {
        var result = new Dictionary<(Guid, Guid), decimal>();
        foreach (var line in lines)
        {
            foreach (var step in book.Steps)
            {
                var key = (line.WbsId, book.Setup(step.Pool)!.AppliedAccountId);
                result[key] = result.GetValueOrDefault(key) + line.Burden[step.Pool];
            }
        }

        return result.Where(kv => kv.Value != 0).ToDictionary();
    }

    private static bool SameAmounts(Dictionary<(Guid Wbs, Guid Account), decimal> expected, JournalEntry posted)
    {
        var actual = posted.Lines.Where(l => l.WbsElementId is not null)
            .GroupBy(l => (l.WbsElementId!.Value, l.AccountId))
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit))
            .Where(kv => kv.Value != 0).ToDictionary();
        return actual.Count == expected.Count && expected.All(kv => actual.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    private static PostedAllocationDto ToPosted(JournalEntry e) => new(e.Id, e.Number, e.Date, e.SourceRevision,
        e.Lines.Where(l => l.WbsElementId is not null).Sum(l => l.Debit - l.Credit), e.PostedBy, e.PostedAtUtc);

    private static async Task<FiscalPeriod> FindPeriodAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Fiscal period", id);

    private static void EnsureOpen(FiscalPeriod period, string what)
    {
        if (period.Status == PeriodStatus.Closed)
        {
            throw new InvalidOperationException($"Fiscal period {period.Name} is closed; reopen it to {what}.");
        }
    }
}
