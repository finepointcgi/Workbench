using Workbench.Application;
using Workbench.Application.Scheduling;
using Workbench.Application.Sheets;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Leave;

namespace Workbench.Tests.Scheduling;

/// <summary>
/// The MS Project rules of the plan editor: indent takes the previous task's start, summaries roll up, outdent reverses
/// indent, links push successors, durations count working days.
/// </summary>
public sealed class PlanScheduleTests
{
    // Monday 5 October 2026.
    private static readonly DateOnly Mon = new(2026, 10, 5);

    private readonly IWorkbenchDbContextFactory _factory = TestDb.CreateFactory();
    private readonly ItemService _items;
    private readonly DependencyService _links;
    private readonly PlanScheduleService _plan;

    public PlanScheduleTests()
    {
        _items = new ItemService(_factory, TimeProvider.System);
        _links = new DependencyService(_factory);
        _plan = new PlanScheduleService(_factory, _items, TimeProvider.System);
    }

    private async Task<Guid> SheetAsync(bool scheduling = true) =>
        (await new SheetService(_factory, TimeProvider.System).CreateAsync(new CreateSheetRequest("Plan", null, scheduling))).Id;

    private async Task<ItemDto> TaskAsync(Guid sheetId, string title, DateOnly? start, int days, Guid? parent = null)
    {
        DateOnly? finish = start is { } s ? WorkCalendar.Weekdays.Finish(s, days) : null;
        return await _items.CreateAsync(sheetId, new CreateItemRequest(title, parent, Start: start, Finish: finish));
    }

    private async Task<ItemDto> GetAsync(Guid sheetId, Guid id) => (await _items.ListAsync(sheetId)).Single(i => i.Id == id);

    [Fact]
    public async Task Indenting_a_task_gives_it_the_start_of_the_task_above_and_keeps_its_duration()
    {
        var sheet = await SheetAsync();
        var design = await TaskAsync(sheet, "Design", Mon, 5);
        var build = await TaskAsync(sheet, "Build", Mon.AddDays(14), 3);

        await _plan.IndentAsync(sheet, build.Id);

        var moved = await GetAsync(sheet, build.Id);
        Assert.Equal(design.Id, moved.ParentId);
        Assert.Equal(Mon, moved.Start); // the new parent's start: Build is its first subtask
        Assert.Equal(Mon.AddDays(2), moved.Finish); // still 3 working days

        // Design is now a summary spanning Build only.
        var summary = await GetAsync(sheet, design.Id);
        Assert.Equal((Mon, Mon.AddDays(2)), (summary.Start!.Value, summary.Finish!.Value));
        var plan = await _plan.GetAsync(sheet);
        Assert.True(plan.For(design.Id)!.IsSummary);
        Assert.Equal("1.1", plan.For(build.Id)!.OutlineNumber);
        Assert.Equal(3, plan.For(build.Id)!.DurationDays);
    }

    [Fact]
    public async Task A_later_indent_starts_with_its_new_previous_sibling()
    {
        var sheet = await SheetAsync();
        var phase = await TaskAsync(sheet, "Phase 1", null, 0);
        await TaskAsync(sheet, "Kickoff", Mon, 1, phase.Id);
        var analysis = await TaskAsync(sheet, "Analysis", Mon.AddDays(7), 5, phase.Id); // Mon 12 Oct – Fri 16 Oct
        var report = await TaskAsync(sheet, "Report", Mon.AddDays(28), 2); // a top-level task after Phase 1

        await _plan.IndentAsync(sheet, report.Id);

        var moved = await GetAsync(sheet, report.Id);
        Assert.Equal(phase.Id, moved.ParentId);
        Assert.Equal(analysis.Start, moved.Start);
        Assert.Equal(Mon.AddDays(8), moved.Finish);
        var summary = await GetAsync(sheet, phase.Id);
        Assert.Equal((Mon, Mon.AddDays(11)), (summary.Start!.Value, summary.Finish!.Value)); // Kickoff's start to Analysis' finish
    }

