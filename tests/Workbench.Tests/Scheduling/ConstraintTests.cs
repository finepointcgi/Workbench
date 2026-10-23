using Workbench.Application.Scheduling;
using Workbench.Contracts;
using Workbench.Contracts.Scheduling;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Scheduling;

/// <summary>
/// MS Project's date constraints: the No Earlier Than kinds move a task later, the Must kinds pin it, the No Later Than kinds
/// are deadlines. Whenever links and a constraint disagree the plan says so (a conflict, and negative slack), never silently.
/// </summary>
public sealed class ConstraintTests
{
    private static readonly DateOnly Mon = PlanKit.Mon;
    private static readonly DateOnly NextMon = Mon.AddDays(7);

    private static SetPlanConstraintRequest Set(PlanConstraintKind kind, DateOnly date) => new(kind, date);

    [Fact]
    public async Task Start_no_earlier_than_moves_a_task_later_and_keeps_its_duration()
    {
        var kit = new PlanKit();
        var sheet = await kit.SheetAsync();
        var task = await kit.TaskAsync(sheet, "Site visit", Mon, 5);

        var conflicts = await kit.Plan.SetConstraintAsync(sheet, task.Id, Set(PlanConstraintKind.StartNoEarlierThan, NextMon.AddDays(2)));

        Assert.Empty(conflicts);
        Assert.Equal((NextMon.AddDays(2), NextMon.AddDays(8)), await kit.DatesAsync(sheet, task.Id)); // Wed to the next Tue
        var dto = (await kit.Plan.GetAsync(sheet)).For(task.Id)!;
        Assert.Equal(new PlanConstraintDto(PlanConstraintKind.StartNoEarlierThan, NextMon.AddDays(2)), dto.Constraint);
        Assert.Equal("Start No Earlier Than", dto.Constraint!.Label);
        Assert.Equal(5, dto.DurationDays);

        // A date earlier than the task doesn't pull it back: links and planned dates only ever push later.
        await kit.Plan.SetConstraintAsync(sheet, task.Id, Set(PlanConstraintKind.StartNoEarlierThan, Mon));
        Assert.Equal(NextMon.AddDays(2), (await kit.GetAsync(sheet, task.Id)).Start);
    }

    [Fact]
    public async Task A_constraint_on_a_day_off_means_the_next_working_day_for_starts_and_the_previous_for_deadlines()
    {
        var kit = new PlanKit();
        var sheet = await kit.SheetAsync();
        var task = await kit.TaskAsync(sheet, "Pour concrete", Mon, 2);

        await kit.Plan.SetConstraintAsync(sheet, task.Id, Set(PlanConstraintKind.StartNoEarlierThan, Mon.AddDays(5))); // Saturday
        Assert.Equal((NextMon, NextMon.AddDays(1)), await kit.DatesAsync(sheet, task.Id));

        await kit.Plan.SetConstraintAsync(sheet, task.Id, Set(PlanConstraintKind.MustFinishOn, NextMon.AddDays(6))); // Sunday
        Assert.Equal((NextMon.AddDays(3), NextMon.AddDays(4)), await kit.DatesAsync(sheet, task.Id)); // Thu–Fri
    }

    [Fact]
    public async Task Finish_no_earlier_than_moves_the_finish_onto_the_date()
    {
        var kit = new PlanKit();
        var sheet = await kit.SheetAsync();
        var task = await kit.TaskAsync(sheet, "Review", Mon, 3); // Mon–Wed

        await kit.Plan.SetConstraintAsync(sheet, task.Id, Set(PlanConstraintKind.FinishNoEarlierThan, Mon.AddDays(4)));

        Assert.Equal((Mon.AddDays(2), Mon.AddDays(4)), await kit.DatesAsync(sheet, task.Id)); // Wed–Fri
    }

