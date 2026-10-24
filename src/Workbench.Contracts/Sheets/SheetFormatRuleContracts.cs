using Workbench.Contracts.Customization;

namespace Workbench.Contracts.Sheets;

/// <summary>
/// A conditional formatting rule, in plain words: "when <see cref="Conditions"/> (all of them, or any with
/// <see cref="MatchAny"/>), format <see cref="Target"/> (the whole row when null, else one field) with a background from the
/// status palette (<see cref="Color"/>, a <see cref="StatusColors"/> key), bold and/or strikethrough". Conditions are filter
/// conditions (<see cref="SheetViewEngine.Matches"/>); dates may be relative to today (<see cref="RelativeDates"/>).
/// </summary>
public sealed record SheetFormatRuleDefinition(
    IReadOnlyList<ListFilterCondition> Conditions,
    bool MatchAny = false,
    string? Target = null,
    string? Color = null,
    bool Bold = false,
    bool Strikethrough = false,
    bool Enabled = true)
{
    /// <summary>The fields the rule reads or formats.</summary>
    public IEnumerable<string> Fields => Conditions.Select(c => c.Field).Concat(Target is null ? [] : [Target]);
}

/// <summary>A saved rule. <paramref name="Id"/> keeps it when the list is saved again.</summary>
public sealed record SheetFormatRuleDto(Guid Id, SheetFormatRuleDefinition Definition);

/// <summary>
/// A sheet's rules in the order they apply, as the caller may see them. Rules reading or coloring a restricted column the caller
/// can't see are left out (and keep their place when the list is saved); <paramref name="HiddenCount"/> says how many.
/// </summary>
public sealed record SheetFormatRulesDto(IReadOnlyList<SheetFormatRuleDto> Rules, bool CanEdit, int HiddenCount = 0)
{
    public static SheetFormatRulesDto Empty { get; } = new([], false);
}

/// <param name="Id">The saved rule this replaces, or null for a new one.</param>
public sealed record SaveSheetFormatRule(Guid? Id, SheetFormatRuleDefinition Definition);

/// <summary>The whole list of rules, in order. Rules missing from it are deleted.</summary>
public sealed record SaveSheetFormatRulesRequest(IReadOnlyList<SaveSheetFormatRule> Rules);

/// <summary>Conditional formatting rules per sheet. Viewers read them; editors save them.</summary>
public interface ISheetFormatRuleService
{
    Task<SheetFormatRulesDto> ListAsync(Guid sheetId, CancellationToken cancellationToken = default);

    Task<SheetFormatRulesDto> SaveAsync(Guid sheetId, SaveSheetFormatRulesRequest request, CancellationToken cancellationToken = default);
}

/// <summary>How a cell (or a whole row) looks: background and text color from the status palette, bold, strikethrough.</summary>
public sealed record CellStyle(StatusColor? Background, bool Bold, bool Strikethrough)
{
    public static CellStyle None { get; } = new(null, false, false);

    public bool IsEmpty => Background is null && !Bold && !Strikethrough;

    /// <summary>Inline CSS for the grid.</summary>
    public string? Css => IsEmpty
        ? null
        : string.Concat(
            Background is { } c ? $"background:{c.Background};color:{c.Text};" : string.Empty,
            Bold ? "font-weight:600;" : string.Empty,
            Strikethrough ? "text-decoration:line-through;" : string.Empty);
}

/// <summary>
/// Works out conditional formatting: which rules each row matches and how its cells look. Rules apply in order: for each
/// property the first matching rule that sets it wins (like Smartsheet), so a rule higher up the list beats one below; bold
/// and strikethrough are on when any matching rule turns them on. A rule naming a field the caller can't see never applies.
/// </summary>
public sealed class SheetFormats
{
    private readonly Dictionary<Guid, List<SheetFormatRuleDefinition>> _matches;

    private SheetFormats(Dictionary<Guid, List<SheetFormatRuleDefinition>> matches) => _matches = matches;

    public static SheetFormats None { get; } = new([]);

