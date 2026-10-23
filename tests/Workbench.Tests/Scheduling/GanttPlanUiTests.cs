using Bunit;
using MudBlazor.Services;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Accessibility;
using Workbench.Ui.Gantt;

namespace Workbench.Tests.Scheduling;

/// <summary>The Gantt's scheduling affordances: conflicts are listed, constraints pinned, baseline variance shown, days off shaded.</summary>
public sealed class GanttPlanUiTests
{
    private static readonly DateOnly Mon = PlanKit.Mon;

    private static ItemDto Item(string title, DateOnly start, DateOnly finish) =>
        new(Guid.NewGuid(), null, 0, title, start, finish, null, new Dictionary<string, string?>());

    [Fact]
    public void The_conflicts_banner_names_each_task_and_offers_a_fix_to_editors()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var id = Guid.NewGuid();
        var plan = new PlanScheduleDto(Guid.NewGuid(), [], [],
            [new PlanConflictDto(id, 4, "Build", PlanConstraintKind.MustStartOn, 2, "Must Start On Mon Nov 2, but its predecessor \"Design\" (task 3) doesn't let it start until Wed Nov 4: 2 working days of conflict.")]);

        var banner = ctx.Render<PlanConflictsBanner>(p => p.Add(b => b.SheetId, Guid.NewGuid()).Add(b => b.Plan, plan).Add(b => b.CanEdit, true));

        Assert.Contains("1 task can't meet its date constraint", banner.Markup);
        Assert.Contains("4. Build", banner.Markup);
        Assert.Contains("2 working days of conflict", banner.Markup);
        Assert.Single(banner.FindAll("button[aria-label='Change the constraint on Build']"));
        A11yAssert.Check(banner);

        var empty = ctx.Render<PlanConflictsBanner>(p => p.Add(b => b.SheetId, Guid.NewGuid()).Add(b => b.Plan, plan with { Conflicts = [] }));
        Assert.Empty(empty.FindAll("[data-testid='plan-conflicts']"));
    }

    [Fact]
    public void The_task_list_shows_constraints_and_baseline_variance()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var late = Item("Build", Mon.AddDays(9), Mon.AddDays(11));
        var onTime = Item("Design", Mon, Mon.AddDays(4));
        var rows = GanttLayout.BuildRows([onTime, late], new HashSet<Guid>());
        var comparison = new BaselineComparison(new BaselineSummary(Guid.NewGuid(), "Approved", DateTime.UtcNow, 2),
        [
            new BaselineVarianceDto(onTime.Id, Mon, Mon.AddDays(4), Mon, Mon.AddDays(4), 0, 0, 0),
            new BaselineVarianceDto(late.Id, Mon.AddDays(9), Mon.AddDays(11), Mon.AddDays(7), Mon.AddDays(9), 2, 2, 0),
        ], 2);
        var constraints = new Dictionary<Guid, PlanConstraintDto> { [late.Id] = new(PlanConstraintKind.FinishNoLaterThan, Mon.AddDays(9)) };

        var list = ctx.Render<GanttTaskList>(p => p
            .Add(l => l.Rows, rows)
            .Add(l => l.SheetId, Guid.NewGuid())
            .Add(l => l.Comparison, comparison)
            .Add(l => l.Constraints, constraints)
            .Add(l => l.CanConstrain, true));

        Assert.Contains("BL finish", list.Markup);
        Assert.Equal(["0d", "+2d"], list.FindAll(".gantt-col-variance[role='cell']").Select(c => c.TextContent.Trim()));
        Assert.Single(list.FindAll(".gantt-col-variance.late"));
        Assert.Single(list.FindAll("button[aria-label='Build: Finish No Later Than Oct 14']"));
        Assert.Single(list.FindAll("button[aria-label='Set a date constraint on Design']"));
        A11yAssert.Check(list);
    }

    [Fact]
    public void Days_off_from_the_plan_calendar_are_shaded_as_runs()
    {
        var scale = GanttScale.Create(GanttZoom.Day, [Mon, Mon.AddDays(20)], Mon);
        var holiday = Mon.AddDays(7);
        var plan = new PlanScheduleDto(Guid.NewGuid(), [], [holiday]);

        var off = scale.DaysOff(plan.IsWorkingDay).ToList();

        Assert.Contains(off, t => t.X == scale.X(holiday.AddDays(-2)) && t.Width == scale.DayWidth * 3); // Sat, Sun and the holiday Monday
        Assert.Equal(scale.Weekends().Count(), scale.DaysOff(null).Count());
    }
}
