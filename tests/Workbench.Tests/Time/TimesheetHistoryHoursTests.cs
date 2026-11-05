using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Time;
using Workbench.Tests.Identity;
using Workbench.Ui.Time;

namespace Workbench.Tests.Time;

public sealed class TimesheetHistoryHoursTests
{
    private static TimeEntryDto Entry(Guid wbs, string code, string name, DateOnly day, decimal hours) =>
        new(Guid.NewGuid(), wbs, code, name, "1001", Guid.NewGuid(), "BA2", day, hours, null, DateTime.UtcNow, DateTime.UtcNow);

    private static IRenderedComponent<HistoryPage> Render(IReadOnlyList<TimeEntryDto> entries, DateOnly monday)
    {
        var sheet = new TimesheetDto(Guid.NewGuid(), Guid.NewGuid(), "Alex Rivera", "analyst@example.com", monday, monday.AddDays(6), TimesheetState.Submitted, 0,
            entries.Sum(e => e.Hours), 40m, null, DateTime.UtcNow, null, null, null, null, null, DateTime.UtcNow);
        var detail = new TimesheetDetailDto(sheet, entries, [], [], [], []);
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Fake<ITimesheetService>.Create(("GetAsync", _ => detail)));
        return ctx.Render<HistoryPage>(p => p.Add(h => h.TimesheetId, sheet.Id));
    }

    [Fact]
    public void An_approver_sees_the_hours_by_charge_code_and_day_not_just_the_audit_trail()
    {
        var monday = new DateOnly(2026, 10, 19);
        var design = Guid.NewGuid();
        var build = Guid.NewGuid();
        var page = Render(
            [
                Entry(design, "1001.01.02", "Requirements review", monday, 6m),
                Entry(design, "1001.01.02", "Requirements review", monday.AddDays(1), 8m),
                Entry(build, "1001.01.03", "Baseline schedule", monday, 2m),
            ],
            monday);

        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid=hours-by-day]")));
        var rows = page.FindAll("[data-testid=hours-by-day] tbody tr").Select(r => r.TextContent).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Contains("Requirements review", rows[0], StringComparison.Ordinal);
        Assert.Contains("14", rows[0], StringComparison.Ordinal); // 6 + 8 for the row
        Assert.Contains("Baseline schedule", rows[1], StringComparison.Ordinal);
        Assert.StartsWith("Daily total", rows[2], StringComparison.Ordinal);
        Assert.Contains("16", rows[2], StringComparison.Ordinal); // 6 + 8 + 2 for the week
    }

    [Fact]
    public void A_sheet_with_no_hours_says_so()
    {
        var page = Render([], new DateOnly(2026, 10, 19));

        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid=hours-by-day-empty]")));
    }
}
