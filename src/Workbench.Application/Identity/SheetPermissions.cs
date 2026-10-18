using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

public sealed class SheetPermissions(AccessControl access) : ISheetPermissions
{
    public Task<WorkspaceRole?> GetMyRoleAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
        access.GetSheetRoleAsync(sheetId, cancellationToken);
}
