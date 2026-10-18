using Workbench.Contracts.Identity;

namespace Workbench.Ui.Views;

/// <summary>
/// The signed-in user's role on the sheet being shown, cascaded by the sheet page to every view so they can hide
/// edit affordances. Purely cosmetic: the server enforces the same rules. Views treat a missing value as full
/// access, which keeps them usable on their own (and in tests).
/// </summary>
public sealed record SheetAccess(WorkspaceRole? Role)
{
    public bool CanComment => Role.CanComment();

    public bool CanEdit => Role.CanEdit();

    public bool CanManage => Role.CanManage();
}
