using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Time;
using Workbench.Ui.Time;

namespace Workbench.Tests.Time;

public sealed class TimesheetGridTests
{
    private static readonly DateOnly Monday = new(2026, 10, 19);
    private static readonly Guid Direct = Guid.NewGuid();
    private static readonly Guid Overhead = Guid.NewGuid();
    private static readonly Guid Pto = Guid.NewGuid();

    private static TimesheetDetailDto Detail(TimesheetState status = TimesheetState.Draft)
    {
        var lcat = Guid.NewGuid();
        var now = new DateTime(2026, 10, 21, 15, 0, 0, DateTimeKind.Utc);
        var sheet = new TimesheetDto(Guid.NewGuid(), Guid.NewGuid(), "Dana Lee", "dana@example.com", Monday, Monday.AddDays(6), status, 0, 16m, 40m,
            null, null, null, null, null, null, null, now);
        var days = Enumerable.Range(0, 7).Select(i => new TimesheetDayDto(Monday.AddDays(i), i > 2, true, true, i < 2 ? now : null, i == 0)).ToList();
        var allDays = days.Select(d => d.Date).ToList();
        ChargeCodeDto Code(Guid id, string code, string name, ChargeCodeKind kind) => new(id, code, name, Guid.NewGuid(), code[..3], "Project", kind, lcat);
        return new TimesheetDetailDto(
            sheet,
            [
                new TimeEntryDto(Guid.NewGuid(), Direct, "1001.01", "Requirements", "1001", lcat, "SE2", Monday, 8m, "Kickoff", now, now),
                new TimeEntryDto(Guid.NewGuid(), Direct, "1001.01", "Requirements", "1001", lcat, "SE2", Monday.AddDays(1), 8m, null, now, now),
            ],
            [],
            days,
            [
                new TimesheetChargeCodeDto(Code(Direct, "1001.01", "Requirements", ChargeCodeKind.Direct), allDays, true),
                new TimesheetChargeCodeDto(Code(Overhead, "IND.OH", "Overhead", ChargeCodeKind.Indirect), allDays, true),
                new TimesheetChargeCodeDto(Code(Pto, "LV.PTO", "Paid time off", ChargeCodeKind.Leave), allDays, false),
            ],
            []);
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    [Fact]
    public async Task Shows_rows_for_entries_and_recent_codes_with_daily_totals()
    {
        await using var ctx = NewContext();
        var grid = ctx.Render<TimesheetGrid>(p => p.Add(g => g.Detail, Detail()));

        Assert.Equal("8", grid.Find("[data-testid='cell-1001.01-2026-10-19']").GetAttribute("value"));
        // Recently used overhead gets an empty row; never-used PTO is only offered in the picker.
        Assert.NotNull(grid.Find("[data-testid='cell-IND.OH-2026-10-19']"));
        Assert.Empty(grid.FindAll("[data-testid='cell-LV.PTO-2026-10-19']"));
        Assert.Equal("8", grid.Find("[data-testid='total-2026-10-20']").TextContent.Trim());
        Assert.Equal("16", grid.Find("[data-testid='grand-total']").TextContent.Trim());
        // Future days can't be entered.
        Assert.True(grid.Find("[data-testid='cell-1001.01-2026-10-23']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Editing_a_cell_updates_totals_and_the_entries_to_save()
    {
        await using var ctx = NewContext();
        var grid = ctx.Render<TimesheetGrid>(p => p.Add(g => g.Detail, Detail()));

        grid.Find("[data-testid='cell-IND.OH-2026-10-19']").Change("1.5");
        grid.Find("[data-testid='cell-1001.01-2026-10-20']").Change("");

        Assert.Equal("9.5", grid.Find("[data-testid='total-2026-10-19']").TextContent.Trim());
        Assert.Equal("9.5", grid.Find("[data-testid='grand-total']").TextContent.Trim());
        Assert.True(grid.Instance.IsDirty);
        var entries = grid.Instance.BuildEntries();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.WbsElementId == Overhead && e.Hours == 1.5m && e.EntryId is null);
        Assert.Contains(entries, e => e.WbsElementId == Direct && e.WorkDate == Monday && e.Comment == "Kickoff" && e.EntryId is not null);
    }

    [Fact]
    public async Task Locked_timesheet_asks_for_a_reason_instead_of_editing()
    {
        await using var ctx = NewContext();
        var asked = 0;
        var grid = ctx.Render<TimesheetGrid>(p => p
            .Add(g => g.Detail, Detail(TimesheetState.Approved))
            .Add(g => g.OnLockedEdit, EventCallback.Factory.Create(this, () => asked++)));

        var cell = grid.Find("[data-testid='cell-1001.01-2026-10-19']");
        Assert.True(cell.HasAttribute("readonly"));
        cell.Click();
        cell.Change("4");

        Assert.Equal(1, asked);
        Assert.False(grid.Instance.IsDirty);
        Assert.Equal("16", grid.Find("[data-testid='grand-total']").TextContent.Trim());
        Assert.Empty(grid.FindAll("[data-testid='add-code']"));
    }

    [Fact]
    public async Task Reviewers_see_a_read_only_grid()
    {
        await using var ctx = NewContext();
        var asked = 0;
        var grid = ctx.Render<TimesheetGrid>(p => p
            .Add(g => g.Detail, Detail(TimesheetState.Submitted))
            .Add(g => g.Editable, false)
            .Add(g => g.OnLockedEdit, EventCallback.Factory.Create(this, () => asked++)));

        grid.Find("[data-testid='cell-1001.01-2026-10-19']").Click();

        Assert.Equal(0, asked);
        Assert.Empty(grid.FindAll("[data-testid='cell-IND.OH-2026-10-19']"));
    }
}
