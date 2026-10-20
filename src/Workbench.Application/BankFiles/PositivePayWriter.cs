using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Workbench.Contracts.BankFiles;

namespace Workbench.Application.BankFiles;

/// <summary>One check on a positive pay file: issued, or a void record for a check already reported.</summary>
public sealed record PositivePayItem(string CheckNumber, DateOnly IssueDate, decimal Amount, string Payee, bool IsVoid);

/// <summary>
/// Writes positive pay files from a layout: CSV (quoted when needed, optional header from each field's <c>Value</c> or name)
/// or fixed width (each field padded or cut to its width). Built-in presets cover the common layouts.
/// </summary>
public static class PositivePayWriter
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static readonly Guid StandardCsvId = new("00000000-0000-0000-0000-00000000a001");
    public static readonly Guid FixedWidth80Id = new("00000000-0000-0000-0000-00000000a002");
    public static readonly Guid ArpFixedId = new("00000000-0000-0000-0000-00000000a003");

    /// <summary>The built-in layouts. Banks differ; these are starting points, copy one to make your own.</summary>
    public static IReadOnlyList<PositivePayLayoutDto> Presets { get; } =
    [
        new(StandardCsvId, "Standard CSV", PositivePayFileFormat.Csv,
        [
            new(PositivePayField.AccountNumber, Value: "Account"),
            new(PositivePayField.CheckNumber, Value: "Check number"),
            new(PositivePayField.IssueDate, Format: "MM/dd/yyyy", Value: "Issue date"),
            new(PositivePayField.Amount, Format: "0.00", Value: "Amount"),
            new(PositivePayField.Payee, Value: "Payee"),
            new(PositivePayField.IssueCode, Value: "Void"),
        ], IncludeHeader: true, IssueCode: "", VoidCode: "V", IsPreset: true),
        new(FixedWidth80Id, "Fixed width, 80 characters with payee", PositivePayFileFormat.FixedWidth,
        [
            new(PositivePayField.AccountNumber, 10, '0', FieldAlign.Right),
            new(PositivePayField.CheckNumber, 10, '0', FieldAlign.Right),
            new(PositivePayField.AmountCents, 10, '0', FieldAlign.Right),
            new(PositivePayField.IssueDate, 8, Format: "MMddyyyy"),
            new(PositivePayField.IssueCode, 1),
            new(PositivePayField.Payee, 41),
        ], IncludeHeader: false, IssueCode: "I", VoidCode: "V", IsPreset: true),
        new(ArpFixedId, "Fixed width ARP, no payee (44 characters)", PositivePayFileFormat.FixedWidth,
        [
            new(PositivePayField.AccountNumber, 15, '0', FieldAlign.Right),
            new(PositivePayField.CheckNumber, 10, '0', FieldAlign.Right),
            new(PositivePayField.AmountCents, 12, '0', FieldAlign.Right),
            new(PositivePayField.IssueDate, 6, Format: "yyMMdd"),
            new(PositivePayField.IssueCode, 1),
        ], IncludeHeader: false, IssueCode: "O", VoidCode: "V", IsPreset: true),
    ];

    public static string Write(PositivePayLayoutDto layout, string accountNumber, string? routingNumber, IEnumerable<PositivePayItem> items)
    {
        Validate(layout.Fields, layout.Format);
        var b = new StringBuilder();
        if (layout.Format == PositivePayFileFormat.Csv && layout.IncludeHeader)
        {
            b.Append(string.Join(",", layout.Fields.Select(f => Csv(f.Value ?? f.Field.ToString())))).Append("\r\n");
        }

        foreach (var item in items)
        {
            var values = layout.Fields.Select(f => Value(f, layout, accountNumber, routingNumber, item));
            b.Append(layout.Format == PositivePayFileFormat.Csv
                ? string.Join(",", values.Select(Csv))
                : string.Concat(layout.Fields.Zip(values, Pad))).Append("\r\n");
        }

        return b.ToString();
    }

    public static void Validate(IReadOnlyList<PositivePayFieldSpec> fields, PositivePayFileFormat format)
    {
        if (fields is null || fields.Count == 0)
        {
            throw new ArgumentException("A layout needs at least one field.");
        }

        if (fields.Count > 40)
        {
            throw new ArgumentException("A layout can have at most 40 fields.");
        }

        if (!fields.Any(f => f.Field == PositivePayField.CheckNumber) || !fields.Any(f => f.Field is PositivePayField.Amount or PositivePayField.AmountCents))
        {
            throw new ArgumentException("A positive pay layout needs the check number and the amount.");
        }

        foreach (var f in fields)
        {
            if (format == PositivePayFileFormat.FixedWidth && f.Width is null or < 1)
            {
                throw new ArgumentException($"Every field in a fixed-width layout needs a width ({f.Field} has none).");
            }

            if (f.Width is > 200)
            {
                throw new ArgumentException("A field can be at most 200 characters wide.");
            }

            if (f.Format is { } fmt && f.Field is PositivePayField.IssueDate)
            {
                try
                {
                    _ = new DateOnly(2026, 1, 2).ToString(fmt, CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    throw new ArgumentException($"\"{fmt}\" isn't a date format (try MM/dd/yyyy or yyMMdd).");
                }
            }
        }
    }

    private static string Value(PositivePayFieldSpec f, PositivePayLayoutDto layout, string account, string? routing, PositivePayItem item) => f.Field switch
    {
        PositivePayField.AccountNumber => account,
        PositivePayField.RoutingNumber => routing ?? "",
        PositivePayField.CheckNumber => item.CheckNumber,
        PositivePayField.IssueDate => item.IssueDate.ToString(string.IsNullOrWhiteSpace(f.Format) ? "yyyy-MM-dd" : f.Format, CultureInfo.InvariantCulture),
        PositivePayField.Amount => item.Amount.ToString(string.IsNullOrWhiteSpace(f.Format) ? "0.00" : f.Format, CultureInfo.InvariantCulture),
        PositivePayField.AmountCents => decimal.Round(item.Amount * 100m, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture),
        PositivePayField.Payee => Ascii(item.Payee),
        PositivePayField.IssueCode => item.IsVoid ? layout.VoidCode : layout.IssueCode,
        PositivePayField.Literal => f.Value ?? "",
        _ => "",
    };

    private static string Pad(PositivePayFieldSpec f, string value)
    {
        var width = f.Width ?? value.Length;
        if (value.Length > width)
        {
            // Numbers keep their rightmost digits; text its start.
            return f.Align == FieldAlign.Right ? value[^width..] : value[..width];
        }

        return f.Align == FieldAlign.Right ? value.PadLeft(width, f.Pad) : value.PadRight(width, f.Pad);
    }

    private static string Ascii(string text) =>
        new(text.Normalize(NormalizationForm.FormD).Where(c => c is >= ' ' and <= '~').ToArray());

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
