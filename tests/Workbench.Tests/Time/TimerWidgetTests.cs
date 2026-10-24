using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Workbench.Application.Time;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Time;
using Workbench.Tests.Mobile;
using Workbench.Ui.Time;

namespace Workbench.Tests.Time;

/// <summary>The timer card over the real timer and timesheet services: start, stop, and the hours on the timesheet.</summary>
public sealed class TimerWidgetTests
{
    private static async Task<(BunitContext Ctx, TimeTestHost Host, TimesheetTimerService Timer)> ContextAsync()
    {
        var host = await TimeTestHost.CreateAsync();
        host.SignInAs(host.Dana);
        var timer = new TimesheetTimerService(host.Factory, host.User, host.Timesheets, host.Projects,
            new TimekeepingClock(host.Clock, Microsoft.Extensions.Options.Options.Create(host.Options)));
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<ITimesheetTimerService>(timer);
        ctx.Services.AddSingleton<ITimesheetService>(host.Timesheets);
        var (user, fake) = Fake<ICurrentUserContext>.Create();
        fake.Returns(nameof(ICurrentUserContext.GetAsync), new CurrentUserInfo(host.Dana.Email, host.Dana.DisplayName, default, host.Dana.Id));
        ctx.Services.AddSingleton(user);
        ctx.Render<MudPopoverProvider>();
        ctx.Render<MudSnackbarProvider>();
        return (ctx, host, timer);
    }

    [Fact]
    public async Task Start_then_stop_adds_the_time_to_todays_hours()
    {
        var (ctx, host, _) = await ContextAsync();
        await using var _ctx = ctx;
        StopTimerResult? stopped = null;
        var widget = ctx.Render<TimerWidget>(p => p.Add(x => x.Stopped, r => stopped = r));

        widget.WaitForAssertion(() => Assert.NotEmpty(widget.FindAll("[data-testid='timer-start']")));
        await widget.Find("[data-testid='timer-start']").ClickAsync(new());
        widget.WaitForAssertion(() => Assert.Contains("Requirements", widget.Find("[data-testid='timer-code']").TextContent));

        host.Clock.UtcNow = host.Clock.UtcNow.AddMinutes(45);
        await widget.Find("[data-testid='timer-stop']").ClickAsync(new());

        widget.WaitForAssertion(() => Assert.NotNull(stopped));
        Assert.Equal(TimerStopOutcome.Added, stopped!.Outcome);
        var entry = Assert.Single((await host.Timesheets.GetOrCreateMyTimesheetAsync(TimeTestHost.Monday)).Entries);
        Assert.Equal(0.75m, entry.Hours);
        widget.WaitForAssertion(() => Assert.NotEmpty(widget.FindAll("[data-testid='timer-start']")));
    }

    [Fact]
    public async Task The_app_bar_shows_a_timer_started_elsewhere()
    {
        var (ctx, host, timer) = await ContextAsync();
        await using var _ctx = ctx;
        var chip = ctx.Render<TimerAppBarChip>();
        Assert.Empty(chip.FindAll("[data-testid='appbar-timer']"));

        await timer.StartAsync(new StartTimerRequest(host.Direct.Id)); // another device
        TimerSignals.Raise(host.Dana.Email);

        chip.WaitForAssertion(() => Assert.Contains("1001.01", chip.Find("[data-testid='appbar-timer']").TextContent));
    }

    [Fact]
    public async Task Without_timekeeping_the_timer_stays_hidden()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        Assert.Empty(ctx.Render<TimerWidget>().Markup.Trim());
        Assert.Empty(ctx.Render<TimerAppBarChip>().Markup.Trim());
    }
}
