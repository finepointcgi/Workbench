using System.Globalization;
using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Grid;

/// <summary>Converts between stored cell values (invariant strings), display text and editor text.</summary>
public static class CellFormat
{
    private const string DateStorageFormat = "yyyy-MM-dd";
    private const string DateDisplayFormat = "MM/dd/yyyy";

    // Costpoint customers are US government contractors, so currency is shown in dollars.
    private static readonly CultureInfo CurrencyCulture = CultureInfo.GetCultureInfo("en-US");

    public static string? GetRaw(ItemDto item, GridColumn column) => column.Kind switch
    {
        GridColumnKind.Title => item.Title,
        GridColumnKind.Start => item.Start?.ToString(DateStorageFormat, CultureInfo.InvariantCulture),
        GridColumnKind.Finish => item.Finish?.ToString(DateStorageFormat, CultureInfo.InvariantCulture),
        GridColumnKind.Wbs => item.CostpointWbsId,
        _ => item.Values.TryGetValue(column.Key, out var value) ? value : null,
    };

    /// <summary>Returns the item's full update request with this column set to <paramref name="value"/>.</summary>
    public static UpdateItemRequest WithValue(ItemDto item, GridColumn column, string? value)
    {
        var request = new UpdateItemRequest(item.Title, item.Start, item.Finish, item.CostpointWbsId, item.Values);
        return column.Kind switch
        {
            GridColumnKind.Title => request with { Title = value ?? string.Empty },
            GridColumnKind.Start => request with { Start = ParseStoredDate(value) },
            GridColumnKind.Finish => request with { Finish = ParseStoredDate(value) },
            GridColumnKind.Wbs => request with { CostpointWbsId = value },
            _ => request with { Values = new Dictionary<string, string?>(item.Values) { [column.Key] = value } },
        };
    }

    public static string Display(string? raw, ColumnKind kind)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        return kind switch
        {
            ColumnKind.Number when TryParseStoredDecimal(raw, out var number) => number.ToString("#,0.##########", CultureInfo.CurrentCulture),
            ColumnKind.Currency when TryParseStoredDecimal(raw, out var amount) => amount.ToString("C2", CurrencyCulture),
            ColumnKind.Date when ParseStoredDate(raw) is { } date => date.ToString(DateDisplayFormat, CultureInfo.InvariantCulture),
            _ => raw,
        };
    }

    /// <summary>The text an editor starts with: the raw value, except dates use the display format.</summary>
    public static string EditText(string? raw, ColumnKind kind) =>
        kind == ColumnKind.Date ? Display(raw, kind) : raw ?? string.Empty;

    public static DateTime? ToDateTime(string? raw) => ParseStoredDate(raw)?.ToDateTime(TimeOnly.MinValue);

    public static string? FromDateTime(DateTime? date) =>
        date is { } d ? DateOnly.FromDateTime(d).ToString(DateStorageFormat, CultureInfo.InvariantCulture) : null;

    /// <summary>Parses what the user typed into the stored form. Empty text clears the cell.</summary>
    public static bool TryNormalize(string? text, ColumnKind kind, out string? value, out string? error)
    {
        value = null;
        error = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return true;
        }

        switch (kind)
        {
            case ColumnKind.Number:
            case ColumnKind.Currency:
                var cleaned = trimmed.Replace("$", string.Empty).Replace(",", string.Empty).Replace(" ", string.Empty);
                if (cleaned.StartsWith('(') && cleaned.EndsWith(')'))
                {
                    cleaned = "-" + cleaned[1..^1];
                }

                if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    error = $"\"{trimmed}\" isn't a number.";
                    return false;
                }

                value = number.ToString("0.############################", CultureInfo.InvariantCulture);
                return true;

            case ColumnKind.Date:
                if (!TryParseDate(trimmed, out var date))
                {
                    error = $"\"{trimmed}\" isn't a date. Use MM/DD/YYYY or YYYY-MM-DD.";
                    return false;
                }

                value = date.ToString(DateStorageFormat, CultureInfo.InvariantCulture);
                return true;

            case ColumnKind.Checkbox:
                value = trimmed is "true" or "1" or "yes" or "x" ? "true" : "false";
                return true;

            default:
                value = trimmed;
                return true;
        }
    }

    public static string KindName(ColumnKind kind) => kind switch
    {
        ColumnKind.CostpointLookup => "Costpoint lookup",
        _ => kind.ToString(),
    };

    private static bool TryParseDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, [DateStorageFormat, DateDisplayFormat, "M/d/yyyy", "M/d/yy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
        || DateOnly.TryParse(text, CurrencyCulture, DateTimeStyles.None, out date);

    private static DateOnly? ParseStoredDate(string? raw) =>
        DateOnly.TryParseExact(raw, DateStorageFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static bool TryParseStoredDecimal(string raw, out decimal value) =>
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
}
