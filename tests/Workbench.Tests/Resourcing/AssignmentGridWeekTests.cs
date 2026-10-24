using Bunit;
using MudBlazor.Services;
using Workbench.Contracts.Resourcing;
using Workbench.Ui.Plan;
using Workbench.Ui.Resourcing;

namespace Workbench.Tests.Resourcing;

/// <summary>The Plan tab's people grid "By week": a column per week, and typing a week saves the row's weeks with the dates around them.</summary>
public sealed class AssignmentGridWeekTests
{
    private static readonly DateOnly ThisWeek = ResourcingFormat.ThisWeek;

    private static ResourcePlanDto Plan(PlanPersonDto person)
    {
        var task = new PlanTaskRowDto(Guid.NewGuid(), "1.1", "Build", 1, false, false, Guid.NewGuid(), "1001.01", ThisWeek, ThisWeek.AddDays(25), 0,
            [person], person.Hours, null, null);
        var totals = new PlanTotalsDto(person.Hours, null, null, null, 0m, null, [], ThisWeek, ThisWeek.AddDays(25), 0, []);
        return new ResourcePlanDto(Guid.NewGuid(), "1001", Guid.NewGuid(), ThisWeek, false, false, true, true, [task], [], totals, null, []);
    }

    [Fact]
    public async Task Typing_a_week_saves_the_rows_weeks_and_the_dates_follow_them()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var person = new PlanPersonDto(Guid.NewGuid(), Guid.NewGuid(), "Dana Lee", null, null, ThisWeek.AddDays(2), ThisWeek.AddDays(18), 60m, null, null, [],
            Weeks: [new(ThisWeek, 40m), new(ThisWeek.AddDays(14), 20m)]);
        IReadOnlyList<PlanChange>? sent = null;
        var grid = ctx.Render<AssignmentGrid>(p => p.Add(g => g.ProjectId, Guid.NewGuid()).Add(g => g.Plan, Plan(person))
            .Add(g => g.Submit, changes => { sent = changes; return Task.FromResult<string?>(null); }));

        grid.FindAll("[data-testid=assign-mode] .mud-toggle-item").Single(e => e.TextContent.Contains("By week")).Click();

        // The row shows its weeks (empty where none) and their total.
        var cells = grid.FindAll("[data-testid=assign-row]")[0].QuerySelectorAll("[data-testid=assign-week-cell]");
        Assert.Equal(("40", "", "20"), (cells[0].GetAttribute("value") ?? "", cells[1].GetAttribute("value") ?? "", cells[2].GetAttribute("value") ?? ""));
        Assert.Contains("60", grid.Find("[data-testid=assign-week-total]").TextContent);

        // Filling the empty week sends all three weeks; the start and end days already in the first and last weeks stay.
        await grid.FindAll("[data-testid=assign-row]")[0].QuerySelectorAll("[data-testid=assign-week-cell]")[1].ChangeAsync(new() { Value = "16" });

        var change = Assert.Single(sent!);
        Assert.Equal((PlanChangeKind.SetPerson, person.AssignmentId, ThisWeek.AddDays(2), ThisWeek.AddDays(18)), (change.Kind, change.AssignmentId, change.From, change.To));
        Assert.Equal([(ThisWeek, 40m), (ThisWeek.AddDays(7), 16m), (ThisWeek.AddDays(14), 20m)], change.Weeks!.Select(w => (w.WeekStart, w.Hours)));

        // Clearing the last week pulls the end date back to the Friday of the week before it.
        await grid.FindAll("[data-testid=assign-row]")[0].QuerySelectorAll("[data-testid=assign-week-cell]")[2].ChangeAsync(new() { Value = "" });
        Assert.Equal(ThisWeek.AddDays(11), Assert.Single(sent!).To);
    }
}
