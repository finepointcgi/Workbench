using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Sheets;

namespace Workbench.Tests.Board;

/// <summary>Shared fakes for the board and calendar view tests.</summary>
internal static class ViewFakes
{
    public static readonly Guid SheetId = Guid.NewGuid();

    public static BunitContext CreateContext(RecordingItemService items, RecordingColumnService? columns = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<IItemService>(items);
        ctx.Services.AddSingleton<IColumnService>(columns ?? new RecordingColumnService());
        // MudSelect / MudChip need a popover provider in the tree.
        ctx.Render<MudBlazor.MudPopoverProvider>();
        return ctx;
    }

    public static SheetDetail Sheet(bool scheduling, params ColumnDto[] columns) =>
        new(new SheetSummary(SheetId, "Test sheet", null, "Workspace", 0, scheduling, null, DateTime.UtcNow), columns);

    public static ColumnDto StatusColumn(string name = "Status") =>
        new(Guid.NewGuid(), name, ColumnKind.Dropdown, 0, ["Not started", "In progress", "Done"], null);

    public static ItemDto Item(string title, ColumnDto? column = null, string? value = null, DateOnly? start = null,
        DateOnly? finish = null, Guid? parentId = null, int order = 0) =>
        new(Guid.NewGuid(), parentId, order, title, start, finish, null,
            column is null || value is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { [column.Id.ToString()] = value });
}

internal sealed class RecordingItemService : IItemService
{
    public List<(Guid ItemId, UpdateItemRequest Request)> Updates { get; } = [];

    public List<CreateItemRequest> Creates { get; } = [];

    public Task<IReadOnlyList<ItemDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ItemDto>>([]);

    public Task<ItemDto> CreateAsync(Guid sheetId, CreateItemRequest request, CancellationToken cancellationToken = default)
    {
        Creates.Add(request);
        return Task.FromResult(new ItemDto(Guid.NewGuid(), request.ParentId, 0, request.Title, request.Start, request.Finish, null,
            request.Values ?? new Dictionary<string, string?>()));
    }

    public Task<ItemDto> UpdateAsync(Guid sheetId, Guid itemId, UpdateItemRequest request, CancellationToken cancellationToken = default)
    {
        Updates.Add((itemId, request));
        return Task.FromResult(new ItemDto(itemId, null, 0, request.Title, request.Start, request.Finish, request.CostpointWbsId, request.Values));
    }

    public Task<ItemDto> MoveAsync(Guid sheetId, Guid itemId, MoveItemRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeleteAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class RecordingColumnService : IColumnService
{
    public List<CreateColumnRequest> Creates { get; } = [];

    public Task<ColumnDto> CreateAsync(Guid sheetId, CreateColumnRequest request, CancellationToken cancellationToken = default)
    {
        Creates.Add(request);
        return Task.FromResult(new ColumnDto(Guid.NewGuid(), request.Name, request.Type, 0, request.Options ?? [], request.Formula));
    }

    public Task<ColumnDto> UpdateAsync(Guid sheetId, Guid columnId, UpdateColumnRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeleteAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
