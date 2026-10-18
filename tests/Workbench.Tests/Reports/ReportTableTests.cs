using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;
using Workbench.Contracts.Reports;
using Workbench.Ui.Reports;

namespace Workbench.Tests.Reports;

public sealed class ReportTableTests
{
    [Fact]
    public async Task Renders_formatted_values_totals_links_and_drill_buttons()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var result = new ReportResultDto("Labor", null,
            [new("employee", "Employee", ReportColumnType.Text), new("hours", "Hours", ReportColumnType.Hours), new("cost", "Cost", ReportColumnType.Money), new("rate", "Rate", ReportColumnType.Rate)],
            [
                new(ReportRowKind.Detail, 1, ["Alex Rivera", "8", "-700", "0.325"], "employee:1", "time/history/1"),
                new(ReportRowKind.Total, 0, ["Total", "8", "700", null]),
            ],
            ["Labor cost is shown to Finance."], DateTime.UtcNow);
        ReportRowDto? drilled = null;

        var table = ctx.Render<ReportTable>(p => p.Add(x => x.Result, result).Add(x => x.OnDrill, EventCallback.Factory.Create<ReportRowDto>(this, r => drilled = r)));

        Assert.Contains("(700.00)", table.Markup);
        Assert.Contains("32.50%", table.Markup);
        Assert.Contains("href=\"time/history/1\"", table.Markup);
        Assert.Contains("Labor cost is shown to Finance.", table.Markup);
        Assert.Single(table.FindAll("tr[data-kind=Total]"));
        table.Find("[data-testid=drill]").Click();
        Assert.Equal("employee:1", drilled?.DrillKey);
    }
}
