using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Migration;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Tests.Identity;
using Workbench.Ui.Setup.Migration;

namespace Workbench.Tests.Costpoint;

public sealed class ParallelRunComponentTests
{
    private static readonly FiscalPeriodDto Period = new(Guid.NewGuid(), 2027, "2026-11", 2, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), FiscalPeriodState.Open);

    private static ReconRunSummaryDto Summary(ReconReportKind kind, int different) => new(Guid.NewGuid(), kind, Period.Id, Period.Name, Period.Start, Period.End, "tb.csv", "Costpoint", 
        different == 0 ? ReconRunOutcome.Matched : ReconRunOutcome.Differences, 20, 20 - different, different, 0, 0, 0, "Ending", 100m, 100m, "fin@example.com", DateTime.UtcNow, null, null, null);

    private static BunitContext Context(ParallelRunDto run)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Fake<IParallelRunService>.Create(("GetAsync", _ => run), ("UploadAsync", _ => run)));
        ctx.Services.AddSingleton(Fake<IFiscalCalendarService>.Create(("ListPeriodsAsync", _ => (IReadOnlyList<FiscalPeriodDto>)[Period])));
        return ctx;
    }

    [Fact]
    public async Task Waiting_state_asks_for_costpoints_two_reports()
    {
        var empty = new ParallelRunDto(Period, true, ParallelState.WaitingForCostpoint, "Waiting for Costpoint. Once Costpoint has closed the month, upload its trial balance and project cost report here.",
            new ParallelHealthDto(0, 0, 0, false),
            [new ParallelReportDto(ReconReportKind.TrialBalance, "Trial balance", "Trial Balance report", null, []), new ParallelReportDto(ReconReportKind.ProjectCost, "Project cost by element", "Project Status Report", null, [])], []);
        await using var ctx = Context(empty);
        var page = ctx.Render<ParallelRunPage>();
        page.WaitForAssertion(() => Assert.Contains("Waiting for Costpoint", page.Find("[data-testid=parallel-verdict]").TextContent));
        Assert.NotNull(page.Find("[data-testid=parallel-file-TrialBalance]"));
        Assert.NotNull(page.Find("[data-testid=parallel-file-ProjectCost]"));
        Assert.Empty(page.FindAll("[data-testid=parallel-health]"));
    }

    [Fact]
    public async Task Differences_show_their_cause_next_step_and_what_is_still_open_in_workbench()
    {
        var cause = new ParallelCauseDto("5010", "Direct labor", ReconReportKind.TrialBalance, ParallelCauseKind.Unposted,
            "Direct labor is $1,500.00 lower in Workbench. Workbench has 1 draft entry on this account worth $1,500.00.", -1500m, "Post the draft entries in the period, then run the comparison again.");
        var run = new ParallelRunDto(Period, true, ParallelState.Differences, "1 difference to look at; each has a likely cause below.", new ParallelHealthDto(1, 2, 0, false),
            [new ParallelReportDto(ReconReportKind.TrialBalance, "Trial balance", "Trial Balance report", Summary(ReconReportKind.TrialBalance, 1), [cause]),
             new ParallelReportDto(ReconReportKind.ProjectCost, "Project cost by element", "Project Status Report", null, [])],
            [new ParallelMonthDto(Period.Id, Period.Name, ParallelState.Differences, 1)]);
        await using var ctx = Context(run);
        var page = ctx.Render<ParallelRunPage>();

        page.WaitForAssertion(() => Assert.Contains("1 draft journal entry", page.Find("[data-testid=parallel-health]").TextContent));
        Assert.Contains("2 timesheets not approved", page.Find("[data-testid=parallel-health]").TextContent);
        var item = page.Find("[data-cause='5010']");
        Assert.Contains("Not posted yet", item.TextContent);
        Assert.Contains("Workbench has 1 draft entry", item.TextContent);
        Assert.Contains("Next: Post the draft entries", item.TextContent);
        Assert.Contains("1 of 20 rows differ", page.Markup);
        Assert.Contains("2026-11", page.Find("[data-testid=parallel-months]").TextContent);
    }
}
