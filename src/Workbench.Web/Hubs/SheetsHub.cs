using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Web.Identity;

namespace Workbench.Web.Hubs;

/// <summary>
/// Relays sheet changes to desktop clients. A client calls <see cref="JoinSheet"/> and then receives
/// <see cref="ChangedMethod"/> messages carrying a <c>SheetChange</c>. The browser UI doesn't use this;
/// it subscribes to the in-process hub directly. Callers must be signed in (cookie or bearer token) and
/// may only join sheets they can see.
/// </summary>
[Authorize]
public sealed class SheetsHub(ISheetPermissions permissions, CurrentPrincipal principal) : Hub
{
    public const string Path = "/hubs/sheets";
    public const string ChangedMethod = "SheetChanged";

    public static string GroupFor(Guid sheetId) => $"sheet:{sheetId:N}";

    public async Task JoinSheet(Guid sheetId)
    {
        // Hub calls run outside the HTTP pipeline, so hand the connection's user to ICurrentUser explicitly.
        principal.Principal = Context.User;
        if (await permissions.GetMyRoleAsync(sheetId, Context.ConnectionAborted) is null)
        {
            throw new HubException(WorkbenchNotFoundException.For("Sheet", sheetId).Message);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(sheetId));
    }

    public Task LeaveSheet(Guid sheetId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(sheetId));
}