    [Fact]
    public async Task Indenting_a_summary_moves_its_subtasks_together()
    {
        var sheet = await SheetAsync();
        await TaskAsync(sheet, "Design", Mon, 5);
        var build = await TaskAsync(sheet, "Build", null, 0);
        var code = await TaskAsync(sheet, "Code", Mon.AddDays(21), 5, build.Id); // Mon 26 Oct
        var test = await TaskAsync(sheet, "Test", Mon.AddDays(28), 2, build.Id); // Mon 2 Nov

        await _plan.IndentAsync(sheet, build.Id);

        Assert.Equal(Mon, (await GetAsync(sheet, code.Id)).Start);
        Assert.Equal(Mon.AddDays(7), (await GetAsync(sheet, test.Id)).Start); // still a week after Code
        Assert.Equal(Mon.AddDays(8), (await GetAsync(sheet, test.Id)).Finish);
    }

    [Fact]
    public async Task An_undated_task_takes_the_start_as_a_one_day_task()
    {
        var sheet = await SheetAsync();
        await TaskAsync(sheet, "Design", Mon.AddDays(2), 5);
        var todo = await TaskAsync(sheet, "Write it up", null, 0);

        await _plan.IndentAsync(sheet, todo.Id);

        var moved = await GetAsync(sheet, todo.Id);
        Assert.Equal((Mon.AddDays(2), Mon.AddDays(2)), (moved.Start!.Value, moved.Finish!.Value));
    }

    [Fact]
    public async Task Indent_on_a_sheet_without_scheduling_only_restructures()
    {
        var sheet = await SheetAsync(scheduling: false);
        var a = await TaskAsync(sheet, "A", Mon, 5);
        var b = await TaskAsync(sheet, "B", Mon.AddDays(14), 3);

        await _plan.IndentAsync(sheet, b.Id);

        var moved = await GetAsync(sheet, b.Id);
        Assert.Equal(a.Id, moved.ParentId);
        Assert.Equal(Mon.AddDays(14), moved.Start);
        Assert.Equal(Mon.AddDays(4), (await GetAsync(sheet, a.Id)).Finish); // no rollup either
    }

    [Fact]
    public async Task Outdent_reverses_indent_and_adopts_the_tasks_below_like_ms_project()
    {
        var sheet = await SheetAsync();
        var phase = await TaskAsync(sheet, "Phase", null, 0);
        var a = await TaskAsync(sheet, "A", Mon, 2, phase.Id);
        var b = await TaskAsync(sheet, "B", Mon.AddDays(7), 3, phase.Id);
        var c = await TaskAsync(sheet, "C", Mon.AddDays(14), 1, phase.Id);
        var next = await TaskAsync(sheet, "Next", Mon.AddDays(21), 1);

        // Indent then outdent the last task: back where it was, keeping its new dates.
        await _plan.IndentAsync(sheet, next.Id);
        Assert.Equal(phase.Id, (await GetAsync(sheet, next.Id)).ParentId);
        await _plan.OutdentAsync(sheet, next.Id);
        var outline = await _plan.GetAsync(sheet);
        Assert.Equal(["Phase", "A", "B", "C", "Next"], outline.Tasks.Select(t => Title(t.ItemId)));
        Assert.Equal(["1", "1.1", "1.2", "1.3", "2"], outline.Tasks.Select(t => t.OutlineNumber));

        // Outdenting B keeps its place: C (below it) becomes its subtask, A stays under Phase.
        await _plan.OutdentAsync(sheet, b.Id);
        outline = await _plan.GetAsync(sheet);
        Assert.Equal(["Phase", "A", "B", "C", "Next"], outline.Tasks.Select(t => Title(t.ItemId)));
        Assert.Equal(["1", "1.1", "2", "2.1", "3"], outline.Tasks.Select(t => t.OutlineNumber));
        var phaseNow = await GetAsync(sheet, phase.Id);
        Assert.Equal((Mon, Mon.AddDays(1)), (phaseNow.Start!.Value, phaseNow.Finish!.Value)); // only A is left under Phase
        var bNow = await GetAsync(sheet, b.Id);
        Assert.Equal((c.Start, c.Finish), (bNow.Start, bNow.Finish)); // B is now a summary of C

        string Title(Guid id) => new[] { phase, a, b, c, next }.Single(i => i.Id == id).Title;
    }

