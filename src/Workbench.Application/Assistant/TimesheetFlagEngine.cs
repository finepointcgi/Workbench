using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Workbench.Contracts.Assistant;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Assistant;

public sealed record FlagEntry(DateOnly WorkDate, decimal Hours, Guid WbsElementId, string WbsCode, Guid ProjectId, string ProjectCode, ChargeKind Kind, bool WbsOpen, bool WbsChargeable, ProjectStatus ProjectStatus);

public sealed record FlagRevision(int TimesheetRevision, DateTime ChangedAtUtc, DateOnly WorkDate, decimal? OldHours, decimal? NewHours, string? OldWbsCode, string? NewWbsCode, string Reason);

/// <summary>When a work day's hours were first entered and how many business days after the work day that was.</summary>
public sealed record FlagDay(DateOnly WorkDate, int BusinessDaysAfter);

/// <summary>One earlier (submitted or approved) timesheet of the same person: the statistical baseline.</summary>
public sealed record FlagHistory(DateOnly PeriodStart, DateOnly PeriodEnd, IReadOnlyList<FlagEntry> Entries);

public sealed record FlagAllocation(Guid ProjectId, string ProjectCode, DateOnly WeekStart, decimal Hours);

/// <summary>A ceiling over a charged WBS element that's nearly or fully used.</summary>
public sealed record FlagCeiling(Guid WbsElementId, string Label, decimal PercentConsumed, bool Blocks);

public sealed record FlagSnapshot(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int Revision,
    DateTime? FirstSubmittedAtUtc,
    IReadOnlyList<FlagEntry> Entries,
    IReadOnlyList<FlagRevision> Revisions,
    IReadOnlyList<FlagDay> Days,
    IReadOnlyList<FlagHistory> History,
    IReadOnlyList<FlagAllocation> Allocations,
    IReadOnlyDictionary<DateOnly, string> Holidays,
    IReadOnlyList<FlagCeiling> Ceilings,
    int LateEntryBusinessDays);

