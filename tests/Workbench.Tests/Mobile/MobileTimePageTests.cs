using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Time;
using Workbench.Ui.Mobile;
using static Workbench.Tests.Mobile.MobileTestData;

namespace Workbench.Tests.Mobile;

public sealed class MobileTimePageTests
{
    private static (Fake<ITimesheetService> Timesheets, Fake<ILeaveTimesheetService> Leave) Register(BunitContext ctx, TimesheetDetailDto detail,
        IReadOnlyList<LeavePrefillDto>? prefill = null)
    {
        var (timesheets, tf) = Fake<ITimesheetService>.Create();
        var (leave, lf) = Fake<ILeaveTimesheetService>.Create();
        tf.Returns(nameof(ITimesheetService.GetOrCreateMyTimesheetAsync), detail);
        lf.On(nameof(ILeaveTimesheetService.GetForTimesheetAsync), a => new TimesheetLeaveDto((Guid)a[0]!, prefill ?? [], []));
        ctx.Services.AddSingleton(timesheets);
        ctx.Services.AddSingleton(leave);
        return (tf, lf);
    }

    [Fact]
    public async Task Today_shows_the_days_entries_and_recent_codes_and_saves_only_that_day()
    {
        await using var ctx = Context();
        var detail = Detail(null, Entry(Direct, "1001.01", Wednesday, 6m), Entry(Overhead, "IND.OH", Monday, 2m));
        var (tf, _) = Register(ctx, detail);
        tf.On(nameof(ITimesheetService.SaveEntriesAsync), _ => detail);

        var dialogs = Providers(ctx);

        var page = ctx.Render<TodayPage>(p => p.Add(x => x.TodayOverride, Wednesday));

        page.WaitForAssertion(() => Assert.Equal("6", page.Find("[data-testid='hours-1001.01']").GetAttribute("value")));
        // Used this period: offered as a row. Never used: only in "Add a charge code".
        Assert.Equal("", page.Find("[data-testid='hours-IND.OH']").GetAttribute("value") ?? "");
        Assert.Empty(page.FindAll("[data-testid='hours-LV.PTO']"));
        // Leave reads by name, not by code.
        Assert.Contains("Paid time off", page.Find("[data-testid='add-code']").InnerHtml);
        Assert.DoesNotContain("LV.PTO", page.Find("[data-testid='add-code']").InnerHtml);
        Assert.True(page.Find("button[data-testid='save']").HasAttribute("disabled"));

        page.FindAll("button[aria-label='More time on Overhead (IND.OH)']").Single().Click();
        page.FindAll("button[aria-label='More time on Overhead (IND.OH)']").Single().Click();
        page.Find("[data-testid='hours-1001.01']").Change("5,5");
        page.Find("button[data-testid='save']").Click();

        var request = (SaveTimeEntriesRequest)tf.Single(nameof(ITimesheetService.SaveEntriesAsync))[1]!;
        Assert.Equal(Wednesday, request.Day);
        Assert.Null(request.Reason);
        Assert.Equal(2, request.Entries.Count);
        Assert.Contains(request.Entries, e => e.WbsElementId == Direct && e.Hours == 5.5m && e.EntryId == detail.Entries[0].Id && e.WorkDate == Wednesday);
        Assert.Contains(request.Entries, e => e.WbsElementId == Overhead && e.Hours == 1m && e.EntryId is null);
    }