    [Fact]
    public async Task Summary_dates_roll_up_and_are_not_edited_directly()
    {
        var sheet = await SheetAsync();
        var phase = await TaskAsync(sheet, "Phase", Mon.AddDays(-30), 1);
        await TaskAsync(sheet, "A", Mon, 2, phase.Id);
        await TaskAsync(sheet, "B", Mon.AddDays(7), 5, phase.Id);

        var summary = await GetAsync(sheet, phase.Id);
        Assert.Equal((Mon, Mon.AddDays(11)), (summary.Start!.Value, summary.Finish!.Value));

        await _items.UpdateAsync(sheet, phase.Id, new UpdateItemRequest("Phase", Mon.AddDays(-3), Mon.AddDays(40), null, summary.Values));
        summary = await GetAsync(sheet, phase.Id);
        Assert.Equal((Mon, Mon.AddDays(11)), (summary.Start!.Value, summary.Finish!.Value));
        await Assert.ThrowsAsync<ArgumentException>(() => _plan.SetDurationAsync(sheet, phase.Id, 3));
    }

    [Fact]
    public async Task Percent_complete_rolls_up_weighted_by_duration()
    {
        var sheet = await SheetAsync();
        var percent = await new ColumnService(_factory, TimeProvider.System).CreateAsync(sheet, new CreateColumnRequest("% Complete", ColumnKind.Number));
        var key = percent.Id.ToString();
        var phase = await TaskAsync(sheet, "Phase", null, 0);
        var a = await TaskAsync(sheet, "A", Mon, 1, phase.Id); // 1 day, done
        var b = await TaskAsync(sheet, "B", Mon.AddDays(7), 3, phase.Id); // 3 days, not started
        await _items.UpdateAsync(sheet, a.Id, new UpdateItemRequest("A", a.Start, a.Finish, null, new Dictionary<string, string?> { [key] = "100" }));

        Assert.Equal("25", (await GetAsync(sheet, phase.Id)).Values[key]);
        Assert.Equal(25m, (await _plan.GetAsync(sheet)).For(phase.Id)!.PercentComplete);
        _ = b;
    }

    [Fact]
    public async Task Finish_to_start_links_push_successors_over_weekends_and_holidays()
    {
        var sheet = await SheetAsync();
        await using (var db = await _factory.CreateAsync())
        {
            db.Set<Holiday>().Add(new Holiday { Date = Mon.AddDays(7), Name = "Columbus Day" }); // Mon 12 Oct
            await db.SaveChangesAsync();
        }

        var design = await TaskAsync(sheet, "Design", Mon, 5); // Mon–Fri
        var build = await TaskAsync(sheet, "Build", Mon, 2);

        await _links.AddAsync(sheet, new CreateDependencyRequest(design.Id, build.Id));

        var pushed = await GetAsync(sheet, build.Id);
        Assert.Equal(Mon.AddDays(8), pushed.Start); // Tue 13 Oct: after the weekend and the holiday
        Assert.Equal(Mon.AddDays(9), pushed.Finish); // still 2 working days

        // Lengthening the predecessor pushes again; links never pull a successor earlier.
        await _plan.SetDurationAsync(sheet, design.Id, 8); // Mon 5 – Thu 15 (skipping the holiday)
        Assert.Equal(Mon.AddDays(11), (await GetAsync(sheet, build.Id)).Start);
        await _plan.SetDurationAsync(sheet, design.Id, 1);
        Assert.Equal(Mon.AddDays(11), (await GetAsync(sheet, build.Id)).Start);
    }

