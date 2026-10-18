using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

public sealed class MembershipAndUserTests
{
    [Fact]
    public async Task Owners_add_change_and_remove_members()
    {
        await using var seeded = await CreateAsync();
        var members = seeded.As(Owner).Host.Get<IWorkspaceMembershipService>();

        var added = await members.SetMemberAsync(seeded.ProgramOfficeId, new SetWorkspaceMemberRequest(" New@Example.com ", WorkspaceRole.Viewer));
        await members.SetMemberAsync(seeded.ProgramOfficeId, new SetWorkspaceMemberRequest("new@example.com", WorkspaceRole.Editor));
        var afterChange = await members.ListMembersAsync(seeded.ProgramOfficeId);
        await members.RemoveMemberAsync(seeded.ProgramOfficeId, "new@example.com");

        Assert.Equal("new@example.com", added.UserEmail);
        Assert.Equal(WorkspaceRole.Editor, afterChange.Single(m => m.UserEmail == "new@example.com").Role);
        Assert.DoesNotContain(await members.ListMembersAsync(seeded.ProgramOfficeId), m => m.UserEmail == "new@example.com");
    }

    [Fact]
    public async Task Only_owners_and_admins_manage_members()
    {
        await using var seeded = await CreateAsync();
        var request = new SetWorkspaceMemberRequest("new@example.com", WorkspaceRole.Owner);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            seeded.As(Editor).Host.Get<IWorkspaceMembershipService>().SetMemberAsync(seeded.ProgramOfficeId, request));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            seeded.As(Viewer).Host.Get<IWorkspaceMembershipService>().RemoveMemberAsync(seeded.ProgramOfficeId, Editor));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() =>
            seeded.As(Outsider).Host.Get<IWorkspaceMembershipService>().ListMembersAsync(seeded.ProgramOfficeId));
        var byAdmin = await seeded.As(Admin).Host.Get<IWorkspaceMembershipService>().SetMemberAsync(seeded.ProgramOfficeId, request);

        Assert.Equal(WorkspaceRole.Owner, byAdmin.Role);
        Assert.Equal(5, (await seeded.As(Viewer).Host.Get<IWorkspaceMembershipService>().ListMembersAsync(seeded.ProgramOfficeId)).Count);
    }

    [Fact]
    public async Task A_workspace_keeps_at_least_one_owner()
    {
        await using var seeded = await CreateAsync();
        var members = seeded.As(Owner).Host.Get<IWorkspaceMembershipService>();

        await Assert.ThrowsAsync<ArgumentException>(() => members.RemoveMemberAsync(seeded.ProgramOfficeId, Owner));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            members.SetMemberAsync(seeded.ProgramOfficeId, new SetWorkspaceMemberRequest(Owner, WorkspaceRole.Editor)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            members.SetMemberAsync(seeded.ProgramOfficeId, new SetWorkspaceMemberRequest("not-an-email", WorkspaceRole.Editor)));
    }

    [Fact]
    public async Task Workspace_lists_and_sheet_workspace_follow_membership()
    {
        await using var seeded = await CreateAsync();

        var mine = await seeded.As(Viewer).Host.Get<IWorkspaceMembershipService>().ListMyWorkspacesAsync();
        var all = await seeded.As(Admin).Host.Get<IWorkspaceMembershipService>().ListMyWorkspacesAsync();
        var forSheet = await seeded.As(Commenter).Host.Get<IWorkspaceMembershipService>().GetForSheetAsync(seeded.SheetId);

        Assert.Equal([("Program Office", (WorkspaceRole?)WorkspaceRole.Viewer)], mine.Select(w => (w.Name, w.MyRole)));
        Assert.Equal(["Finance", "Program Office"], all.Select(w => w.Name));
        Assert.Equal((seeded.ProgramOfficeId, WorkspaceRole.Commenter), (forSheet.Id, forSheet.MyRole!.Value));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() =>
            seeded.As(Viewer).Host.Get<IWorkspaceMembershipService>().GetForSheetAsync(seeded.FinanceSheetId));
    }

    [Fact]
    public async Task Get_me_provisions_the_user_just_in_time()
    {
        await using var seeded = await CreateAsync();

        var me = await seeded.As("Newcomer@Example.com").Host.Get<IUserDirectory>().GetMeAsync();
        var found = await seeded.Host.Get<IUserDirectory>().SearchAsync("newcomer");

        Assert.Equal(("newcomer@example.com", "newcomer", OrgRoles.Employee), (me.Email, me.DisplayName, me.Roles));
        Assert.Single(found);
    }

    [Fact]
    public async Task Provisioner_creates_once_updates_names_and_only_promotes()
    {
        await using var seeded = await CreateAsync();
        var provisioner = seeded.Host.Get<UserProvisioner>();

        var first = await provisioner.EnsureUserAsync("sam@example.com", "Sam");
        var renamed = await provisioner.EnsureUserAsync("SAM@example.com", "Samantha", makeAdmin: true);
        var again = await provisioner.EnsureUserAsync("sam@example.com", null, makeAdmin: false);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal("Samantha", renamed.DisplayName);
        Assert.True(again.IsAdmin);
    }

    [Fact]
    public async Task Only_admins_change_org_roles_and_one_admin_remains()
    {
        await using var seeded = await CreateAsync();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Owner).Host.Get<IUserDirectory>().SetRolesAsync(Viewer, OrgRoles.Admin));
        var promoted = await seeded.As(Admin).Host.Get<IUserDirectory>().SetRolesAsync(Viewer, OrgRoles.Admin | OrgRoles.Timekeeper);
        var demoted = await seeded.Host.Get<IUserDirectory>().SetRolesAsync(Viewer, OrgRoles.Finance);

        Assert.True(promoted.IsAdmin);
        Assert.Equal(OrgRoles.Finance, demoted.Roles);
        await Assert.ThrowsAsync<ArgumentException>(() => seeded.Host.Get<IUserDirectory>().SetRolesAsync(Admin, OrgRoles.Finance));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => seeded.Host.Get<IUserDirectory>().SetRolesAsync("nobody@example.com", OrgRoles.Admin));
    }
}
