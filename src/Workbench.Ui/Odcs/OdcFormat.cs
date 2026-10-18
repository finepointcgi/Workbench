using MudBlazor;
using Workbench.Contracts.Odcs;

namespace Workbench.Ui.Odcs;

internal static class OdcFormat
{
    public static string Money(decimal amount) => amount.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    public static Color StatusColor(OdcApprovalState status) => status switch
    {
        OdcApprovalState.Submitted => Color.Info,
        OdcApprovalState.Approved => Color.Success,
        OdcApprovalState.Rejected => Color.Error,
        _ => Color.Default,
    };

    public static string TypeName(OdcCostType type) => type.ToString();

    public static DateTime? ToDateTime(DateOnly? date) => date?.ToDateTime(TimeOnly.MinValue);

    public static DateOnly? ToDateOnly(DateTime? date) => date is { } d ? DateOnly.FromDateTime(d) : null;

    /// <summary>The message to show for an error from an ODC service call.</summary>
    public static bool IsExpected(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or IOException
            or Workbench.Contracts.WorkbenchNotFoundException;
}
