using System.Globalization;
using Workbench.Contracts;

namespace Workbench.Ui.Mobile;

internal static class MobileFormat
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string Money(decimal amount) => amount.ToString("C2", Us);

    public static string Hours(decimal hours) => hours.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Day(DateOnly date) => date.ToString("ddd, MMM d", CultureInfo.InvariantCulture);

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly? ParseIso(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static string Period(DateOnly start, DateOnly end) =>
        $"{start.ToString("MMM d", CultureInfo.InvariantCulture)} – {end.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}";

    /// <summary>Hours typed on a phone keyboard: "7.5", "7,5" or "". Null when it isn't a number of hours.</summary>
    public static decimal? ParseHours(string? value)
    {
        var text = (value ?? "").Trim().Replace(',', '.');
        if (text.Length == 0)
        {
            return 0m;
        }

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var hours) && hours is >= 0 and <= 24
            ? decimal.Round(hours, 2)
            : null;
    }

    /// <summary>Errors a service call can be expected to raise: show the message and carry on.</summary>
    public static bool IsExpected(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or IOException
            or TaskCanceledException or WorkbenchNotFoundException;
}
