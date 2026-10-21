using System.Globalization;
using Workbench.Contracts.Receipts;

namespace Workbench.Ui.Receipts;

internal static class ReceiptFormat
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string Money(decimal? amount) => amount is { } a ? a.ToString("C2", Us) : "—";

    public static string Date(DateOnly? date) => date is { } d ? d.ToString("MMM d, yyyy", Us) : "—";

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.0} MB",
    };

    public static string Kind(ReceiptDocumentKind kind) => kind switch
    {
        ReceiptDocumentKind.LodgingFolio => "Hotel folio",
        ReceiptDocumentKind.CardStatement => "Card statement",
        ReceiptDocumentKind.Other => "Other",
        _ => "Receipt",
    };

    /// <summary>"From the receipt" or, when the assistant read it, a reminder to check it.</summary>
    public static string? Source(ReceiptExtractionDto? reading, string field) =>
        reading is null || !reading.Found(field) ? null
        : reading.ReadBy.TryGetValue(field, out var by) && by == ReceiptReadMethod.Assistant ? "Read by the AI assistant; check it"
        : "From the receipt";

    public static string Uses(ReceiptDto receipt) => receipt.Uses.Count switch
    {
        0 => "Not attached",
        1 => "On 1 line",
        var n => $"On {n} lines",
    };

    public static string Stay(DateOnly checkIn, DateOnly checkOut)
    {
        var nights = checkOut.DayNumber - checkIn.DayNumber;
        var range = checkIn.Month == checkOut.Month && checkIn.Year == checkOut.Year
            ? $"{checkIn.ToString("MMM d", Us)}–{checkOut.Day}"
            : $"{checkIn.ToString("MMM d", Us)}–{checkOut.ToString("MMM d", Us)}";
        return $"{range} ({nights} night{(nights == 1 ? "" : "s")})";
    }
}
