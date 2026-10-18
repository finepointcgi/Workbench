using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Costpoint;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

/// <summary>The permission decorators in front of every feature service, exercised through the real registrations.</summary>
public sealed class PermissionEnforcementTests
{
    public static TheoryData<string> CannotEdit => new() { Viewer, Commenter };

    public static TheoryData<string> CanEdit => new() { Editor, Owner, Admin };

    [Fact]
    public async Task Sheet_list_only_has_sheets_in_the_users_workspaces()
    {
        await using var seeded = await CreateAsync();

        var viewer = await seeded.As(Viewer).Host.Sheets.ListAsync();
        var outsider = await seeded.As(Outsider).Host.Sheets.ListAsync();
        var admin = await seeded.As(Admin).Host.Sheets.ListAsync();

        Assert.Equal([seeded.SheetId], viewer.Select(s => s.Id));
        Assert.Empty(outsider);
        Assert.Equal(2, admin.Count);
    }

    [Fact]
    public async Task Get_returns_null_for_sheets_the_user_cant_see()
    {
        await using var seeded = await CreateAsync();

        Assert.Null(await seeded.As(Viewer).Host.Sheets.GetAsync(seeded.FinanceSheetId));
        Assert.Null(await seeded.As(Outsider).Host.Sheets.GetAsync(seeded.SheetId));
        Assert.NotNull(await seeded.As(Viewer).Host.Sheets.GetAsync(seeded.SheetId));
        Assert.NotNull(await seeded.As(Admin).Host.Sheets.GetAsync(seeded.FinanceSheetId));
    }

