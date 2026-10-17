using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Sheets;

namespace Workbench.Tests.Collaboration;

/// <summary>The real application registrations over an in-memory database, blob store and a switchable user.</summary>
internal sealed class CollaborationHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    public CollaborationHost()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TestDb.CreateFactory());
        services.AddSingleton<IBlobStore>(Blobs);
        services.AddSingleton<ICurrentUser>(User);
        services.AddWorkbenchApplication();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _scope = _provider.CreateAsyncScope();
        Events.SubscribeAll(change =>
        {
            Changes.Add(change);
            return Task.CompletedTask;
        });
    }

    public MemoryBlobStore Blobs { get; } = new();
    public TestUser User { get; } = new();
    public ConcurrentBag<SheetChange> Changes { get; } = [];

    public SheetEventHub Events => _provider.GetRequiredService<SheetEventHub>();
    public ISheetService Sheets => Get<ISheetService>();
    public IItemService Items => Get<IItemService>();
    public IColumnService Columns => Get<IColumnService>();
    public ICommentService Comments => Get<ICommentService>();
    public IAttachmentService Attachments => Get<IAttachmentService>();
    public IActivityService Activity => Get<IActivityService>();

    public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public async Task<(Guid SheetId, ItemDto Item)> NewSheetWithItemAsync(string title = "Task")
    {
        var sheetId = (await Sheets.CreateAsync(new CreateSheetRequest("Plan", null, true))).Id;
        var item = await Items.CreateAsync(sheetId, new CreateItemRequest(title));
        return (sheetId, item);
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }
}

internal sealed class TestUser : ICurrentUser
{
    public string Email { get; set; } = "alice@example.com";
}

internal sealed class MemoryBlobStore : IBlobStore
{
    public ConcurrentDictionary<string, byte[]> Blobs { get; } = new();

    public async Task<long> SaveAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        Blobs[key] = buffer.ToArray();
        return buffer.Length;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
        Blobs.TryGetValue(key, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes))
            : throw new FileNotFoundException(key);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        Blobs.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