/// <summary>
/// The deterministic timesheet checks: plain rules (weekend/holiday hours, late entry, revisions, closed and
/// ceiling-limited codes) and comparisons against the person's own history (mean and standard deviation over at least
/// <see cref="MinimumHistory"/> earlier timesheets) and allocations. Pure: the same snapshot always gives the same flags.
/// </summary>
public static class TimesheetFlagEngine
{
    public const int MinimumHistory = 4;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IReadOnlyList<TimesheetFlagDto> Evaluate(FlagSnapshot s)
    {
        var flags = new List<TimesheetFlagDto>();
        var worked = s.Entries.Where(e => e.Hours > 0).ToList();
        var nonLeave = worked.Where(e => e.Kind != ChargeKind.Leave).ToList();

        // Weekend hours, unless the person regularly works weekends.
        var weekend = nonLeave.Where(e => IsWeekend(e.WorkDate)).ToList();
        var historyWeekendShare = s.History.Count == 0 ? 0 : s.History.Count(h => h.Entries.Any(e => e.Hours > 0 && e.Kind != ChargeKind.Leave && IsWeekend(e.WorkDate))) / (decimal)s.History.Count;
        if (weekend.Count > 0 && !(s.History.Count >= MinimumHistory && historyWeekendShare >= 0.5m))
        {
            var days = weekend.Select(e => e.WorkDate).Distinct().Order().ToList();
            flags.Add(Flag(TimesheetFlagRules.Weekend, TimesheetFlagSeverity.Info,
                $"{H(weekend.Sum(e => e.Hours))} on {Days(days)} (a weekend), on {Codes(weekend)}.",
                [("Weekend hours", H(weekend.Sum(e => e.Hours))), ("Usual weekend work", $"{Percent(historyWeekendShare)} of earlier timesheets")], days));
        }

        // Hours worked on a company holiday (holiday leave itself is fine).
        var holiday = nonLeave.Where(e => s.Holidays.ContainsKey(e.WorkDate)).ToList();
        if (holiday.Count > 0)
        {
            var days = holiday.Select(e => e.WorkDate).Distinct().Order().ToList();
            flags.Add(Flag(TimesheetFlagRules.Holiday, TimesheetFlagSeverity.Warning,
                $"{H(holiday.Sum(e => e.Hours))} charged to {Codes(holiday)} on {string.Join(", ", days.Select(d => $"{s.Holidays[d]} ({d:MMM d})"))}.",
                [("Holiday hours", H(holiday.Sum(e => e.Hours)))], days));
        }

        // Round-number hours compared with how precisely this person usually records time.
        var historyEntries = s.History.SelectMany(h => h.Entries).Where(e => e.Hours > 0).ToList();
        if (worked.Count >= 5 && historyEntries.Count >= 20)
        {
            var share = worked.Count(e => e.Hours == decimal.Truncate(e.Hours)) / (decimal)worked.Count;
            var usual = historyEntries.Count(e => e.Hours == decimal.Truncate(e.Hours)) / (decimal)historyEntries.Count;
            if (share >= 0.9m && usual < 0.6m)
            {
                flags.Add(Flag(TimesheetFlagRules.RoundNumbers, TimesheetFlagSeverity.Info,
                    $"{Percent(share)} of the entries are whole hours, against {Percent(usual)} on this person's earlier timesheets.",
                    [("Whole-hour entries", $"{Percent(share)}"), ("Usually", $"{Percent(usual)}")], []));
            }
        }

        // Days whose hours were first entered late.
        var late = s.Days.Where(d => d.BusinessDaysAfter > s.LateEntryBusinessDays).OrderBy(d => d.WorkDate).ToList();
        if (late.Count > 0)
        {
            flags.Add(Flag(TimesheetFlagRules.LateEntry, late.Count >= 3 ? TimesheetFlagSeverity.Warning : TimesheetFlagSeverity.Info,
                $"{late.Count} day(s) were first entered more than {s.LateEntryBusinessDays} business day(s) after the work day: "
                + string.Join(", ", late.Select(d => $"{d.WorkDate:MMM d} ({d.BusinessDaysAfter} business days later)")) + ".",
                [("Late days", late.Count.ToString(Invariant)), ("Allowed", $"{s.LateEntryBusinessDays} business day(s)")], late.Select(d => d.WorkDate).ToList()));
        }

        // Changes after the first submission, especially hours moved between charge codes.
        if (s.Revisions.Count > 0)
        {
            var moved = s.Revisions.Where(r => r.OldWbsCode is not null && r.NewWbsCode is not null && r.OldWbsCode != r.NewWbsCode).ToList();
            var byRevision = s.Revisions.GroupBy(r => (r.TimesheetRevision, r.ChangedAtUtc.Date))
                .Where(g => g.Any(r => (r.NewHours ?? 0) < (r.OldHours ?? 0)) && g.Any(r => (r.NewHours ?? 0) > (r.OldHours ?? 0))
                    && g.Select(r => r.NewWbsCode ?? r.OldWbsCode).Distinct().Count() > 1)
                .ToList();
            var shifting = moved.Count > 0 || byRevision.Count > 0;
            var reasons = s.Revisions.Select(r => r.Reason).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().Take(3).ToList();
            flags.Add(Flag(TimesheetFlagRules.Revisions, shifting || s.Revisions.Count >= 5 ? TimesheetFlagSeverity.Warning : TimesheetFlagSeverity.Info,
                $"{s.Revisions.Count} change(s) after the first submission"
                + (shifting ? ", including hours moved between charge codes" + (moved.Count > 0 ? $" ({string.Join(", ", moved.Take(3).Select(m => $"{m.OldWbsCode} → {m.NewWbsCode}"))})" : "") : "")
                + (reasons.Count > 0 ? $". Reasons given: {string.Join("; ", reasons.Select(r => $"“{Clip(r, 80)}”"))}" : "") + ".",
                [("Changes", s.Revisions.Count.ToString(Invariant)), ("Timesheet revision", s.Revision.ToString(Invariant)), ("Hours moved between codes", shifting ? "Yes" : "No")],
                s.Revisions.Select(r => r.WorkDate).Distinct().Order().ToList()));
        }

        // Charges to codes that are closed, not chargeable, or on a closed or on-hold project.
        var closed = worked.Where(e => !e.WbsOpen || !e.WbsChargeable || e.ProjectStatus is ProjectStatus.Closed or ProjectStatus.OnHold).ToList();
        if (closed.Count > 0)
        {
            flags.Add(Flag(TimesheetFlagRules.ClosedCode, TimesheetFlagSeverity.Warning,
                $"{H(closed.Sum(e => e.Hours))} on {string.Join(", ", closed.GroupBy(e => e.WbsCode).Select(g => $"{g.Key} ({Why(g.First())})"))}.",
                [("Hours", H(closed.Sum(e => e.Hours)))], closed.Select(e => e.WorkDate).Distinct().Order().ToList()));
        }

        // Charges to codes under a ceiling that's 95% or more used.
        var limited = s.Ceilings.Where(c => c.PercentConsumed >= 0.95m && worked.Any(e => e.WbsElementId == c.WbsElementId)).ToList();
        if (limited.Count > 0)
        {
            var hours = worked.Where(e => limited.Any(c => c.WbsElementId == e.WbsElementId)).ToList();
            flags.Add(Flag(TimesheetFlagRules.CeilingLimited, limited.Any(c => c.PercentConsumed >= 1m) ? TimesheetFlagSeverity.Warning : TimesheetFlagSeverity.Info,
                $"{H(hours.Sum(e => e.Hours))} on {Codes(hours)}, under {string.Join("; ", limited.Select(c => $"{c.Label} ({Percent(c.PercentConsumed)} used{(c.Blocks ? ", blocking" : "")})").Distinct())}.",
                [("Hours", H(hours.Sum(e => e.Hours)))], hours.Select(e => e.WorkDate).Distinct().Order().ToList()));
        }

        // Charging pattern against the person's own history.
        if (s.History.Count >= MinimumHistory)
        {
            flags.AddRange(HistoryFlags(s, nonLeave));
        }

        // Charging against allocations.
        if (s.Allocations.Count > 0)
        {
            flags.AddRange(AllocationFlags(s, nonLeave));
        }

        return flags;
    }