    [Fact]
    public async Task Must_start_on_pins_the_task_and_reports_predecessors_that_push_past_it()
    {
        var kit = new PlanKit();
        var sheet = await kit.SheetAsync();
        var design = await kit.TaskAsync(sheet, "Design", Mon, 5);
        var build = await kit.TaskAsync(sheet, "Build", Mon.AddDays(21), 3);
        await kit.LinkAsync(sheet, design.Id, build.Id);

        // Pinned earlier than planned: Must Start On moves it back, unlike the other kinds.
        Assert.Empty(await kit.Plan.SetConstraintAsync(sheet, build.Id, Set(PlanConstraintKind.MustStartOn, NextMon)));
        Assert.Equal((NextMon, NextMon.AddDays(2)), await kit.DatesAsync(sheet, build.Id));

        // Design now runs to Wednesday of next week; Build stays pinned and the plan shows why it can't be.
        await kit.Plan.SetDurationAsync(sheet, design.Id, 8);

        Assert.Equal((NextMon, NextMon.AddDays(2)), await kit.DatesAsync(sheet, build.Id));
        var conflict = Assert.Single((await kit.Plan.GetAsync(sheet)).AllConflicts);
        Assert.Equal(build.Id, conflict.ItemId);
        Assert.Equal(2, conflict.TaskNumber);
        Assert.Equal(PlanConstraintKind.MustStartOn, conflict.Kind);
        Assert.Equal(3, conflict.Days); // Design lets it start Thu at the earliest: Mon + 3 working days
        Assert.Equal(design.Id, conflict.PredecessorId);
        Assert.Contains("Must Start On Mon Oct 12", conflict.Message);
        Assert.Contains("\"Design\" (task 1)", conflict.Message);
        Assert.Contains("Thu Oct 15", conflict.Message);

        // The critical path shows it too: the pinned start leaves Design no room.
        var analysis = await kit.Links.AnalyzeAsync(sheet);
        Assert.True(analysis.Items.Single(i => i.ItemId == design.Id).SlackDays < 0);

        // Clearing the constraint lets the link push Build again.
        Assert.Empty(await kit.Plan.SetConstraintAsync(sheet, build.Id, new SetPlanConstraintRequest(PlanConstraintKind.AsSoonAsPossible, null)));
        Assert.Equal(NextMon.AddDays(3), (await kit.GetAsync(sheet, build.Id)).Start);
        Assert.Null((await kit.Plan.GetAsync(sheet)).For(build.Id)!.Constraint);
    }

    [Fact]
    public async Task Must_finish_on_pins_the_finish()
    {
        var kit = new PlanKit();
        var sheet = await kit.SheetAsync();
        var prep = await kit.TaskAsync(sheet, "Prep", Mon, 2);
        var delivery = await kit.TaskAsync(sheet, "Deliver", Mon, 3);
        await kit.LinkAsync(sheet, prep.Id, delivery.Id);

        Assert.Empty(await kit.Plan.SetConstraintAsync(sheet, delivery.Id, Set(PlanConstraintKind.MustFinishOn, Mon.AddDays(4))));
        Assert.Equal((Mon.AddDays(2), Mon.AddDays(4)), await kit.DatesAsync(sheet, delivery.Id));

        await kit.Plan.SetDurationAsync(sheet, prep.Id, 4); // Prep to Thursday: Deliver can't start Wednesday any more
        var conflict = Assert.Single(await kit.Plan.SetConstraintAsync(sheet, delivery.Id, Set(PlanConstraintKind.MustFinishOn, Mon.AddDays(4))));
        Assert.Equal(PlanConstraintKind.MustFinishOn, conflict.Kind);
        Assert.Equal(2, conflict.Days);
        Assert.Equal((Mon.AddDays(2), Mon.AddDays(4)), await kit.DatesAsync(sheet, delivery.Id));
    }

