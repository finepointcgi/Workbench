using System.Globalization;
using MudBlazor;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Closeout;

namespace Workbench.Ui.Ceilings;

public static class CeilingFormat
{
    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

    public static string Amount(CeilingMetric metric, decimal value) =>
        metric == CeilingMetric.Hours ? value.ToString("#,0.##", Usd) + " h" : value.ToString("C0", Usd);

    public static string Metric(CeilingMetric metric) => metric switch
    {
        CeilingMetric.Hours => "Hours",
        CeilingMetric.Cost => "Cost",
        _ => "Billed",
    };

    public static string Scope(CeilingScope scope) => scope switch
    {
        CeilingScope.Clin => "CLIN",
        CeilingScope.Wbs => "WBS",
        CeilingScope.LaborCategory => "Labor category",
        _ => scope.ToString(),
    };

    public static Color Color(CeilingAlertLevel level) => level switch
    {
        CeilingAlertLevel.Over => MudBlazor.Color.Error,
        CeilingAlertLevel.Critical => MudBlazor.Color.Warning,
        CeilingAlertLevel.Warning => MudBlazor.Color.Info,
        _ => MudBlazor.Color.Success,
    };

    public static string State(CloseoutState state) => state switch
    {
        CloseoutState.PhysicallyComplete => "Physically complete",
        _ => state.ToString(),
    };
}