    private static IEnumerable<TimesheetFlagDto> HistoryFlags(FlagSnapshot s, IReadOnlyList<FlagEntry> current)
    {
        var total = current.Sum(e => e.Hours);
        if (total <= 0)
        {
            yield break;
        }

        var history = s.History.Select(h => h.Entries.Where(e => e.Hours > 0 && e.Kind != ChargeKind.Leave).ToList()).Where(h => h.Sum(e => e.Hours) > 0).ToList();
        if (history.Count < MinimumHistory)
        {
            yield break;
        }

        var projects = current.Select(e => e.ProjectCode).Concat(history.SelectMany(h => h.Select(e => e.ProjectCode))).Distinct().Order(StringComparer.Ordinal).ToList();
        var notes = new List<string>();
        var facts = new List<(string, string)>();
        foreach (var project in projects)
        {
            var hours = current.Where(e => e.ProjectCode == project).Sum(e => e.Hours);
            var share = hours / total;
            var shares = history.Select(h => h.Where(e => e.ProjectCode == project).Sum(e => e.Hours) / h.Sum(e => e.Hours)).ToList();
            var (mean, sd) = Stats(shares);
            if (mean == 0 && share >= 0.25m && hours >= 4)
            {
                notes.Add($"first hours on project {project} in the last {history.Count} timesheets ({H(hours)}, {Percent(share)} of the time)");
                facts.Add(($"Project {project}", $"{Percent(share)} now, never before"));
            }
            else if (mean > 0 && Math.Abs(share - mean) > Math.Max(3 * sd, 0.25m) && (hours >= 4 || mean >= 0.25m))
            {
                notes.Add($"project {project} is {Percent(share)} of the time, against a usual {Percent(mean)} (±{Percent(sd)})");
                facts.Add(($"Project {project}", $"{Percent(share)} now, usually {Percent(mean)}"));
            }
        }

        if (notes.Count > 0)
        {
            yield return Flag(TimesheetFlagRules.HistoryPattern, TimesheetFlagSeverity.Info,
                $"Compared with this person's last {history.Count} timesheets: {string.Join("; ", notes)}.", facts, []);
        }

        // Indirect against direct time.
        var indirectShare = current.Where(e => e.Kind == ChargeKind.Indirect).Sum(e => e.Hours) / total;
        var usual = history.Select(h => h.Where(e => e.Kind == ChargeKind.Indirect).Sum(e => e.Hours) / h.Sum(e => e.Hours)).ToList();
        var (m, d) = Stats(usual);
        if (Math.Abs(indirectShare - m) > Math.Max(2.5m * d, 0.2m))
        {
            yield return Flag(TimesheetFlagRules.IndirectMix, TimesheetFlagSeverity.Info,
                $"Indirect time is {Percent(indirectShare)} of the hours worked, against a usual {Percent(m)} (±{Percent(d)}) over the last {history.Count} timesheets"
                + (indirectShare < m ? "; more time than usual went to direct (billable) codes." : "; more time than usual went to indirect codes."),
                [("Indirect share", $"{Percent(indirectShare)}"), ("Usually", $"{Percent(m)}")], []);
        }
    }

