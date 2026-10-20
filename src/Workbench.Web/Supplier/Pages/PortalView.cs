using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Workbench.Contracts.Purchasing;

namespace Workbench.Web.Supplier.Pages;

/// <summary>What every portal page gets: who's signed in (null on the sign-in pages), the form token and a message.</summary>
public sealed record PortalView(SupplierSessionDto? Session, AntiforgeryTokenSet Token, string Section, string? Notice = null, string? Error = null);

internal static class PortalFormat
{
    public static string Money(decimal value) => value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));

    public static string Price(decimal value) => value.ToString("#,0.00##", CultureInfo.InvariantCulture);

    public static string Quantity(decimal value) => value.ToString("#,0.####", CultureInfo.InvariantCulture);

    public static string Date(DateOnly? value) => value?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) ?? "";

    public static string Input(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    public static string Input(decimal? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";

    public static string When(DateTime utc) => utc.ToString("MMM d, yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public static string Label(AcknowledgementState state) => state switch
    {
        AcknowledgementState.Accepted => "Accepted",
        AcknowledgementState.AcceptedWithChanges => "Accepted with changes",
        _ => "Rejected",
    };

    public static string Label(ShipNoticeState state) => state switch
    {
        ShipNoticeState.InTransit => "In transit",
        ShipNoticeState.Received => "Received",
        _ => "Cancelled",
    };
}
