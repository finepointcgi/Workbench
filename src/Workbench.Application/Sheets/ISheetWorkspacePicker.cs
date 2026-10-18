using Workbench.Domain;

namespace Workbench.Application.Sheets;

/// <summary>
/// Chooses the workspace for a new sheet. It may add a new workspace to <paramref name="db"/>; the sheet service
/// saves it with the sheet. Returning null keeps the default (the first workspace by name).
/// </summary>
public interface ISheetWorkspacePicker
{
    Task<Workspace?> PickAsync(IWorkbenchDbContext db, CancellationToken cancellationToken);
}
