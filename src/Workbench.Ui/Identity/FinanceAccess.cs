namespace Workbench.Ui.Identity;

/// <summary>
/// Whether the signed-in user is in Finance or an administrator, cascaded by <see cref="FinanceAccessScope"/> (the
/// billing and purchasing layouts). Use <see cref="FinanceOnly"/> to show Finance-only actions; the server refuses them
/// for everyone else anyway, so this only keeps people from seeing buttons they can't use.
/// </summary>
public sealed record FinanceAccess(bool IsFinance);
