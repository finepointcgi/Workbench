using System.Globalization;
using System.Text.Json;
using Workbench.Contracts.Customization;
using Workbench.Domain.Customization;
using CustomFieldType = Workbench.Contracts.Customization.CustomFieldType;
using DomainType = Workbench.Domain.Customization.CustomFieldType;

namespace Workbench.Application.Customization;

/// <summary>A parsed value in its storage columns (see <see cref="CustomFieldValue"/>).</summary>
public sealed record StoredValue(string? Text, decimal? Number, DateOnly? Date, bool? Bool);

/// <summary>
/// Parses, validates and formats custom field values by type. Pure: picklist options come in, people and record links
/// (which need the database) are checked by <see cref="CustomFieldService"/>.
/// </summary>
public static class CustomFieldCodec
{
    public const int TextMax = 500;
    public const int LongTextMax = 4000;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The value parsed from invariant text, or null when blank. Throws <see cref="ArgumentException"/> naming the field.</summary>
    public static StoredValue? Parse(CustomFieldDefinition field, string? input, IReadOnlyList<ValueListItemDto>? options, string? currentText = null)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string Fail(string why) => throw new ArgumentException($"{field.Label}: {why}");
        switch (field.FieldType)
        {
            case DomainType.Text:
                if (text.Contains('\n') || text.Contains('\r'))
                {
                    Fail("use a single line (or make it a long text field).");
                }

                return text.Length > TextMax ? throw new ArgumentException($"{field.Label}: at most {TextMax} characters.") : new(text, null, null, null);
            case DomainType.LongText:
                return input!.Length > LongTextMax ? throw new ArgumentException($"{field.Label}: at most {LongTextMax} characters.") : new(input.Trim(), null, null, null);
            case DomainType.Number:
            case DomainType.Currency:
                if (!decimal.TryParse(text.Replace("$", "", StringComparison.Ordinal), NumberStyles.Number, Invariant, out var number))
                {
                    Fail($"'{text}' isn't a number.");
                }

                if (Math.Abs(number) >= 100_000_000_000_000m)
                {
                    Fail("the number is too large.");
                }

                return new(null, field.FieldType == DomainType.Currency ? Math.Round(number, 2, MidpointRounding.AwayFromZero) : Math.Round(number, 4, MidpointRounding.AwayFromZero), null, null);
            case DomainType.Date:
                return DateOnly.TryParseExact(text, "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var date)
                    || DateOnly.TryParse(text, Invariant, DateTimeStyles.None, out date)
                    ? new(null, null, date, null)
                    : throw new ArgumentException($"{field.Label}: '{text}' isn't a date (use yyyy-MM-dd).");
            case DomainType.YesNo:
                return text.ToLowerInvariant() switch
                {
                    "true" or "yes" or "y" or "1" => new(null, null, null, true),
                    "false" or "no" or "n" or "0" => new(null, null, null, false),
                    _ => throw new ArgumentException($"{field.Label}: use yes or no."),
                };
            case DomainType.Picklist:
                return new(Option(field, text, options, currentText is null ? [] : [currentText]), null, null, null);
            case DomainType.MultiPicklist:
            {
                var current = currentText is null ? [] : Codes(currentText);
                var codes = SplitList(text).Select(c => Option(field, c, options, current)).Distinct(StringComparer.Ordinal).ToList();
                var order = (options ?? []).Select(o => o.Code).ToList();
                codes = codes.OrderBy(c => order.IndexOf(c) is var i and >= 0 ? i : int.MaxValue).ToList();
                return codes.Count == 0 ? null : new(JsonSerializer.Serialize(codes), null, null, null);
            }
            case DomainType.Person:
                return text.Length > 320 || !text.Contains('@') ? throw new ArgumentException($"{field.Label}: '{text}' isn't a person's email.") : new(text.ToLowerInvariant(), null, null, null);
            case DomainType.RecordLink:
                return Guid.TryParse(text, out var id) ? new(id.ToString(), null, null, null) : throw new ArgumentException($"{field.Label}: choose a record.");
            case DomainType.Url:
                return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && text.Length <= 2000
                    ? new(uri.ToString(), null, null, null)
                    : throw new ArgumentException($"{field.Label}: '{text}' isn't a web address (https://…).");
            default:
                throw new ArgumentException($"{field.Label}: unsupported field type.");
        }
    }