    [Fact]
    public async Task Outsiders_get_not_found_rather_than_forbidden()
    {
        await using var seeded = await CreateAsync();
        var host = seeded.As(Outsider).Host;

        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Items.ListAsync(seeded.SheetId));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Activity.ListAsync(seeded.SheetId));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Comments.ListAsync(seeded.SheetId, seeded.ItemId));
    }

    [Theory]
    [MemberData(nameof(CannotEdit))]
    public async Task Viewers_and_commenters_can_read_but_not_change(string email)
    {
        await using var seeded = await CreateAsync();
        var host = seeded.As(email).Host;

        Assert.Single(await host.Items.ListAsync(seeded.SheetId));
        Assert.Empty(await host.Get<IDependencyService>().ListAsync(seeded.SheetId));
        Assert.Empty(await host.Get<IBaselineService>().ListAsync(seeded.SheetId));
        Assert.Empty(await host.Attachments.ListAsync(seeded.SheetId, seeded.ItemId));
        Assert.NotNull(await host.Activity.ListAsync(seeded.SheetId));

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Items.CreateAsync(seeded.SheetId, new CreateItemRequest("New")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            host.Items.UpdateAsync(seeded.SheetId, seeded.ItemId, new UpdateItemRequest("Renamed", null, null, null, new Dictionary<string, string?>())));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Items.MoveAsync(seeded.SheetId, seeded.ItemId, new MoveItemRequest(null, 0)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Items.DeleteAsync(seeded.SheetId, seeded.ItemId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Columns.CreateAsync(seeded.SheetId, new CreateColumnRequest("Notes", ColumnKind.Text)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            host.Get<IDependencyService>().AddAsync(seeded.SheetId, new CreateDependencyRequest(seeded.ItemId, Guid.NewGuid())));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IBaselineService>().CaptureAsync(seeded.SheetId, new CaptureBaselineRequest("B")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            host.Attachments.UploadAsync(seeded.SheetId, seeded.ItemId, "notes.txt", new MemoryStream([1])));
        Assert.Empty(seeded.Host.Changes);
    }

    [Theory]
    [MemberData(nameof(CanEdit))]
    public async Task Editors_owners_and_admins_can_change_the_sheet(string email)
    {
        await using var seeded = await CreateAsync();
        var host = seeded.As(email).Host;

        var item = await host.Items.CreateAsync(seeded.SheetId, new CreateItemRequest("Next"));
        await host.Columns.CreateAsync(seeded.SheetId, new CreateColumnRequest("Notes", ColumnKind.Text));
        await host.Get<IDependencyService>().AddAsync(seeded.SheetId, new CreateDependencyRequest(seeded.ItemId, item.Id));
        await host.Get<IBaselineService>().CaptureAsync(seeded.SheetId, new CaptureBaselineRequest("B"));
        await host.Attachments.UploadAsync(seeded.SheetId, item.Id, "notes.txt", new MemoryStream([1]));
        await host.Items.DeleteAsync(seeded.SheetId, item.Id);

        Assert.Single(await host.Items.ListAsync(seeded.SheetId));
    }

    [Fact]
    public async Task Viewers_cant_comment_but_commenters_can()
    {
        await using var seeded = await CreateAsync();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            seeded.As(Viewer).Host.Comments.CreateAsync(seeded.SheetId, seeded.ItemId, new CreateCommentRequest("Hi")));
        var comment = await seeded.As(Commenter).Host.Comments.CreateAsync(seeded.SheetId, seeded.ItemId, new CreateCommentRequest("Looks good"));

        Assert.Equal(Commenter, comment.AuthorEmail);
        Assert.Single(await seeded.As(Viewer).Host.Comments.ListAsync(seeded.SheetId, seeded.ItemId));
    }

    [Fact]
    public async Task Creating_a_sheet_needs_editor_access_and_uses_an_editable_workspace()
    {
        await using var seeded = await CreateAsync();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Viewer).Host.Sheets.CreateAsync(new CreateSheetRequest("Mine", null, false)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Outsider).Host.Sheets.CreateAsync(new CreateSheetRequest("Mine", null, false)));
        var created = await seeded.As(Editor).Host.Sheets.CreateAsync(new CreateSheetRequest("Analysis", null, false));

        // "Finance" sorts first, but the editor only has access to Program Office.
        Assert.Equal("Program Office", created.Workspace);
        Assert.Contains(await seeded.As(Viewer).Host.Sheets.ListAsync(), s => s.Id == created.Id);
    }

    [Fact]
    public async Task The_first_sheet_on_an_empty_install_makes_its_creator_the_owner()
    {
        await using var host = new Collaboration.CollaborationHost();

        var sheet = await host.Sheets.CreateAsync(new CreateSheetRequest("Plan", null, false));

        Assert.Equal(WorkspaceRole.Owner, await host.Get<ISheetPermissions>().GetMyRoleAsync(sheet.Id));
    }

    [Fact]
    public async Task Manual_costpoint_sync_is_for_admins()
    {
        await using var seeded = await CreateAsync();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Owner).Host.Get<ICostpointService>().SyncNowAsync());
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => seeded.As(Outsider).Host.Get<ICostpointService>().GetPlanVsActualAsync(seeded.SheetId));
    }

    [Fact]
    public async Task Nobody_signed_in_is_refused()
    {
        await using var seeded = await CreateAsync();
        var host = seeded.As(string.Empty).Host;

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Sheets.ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Items.ListAsync(seeded.SheetId));
        Assert.Null(await host.Get<ISheetPermissions>().GetMyRoleAsync(seeded.SheetId));
    }

    [Fact]
    public async Task My_role_comes_from_membership_and_admins_are_owners()
    {
        await using var seeded = await CreateAsync();
        Assert.Equal(WorkspaceRole.Viewer, await seeded.As(Viewer).Host.Get<ISheetPermissions>().GetMyRoleAsync(seeded.SheetId));
        Assert.True(await seeded.As(Commenter).Host.Get<ISheetPermissions>().CanCommentAsync(seeded.SheetId));
        Assert.False(await seeded.As(Commenter).Host.Get<ISheetPermissions>().CanEditAsync(seeded.SheetId));
        Assert.True(await seeded.As(Editor).Host.Get<ISheetPermissions>().CanEditAsync(seeded.SheetId));
        Assert.False(await seeded.As(Editor).Host.Get<ISheetPermissions>().CanManageAsync(seeded.SheetId));
        Assert.True(await seeded.As(Owner).Host.Get<ISheetPermissions>().CanManageAsync(seeded.SheetId));
        Assert.Equal(WorkspaceRole.Owner, await seeded.As(Admin).Host.Get<ISheetPermissions>().GetMyRoleAsync(seeded.FinanceSheetId));
        Assert.Null(await seeded.As(Outsider).Host.Get<ISheetPermissions>().GetMyRoleAsync(seeded.SheetId));
        Assert.Null(await seeded.As(Admin).Host.Get<ISheetPermissions>().GetMyRoleAsync(Guid.NewGuid()));
    }

    [Fact]
    public void Role_helpers_follow_the_role_order()
    {
        Assert.False(((WorkspaceRole?)null).CanView());
        Assert.True(((WorkspaceRole?)WorkspaceRole.Viewer).CanView());
        Assert.False(((WorkspaceRole?)WorkspaceRole.Viewer).CanComment());
        Assert.True(((WorkspaceRole?)WorkspaceRole.Commenter).CanComment());
        Assert.False(((WorkspaceRole?)WorkspaceRole.Commenter).CanEdit());
        Assert.True(((WorkspaceRole?)WorkspaceRole.Editor).CanEdit());
        Assert.False(((WorkspaceRole?)WorkspaceRole.Editor).CanManage());
        Assert.True(((WorkspaceRole?)WorkspaceRole.Owner).CanManage());
    }
}