    [Fact]
    public async Task Start_no_later_than_is_reported_when_links_push_past_it()
    {
        var kit = new PlanKit();
        var sheet = await kit.SheetAsync();
        var permit = await kit.TaskAsync(sheet, "Permit", Mon, 5);
        var dig = await kit.TaskAsync(sheet, "Dig", NextMon, 2);
        await kit.LinkAsync(sheet, permit.Id, dig.Id);
        Assert.Empty(await kit.Plan.SetConstraintAsync(sheet, dig.Id, Set(PlanConstraintKind.StartNoLaterThan, NextMon)));

        await kit.Plan.SetDurationAsync(sheet, permit.Id, 7);

        Assert.Equal(NextMon.AddDays(2), (await kit.GetAsync(sheet, dig.Id)).Start); // the link wins; the deadline is missed
        var conflict = Assert.Single((await kit.Plan.GetAsync(sheet)).AllConflicts);
        Assert.Equal(PlanConstraintKind.StartNoLaterThan, conflict.Kind);
        Assert.Equal(2, conflict.Days);
        Assert.Equal("Starts Wed Oct 14 because of its predecessor \"Permit\" (task 1), 2 working days after its Start No Later Than Mon Oct 12.", conflict.Message);
    }

    [Fact]
    public async Task Finish_no_later_than_is_a_deadline_with_negative_slack_when_missed()
    {
        var kit = new PlanKit();
        var sheet = await kit.SheetAsync();
        var draft = await kit.TaskAsync(sheet, "Draft", Mon, 5);
        var submit = await kit.TaskAsync(sheet, "Submit", NextMon, 1);
        await kit.TaskAsync(sheet, "Close-out", NextMon.AddDays(7), 1); // the plan runs on after the deadline
        await kit.LinkAsync(sheet, draft.Id, submit.Id);

        Assert.Empty(await kit.Plan.SetConstraintAsync(sheet, submit.Id, Set(PlanConstraintKind.FinishNoLaterThan, NextMon.AddDays(1))));
        var slack = (await kit.Links.AnalyzeAsync(sheet)).Items.Single(i => i.ItemId == submit.Id);
        Assert.Equal(1, slack.SlackDays); // a day to spare before the deadline
        Assert.False(slack.IsCritical);

        await kit.Plan.SetDurationAsync(sheet, draft.Id, 8); // Submit now Thursday

        var conflict = Assert.Single((await kit.Plan.GetAsync(sheet)).AllConflicts);
        Assert.Equal(submit.Id, conflict.ItemId);
        Assert.Equal(2, conflict.Days);
        Assert.Contains("Finishes Thu Oct 15", conflict.Message);
        var analysis = await kit.Links.AnalyzeAsync(sheet);
        Assert.Equal(-2, analysis.Items.Single(i => i.ItemId == submit.Id).SlackDays);
        Assert.Equal(-2, analysis.Items.Single(i => i.ItemId == draft.Id).SlackDays);
        Assert.Contains(draft.Id, analysis.CriticalPath);
    }

    [Fact]
    public async Task A_later_link_wins_over_start_no_earlier_than()
    {
        var kit = new PlanKit();
        var sheet = await kit.SheetAsync();
        var a = await kit.TaskAsync(sheet, "A", Mon, 10);
        var b = await kit.TaskAsync(sheet, "B", Mon, 1);
        await kit.Plan.SetConstraintAsync(sheet, b.Id, Set(PlanConstraintKind.StartNoEarlierThan, Mon.AddDays(2)));
        await kit.LinkAsync(sheet, a.Id, b.Id);

        Assert.Equal(Mon.AddDays(14), (await kit.GetAsync(sheet, b.Id)).Start);
        Assert.Empty((await kit.Plan.GetAsync(sheet)).AllConflicts);
    }

