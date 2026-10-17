using Workbench.Application.Scheduling;
using Workbench.Contracts.Scheduling;

namespace Workbench.Tests.Scheduling;

public sealed class CriticalPathTests
{
    // Monday 2026-10-05.
    private static readonly DateOnly Monday = new(2026, 10, 5);

    [Fact]
    public void Working_day_index_skips_weekends()
    {
        var friday = Monday.AddDays(4);
        Assert.Equal(WorkingDays.Index(friday) + 1, WorkingDays.Index(Monday.AddDays(7)));
        Assert.Equal(WorkingDays.Index(Monday.AddDays(7)), WorkingDays.Index(Monday.AddDays(5)));
        Assert.Equal(5, WorkingDays.Duration(Monday, friday));
        Assert.Equal(6, WorkingDays.Duration(friday, Monday.AddDays(11)));
        Assert.Equal(0, WorkingDays.Duration(Monday, Monday));
        Assert.Equal(friday, WorkingDays.FromIndex(WorkingDays.Index(friday)));
    }

    [Fact]
    public void Finds_the_longest_path_and_slack_on_a_small_network()
    {
        //        ┌─ B (5d) ─┐
        //  A (2d)           D (1d)
        //        └─ C (2d) ─┘
        var a = new ScheduleTask(Guid.NewGuid(), Monday, Monday.AddDays(1));               // Mon–Tue
        var b = new ScheduleTask(Guid.NewGuid(), Monday.AddDays(2), Monday.AddDays(8));     // Wed–next Tue (5 working days)
        var c = new ScheduleTask(Guid.NewGuid(), Monday.AddDays(2), Monday.AddDays(3));     // Wed–Thu
        var d = new ScheduleTask(Guid.NewGuid(), Monday.AddDays(9), Monday.AddDays(10));    // next Wed–Thu
        ScheduleLink[] links =
        [
            new(a.Id, b.Id, DependencyKind.FinishToStart, 0),
            new(a.Id, c.Id, DependencyKind.FinishToStart, 0),
            new(b.Id, d.Id, DependencyKind.FinishToStart, 0),
            new(c.Id, d.Id, DependencyKind.FinishToStart, 0),
        ];

        var result = CriticalPathCalculator.Analyze([a, b, c, d], links);

        Assert.Equal([a.Id, b.Id, d.Id], result.CriticalPath);
        Assert.Equal(3, result.Items.Single(i => i.ItemId == c.Id).SlackDays);
        Assert.Equal(Monday, result.ProjectStart);
        Assert.Equal(Monday.AddDays(10), result.ProjectFinish);
    }

    [Fact]
    public void Predecessors_push_successors_later_with_lag()
    {
        var a = new ScheduleTask(Guid.NewGuid(), Monday, Monday.AddDays(4));     // Mon–Fri
        var b = new ScheduleTask(Guid.NewGuid(), Monday, Monday.AddDays(1));     // planned too early
        var result = CriticalPathCalculator.Analyze([a, b], [new(a.Id, b.Id, DependencyKind.FinishToStart, 2)]);

        var slackB = result.Items.Single(i => i.ItemId == b.Id);
        Assert.Equal(Monday.AddDays(9), slackB.EarlyStart);   // next Mon + 2 working days lag = Wed
        Assert.Equal(Monday.AddDays(10), slackB.EarlyFinish);
        Assert.Equal([a.Id, b.Id], result.CriticalPath);
    }

    [Fact]
    public void Start_to_start_and_finish_to_finish_links_constrain_the_right_ends()
    {
        var a = new ScheduleTask(Guid.NewGuid(), Monday, Monday.AddDays(4));     // Mon–Fri
        var ss = new ScheduleTask(Guid.NewGuid(), Monday, Monday.AddDays(1));    // Mon–Tue, SS +1
        var ff = new ScheduleTask(Guid.NewGuid(), Monday, Monday.AddDays(1));    // Mon–Tue, FF +0
        var result = CriticalPathCalculator.Analyze([a, ss, ff],
        [
            new(a.Id, ss.Id, DependencyKind.StartToStart, 1),
            new(a.Id, ff.Id, DependencyKind.FinishToFinish, 0),
        ]);

        Assert.Equal(Monday.AddDays(1), result.Items.Single(i => i.ItemId == ss.Id).EarlyStart);
        Assert.Equal(Monday.AddDays(4), result.Items.Single(i => i.ItemId == ff.Id).EarlyFinish);
        Assert.True(result.Items.Single(i => i.ItemId == ff.Id).IsCritical);
        Assert.Equal(2, result.Items.Single(i => i.ItemId == ss.Id).SlackDays);
    }

    [Fact]
    public void Empty_network_has_no_dates()
    {
        var result = CriticalPathCalculator.Analyze([], []);
        Assert.Null(result.ProjectFinish);
        Assert.Empty(result.CriticalPath);
    }
}
