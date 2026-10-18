using Workbench.Application.Identity;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Sheets;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Sheets;

namespace Workbench.Tests.Collaboration;

public sealed class ActivityRecordingTests
{
    [Fact]
    public async Task Item_service_is_decorated()
    {
        await using var host = new CollaborationHost();

        // Permission checks wrap activity recording, so refused calls never reach it; the tests below show recording still runs.
        Assert.IsType<PermissionCheckingItemService>(host.Items);
        Assert.IsType<PermissionCheckingColumnService>(host.Columns);
    }

    [Fact]
    public async Task Update_records_readable_field_changes()
    {
        await using var host = new CollaborationHost();
        var (sheetId, item) = await host.NewSheetWithItemAsync("Design");
        var status = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Status", ColumnKind.Dropdown, ["Open", "Done"]));

        await host.Items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest("Design", new DateOnly(2026, 11, 2), null, null,
            new Dictionary<string, string?> { [status.Id.ToString()] = "Done" }));

        var entry = (await host.Activity.ListAsync(sheetId, item.Id))[0];
        Assert.Equal(ActivityKinds.ItemUpdated, entry.Kind);
        Assert.Equal("Updated \"Design\": Start: (empty) → \"2026-11-02\"; Status: (empty) → \"Done\"", entry.Summary);
        Assert.Equal("alice@example.com", entry.Actor);
        Assert.Contains(host.Changes, c => c.Kind == SheetChangeKind.ItemUpdated && c.ItemId == item.Id);
    }

    [Fact]
    public async Task Update_without_changes_records_nothing()
    {
        await using var host = new CollaborationHost();
        var (sheetId, item) = await host.NewSheetWithItemAsync("Same");

        await host.Items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest("Same", null, null, null, new Dictionary<string, string?>()));

        Assert.DoesNotContain(await host.Activity.ListAsync(sheetId), a => a.Kind == ActivityKinds.ItemUpdated);
    }

    [Fact]
    public async Task Create_move_and_column_changes_are_recorded_newest_first()
    {
        await using var host = new CollaborationHost();
        var (sheetId, a) = await host.NewSheetWithItemAsync("A");
        var b = await host.Items.CreateAsync(sheetId, new CreateItemRequest("B"));
        await host.Items.MoveAsync(sheetId, b.Id, new MoveItemRequest(a.Id, 0));
        var column = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Owner", ColumnKind.Person));
        await host.Columns.UpdateAsync(sheetId, column.Id, new UpdateColumnRequest("Lead", [], null, 0));
        await host.Columns.DeleteAsync(sheetId, column.Id);

        var summaries = (await host.Activity.ListAsync(sheetId)).Select(e => e.Summary);

        Assert.Equal(
        [
            "Deleted column \"Lead\"",
            "Renamed column \"Owner\" to \"Lead\"",
            "Added column \"Owner\" (Person)",
            "Moved \"B\"",
            "Added \"B\"",
            "Added \"A\"",
        ], summaries);
    }

    [Fact]
    public async Task Deleting_an_item_removes_comments_and_files_of_it_and_its_subitems()
    {
        await using var host = new CollaborationHost();
        var (sheetId, parent) = await host.NewSheetWithItemAsync("Phase");
        var child = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Child", parent.Id));
        var other = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Other"));
        await host.Comments.CreateAsync(sheetId, child.Id, new CreateCommentRequest("On child"));
        await host.Attachments.UploadAsync(sheetId, child.Id, "plan.pdf", new MemoryStream([1]));
        await host.Comments.CreateAsync(sheetId, other.Id, new CreateCommentRequest("Keep me"));

        await host.Items.DeleteAsync(sheetId, parent.Id);

        Assert.Empty(host.Blobs.Blobs);
        Assert.Single(await host.Comments.ListAsync(sheetId, other.Id));
        var entry = (await host.Activity.ListAsync(sheetId))[0];
        Assert.Equal("Deleted \"Phase\" and 1 subitem", entry.Summary);
        Assert.Contains(host.Changes, c => c.Kind == SheetChangeKind.ItemDeleted && c.ItemId == parent.Id);
    }

    [Fact]
    public void Decorate_requires_a_registration_and_keeps_its_lifetime()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.Decorate<IItemService, ActivityRecordingItemService>());

        services.AddScoped<IItemService, ItemService>();
        services.Decorate<IItemService, ActivityRecordingItemService>();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IItemService));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.NotNull(descriptor.ImplementationFactory);
    }
}
