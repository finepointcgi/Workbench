using Workbench.Application.Assistant;
using Workbench.Contracts.Assistant;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Tests.Assistant;

public sealed class TimesheetFlagEngineTests
{
    // Week of Mon Oct 12 – Sun Oct 18, 2026.
    private static readonly DateOnly Monday = new(2026, 10, 12);
    private static readonly Guid Direct = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly Guid Overhead = Guid.NewGuid();
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    private static FlagEntry A(int day, decimal hours) => new(Monday.AddDays(day), hours, Direct, "1001.01", ProjectA, "1001", ChargeKind.Direct, true, true, ProjectStatus.Active);

    private static FlagEntry B(int day, decimal hours) => new(Monday.AddDays(day), hours, Other, "2002.01", ProjectB, "2002", ChargeKind.Direct, true, true, ProjectStatus.Active);

    private static FlagEntry Oh(int day, decimal hours) => new(Monday.AddDays(day), hours, Overhead, "OH.01", Guid.Empty, "OH", ChargeKind.Indirect, true, true, ProjectStatus.Active);

    /// <summary>A usual week: 7.5 h on 1001 and 0.5 h overhead, Monday to Friday.</summary>
    private static List<FlagEntry> UsualWeek(int weeksAgo = 0) =>
        Enumerable.Range(0, 5).SelectMany(d => new[] { A(d - (7 * weeksAgo), 7.5m), Oh(d - (7 * weeksAgo), 0.5m) }).ToList();

    private static FlagSnapshot Snapshot(
        IReadOnlyList<FlagEntry> entries,
        IReadOnlyList<FlagHistory>? history = null,
        IReadOnlyList<FlagRevision>? revisions = null,
        IReadOnlyList<FlagDay>? days = null,
        IReadOnlyList<FlagAllocation>? allocations = null,
        IReadOnlyDictionary<DateOnly, string>? holidays = null,
        IReadOnlyList<FlagCeiling>? ceilings = null) =>
        new(Monday, Monday.AddDays(6), revisions is { Count: > 0 } ? 1 : 0, null, entries, revisions ?? [], days ?? [], history ?? History(6), allocations ?? [],
            holidays ?? new Dictionary<DateOnly, string>(), ceilings ?? [], 1);

    private static List<FlagHistory> History(int weeks) =>
        Enumerable.Range(1, weeks).Select(w => new FlagHistory(Monday.AddDays(-7 * w), Monday.AddDays(-7 * w + 6), UsualWeek(w))).ToList();

    private static IReadOnlyList<string> Rules(FlagSnapshot s) => TimesheetFlagEngine.Evaluate(s).Select(f => f.RuleKey).ToList();

    [Fact]
    public void A_usual_timesheet_has_no_flags()
    {
        Assert.Empty(Rules(Snapshot(UsualWeek(), days: Enumerable.Range(0, 5).Select(d => new FlagDay(Monday.AddDays(d), 0)).ToList())));
    }

    [Fact]
    public void Weekend_hours_are_flagged_unless_the_person_usually_works_weekends()
    {
        var entries = UsualWeek().Append(A(5, 4)).ToList();
        var flag = Assert.Single(TimesheetFlagEngine.Evaluate(Snapshot(entries)), f => f.RuleKey == TimesheetFlagRules.Weekend);
        Assert.Contains("4 h", flag.Detail);
        Assert.Equal([Monday.AddDays(5)], flag.Dates);

        var weekendWorker = Enumerable.Range(1, 6).Select(w => new FlagHistory(Monday.AddDays(-7 * w), Monday.AddDays(-7 * w + 6), UsualWeek(w).Append(A(5 - (7 * w), 4)).ToList())).ToList();
        Assert.DoesNotContain(TimesheetFlagRules.Weekend, Rules(Snapshot(entries, weekendWorker)));
    }

    [Fact]
    public void Work_on_a_holiday_is_flagged_but_holiday_leave_is_not()
    {
        var holidays = new Dictionary<DateOnly, string> { [Monday] = "Columbus Day" };
        var flag = Assert.Single(TimesheetFlagEngine.Evaluate(Snapshot(UsualWeek(), holidays: holidays)), f => f.RuleKey == TimesheetFlagRules.Holiday);
        Assert.Contains("Columbus Day", flag.Detail);
        Assert.Equal(TimesheetFlagSeverity.Warning, flag.Severity);

        var leave = new FlagEntry(Monday, 8, Guid.NewGuid(), "LV.HOL", Guid.Empty, "LV", ChargeKind.Leave, true, true, ProjectStatus.Active);
        var withLeave = UsualWeek().Where(e => e.WorkDate != Monday).Append(leave).ToList();
        Assert.DoesNotContain(TimesheetFlagRules.Holiday, Rules(Snapshot(withLeave, holidays: holidays)));
    }

    [Fact]
    public void Late_entries_are_flagged_with_how_late()
    {
        var days = new[] { new FlagDay(Monday, 0), new FlagDay(Monday.AddDays(1), 3), new FlagDay(Monday.AddDays(2), 4) };
        var flag = Assert.Single(TimesheetFlagEngine.Evaluate(Snapshot(UsualWeek(), days: days)), f => f.RuleKey == TimesheetFlagRules.LateEntry);
        Assert.Contains("2 day(s)", flag.Detail);
        Assert.Equal(TimesheetFlagSeverity.Info, flag.Severity);
    }

