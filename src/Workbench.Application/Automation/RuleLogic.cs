using System.Globalization;
using System.Text.RegularExpressions;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Automation;

namespace Workbench.Application.Automation;

/// <summary>Pure rule evaluation: reading fields, matching triggers and conditions, and filling templates.</summary>
public static partial class RuleLogic
{
    public static string? Read(ItemDto item, string field) => field switch
    {
        RuleFields.Title => item.Title,
        RuleFields.Start => item.Start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        RuleFields.Finish => item.Finish?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => item.Values.GetValueOrDefault(field),
    };

    public static DateOnly? ReadDate(ItemDto item, string field) => field switch
    {
        RuleFields.Start => item.Start,
        RuleFields.Finish => item.Finish,
        _ => DateOnly.TryParseExact(item.Values.GetValueOrDefault(field), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
    };

    /// <summary>Blank-insensitive, case-insensitive comparison of two cell values.</summary>
    public static bool Same(string? a, string? b) =>
        string.Equals(Blank(a), Blank(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsBlank(string? value) => Blank(value) is null;

    /// <summary>Whether a ColumnChanged trigger fires for this update.</summary>
    public static bool ChangeMatches(RuleTrigger trigger, ItemDto before, ItemDto after)
    {
        if (trigger.Kind != RuleTriggerKind.ColumnChanged || trigger.Field is not { } field)
        {
            return false;
        }

        var to = Read(after, field);
        return !Same(Read(before, field), to) && (trigger.ToValue is null || Same(to, trigger.ToValue));
    }

    public static bool ConditionHolds(RuleCondition condition, ItemDto item)
    {
        var value = Read(item, condition.Field);
        return condition.Operator switch
        {
            RuleConditionOperator.Equals => Same(value, condition.Value),
            RuleConditionOperator.NotEquals => !Same(value, condition.Value),
            RuleConditionOperator.IsBlank => IsBlank(value),
            RuleConditionOperator.Contains => !IsBlank(condition.Value) && (value ?? "").Contains(condition.Value!.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public static bool ConditionsHold(Rule rule, ItemDto item) => rule.Conditions.All(c => ConditionHolds(c, item));

    /// <summary>
    /// Replaces <c>{Title}</c>, <c>{Start}</c>, <c>{Finish}</c>, <c>{Today}</c> and <c>{Column name}</c> (case-insensitive).
    /// Unknown placeholders are left as they are.
    /// </summary>
    public static string Render(string? template, ItemDto item, IReadOnlyList<Column> columns, DateOnly today) =>
        Placeholder().Replace(template ?? "", m =>
        {
            var name = m.Groups[1].Value.Trim();
            if (name.Equals("today", StringComparison.OrdinalIgnoreCase))
            {
                return today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            if (name.Equals(RuleFields.Title, StringComparison.OrdinalIgnoreCase)
                || name.Equals(RuleFields.Start, StringComparison.OrdinalIgnoreCase)
                || name.Equals(RuleFields.Finish, StringComparison.OrdinalIgnoreCase))
            {
                return Read(item, name.ToLowerInvariant()) ?? "";
            }

            var column = columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return column is null ? m.Value : item.Values.GetValueOrDefault(column.Id.ToString()) ?? "";
        });

    /// <summary>A human name for a field: "Title", "Finish" or the column's name.</summary>
    public static string FieldName(string? field, IReadOnlyList<Column> columns) => field switch
    {
        RuleFields.Title => "Title",
        RuleFields.Start => "Start",
        RuleFields.Finish => "Finish",
        null => "(none)",
        _ => columns.FirstOrDefault(c => c.Id.ToString() == field)?.Name ?? "(deleted column)",
    };

    /// <summary>Emails in a Person cell (one, or several separated by commas or semicolons).</summary>
    public static IEnumerable<string> People(string? value) =>
        (value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The names in a template's <c>{placeholders}</c>.</summary>
    public static IEnumerable<string> PlaceholderNames(string? template) =>
        Placeholder().Matches(template ?? "").Select(m => m.Groups[1].Value.Trim());

    [GeneratedRegex(@"\{([^{}]{1,100})\}")]
    private static partial Regex Placeholder();
}
