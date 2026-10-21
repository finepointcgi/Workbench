using Workbench.Contracts.Identity;
using Workbench.Contracts.Materials;

namespace Workbench.Ui.Materials;

/// <summary>Labels for kits, landed cost, physical inventory and the capacity schedule.</summary>
public static class ShopFloorFormat
{
    public static string Label(KitState s) => s switch
    {
        KitState.Open => "Open",
        KitState.Staged => "Staged",
        KitState.Issued => "Issued",
        _ => "Cancelled",
    };

    public static MudBlazor.Color Color(KitState s) => s switch
    {
        KitState.Open => MudBlazor.Color.Info,
        KitState.Staged => MudBlazor.Color.Primary,
        KitState.Issued => MudBlazor.Color.Success,
        _ => MudBlazor.Color.Default,
    };

    public static string Label(LandedChargeType t) => t.ToString();

    public static string Label(LandedCostBasis b) => b switch
    {
        LandedCostBasis.Value => "By value",
        LandedCostBasis.Quantity => "By quantity",
        _ => "By weight",
    };

    public static string Label(LandedChargeState s) => s.ToString();

    public static MudBlazor.Color Color(LandedChargeState s) => s switch
    {
        LandedChargeState.Draft => MudBlazor.Color.Info,
        LandedChargeState.Posted => MudBlazor.Color.Success,
        _ => MudBlazor.Color.Default,
    };

    public static string Label(PhysicalInventoryState s) => s == PhysicalInventoryState.Counting ? "Counting (frozen)" : s.ToString();

    public static MudBlazor.Color Color(PhysicalInventoryState s) => s switch
    {
        PhysicalInventoryState.Counting => MudBlazor.Color.Warning,
        PhysicalInventoryState.Posted => MudBlazor.Color.Success,
        _ => MudBlazor.Color.Default,
    };

    public static string Label(CountTagState s) => s switch
    {
        CountTagState.NotCounted => "Not counted",
        CountTagState.RecountNeeded => "Recount needed",
        _ => s.ToString(),
    };

    public static MudBlazor.Color Color(CountTagState s) => s switch
    {
        CountTagState.Counted or CountTagState.Recounted => MudBlazor.Color.Success,
        CountTagState.RecountNeeded => MudBlazor.Color.Warning,
        _ => MudBlazor.Color.Default,
    };

    /// <summary>Finance and administrators post landed cost and count adjustments.</summary>
    public static bool CanPost(CurrentUserInfo? user) => user is null || user.Can(Workbench.Contracts.Identity.Permission.MaterialsCosting);

    /// <summary>Landed cost drafts: Materials, Finance and administrators.</summary>
    public static bool CanDraftLandedCost(CurrentUserInfo? user) => user is null || user.CanAny(Workbench.Contracts.Identity.Permission.MaterialsManage, Workbench.Contracts.Identity.Permission.MaterialsCosting);

    public static string Weekdays(string mask)
    {
        string[] names = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        var days = names.Where((_, i) => i < mask.Length && mask[i] == 'Y').ToList();
        return days.Count == 7 ? "Every day" : string.Join(", ", days);
    }
}