    /// <summary>The invariant text of a stored value (what the API and CSV carry).</summary>
    public static string? ToText(DomainType type, StoredValue? value)
    {
        if (value is null)
        {
            return null;
        }

        return type switch
        {
            DomainType.Number => value.Number?.ToString("0.####", Invariant),
            DomainType.Currency => value.Number?.ToString("0.00", Invariant),
            DomainType.Date => value.Date?.ToString("yyyy-MM-dd", Invariant),
            DomainType.YesNo => value.Bool is { } b ? (b ? "true" : "false") : null,
            DomainType.MultiPicklist => value.Text is null ? null : string.Join(", ", Codes(value.Text)),
            _ => value.Text,
        };
    }

    public static StoredValue Of(CustomFieldValue row) => new(row.TextValue, row.NumberValue, row.DateValue, row.BoolValue);

    /// <summary>What people read: option labels for codes, yes/no, grouped numbers; people and links are resolved by the caller.</summary>
    public static string? Display(DomainType type, StoredValue? value, IReadOnlyList<ValueListItemDto>? options)
    {
        if (value is null)
        {
            return null;
        }

        string Label(string code) => options?.FirstOrDefault(o => o.Code == code)?.Label ?? code;
        return type switch
        {
            DomainType.Number => value.Number?.ToString("#,0.####", Invariant),
            DomainType.Currency => value.Number?.ToString("$#,0.00;-$#,0.00", Invariant),
            DomainType.Date => value.Date?.ToString("MMM d, yyyy", Invariant),
            DomainType.YesNo => value.Bool is { } b ? (b ? "Yes" : "No") : null,
            DomainType.Picklist => value.Text is null ? null : Label(value.Text),
            DomainType.MultiPicklist => value.Text is null ? null : string.Join(", ", Codes(value.Text).Select(Label)),
            _ => value.Text,
        };
    }

    public static IReadOnlyList<string> Codes(string stored)
    {
        if (stored.StartsWith('['))
        {
            try
            {
                return JsonSerializer.Deserialize<List<string>>(stored) ?? [];
            }
            catch (JsonException)
            {
            }
        }

        return SplitList(stored);
    }

    public static bool Same(StoredValue? a, StoredValue? b) => a == b;

    public static bool IsBlank(string? text) => string.IsNullOrWhiteSpace(text);

    private static List<string> SplitList(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                return (JsonSerializer.Deserialize<List<string>>(trimmed) ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            }
            catch (JsonException)
            {
            }
        }

        return trimmed.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>An active option's code, matched by code then label (ignoring case); an inactive one only when it's already the value.</summary>
    private static string Option(CustomFieldDefinition field, string text, IReadOnlyList<ValueListItemDto>? options, IReadOnlyCollection<string> current)
    {
        var match = options?.FirstOrDefault(o => string.Equals(o.Code, text, StringComparison.Ordinal))
            ?? options?.FirstOrDefault(o => string.Equals(o.Code, text, StringComparison.OrdinalIgnoreCase) || string.Equals(o.Label, text, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new ArgumentException($"{field.Label}: '{text}' isn't one of the choices.");
        }

        return match.IsActive || current.Contains(match.Code) ? match.Code : throw new ArgumentException($"{field.Label}: '{match.Label}' is no longer offered.");
    }

    internal static CustomFieldType ToContract(DomainType type) => (CustomFieldType)(int)type;

    internal static DomainType ToDomain(CustomFieldType type) => (DomainType)(int)type;
}
