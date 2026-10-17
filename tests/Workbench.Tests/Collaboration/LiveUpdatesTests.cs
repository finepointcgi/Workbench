using Workbench.Application.Collaboration;
using Workbench.Contracts.Collaboration;

namespace Workbench.Tests.Collaboration;

public sealed class LiveUpdatesTests
{
    private static SheetChange Change(Guid sheetId) => new(sheetId, SheetChangeKind.ItemUpdated, Guid.NewGuid(), null, "a@example.com", DateTime.UtcNow);

    [Fact]
    public async Task Delivers_only_the_subscribed_sheet_until_disposed()
    {
        var hub = new SheetEventHub();
        var sheetId = Guid.NewGuid();
        var received = new List<SheetChange>();
        var subscription = await hub.SubscribeAsync(sheetId, c => { received.Add(c); return Task.CompletedTask; });

        await hub.PublishAsync(Change(sheetId));
        await hub.PublishAsync(Change(Guid.NewGuid()));
        await subscription.DisposeAsync();
        await hub.PublishAsync(Change(sheetId));

        Assert.Single(received);
        Assert.Equal(0, hub.SubscriberCount(sheetId));
    }

    [Fact]
    public async Task A_failing_handler_does_not_stop_the_others()
    {
        var hub = new SheetEventHub();
        var sheetId = Guid.NewGuid();
        var delivered = 0;
        await hub.SubscribeAsync(sheetId, _ => throw new InvalidOperationException("circuit gone"));
        await hub.SubscribeAsync(sheetId, _ => { delivered++; return Task.CompletedTask; });
        hub.SubscribeAll(_ => { delivered++; return Task.CompletedTask; });

        await hub.PublishAsync(Change(sheetId));

        Assert.Equal(2, delivered);
    }
}
