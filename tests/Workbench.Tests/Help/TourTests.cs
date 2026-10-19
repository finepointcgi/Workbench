using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Support;
using Workbench.Tests.Identity;
using Workbench.Ui.Help;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Help;

public sealed class TourTests
{
    private static NavAccess Access(OrgRoles roles, bool approver = false) =>
        new(new CurrentUserInfo("a@example.com", "A", roles, null), IsTimesheetApprover: approver);

    [Fact]
    public void Each_role_gets_its_own_short_tour()
    {
        Assert.Equal("employee", Tours.For(Access(OrgRoles.Employee)).Key);
        Assert.Equal("manager", Tours.For(Access(OrgRoles.Employee, approver: true)).Key);
        Assert.Equal("finance", Tours.For(Access(OrgRoles.Finance)).Key);
        Assert.Equal("admin", Tours.For(Access(OrgRoles.Admin | OrgRoles.Finance)).Key);
        Assert.All(Tours.All, t => Assert.InRange(t.Steps.Count, 3, 5));
        Assert.Equal(Tours.All.Count, Tours.All.Select(t => t.Key).Distinct().Count());
    }

    private static BunitContext Context(IHelpStateService state)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(state);
        return ctx;
    }

    [Fact]
    public async Task First_sign_in_shows_the_tour_and_skipping_remembers_it()
    {
        var state = new FakeHelpState();
        await using var ctx = Context(state);
        var cut = ctx.Render<WelcomeTour>(p => p.Add(x => x.Access, Access(OrgRoles.Employee)));

        cut.WaitForAssertion(() => Assert.Equal("Step 1 of 5", cut.Find("[data-testid='tour-progress']").TextContent));
        cut.Find("[data-testid='tour-next']").Click();
        Assert.Equal("Step 2 of 5", cut.Find("[data-testid='tour-progress']").TextContent);
        cut.Find("[data-testid='tour-back']").Click();
        cut.Find("[data-testid='tour-skip']").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='welcome-tour']")));
        Assert.Equal(["employee"], state.Completed);
    }

    [Fact]
    public async Task Finishing_the_tour_remembers_it_and_escape_dismisses()
    {
        var state = new FakeHelpState();
        await using var ctx = Context(state);
        var cut = ctx.Render<WelcomeTour>(p => p.Add(x => x.Access, Access(OrgRoles.Finance)));
        cut.WaitForAssertion(() => cut.Find("[data-testid='welcome-tour']"));
        for (var i = 0; i < Tours.Finance.Steps.Count; i++)
        {
            cut.Find("[data-testid='tour-next']").Click();
        }

        Assert.Empty(cut.FindAll("[data-testid='welcome-tour']"));
        Assert.Equal(["finance"], state.Completed);

        var again = new FakeHelpState();
        await using var ctx2 = Context(again);
        var cut2 = ctx2.Render<WelcomeTour>(p => p.Add(x => x.Access, Access(OrgRoles.Employee)));
        cut2.WaitForAssertion(() => cut2.Find("[data-testid='welcome-tour']"));
        cut2.Find("[data-testid='welcome-tour']").KeyDown("Escape");
        Assert.Equal(["employee"], again.Completed);
    }

    [Fact]
    public async Task A_finished_tour_does_not_show_again_and_waits_for_sign_in()
    {
        await using var ctx = Context(new FakeHelpState("employee"));
        var cut = ctx.Render<WelcomeTour>(p => p.Add(x => x.Access, Access(OrgRoles.Employee)));
        Assert.Empty(cut.FindAll("[data-testid='welcome-tour']"));

        await using var anon = Context(new FakeHelpState());
        var anonymous = anon.Render<WelcomeTour>(p => p.Add(x => x.Access, NavAccess.Anonymous));
        Assert.Empty(anonymous.FindAll("[data-testid='welcome-tour']"));
    }

    [Fact]
    public async Task Tour_state_is_kept_on_the_server_per_user()
    {
        await using var seeded = await SeededHost.CreateAsync();
        var state = seeded.Host.Get<IHelpStateService>();

        seeded.As(SeededHost.Editor);
        Assert.Empty((await state.GetAsync()).CompletedTours);
        await state.CompleteTourAsync("employee");
        await state.CompleteTourAsync("employee");
        await state.MarkWhatsNewSeenAsync("2026.10.3");

        // Someone else starts fresh.
        seeded.As(SeededHost.Viewer);
        Assert.Empty((await state.GetAsync()).CompletedTours);
        await state.CompleteTourAsync("Manager");

        seeded.As(SeededHost.Editor);
        var mine = await state.GetAsync();
        Assert.Equal(["employee"], mine.CompletedTours);
        Assert.Equal("2026.10.3", mine.WhatsNewSeen);
        Assert.True(mine.HasCompleted("EMPLOYEE"));

        await state.RestartToursAsync();
        Assert.Empty((await state.GetAsync()).CompletedTours);
        seeded.As(SeededHost.Viewer);
        Assert.Equal(["manager"], (await state.GetAsync()).CompletedTours);

        await Assert.ThrowsAsync<ArgumentException>(() => state.CompleteTourAsync("bad key!"));
        await Assert.ThrowsAsync<ArgumentException>(() => state.MarkWhatsNewSeenAsync(new string('x', 40)));

        seeded.As("");
        Assert.Empty((await state.GetAsync()).CompletedTours);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => state.CompleteTourAsync("employee"));
    }
}