    [Fact]
    public async Task Typing_hours_turns_Save_on_straight_away_without_leaving_the_box()
    {
        await using var ctx = Context();
        var detail = Detail(null, Entry(Direct, "1001.01", Wednesday, 6m), Entry(Overhead, "IND.OH", Monday, 2m));
        Register(ctx, detail);
        Providers(ctx);

        var page = ctx.Render<TodayPage>(p => p.Add(x => x.TodayOverride, Wednesday));
        page.WaitForAssertion(() => Assert.Equal("6", page.Find("[data-testid='hours-1001.01']").GetAttribute("value")));
        Assert.True(page.Find("button[data-testid='save']").HasAttribute("disabled"));

        page.Find("[data-testid='hours-1001.01']").Input("8");

        Assert.False(page.Find("button[data-testid='save']").HasAttribute("disabled"));
        Assert.Contains("8", page.Find("button[data-testid='save']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Today_needs_a_reason_once_the_timesheet_was_submitted()
    {
        await using var ctx = Context();
        var detail = Detail(Sheet(firstSubmitted: DateTime.UtcNow), Entry(Direct, "1001.01", Wednesday, 8m));
        var (tf, _) = Register(ctx, detail);
        tf.On(nameof(ITimesheetService.SaveEntriesAsync), _ => detail);

        var dialogs = Providers(ctx);

        var page = ctx.Render<TodayPage>(p => p.Add(x => x.TodayOverride, Wednesday));
        page.WaitForAssertion(() => page.Find("[data-testid='hours-1001.01']"));
        page.Find("[data-testid='hours-1001.01']").Change("7");

        Assert.True(page.Find("button[data-testid='save']").HasAttribute("disabled"));
        page.Find("[data-testid='reason']").Input("Typo in hours");
        page.Find("button[data-testid='save']").Click();

        var request = (SaveTimeEntriesRequest)tf.Single(nameof(ITimesheetService.SaveEntriesAsync))[1]!;
        Assert.Equal("Typo in hours", request.Reason);
    }

    [Fact]
    public async Task Today_is_read_only_when_the_timesheet_is_submitted()
    {
        await using var ctx = Context();
        Register(ctx, Detail(Sheet(TimesheetState.Submitted), Entry(Direct, "1001.01", Wednesday, 8m)));

        var dialogs = Providers(ctx);

        var page = ctx.Render<TodayPage>(p => p.Add(x => x.TodayOverride, Wednesday));

        page.WaitForAssertion(() => page.Find("[data-testid='locked']"));
        Assert.True(page.Find("[data-testid='hours-1001.01']").HasAttribute("disabled"));
        Assert.Empty(page.FindAll("button[data-testid='save']"));
    }

    [Fact]
    public async Task Today_prefills_approved_leave()
    {
        await using var ctx = Context();
        Register(ctx, Detail(), [new LeavePrefillDto(Pto, "LV.PTO", Wednesday, 8m, "Approved leave")]);

        var dialogs = Providers(ctx);

        var page = ctx.Render<TodayPage>(p => p.Add(x => x.TodayOverride, Wednesday));

        page.WaitForAssertion(() => Assert.Equal("8", page.Find("[data-testid='hours-LV.PTO']").GetAttribute("value")));
        Assert.NotNull(page.Find("[data-testid='leave-prefill']"));
        Assert.False(page.Find("button[data-testid='save']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Timesheet_shows_daily_totals_and_submits_with_certification()
    {
        await using var ctx = Context();
        var detail = Detail(null, Entry(Direct, "1001.01", Monday, 8m), Entry(Overhead, "IND.OH", Monday, 1m), Entry(Direct, "1001.01", Monday.AddDays(1), 7m));
        var (tf, _) = Register(ctx, detail);
        tf.On(nameof(ITimesheetService.SubmitAsync), _ => new SubmitTimesheetResult(Detail(Sheet(TimesheetState.Submitted)), ["Below standard hours"]));

        var dialogs = Providers(ctx);

        var page = ctx.Render<TimesheetPage>(p => p.Add(x => x.TodayOverride, Wednesday));
        page.WaitForAssertion(() => Assert.Equal("9 h", page.Find("[data-testid='day-total-2026-10-19']").TextContent.Trim()));
        Assert.Equal("7 h", page.Find("[data-testid='day-total-2026-10-20']").TextContent.Trim());

        page.Find("button[data-testid='submit']").Click();
        dialogs.WaitForAssertion(() => Assert.Contains(TimesheetCertification.Text, dialogs.Markup));
        var confirm = dialogs.FindAll("button").Single(b => b.TextContent.Contains("Certify and submit"));
        Assert.True(confirm.HasAttribute("disabled"));
        dialogs.Find("input[type='checkbox']").Change(true);
        dialogs.FindAll("button").Single(b => b.TextContent.Contains("Certify and submit")).Click();

        page.WaitForAssertion(() => Assert.True(((SubmitTimesheetRequest)tf.Single(nameof(ITimesheetService.SubmitAsync))[1]!).Certify));
        page.WaitForAssertion(() => page.Find("button[data-testid='reopen']"));
    }

    [Fact]
    public async Task Timesheet_recall_needs_a_reason()
    {
        await using var ctx = Context();
        var (tf, _) = Register(ctx, Detail(Sheet(TimesheetState.Submitted)));
        tf.On(nameof(ITimesheetService.ReopenAsync), _ => Detail());

        var dialogs = Providers(ctx);

        var page = ctx.Render<TimesheetPage>(p => p.Add(x => x.TodayOverride, Wednesday));
        page.WaitForAssertion(() => page.Find("button[data-testid='reopen']"));
        page.Find("button[data-testid='reopen']").Click();
        dialogs.WaitForAssertion(() => dialogs.Find("textarea"));
        dialogs.Find("textarea").Input("Forgot Friday");
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Recall").Click();

        page.WaitForAssertion(() => Assert.Equal("Forgot Friday", tf.Single(nameof(ITimesheetService.ReopenAsync))[1]));
        page.WaitForAssertion(() => page.Find("button[data-testid='submit']"));
    }
}
