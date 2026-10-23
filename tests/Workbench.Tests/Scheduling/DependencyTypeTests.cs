using Workbench.Application.Scheduling;
using Workbench.Contracts.Scheduling;

namespace Workbench.Tests.Scheduling;

/// <summary>
/// Start-to-start, finish-to-finish and start-to-finish links, with lag and lead, through the plan editor (which moves the
/// tasks) and the critical path (which must agree with it: a task the links hold in place has no slack).
/// </summary>
public sealed class DependencyTypeTests
{
    private static readonly DateOnly Mon = PlanKit.Mon;
    private static readonly DateOnly ColumbusDay = new(2026, 10, 12);

    private readonly PlanKit _kit = new();

    private async Task<(Guid Sheet, Guid A, Guid B)> PairAsync(DateOnly aStart, int aDays, DateOnly bStart, int bDays)
    {
        var sheet = await _kit.SheetAsync();
        var a = await _kit.TaskAsync(sheet, "A", aStart, aDays);
        var b = await _kit.TaskAsync(sheet, "B", bStart, bDays);
        return (sheet, a.Id, b.Id);
    }

    /// <summary>The critical path's early dates are where the plan editor put each task: they apply the links the same way.</summary>
    private async Task AssertAgreesAsync(Guid sheet, params Guid[] ids)
    {
        var analysis = await _kit.Links.AnalyzeAsync(sheet);
        foreach (var id in ids)
        {
            var (start, finish) = await _kit.DatesAsync(sheet, id);
            var slack = analysis.Items.Single(i => i.ItemId == id);
            Assert.Equal((start, finish), (slack.EarlyStart, slack.EarlyFinish));
        }
    }

    private async Task AssertCriticalAsync(Guid sheet, params Guid[] ids)
    {
        await AssertAgreesAsync(sheet, ids);
        var analysis = await _kit.Links.AnalyzeAsync(sheet);
        foreach (var id in ids)
        {
            Assert.Equal(0, analysis.Items.Single(i => i.ItemId == id).SlackDays);
        }
    }

    [Theory]
    [InlineData(0, 7)]   // Wed: with A
    [InlineData(2, 9)]   // Fri: two working days after A starts
    [InlineData(3, 12)]  // the next Monday (skips the weekend)
    [InlineData(-2, 5)]  // a two-day lead: Mon
    public async Task Start_to_start_starts_with_the_predecessor_plus_lag(int lag, int expectedDay)
    {
        var (sheet, a, b) = await PairAsync(Mon.AddDays(2), 5, Mon, 2); // A Wed 7 Oct
        await _kit.LinkAsync(sheet, a, b, DependencyKind.StartToStart, lag);

        Assert.Equal(new DateOnly(2026, 10, expectedDay), (await _kit.GetAsync(sheet, b)).Start);
        Assert.Equal(2, (await _kit.Plan.GetAsync(sheet)).For(b)!.DurationDays);
        if (lag >= 0)
        {
            Assert.Equal($"1SS{(lag == 0 ? "" : $"+{lag}d")}", (await _kit.Plan.GetAsync(sheet)).For(b)!.Predecessors);
        }
    }

    [Theory]
    [InlineData(0, 8, 9)]    // B Thu–Fri: finishes with A
    [InlineData(1, 9, 12)]   // Fri–Mon
    [InlineData(-1, 7, 8)]   // Wed–Thu
    public async Task Finish_to_finish_finishes_with_the_predecessor_plus_lag(int lag, int startDay, int finishDay)
    {
        var (sheet, a, b) = await PairAsync(Mon, 5, Mon, 2); // A Mon–Fri
        await _kit.LinkAsync(sheet, a, b, DependencyKind.FinishToFinish, lag);

        Assert.Equal((new DateOnly(2026, 10, startDay), new DateOnly(2026, 10, finishDay)), await _kit.DatesAsync(sheet, b));
        await AssertAgreesAsync(sheet, b);
        if (lag >= 0)
        {
            await AssertCriticalAsync(sheet, a, b); // B finishes with (or after) A, so both drive the finish
        }
    }

    [Theory]
    [InlineData(0, 13, 14)]  // A starts Thu 15: B finishes the day before, Tue 13–Wed 14
    [InlineData(1, 14, 15)]  // finishes on A's start day
    [InlineData(2, 15, 16)]
    [InlineData(-1, 12, 13)]
    public async Task Start_to_finish_finishes_by_the_day_before_the_predecessor_starts(int lag, int startDay, int finishDay)
    {
        var (sheet, a, b) = await PairAsync(new DateOnly(2026, 10, 15), 3, Mon, 2);
        await _kit.LinkAsync(sheet, a, b, DependencyKind.StartToFinish, lag);

        Assert.Equal((new DateOnly(2026, 10, startDay), new DateOnly(2026, 10, finishDay)), await _kit.DatesAsync(sheet, b));
        await AssertAgreesAsync(sheet, a, b); // the engine and the critical path agree on where SF puts B
    }

    [Fact]
    public async Task Links_only_push_later_never_earlier()
    {
        foreach (var type in Enum.GetValues<DependencyKind>())
        {
            var (sheet, a, b) = await PairAsync(Mon, 2, Mon.AddDays(21), 2);
            await _kit.LinkAsync(sheet, a, b, type);
            Assert.Equal(Mon.AddDays(21), (await _kit.GetAsync(sheet, b)).Start);
            Assert.True((await _kit.Links.AnalyzeAsync(sheet)).Items.Single(i => i.ItemId == a).SlackDays > 0);
        }
    }