    [Fact]
    public async Task Constraints_need_a_date_a_task_without_subtasks_and_a_supported_kind()
    {
        var kit = new PlanKit();
        var sheet = await kit.SheetAsync();
        var phase = await kit.TaskAsync(sheet, "Phase", null, 0);
        await kit.TaskAsync(sheet, "Work", Mon, 2, phase.Id);
        var other = await kit.TaskAsync(sheet, "Other", Mon, 2);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Plan.SetConstraintAsync(sheet, phase.Id, Set(PlanConstraintKind.MustStartOn, Mon)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Plan.SetConstraintAsync(sheet, other.Id, new SetPlanConstraintRequest(PlanConstraintKind.StartNoEarlierThan, null)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Plan.SetConstraintAsync(sheet, other.Id, Set(PlanConstraintKind.AsLateAsPossible, Mon)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Plan.SetConstraintAsync(sheet, other.Id, Set((PlanConstraintKind)42, Mon)));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Plan.SetConstraintAsync(sheet, Guid.NewGuid(), Set(PlanConstraintKind.MustStartOn, Mon)));
    }

    [Fact]
    public async Task An_undated_task_takes_its_constraint_date_and_a_deleted_task_takes_its_constraint_with_it()
    {
        var kit = new PlanKit();
        var sheet = await kit.SheetAsync();
        var task = await kit.TaskAsync(sheet, "Kickoff", null, 0);

        await kit.Plan.SetConstraintAsync(sheet, task.Id, Set(PlanConstraintKind.MustStartOn, NextMon));
        Assert.Equal((NextMon, NextMon), await kit.DatesAsync(sheet, task.Id));

        await kit.Items.DeleteAsync(sheet, task.Id);
        var plan = await kit.Plan.GetAsync(sheet);
        Assert.Empty(plan.Tasks);
        Assert.Empty(plan.AllConflicts);
    }

    [Fact]
    public void The_engine_applies_every_kind_on_its_own()
    {
        var calendar = WorkCalendar.Weekdays;
        DateOnly? Start(PlanConstraintKind kind, DateOnly date)
        {
            var item = new Workbench.Domain.Item { Title = "T", Start = Mon.AddDays(7), Finish = Mon.AddDays(9) }; // 3 days, Mon 12–Wed 14
            ScheduleEngine.Reflow([item], [], calendar, null, new Dictionary<Guid, PlanConstraintDto> { [item.Id] = new(kind, date) });
            return item.Start;
        }

        Assert.Equal(Mon.AddDays(8), Start(PlanConstraintKind.StartNoEarlierThan, Mon.AddDays(8)));
        Assert.Equal(Mon.AddDays(7), Start(PlanConstraintKind.StartNoEarlierThan, Mon));
        Assert.Equal(Mon.AddDays(8), Start(PlanConstraintKind.FinishNoEarlierThan, Mon.AddDays(10)));
        Assert.Equal(Mon.AddDays(1), Start(PlanConstraintKind.MustStartOn, Mon.AddDays(1)));
        Assert.Equal(Mon, Start(PlanConstraintKind.MustFinishOn, Mon.AddDays(2)));
        Assert.Equal(Mon.AddDays(7), Start(PlanConstraintKind.StartNoLaterThan, Mon)); // deadlines never move a task
        Assert.Equal(Mon.AddDays(7), Start(PlanConstraintKind.FinishNoLaterThan, Mon));
        Assert.Equal(Mon.AddDays(7), Start(PlanConstraintKind.AsLateAsPossible, Mon));
    }

    [Fact]
    public async Task Only_editors_set_constraints()
    {
        await using var seeded = await SeededHost.CreateAsync();
        IPlanScheduleService As(string user) => seeded.As(user).Host.Get<IPlanScheduleService>(); // As switches the host's signed-in user
        var task = (await As(SeededHost.Viewer).GetAsync(seeded.SheetId)).Tasks.First(t => !t.IsSummary);

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
            As(SeededHost.Viewer).SetConstraintAsync(seeded.SheetId, task.ItemId, Set(PlanConstraintKind.StartNoEarlierThan, Mon)));
        await As(SeededHost.Editor).SetConstraintAsync(seeded.SheetId, task.ItemId, Set(PlanConstraintKind.StartNoEarlierThan, Mon));
        Assert.Equal(PlanConstraintKind.StartNoEarlierThan, (await As(SeededHost.Viewer).GetAsync(seeded.SheetId)).For(task.ItemId)!.Constraint!.Kind);
    }
}
