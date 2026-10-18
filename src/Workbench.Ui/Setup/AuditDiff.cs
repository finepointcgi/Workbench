using System.Text.Json;

namespace Workbench.Ui.Setup;

/// <summary>One field of an audit event's before/after JSON.</summary>
public sealed record AuditFieldChange(string Field, string? Before, string? After)
{
    public bool Changed => !string.Equals(Before, After, StringComparison.Ordinal);
}

/// <summary>Turns audit before/after JSON into a field-by-field comparison.</summary>
public static class AuditDiff
{
    /// <summary>Every top-level field present on either side, in the order first seen. Non-object JSON shows as one "value" field.</summary>
    public static IReadOnlyList<AuditFieldChange> Compare(string? beforeJson, string? afterJson)
    {
        var before = Flatten(beforeJson);
        var after = Flatten(afterJson);
        return before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal)
            .Select(k => new AuditFieldChange(k, before.GetValueOrDefault(k), after.GetValueOrDefault(k)))
            .ToList();
    }

    private static Dictionary<string, string?> Flatten(string? json)
    {
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return fields;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                fields["value"] = Text(doc.RootElement);
                return fields;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                fields[property.Name] = Text(property.Value);
            }
        }
        catch (JsonException)
        {
            fields["value"] = json;
        }

        return fields;
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };
}