    [Fact]
    public void Revisions_that_move_hours_between_codes_are_warnings()
    {
        var at = Monday.AddDays(9).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var moved = new[] { new FlagRevision(1, at, Monday, 8, 8, "OH.01", "1001.01", "Charged the wrong code") };
        var flag = Assert.Single(TimesheetFlagEngine.Evaluate(Snapshot(UsualWeek(), revisions: moved)), f => f.RuleKey == TimesheetFlagRules.Revisions);
        Assert.Equal(TimesheetFlagSeverity.Warning, flag.Severity);
        Assert.Contains("OH.01 → 1001.01", flag.Detail);
        Assert.Contains("Charged the wrong code", flag.Detail);

        var corrected = new[] { new FlagRevision(1, at, Monday, 8, 7.5m, "1001.01", "1001.01", "Typo") };
        Assert.Equal(TimesheetFlagSeverity.Info, Assert.Single(TimesheetFlagEngine.Evaluate(Snapshot(UsualWeek(), revisions: corrected)), f => f.RuleKey == TimesheetFlagRules.Revisions).Severity);
    }

    [Fact]
    public void Charges_to_closed_and_ceiling_limited_codes_are_flagged()
    {
        var closed = UsualWeek().Append(new FlagEntry(Monday, 2, Guid.NewGuid(), "3003.01", Guid.NewGuid(), "3003", ChargeKind.Direct, true, true, ProjectStatus.Closed)).ToList();
        Assert.Contains("project closed", Assert.Single(TimesheetFlagEngine.Evaluate(Snapshot(closed)), f => f.RuleKey == TimesheetFlagRules.ClosedCode).Detail);

        var ceilings = new[] { new FlagCeiling(Direct, "Project 1001 hours", 1.02m, true) };
        var flag = Assert.Single(TimesheetFlagEngine.Evaluate(Snapshot(UsualWeek(), ceilings: ceilings)), f => f.RuleKey == TimesheetFlagRules.CeilingLimited);
        Assert.Equal(TimesheetFlagSeverity.Warning, flag.Severity);
        Assert.Contains("blocking", flag.Detail);
        Assert.DoesNotContain(TimesheetFlagRules.CeilingLimited, Rules(Snapshot(UsualWeek(), ceilings: [new FlagCeiling(Direct, "Project 1001 hours", 0.5m, false)])));
    }

    [Fact]
    public void Charging_that_differs_from_the_persons_history_is_flagged()
    {
        // Half the week on a project never charged before.
        var unusual = Enumerable.Range(0, 5).SelectMany(d => new[] { A(d, 3.5m), B(d, 4m), Oh(d, 0.5m) }).ToList();
        var flag = Assert.Single(TimesheetFlagEngine.Evaluate(Snapshot(unusual)), f => f.RuleKey == TimesheetFlagRules.HistoryPattern);
        Assert.Contains("first hours on project 2002", flag.Detail);
        Assert.Contains("project 1001 is 44%", flag.Detail);

        // Too little history: no statistical flag.
        Assert.DoesNotContain(TimesheetFlagRules.HistoryPattern, Rules(Snapshot(unusual, History(TimesheetFlagEngine.MinimumHistory - 1))));
    }

    [Fact]
    public void A_shift_between_indirect_and_direct_time_is_flagged()
    {
        var allDirect = Enumerable.Range(0, 5).Select(d => A(d, 8)).ToList();
        Assert.DoesNotContain(TimesheetFlagRules.IndirectMix, Rules(Snapshot(allDirect)));

        var mostlyOverhead = Enumerable.Range(0, 5).SelectMany(d => new[] { A(d, 2), Oh(d, 6) }).ToList();
        var flag = Assert.Single(TimesheetFlagEngine.Evaluate(Snapshot(mostlyOverhead)), f => f.RuleKey == TimesheetFlagRules.IndirectMix);
        Assert.Contains("75%", flag.Detail);
    }

    [Fact]
    public void Round_number_padding_is_compared_with_the_persons_usual_precision()
    {
        var round = Enumerable.Range(0, 5).Select(d => A(d, 8)).ToList();
        Assert.Contains(TimesheetFlagRules.RoundNumbers, Rules(Snapshot(round)));

        var alwaysRound = Enumerable.Range(1, 6).Select(w => new FlagHistory(Monday.AddDays(-7 * w), Monday.AddDays(-7 * w + 6), Enumerable.Range(0, 5).Select(d => A(d - (7 * w), 8)).ToList())).ToList();
        Assert.DoesNotContain(TimesheetFlagRules.RoundNumbers, Rules(Snapshot(round, alwaysRound)));
    }

    [Fact]
    public void Charging_against_allocations_is_flagged()
    {
        var allocations = new[] { new FlagAllocation(ProjectA, "1001", Monday, 10), new FlagAllocation(ProjectB, "2002", Monday, 30) };
        var flag = Assert.Single(TimesheetFlagEngine.Evaluate(Snapshot(UsualWeek(), allocations: allocations)), f => f.RuleKey == TimesheetFlagRules.AllocationPattern);
        Assert.Contains("37.5 h on project 1001 against 10 h allocated", flag.Detail);
        Assert.Contains("only 0 h on project 2002 against 30 h allocated", flag.Detail);
        Assert.Equal(15m, TimesheetFlagEngine.Prorate(new FlagAllocation(ProjectA, "1001", Monday.AddDays(-7), 25), Monday.AddDays(-5), Monday.AddDays(10)));
    }

    [Fact]
    public void The_same_snapshot_always_gives_the_same_flags_and_hashes()
    {
        var s = Snapshot(UsualWeek().Append(A(6, 3)).ToList(), holidays: new Dictionary<DateOnly, string> { [Monday] = "Columbus Day" });
        Assert.Equal(TimesheetFlagEngine.Evaluate(s).Select(f => f.FactsHash), TimesheetFlagEngine.Evaluate(s).Select(f => f.FactsHash));
        Assert.All(TimesheetFlagEngine.Evaluate(s), f => Assert.Equal(32, f.FactsHash.Length));
    }
}
