using Workbench.Contracts.Customization;

namespace Workbench.Ui.Customization;

/// <summary>Who a layout is for, in words (the same wording as the server's layout source).</summary>
public static class FormLayoutNames
{
    public static string Describe(string? roleKey, string? companyCode) => (roleKey, companyCode) switch
    {
        (null, null) => "everyone",
        (null, { } c) => $"everyone in {c}",
        ({ } r, null) => CustomizationRoles.NameOf(r),
        ({ } r, { } c) => $"{CustomizationRoles.NameOf(r)} in {c}",
    };
}
