using Workbench.Contracts.Identity;

namespace Workbench.Ui.Identity;

/// <summary>
/// The signed-in user's permissions, cascaded by <see cref="FinanceAccessScope"/> (the billing and purchasing layouts).
/// Use <see cref="FinanceOnly"/> to show actions that need a permission; the server refuses them for everyone else
/// anyway, so this only keeps people from seeing buttons they can't use.
/// </summary>
public sealed record FinanceAccess(PermissionSet Permissions)
{
    public static FinanceAccess None { get; } = new(PermissionSet.Empty);

    public bool Can(string permission) => Permissions.Has(permission);
}