    private static IEnumerable<TimesheetFlagDto> AllocationFlags(FlagSnapshot s, IReadOnlyList<FlagEntry> current)
    {
        var notes = new List<string>();
        var facts = new List<(string, string)>();
        var direct = current.Where(e => e.Kind == ChargeKind.Direct).ToList();
        foreach (var project in direct.Select(e => e.ProjectCode).Concat(s.Allocations.Select(a => a.ProjectCode)).Distinct().Order(StringComparer.Ordinal))
        {
            var charged = direct.Where(e => e.ProjectCode == project).Sum(e => e.Hours);
            var allocated = s.Allocations.Where(a => a.ProjectCode == project).Sum(a => Prorate(a, s.PeriodStart, s.PeriodEnd));
            if (allocated == 0 && charged >= 4)
            {
                notes.Add($"{H(charged)} on project {project}, which has no allocation for this person");
                facts.Add(($"Project {project}", $"{H(charged)} charged, none allocated"));
            }
            else if (allocated > 0 && charged > allocated * 1.25m + 4)
            {
                notes.Add($"{H(charged)} on project {project} against {H(allocated)} allocated");
                facts.Add(($"Project {project}", $"{H(charged)} charged, {H(allocated)} allocated"));
            }
            else if (allocated >= 8 && charged < allocated * 0.25m)
            {
                notes.Add($"only {H(charged)} on project {project} against {H(allocated)} allocated");
                facts.Add(($"Project {project}", $"{H(charged)} charged, {H(allocated)} allocated"));
            }
        }

        if (notes.Count > 0)
        {
            yield return Flag(TimesheetFlagRules.AllocationPattern, TimesheetFlagSeverity.Info, $"Against resourcing allocations: {string.Join("; ", notes)}.", facts, []);
        }
    }

    /// <summary>An allocation week's hours that fall inside the period (spread over its Monday–Friday).</summary>
    internal static decimal Prorate(FlagAllocation a, DateOnly start, DateOnly end)
    {
        var inside = Enumerable.Range(0, 5).Select(i => a.WeekStart.AddDays(i)).Count(d => d >= start && d <= end);
        return decimal.Round(a.Hours * inside / 5m, 2);
    }

    internal static (decimal Mean, decimal StdDev) Stats(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
        {
            return (0, 0);
        }

        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return (mean, (decimal)Math.Sqrt((double)variance));
    }

    private static TimesheetFlagDto Flag(string rule, TimesheetFlagSeverity severity, string detail, IReadOnlyList<(string Label, string Value)> facts, IReadOnlyList<DateOnly> dates)
    {
        var factDtos = facts.Select(f => new TimesheetFlagFactDto(f.Label, f.Value)).ToList();
        var canonical = $"{rule}|{severity}|{detail}|{string.Join("|", factDtos.Select(f => $"{f.Label}={f.Value}"))}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
        return new TimesheetFlagDto(rule, TimesheetFlagRules.Label(rule), severity, detail, factDtos, dates, hash);
    }

    private static string Percent(decimal fraction) => AssistantToolbox.Percent(fraction);

    private static bool IsWeekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static string H(decimal hours) => $"{hours.ToString("0.##", Invariant)} h";

    private static string Codes(IEnumerable<FlagEntry> entries) => string.Join(", ", entries.Select(e => e.WbsCode).Distinct().Order(StringComparer.Ordinal));

    private static string Days(IReadOnlyList<DateOnly> days) => string.Join(", ", days.Select(d => d.ToString("ddd MMM d", Invariant)));

    private static string Why(FlagEntry e) =>
        e.ProjectStatus == ProjectStatus.Closed ? "project closed"
        : e.ProjectStatus == ProjectStatus.OnHold ? "project on hold"
        : !e.WbsOpen ? "charge code closed"
        : "not chargeable";

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