    [Fact]
    public async Task Moving_a_predecessor_pushes_a_chain_of_mixed_links_across_a_holiday()
    {
        await _kit.HolidayAsync(ColumbusDay, "Columbus Day");
        var sheet = await _kit.SheetAsync();
        var a = await _kit.TaskAsync(sheet, "A", Mon, 3);  // Mon–Wed
        var b = await _kit.TaskAsync(sheet, "B", Mon, 2);
        var c = await _kit.TaskAsync(sheet, "C", Mon, 4);
        var d = await _kit.TaskAsync(sheet, "D", Mon, 1);
        await _kit.LinkAsync(sheet, a.Id, b.Id);                                  // B: Thu–Fri
        await _kit.LinkAsync(sheet, b.Id, c.Id, DependencyKind.StartToStart, 1);  // C: Fri, (Mon off), Tue–Thu
        await _kit.LinkAsync(sheet, c.Id, d.Id, DependencyKind.FinishToFinish);   // D: Thu

        Assert.Equal((Mon.AddDays(3), Mon.AddDays(4)), await _kit.DatesAsync(sheet, b.Id));
        Assert.Equal((Mon.AddDays(4), new DateOnly(2026, 10, 15)), await _kit.DatesAsync(sheet, c.Id));
        Assert.Equal((new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15)), await _kit.DatesAsync(sheet, d.Id));

        await _kit.Plan.SetDurationAsync(sheet, a.Id, 5); // A to Fri: B Tue–Wed (after the holiday), C Wed–Mon, D Mon
        Assert.Equal((new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 14)), await _kit.DatesAsync(sheet, b.Id));
        Assert.Equal((new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 19)), await _kit.DatesAsync(sheet, c.Id));
        Assert.Equal(new DateOnly(2026, 10, 19), (await _kit.GetAsync(sheet, d.Id)).Finish);
        await AssertCriticalAsync(sheet, a.Id, b.Id, c.Id, d.Id);
    }

    [Fact]
    public async Task Milestones_link_without_taking_a_day()
    {
        var sheet = await _kit.SheetAsync();
        var gate = await _kit.TaskAsync(sheet, "Gate", Mon.AddDays(2), 0); // Wed milestone
        var after = await _kit.TaskAsync(sheet, "After", Mon, 2);
        var together = await _kit.TaskAsync(sheet, "Together", Mon, 2);
        await _kit.LinkAsync(sheet, gate.Id, after.Id);
        await _kit.LinkAsync(sheet, gate.Id, together.Id, DependencyKind.StartToStart);

        Assert.Equal(Mon.AddDays(2), (await _kit.GetAsync(sheet, after.Id)).Start); // FS from a milestone: that same day
        Assert.Equal(Mon.AddDays(2), (await _kit.GetAsync(sheet, together.Id)).Start);
    }

    [Fact]
    public void The_critical_path_handles_each_link_type_with_lag()
    {
        var calendar = WorkCalendar.Weekdays;
        var a = new ScheduleTask(Guid.NewGuid(), Mon, Mon.AddDays(4));               // Mon–Fri
        var ss = new ScheduleTask(Guid.NewGuid(), Mon.AddDays(2), Mon.AddDays(3));   // SS+2: Wed–Thu, held by the link
        var ff = new ScheduleTask(Guid.NewGuid(), Mon.AddDays(3), Mon.AddDays(4));   // FF: Thu–Fri
        var sf = new ScheduleTask(Guid.NewGuid(), Mon.AddDays(-3), Mon.AddDays(-3)); // SF+0: the Friday before A starts
        var result = CriticalPathCalculator.Analyze([a, ss, ff, sf],
        [
            new(a.Id, ss.Id, DependencyKind.StartToStart, 2),
            new(a.Id, ff.Id, DependencyKind.FinishToFinish, 0),
            new(a.Id, sf.Id, DependencyKind.StartToFinish, 0),
        ], calendar);

        Assert.Equal(Mon.AddDays(2), result.Items.Single(i => i.ItemId == ss.Id).EarlyStart);
        Assert.Equal(1, result.Items.Single(i => i.ItemId == ss.Id).SlackDays); // could finish as late as Fri
        Assert.True(result.Items.Single(i => i.ItemId == ff.Id).IsCritical);
        Assert.Equal(Mon.AddDays(-3), result.Items.Single(i => i.ItemId == sf.Id).EarlyFinish);
        Assert.Equal(Mon.AddDays(-3), result.ProjectStart);
        Assert.Equal(1, WorkCalendar.Weekdays.Duration(sf.Start, sf.Finish)); // a one-day task, not a milestone
    }

    [Fact]
    public async Task Predecessor_text_round_trips_every_type_and_lag()
    {
        var sheet = await _kit.SheetAsync();
        var a = await _kit.TaskAsync(sheet, "A", Mon, 5);
        var b = await _kit.TaskAsync(sheet, "B", Mon, 5);
        var c = await _kit.TaskAsync(sheet, "C", Mon, 5);
        var d = await _kit.TaskAsync(sheet, "D", Mon, 5);
        var e = await _kit.TaskAsync(sheet, "E", Mon.AddDays(28), 2);

        await _kit.Plan.SetPredecessorsAsync(sheet, e.Id, "1, 2SS+3d, 3FF-1 day, 4SF+1w");

        var links = (await _kit.Links.ListAsync(sheet)).Where(l => l.SuccessorId == e.Id).OrderBy(l => l.LagDays).ToList();
        Assert.Equal([(c.Id, DependencyKind.FinishToFinish, -1), (a.Id, DependencyKind.FinishToStart, 0), (b.Id, DependencyKind.StartToStart, 3), (d.Id, DependencyKind.StartToFinish, 5)],
            links.Select(l => (l.PredecessorId, l.Type, l.LagDays)).ToList());
        Assert.Equal("1, 2SS+3d, 3FF-1d, 4SF+5d", (await _kit.Plan.GetAsync(sheet)).For(e.Id)!.Predecessors);
    }
}
