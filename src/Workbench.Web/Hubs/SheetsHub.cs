using Microsoft.AspNetCore.SignalR;

namespace Workbench.Web.Hubs;

/// <summary>
/// Relays sheet changes to desktop clients. A client calls <see cref="JoinSheet"/> and then receives
/// <see cref="ChangedMethod"/> messages carrying a <c>SheetChange</c>. The browser UI doesn't use this;
/// it subscribes to the in-process hub directly.
/// </summary>
// TODO: require auth and check sheet access in JoinSheet once identity is wired up.
public sealed class SheetsHub : Hub
{
    public const string Path = "/hubs/sheets";
    public const string ChangedMethod = "SheetChanged";

    public static string GroupFor(Guid sheetId) => $"sheet:{sheetId:N}";

    public Task JoinSheet(Guid sheetId) => Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(sheetId));

    public Task LeaveSheet(Guid sheetId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(sheetId));
}
