using System.Globalization;
using MudBlazor;
using Workbench.Contracts;
using Workbench.Contracts.Travel;

namespace Workbench.Ui.Travel;

public static class TravelFormat
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string Money(decimal value) => value.ToString("C2", Us);

    public static string Dates(DateOnly start, DateOnly end) =>
        start == end ? start.ToString("MMM d, yyyy", Us)
        : start.Year == end.Year ? $"{start.ToString("MMM d", Us)} – {end.ToString("MMM d, yyyy", Us)}"
        : $"{start.ToString("MMM d, yyyy", Us)} – {end.ToString("MMM d, yyyy", Us)}";

    public static Color StatusColor(TravelAuthorizationState status) => status switch
    {
        TravelAuthorizationState.Submitted => Color.Info,
        TravelAuthorizationState.Approved => Color.Success,
        TravelAuthorizationState.Rejected => Color.Error,
        TravelAuthorizationState.Cancelled => Color.Dark,
        _ => Color.Default,
    };

    public static string StatusText(TravelAuthorizationState status) => status switch
    {
        TravelAuthorizationState.Rejected => "Sent back",
        _ => TravelLabels.State(status),
    };

    public static (string Text, Color Color) Charge(CardChargeState state) => state switch
    {
        CardChargeState.Unassigned => ("Not on a report", Color.Warning),
        CardChargeState.OnDraftReport => ("On a draft report", Color.Info),
        CardChargeState.Submitted => ("Submitted", Color.Primary),
        CardChargeState.Approved => ("Approved", Color.Success),
        _ => ("Excluded", Color.Dark),
    };

    public static string Payment(CardPaymentKind kind) => kind == CardPaymentKind.CompanyPaid ? "Company pays the issuer" : "Employee pays the issuer";

    public static bool IsExpected(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or IOException
            or TaskCanceledException or WorkbenchNotFoundException;
}