    public static SheetFormats Evaluate(
        IReadOnlyList<ItemDto> items, IReadOnlyList<ColumnDto> columns, IReadOnlyList<SheetFormatRuleDefinition>? rules, DateOnly today)
    {
        var usable = (rules ?? []).Where(r => r.Enabled && r.Conditions.Count > 0 && r.Fields.All(f => Known(f, columns))).ToList();
        if (usable.Count == 0)
        {
            return None;
        }

        var matches = new Dictionary<Guid, List<SheetFormatRuleDefinition>>();
        foreach (var item in items)
        {
            var hit = usable.Where(r => r.MatchAny
                ? r.Conditions.Any(c => SheetViewEngine.Matches(item, c, columns, today))
                : r.Conditions.All(c => SheetViewEngine.Matches(item, c, columns, today))).ToList();
            if (hit.Count > 0)
            {
                matches[item.Id] = hit;
            }
        }

        return new SheetFormats(matches);
    }

    /// <summary>A built-in field or one of <paramref name="columns"/> (those the caller can see).</summary>
    public static bool Known(string field, IReadOnlyList<ColumnDto> columns) =>
        field is SheetFields.Title or SheetFields.Start or SheetFields.Finish or SheetFields.Wbs
        || columns.Any(c => string.Equals(c.Id.ToString(), field, StringComparison.OrdinalIgnoreCase));

    public bool Any => _matches.Count > 0;

    /// <summary>The rules a row matches, in order.</summary>
    public IReadOnlyList<SheetFormatRuleDefinition> RulesFor(Guid itemId) => _matches.TryGetValue(itemId, out var hit) ? hit : [];

    /// <summary>The whole row's look (rules that format the row).</summary>
    public CellStyle Row(Guid itemId) => Merge(RulesFor(itemId).Where(r => r.Target is null));

    /// <summary>One cell's look: rules on its field and on the whole row, in rule order.</summary>
    public CellStyle Cell(Guid itemId, string field) =>
        Merge(RulesFor(itemId).Where(r => r.Target is null || string.Equals(r.Target, field, StringComparison.OrdinalIgnoreCase)));

    private static CellStyle Merge(IEnumerable<SheetFormatRuleDefinition> rules)
    {
        StatusColor? background = null;
        var bold = false;
        var strike = false;
        foreach (var rule in rules)
        {
            background ??= StatusColors.Find(rule.Color);
            bold |= rule.Bold;
            strike |= rule.Strikethrough;
        }

        return background is null && !bold && !strike ? CellStyle.None : new CellStyle(background, bold, strike);
    }
}

/// <summary>A rule in plain words: "When Status is Stuck, color the whole row Red".</summary>
public static class SheetFormatText
{
    public static string Describe(SheetFormatRuleDefinition rule, Func<string, string> fieldName)
    {
        var conditions = string.Join(rule.MatchAny ? " or " : " and ", rule.Conditions.Select(c => Condition(c, fieldName)));
        var what = rule.Target is null ? "the whole row" : fieldName(rule.Target);
        var formats = new List<string>();
        if (StatusColors.Find(rule.Color) is { } color)
        {
            formats.Add($"color {what} {color.Name.ToLowerInvariant()}");
        }

        if (rule.Bold)
        {
            formats.Add(formats.Count == 0 ? $"make {what} bold" : "bold");
        }

        if (rule.Strikethrough)
        {
            formats.Add(formats.Count == 0 ? $"strike through {what}" : "struck through");
        }

        return $"When {(conditions.Length == 0 ? "…" : conditions)}, {(formats.Count == 0 ? "do nothing yet" : string.Join(", ", formats))}";
    }

    public static string Condition(ListFilterCondition condition, Func<string, string> fieldName)
    {
        var values = SheetViewEngine.Values(condition.Value)
            .Select(v => RelativeDates.Offset(v) is { } days ? RelativeDates.Describe(days) : v.Replace("\n", ", "))
            .ToList();
        var op = SheetFilterText.Operator(condition.Operator);
        if (values.Any(v => v.EndsWith("ago", StringComparison.Ordinal) || v.EndsWith("today", StringComparison.Ordinal) || v is "tomorrow" or "yesterday"))
        {
            op = condition.Operator switch
            {
                ListFilterOperator.LessThan => "is before",
                ListFilterOperator.LessOrEqual => "is on or before",
                ListFilterOperator.GreaterThan => "is after",
                ListFilterOperator.GreaterOrEqual => "is on or after",
                _ => op,
            };
        }

        return SheetFilterText.TakesValue(condition.Operator)
            ? $"{fieldName(condition.Field)} {op} {string.Join(" or ", values)}"
            : $"{fieldName(condition.Field)} {op}";
    }
}