    [Fact]
    public async Task Predecessors_still_hold_after_an_indent()
    {
        var sheet = await SheetAsync();
        var design = await TaskAsync(sheet, "Design", Mon, 5);
        var spec = await TaskAsync(sheet, "Spec", Mon.AddDays(14), 1);
        var review = await TaskAsync(sheet, "Review", Mon.AddDays(14), 1);
        await _plan.SetPredecessorsAsync(sheet, review.Id, "2"); // Review waits on Spec (task 2)

        await _plan.IndentAsync(sheet, spec.Id); // Spec moves to Design's start: Mon 5
        await _plan.IndentAsync(sheet, review.Id); // Review would start with Spec, but must follow it

        Assert.Equal(Mon, (await GetAsync(sheet, spec.Id)).Start);
        Assert.Equal(Mon.AddDays(1), (await GetAsync(sheet, review.Id)).Start);
        Assert.Equal("2", (await _plan.GetAsync(sheet)).For(review.Id)!.Predecessors);
        _ = design;
    }

    [Fact]
    public async Task Durations_count_working_days_and_zero_is_a_milestone()
    {
        var sheet = await SheetAsync();
        var task = await TaskAsync(sheet, "Task", Mon.AddDays(3), 1); // Thursday

        await _plan.SetDurationAsync(sheet, task.Id, 3);
        var item = await GetAsync(sheet, task.Id);
        Assert.Equal(Mon.AddDays(7), item.Finish); // Thu, Fri, Mon
        Assert.False(item.IsMilestone);

        await _plan.SetDurationAsync(sheet, task.Id, 0);
        item = await GetAsync(sheet, task.Id);
        Assert.True(item.IsMilestone);
        Assert.Equal(item.Start, item.Finish);
        Assert.Equal(0, (await _plan.GetAsync(sheet)).For(task.Id)!.DurationDays);
    }

    [Fact]
    public async Task Predecessor_text_parses_types_and_lags_and_rejects_bad_links()
    {
        var sheet = await SheetAsync();
        var phase = await TaskAsync(sheet, "Phase", null, 0);
        var a = await TaskAsync(sheet, "A", Mon, 2, phase.Id);
        var b = await TaskAsync(sheet, "B", Mon, 2);
        var c = await TaskAsync(sheet, "C", Mon, 2);

        await _plan.SetPredecessorsAsync(sheet, c.Id, "2, 3SS+2d");
        var links = await _links.ListAsync(sheet);
        Assert.Contains(links, l => l.PredecessorId == a.Id && l.SuccessorId == c.Id && l.Type == DependencyKind.FinishToStart && l.LagDays == 0);
        Assert.Contains(links, l => l.PredecessorId == b.Id && l.SuccessorId == c.Id && l.Type == DependencyKind.StartToStart && l.LagDays == 2);
        Assert.Equal("2, 3SS+2d", (await _plan.GetAsync(sheet)).For(c.Id)!.Predecessors);
        Assert.Equal(Mon.AddDays(2), (await GetAsync(sheet, c.Id)).Start); // after A (Mon–Tue): Wed

        await Assert.ThrowsAsync<ArgumentException>(() => _plan.SetPredecessorsAsync(sheet, a.Id, "4")); // C already waits on A
        await Assert.ThrowsAsync<ArgumentException>(() => _plan.SetPredecessorsAsync(sheet, a.Id, "1")); // its own summary
        await Assert.ThrowsAsync<ArgumentException>(() => _plan.SetPredecessorsAsync(sheet, b.Id, "3")); // itself
        await Assert.ThrowsAsync<ArgumentException>(() => _plan.SetPredecessorsAsync(sheet, b.Id, "9"));
        await Assert.ThrowsAsync<ArgumentException>(() => _plan.SetPredecessorsAsync(sheet, b.Id, "two"));

        await _plan.SetPredecessorsAsync(sheet, c.Id, "");
        Assert.Empty(await _links.ListAsync(sheet));
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("5d", 5)]
    [InlineData("5 days", 5)]
    [InlineData("2w", 10)]
    [InlineData("3 days?", 3)]
    [InlineData("0", 0)]
    public void Durations_parse_like_ms_project(string text, int days)
    {
        Assert.True(ScheduleEngine.TryParseDuration(text, out var parsed));
        Assert.Equal(days, parsed);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-2")]
    [InlineData("five")]
    public void Bad_durations_are_rejected(string text) => Assert.False(ScheduleEngine.TryParseDuration(text, out _));
}
