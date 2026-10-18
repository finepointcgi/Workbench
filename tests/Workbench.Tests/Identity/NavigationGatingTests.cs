using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Time;
using Workbench.Ui.Layout;
using Workbench.Ui.Time;

namespace Workbench.Tests.Identity;

/// <summary>Navigation and the /setup layout follow the user's roles (the server enforces the same rules).</summary>
public sealed class NavigationGatingTests
{
    private static BunitContext NewContext(OrgRoles roles, bool isApprover = false)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<ICurrentUserContext>(new FakeUserContext(roles));
        ctx.Services.AddSingleton<ITimesheetApprovalService>(new FakeApprovals(isApprover));
        return ctx;
    }

    [Theory]
    [InlineData(OrgRoles.Employee, false)]
    [InlineData(OrgRoles.Timekeeper, false)]
    [InlineData(OrgRoles.Finance, true)]
    [InlineData(OrgRoles.Admin, true)]
    public async Task Setup_group_and_pages_are_for_finance_and_admin(OrgRoles roles, bool allowed)
    {
        await using var ctx = NewContext(roles);

        var nav = ctx.Render<MudNavMenu>(p => p.AddChildContent<SetupNavLinks>());
        var layout = ctx.Render<SetupLayout>(p => p.Add(l => l.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<p data-body>People</p>"))));

        nav.WaitForAssertion(() => Assert.Equal(allowed, nav.Markup.Contains("setup/people")));
        layout.WaitForAssertion(() =>
        {
            Assert.Equal(allowed, layout.FindAll("[data-body]").Count == 1);
            Assert.Equal(!allowed, layout.FindAll("[data-forbidden]").Count == 1);
        });
    }

    [Theory]
    [InlineData(OrgRoles.Employee, false)]
    [InlineData(OrgRoles.Timekeeper, false)]
    [InlineData(OrgRoles.Finance, true)]
    [InlineData(OrgRoles.Admin, true)]
    public async Task Accounting_pages_are_for_finance_and_admin(OrgRoles roles, bool allowed)
    {
        await using var ctx = NewContext(roles);

        var layout = ctx.Render<AccountingLayout>(p => p.Add(l => l.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<p data-body>Journal</p>"))));

        layout.WaitForAssertion(() =>
        {
            Assert.Equal(allowed, layout.FindAll("[data-body]").Count == 1);
            Assert.Equal(!allowed, layout.FindAll("[data-forbidden]").Count == 1);
        });
    }

    [Theory]
    [InlineData(OrgRoles.Employee, false)]
    [InlineData(OrgRoles.Finance, false)]
    [InlineData(OrgRoles.Admin, true)]
    public async Task Admin_layout_is_for_admins_only(OrgRoles roles, bool allowed)
    {
        await using var ctx = NewContext(roles);

        var layout = ctx.Render<AdminLayout>(p => p.Add(l => l.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<p data-body>Costpoint</p>"))));

        layout.WaitForAssertion(() =>
        {
            Assert.Equal(allowed, layout.FindAll("[data-body]").Count == 1);
            Assert.Equal(!allowed, layout.FindAll("[data-forbidden]").Count == 1);
        });
    }

    [Theory]
    [InlineData(OrgRoles.Employee, false, false)]
    [InlineData(OrgRoles.Finance, false, false)]
    [InlineData(OrgRoles.Employee, true, true)] // a supervisor
    [InlineData(OrgRoles.Timekeeper, false, true)]
    [InlineData(OrgRoles.Admin, false, true)]
    public async Task Timesheet_approvals_link_is_for_supervisors_admins_and_timekeepers(OrgRoles roles, bool isApprover, bool shown)
    {
        await using var ctx = NewContext(roles, isApprover);

        var nav = ctx.Render<MudNavMenu>(p => p.AddChildContent<TimeNavLinks>());

        nav.WaitForAssertion(() => Assert.Equal(shown, nav.Markup.Contains("time/approvals")));
    }

    private sealed class FakeUserContext(OrgRoles roles) : ICurrentUserContext
    {
        public Task<CurrentUserInfo> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CurrentUserInfo("someone@example.com", "Someone", roles, null));
    }

    private sealed class FakeApprovals(bool isApprover) : ITimesheetApprovalService
    {
        public Task<bool> IsApproverAsync(CancellationToken cancellationToken = default) => Task.FromResult(isApprover);

        public Task<IReadOnlyList<TimesheetDto>> ListPendingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TimesheetDto> ApproveAsync(Guid timesheetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TimesheetDto> RejectAsync(Guid timesheetId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TeamStatusDto> GetTeamStatusAsync(DateOnly date, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
