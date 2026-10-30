using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Migration;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Migration;

/// <summary>What Workbench's ledger holds on one account around a period, for explaining a trial balance difference.</summary>
public sealed record AccountFacts(
    bool Exists,
    int PostedLines,
    int DraftCount,
    decimal DraftNet,
    string? DraftExample,
    int LateCount,
    decimal LateNet,
    string? LateExample,
    int EarlyNextCount,
    decimal EarlyNextNet,
    string? EarlyNextExample,
    IReadOnlyList<(decimal Amount, string Entry, string Description)> Lines)
{
    public static AccountFacts None { get; } = new(false, 0, 0, 0, null, 0, 0, null, 0, 0, null, []);
}

/// <summary>
/// Turns the differences of a reconciliation run into likely causes in plain words: rounding, a difference carried over
/// from an earlier month, draft entries not yet posted, entries posted or dated just outside the month, one entry the size
/// of the whole difference, burden applied at a different rate. Only a hint to start from; the detail behind each key stays
/// one click away. The rules themselves are static and take plain figures, so they can be tested without a database.
/// </summary>
public sealed class ParallelRunExplainer(IWorkbenchDbContextFactory dbFactory)
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private const decimal Cent = 0.005m;

    /// <summary>Entries dated this many days after the month end count as "just outside the month".</summary>
    private const int CutoffDays = 5;

    public static string Money(decimal value) => value.ToString("C2", Us);

    public async Task<IReadOnlyList<ParallelCauseDto>> ExplainAsync(ReconRunDetailDto run, CancellationToken cancellationToken)
    {
        var different = run.Rows.Where(r => r.Status != ReconRowStatus.Matched).ToList();
        if (different.Count == 0)
        {
            return [];
        }

        var kind = run.Run.Kind;
        var health = await HealthAsync(run.Run.FiscalPeriodId, run.Run.From, run.Run.To, cancellationToken);
        if (kind == ReconReportKind.TrialBalance)
        {
            var facts = await AccountFactsAsync(run.Run.FiscalPeriodId, run.Run.From, run.Run.To, different.Select(r => r.KeyParts[0]).ToList(), cancellationToken);
            return different.Select(r => ExplainTrialBalance(r, facts.GetValueOrDefault(r.KeyParts[0].Trim().ToUpperInvariant()) ?? AccountFacts.None)).ToList();
        }

        if (kind == ReconReportKind.ProjectCost)
        {
            return different.Select(r => ExplainProjectCost(r, health)).ToList();
        }

        return different.Select(r => Cause(r, kind, ParallelCauseKind.Other,
            r.Status switch
            {
                ReconRowStatus.OnlyInCostpoint => "Costpoint lists this and Workbench has nothing for it.",
                ReconRowStatus.OnlyInWorkbench => "Workbench has this and the Costpoint report doesn't list it.",
                _ => "The amounts differ.",
            }, "Open the detail to see the entries on each side.")).ToList();
    }

    public async Task<ParallelHealthDto> HealthAsync(Guid periodId, DateOnly start, DateOnly to, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var drafts = await db.Set<JournalEntry>().AsNoTracking().CountAsync(e => e.FiscalPeriodId == periodId && e.Status == JournalStatus.Draft, cancellationToken);
        var sheets = await db.Set<Timesheet>().AsNoTracking().CountAsync(t => t.PeriodStart <= to && t.PeriodEnd >= start && t.Status != TimesheetStatus.Approved, cancellationToken);
        var bills = await db.Set<ApBill>().AsNoTracking().CountAsync(b => b.InvoiceDate >= start && b.InvoiceDate <= to
            && (b.Status == ApBillStatus.Draft || b.Status == ApBillStatus.PendingApproval || b.Status == ApBillStatus.Approved), cancellationToken);
        var closed = await db.Set<FiscalPeriod>().AsNoTracking().AnyAsync(p => p.Id == periodId && p.Status == PeriodStatus.Closed, cancellationToken);
        return new ParallelHealthDto(drafts, sheets, bills, closed);
    }

    private async Task<Dictionary<string, AccountFacts>> AccountFactsAsync(Guid periodId, DateOnly start, DateOnly to, List<string> numbers, CancellationToken cancellationToken)
    {
        var wanted = numbers.Select(n => n.Trim().ToUpperInvariant()).ToHashSet();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var accounts = (await db.Set<Account>().AsNoTracking().Select(a => new { a.Id, a.Number }).ToListAsync(cancellationToken))
            .Where(a => wanted.Contains(a.Number.Trim().ToUpperInvariant())).ToDictionary(a => a.Id, a => a.Number.Trim().ToUpperInvariant());
        var ids = accounts.Keys.ToList();
        var until = to.AddDays(CutoffDays);
        var lines = await (from l in db.Set<JournalLine>().AsNoTracking()
                           join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                           where ids.Contains(l.AccountId) && e.Date >= start && e.Date <= until && e.Status != JournalStatus.Reversed
                           select new { l.AccountId, l.Debit, l.Credit, e.Number, e.Description, e.Date, e.Status, e.FiscalPeriodId, e.PostedAtUtc }).ToListAsync(cancellationToken);

        var result = new Dictionary<string, AccountFacts>();
        foreach (var number in wanted)
        {
            var id = accounts.FirstOrDefault(a => a.Value == number).Key;
            if (id == Guid.Empty)
            {
                result[number] = AccountFacts.None;
                continue;
            }

            var mine = lines.Where(l => l.AccountId == id).ToList();
            var inPeriod = mine.Where(l => l.Date <= to).ToList();
            var posted = inPeriod.Where(l => l.Status == JournalStatus.Posted).ToList();
            var draft = inPeriod.Where(l => l.Status == JournalStatus.Draft).ToList();
            var late = posted.Where(l => l.PostedAtUtc is { } p && DateOnly.FromDateTime(p) > to).ToList();
            var early = mine.Where(l => l.Date > to && l.Status == JournalStatus.Posted).ToList();
            result[number] = new AccountFacts(true, posted.Count,
                draft.Count, draft.Sum(l => l.Debit - l.Credit), draft.FirstOrDefault()?.Description,
                late.Count, late.Sum(l => l.Debit - l.Credit), late.FirstOrDefault() is { } lf ? $"{lf.Number} {lf.Description}" : null,
                early.Count, early.Sum(l => l.Debit - l.Credit), early.FirstOrDefault() is { } ef ? $"{ef.Number} {ef.Description}" : null,
                posted.Select(l => (l.Debit - l.Credit, l.Number, l.Description)).ToList());
        }

        return result;
    }

    // ---- The rules ----

    private static ParallelCauseDto Cause(ReconRowDto row, ReconReportKind report, ParallelCauseKind kind, string summary, string? next, decimal? difference = null) =>
        new(row.Key, row.Label, report, kind, summary, difference ?? row.Values.FirstOrDefault(v => !v.WithinTolerance)?.Difference ?? row.Values.FirstOrDefault()?.Difference ?? 0m, next);

    public static ParallelCauseDto ExplainTrialBalance(ReconRowDto row, AccountFacts facts)
    {
        const ReconReportKind report = ReconReportKind.TrialBalance;
        var name = row.Label is { Length: > 0 } l ? $"{row.Key} {l}" : $"Account {row.Key}";
        var ending = row.Values.FirstOrDefault(v => v.Field == "Ending");
        var cost = ending?.Costpoint ?? 0m;
        var wb = ending?.Workbench ?? 0m;

        if (row.Status == ReconRowStatus.OnlyInCostpoint)
        {
            return !facts.Exists
                ? Cause(row, report, ParallelCauseKind.MissingInWorkbench, $"{name} is in Costpoint's report but not in Workbench's chart of accounts.",
                    "Import the chart of accounts again, or add a key rewrite if Costpoint numbers it differently.", -cost)
                : Cause(row, report, ParallelCauseKind.MissingInWorkbench, $"Costpoint shows {Money(cost)} on {name}; Workbench has no balance on it.",
                    facts.DraftCount > 0 ? $"{facts.DraftCount} draft entr{(facts.DraftCount == 1 ? "y" : "ies")} touch it ({Money(facts.DraftNet)}); post them." : "Look for a transaction keyed in Costpoint only.", -cost);
        }

        if (row.Status == ReconRowStatus.OnlyInWorkbench)
        {
            return Cause(row, report, ParallelCauseKind.MissingInCostpoint, $"Workbench shows {Money(wb)} on {name}; the Costpoint report doesn't list the account.",
                "Check the report wasn't filtered to fewer accounts, or whether the entry was keyed in Workbench only.", wb);
        }

        var diff = wb - cost;
        var opening = row.Values.FirstOrDefault(v => v.Field == "Opening");
        var remaining = diff;
        string? carried = null;
        if (opening is { WithinTolerance: false })
        {
            var open = opening.Difference;
            if (Math.Abs(open - diff) < Cent)
            {
                return Cause(row, report, ParallelCauseKind.CarriedOver,
                    $"{name} already differed by {Money(open)} at the start of the month, and nothing this month changed it.",
                    "Reconcile the previous month first; fix that difference and this one clears.", diff);
            }

            carried = $"{Money(open)} of it was already there at the start of the month. ";
            remaining = diff - open;
        }

        var prefix = $"{name} is {Money(Math.Abs(diff))} {(diff > 0 ? "higher" : "lower")} in Workbench. {carried}";
        var gap = Math.Abs(remaining);
        if (gap < Cent)
        {
            return Cause(row, report, ParallelCauseKind.CarriedOver, prefix.TrimEnd(), "Reconcile the previous month first.", diff);
        }

        if (gap <= 1m)
        {
            return Cause(row, report, ParallelCauseKind.Rounding, $"{prefix}A difference under a dollar is usually rounding (burden or allocation rounded at a different level).",
                "Sign off with a note, or align the rounding level.", diff);
        }

        if (facts.DraftCount > 0 && Math.Abs(facts.DraftNet + remaining) < Cent)
        {
            return Cause(row, report, ParallelCauseKind.Unposted,
                $"{prefix}Workbench has {facts.DraftCount} draft entr{(facts.DraftCount == 1 ? "y" : "ies")} on this account worth {Money(facts.DraftNet)} (for example \"{facts.DraftExample}\"); posted, they would close the difference.",
                "Post the draft entries in the period, then run the comparison again.", diff);
        }

        if (facts.LateCount > 0 && Math.Abs(facts.LateNet - remaining) < Cent)
        {
            return Cause(row, report, ParallelCauseKind.Timing,
                $"{prefix}{facts.LateCount} entr{(facts.LateCount == 1 ? "y" : "ies")} worth {Money(facts.LateNet)} (for example {facts.LateExample}) were posted in Workbench after the month ended, so Costpoint probably booked them in a later month.",
                "Confirm which period Costpoint used; if it's the next month, this clears next month.", diff);
        }

        if (facts.EarlyNextCount > 0 && Math.Abs(facts.EarlyNextNet + remaining) < Cent)
        {
            return Cause(row, report, ParallelCauseKind.Timing,
                $"{prefix}{facts.EarlyNextCount} entr{(facts.EarlyNextCount == 1 ? "y" : "ies")} worth {Money(facts.EarlyNextNet)} (for example {facts.EarlyNextExample}) are dated just after the month end in Workbench, but Costpoint counted them in this month.",
                "Check the transaction date used in each system.", diff);
        }

        if (facts.Lines.FirstOrDefault(x => Math.Abs(Math.Abs(x.Amount) - gap) < Cent) is { Entry: not null } single && single.Amount != 0)
        {
            return Cause(row, report, ParallelCauseKind.SingleEntry,
                $"{prefix}One entry in Workbench, {single.Entry} \"{single.Description}\" ({Money(single.Amount)}), is exactly the size of the difference.",
                single.Amount > 0 && remaining > 0 ? "Check it was also keyed in Costpoint for this month." : "Check it was also keyed in Workbench for this month, with the same sign.", diff);
        }

        if (facts.DraftCount > 0)
        {
            return Cause(row, report, ParallelCauseKind.Unposted,
                $"{prefix}Workbench also has {facts.DraftCount} draft entr{(facts.DraftCount == 1 ? "y" : "ies")} on this account worth {Money(facts.DraftNet)} that may explain part of it.",
                "Post the drafts, run the comparison again, and open the detail for what remains.", diff);
        }

        return Cause(row, report, ParallelCauseKind.Other, $"{prefix}No single entry, draft or late posting explains it.", "Open the detail and compare the entries line by line.", diff);
    }

    public static ParallelCauseDto ExplainProjectCost(ReconRowDto row, ParallelHealthDto health)
    {
        const ReconReportKind report = ReconReportKind.ProjectCost;
        var name = row.Label is { Length: > 0 } l ? $"{row.Key} {l}" : $"Project {row.Key}";
        ReconValueDto? V(string f) => row.Values.FirstOrDefault(v => v.Field == f);

        if (row.Status == ReconRowStatus.OnlyInCostpoint)
        {
            return Cause(row, report, ParallelCauseKind.MissingInWorkbench, $"{name} has cost in Costpoint and none in Workbench.",
                "Check the project exists in Workbench with the same code (or at the same level, project vs WBS), and that its time and bills are entered.");
        }

        if (row.Status == ReconRowStatus.OnlyInWorkbench)
        {
            return Cause(row, report, ParallelCauseKind.MissingInCostpoint, $"{name} has cost in Workbench and the Costpoint report doesn't list it.",
                "Check the Costpoint report covers this project and period.");
        }

        var dl = V("DirectLabor");
        var odc = V("DirectOdc");
        var layers = new[] { ("Fringe", "fringe"), ("Overhead", "overhead"), ("GeneralAndAdministrative", "G&A") }
            .Select(x => (x.Item2, Value: V(x.Item1))).Where(x => x.Value is { WithinTolerance: false }).ToList();
        var laborOk = dl is null || dl.WithinTolerance;
        var odcOk = odc is null || odc.WithinTolerance;

        if (laborOk && odcOk && layers.Count > 0)
        {
            var parts = new List<string>();
            var cpBase = (dl?.Costpoint ?? 0m);
            var wbBase = (dl?.Workbench ?? 0m);
            var cpFringe = V("Fringe")?.Costpoint ?? 0m;
            var wbFringe = V("Fringe")?.Workbench ?? 0m;
            foreach (var (label, value) in layers)
            {
                var (cpRate, wbRate) = label switch
                {
                    "fringe" => (Ratio(value!.Costpoint, cpBase), Ratio(value.Workbench, wbBase)),
                    "overhead" => (Ratio(value!.Costpoint, cpBase + cpFringe), Ratio(value.Workbench, wbBase + wbFringe)),
                    _ => (Ratio(value!.Costpoint, (row.Values.Sum(v => v.Field is "DirectLabor" or "Fringe" or "Overhead" or "DirectOdc" ? v.Costpoint : 0m))),
                        Ratio(value.Workbench, row.Values.Sum(v => v.Field is "DirectLabor" or "Fringe" or "Overhead" or "DirectOdc" ? v.Workbench : 0m))),
                };
                parts.Add(cpRate is { } c && wbRate is { } w
                    ? $"{label}: Costpoint works out to {c:P2}, Workbench to {w:P2} ({Money(value.Difference)})"
                    : $"{label}: {Money(value.Difference)}");
            }

            return Cause(row, report, ParallelCauseKind.RateDifference,
                $"{name}: direct labor and other direct costs agree, but the burden differs. {string.Join("; ", parts)}.",
                "Compare the provisional rates in Indirect › Rates with Costpoint's billing rates for the period; set one right and rerun the allocation.",
                layers.Sum(x => x.Value!.Difference));
        }

        if (!laborOk)
        {
            var d = dl!.Difference;
            var unapproved = d < 0 && health.UnapprovedTimesheets > 0
                ? $" Workbench still has {health.UnapprovedTimesheets} timesheet{(health.UnapprovedTimesheets == 1 ? "" : "s")} in this month that aren't approved, and their hours aren't in the cost yet."
                : "";
            return Cause(row, report, d < 0 && health.UnapprovedTimesheets > 0 ? ParallelCauseKind.Unposted : ParallelCauseKind.Hours,
                $"{name}: direct labor is {Money(Math.Abs(d))} {(d > 0 ? "higher" : "lower")} in Workbench, so hours or cost rates differ.{unapproved}",
                "Run the Labor distribution comparison for this project to see which employees differ, then check their cost rates and approved hours.", d);
        }

        if (!odcOk)
        {
            var d = odc!.Difference;
            var unposted = d < 0 && health.UnpostedBills > 0
                ? $" Workbench has {health.UnpostedBills} vendor bill{(health.UnpostedBills == 1 ? "" : "s")} dated in this month that aren't posted yet."
                : "";
            return Cause(row, report, d < 0 && health.UnpostedBills > 0 ? ParallelCauseKind.Unposted : ParallelCauseKind.Timing,
                $"{name}: other direct costs are {Money(Math.Abs(d))} {(d > 0 ? "higher" : "lower")} in Workbench; vendor bills and expenses are usually posted in different months.{unposted}",
                "Open the detail and compare bill and expense dates with Costpoint's.", d);
        }

        return Cause(row, report, ParallelCauseKind.Other, $"{name}: the total differs and no single cost element stands out.", "Open the detail and compare the elements.");
    }

    private static decimal? Ratio(decimal amount, decimal basis) => basis == 0m ? null : amount / basis;
}
