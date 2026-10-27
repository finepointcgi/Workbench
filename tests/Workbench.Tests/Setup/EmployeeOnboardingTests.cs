using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Security;
using Workbench.Tests.Mobile;
using Workbench.Ui.Setup;

namespace Workbench.Tests.Setup;

/// <summary>Adding a person is one visit: the next employee number is suggested, and the page says whether they can sign in and offers the invitation.</summary>
public sealed class EmployeeOnboardingTests
{
    private static readonly EmployeeDto Alex = new(Guid.NewGuid(), "E1006", "alex@example.com", "Alex Rivera", null, null, null, new DateOnly(2026, 1, 5), null, 40m, true);

    [Theory]
    [InlineData(new[] { "E1001", "E1004", "E1005" }, "E1006")]
    [InlineData(new[] { "E0099" }, "E0100")]
    [InlineData(new[] { "7", "12" }, "13")]
    [InlineData(new[] { "ABC", "temp" }, "")]
    [InlineData(new[] { "x-1" }, "x-2")]
    [InlineData(new string[0], "")]
    public void The_next_employee_number_follows_the_companys_pattern(string[] existing, string expected) =>
        Assert.Equal(expected, SetupFormat.NextEmployeeNumber(existing));

    [Fact]
    public void Copying_bill_rates_takes_each_categorys_current_rate_and_leaves_what_the_contract_already_has()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        BillRateDto Rate(Guid category, string from, decimal rate) => new(Guid.NewGuid(), Guid.NewGuid(), category, DateOnly.Parse(from), rate);
        var source = new[] { Rate(a, "2025-01-01", 100m), Rate(a, "2026-01-01", 110m), Rate(a, "2027-01-01", 120m), Rate(b, "2026-06-01", 90m), Rate(c, "2026-01-01", 75m) };
        var existing = new[] { Rate(c, "2026-01-01", 80m) };

        var copied = SetupFormat.RatesToCopy(source, existing, new DateOnly(2026, 10, 26));

        Assert.Equal([(b, 90m), (a, 110m)], copied.Select(r => (r.LaborCategoryId, r.HourlyBillRate))); // a's 2026 rate is in force; a future one isn't taken; c is already priced
        Assert.Equal([(a, 100m)], SetupFormat.RatesToCopy([Rate(a, "2027-01-01", 100m)], [], new DateOnly(2026, 10, 26)).Select(r => (r.LaborCategoryId, r.HourlyBillRate))); // only a future rate: take it
    }

    private static (BunitContext Ctx, Fake<IInviteService> Invites) Context(IReadOnlyList<UserAdminDto> users, IReadOnlyList<InviteDto> invites)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var (inviteService, inviteFake) = Fake<IInviteService>.Create();
        inviteFake.Returns(nameof(IInviteService.ListAsync), invites);
        inviteFake.Returns(nameof(IInviteService.CreateAsync), new InviteDto(Guid.NewGuid(), Alex.Email, Alex.DisplayName, OrgRoles.Employee, "pm@example.com",
            DateTime.UtcNow, DateTime.UtcNow.AddDays(7), null, null, InviteStatus.Pending));
        var (userService, userFake) = Fake<IUserAdminService>.Create();
        userFake.Returns(nameof(IUserAdminService.ListUsersAsync), users);
        ctx.Services.AddSingleton(inviteService);
        ctx.Services.AddSingleton(userService);
        return (ctx, inviteFake);
    }

    [Fact]
    public void Someone_who_cant_sign_in_yet_is_offered_an_invitation_with_one_click()
    {
        var (ctx, invites) = Context([], []);
        using var _ = ctx;
        var panel = ctx.Render<EmployeeSignInPanel>(p => p.Add(x => x.Employee, Alex));

        panel.WaitForAssertion(() => Assert.Contains("can't sign in yet", panel.Find("[data-testid='employee-signin']").TextContent, StringComparison.Ordinal));
        panel.Find("[data-testid='signin-invite']").Click();

        panel.WaitForAssertion(() => Assert.Single(invites.Calls, c => c.Method == nameof(IInviteService.CreateAsync)));
        var request = (CreateInviteRequest)invites.Single(nameof(IInviteService.CreateAsync))[0]!;
        Assert.Equal(("alex@example.com", "Alex Rivera", OrgRoles.Employee), (request.Email, request.DisplayName, request.Roles));
    }

    [Fact]
    public void Someone_with_an_account_just_says_so_and_nothing_is_offered()
    {
        var account = new UserAdminDto(Guid.NewGuid(), Alex.Email, Alex.DisplayName, OrgRoles.Employee, true, DateTime.UtcNow, null, null, null, null, Alex.Id, Alex.DisplayName,
            default, false);
        var (ctx, _) = Context([account], []);
        using var _ = ctx;
        var panel = ctx.Render<EmployeeSignInPanel>(p => p.Add(x => x.Employee, Alex));

        panel.WaitForAssertion(() => Assert.NotNull(panel.Find("[data-testid='signin-has-account']")));
        Assert.Empty(panel.FindAll("[data-testid='signin-invite']"));
    }
}
