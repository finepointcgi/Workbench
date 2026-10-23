namespace Workbench.Contracts.Sheets;

/// <summary>A status label color: the pill's background and the text drawn on it (contrast at least 4.5:1).</summary>
public sealed record StatusColor(string Key, string Name, string Background, string Text);

/// <summary>
/// The colors a Dropdown column's options (status labels) may take. A fixed palette, so every label's text stays readable
/// (WCAG 1.4.3, at least 4.5:1, checked by a test); the label text is always shown, so color is never the only signal.
/// </summary>
public static class StatusColors
{
    public static StatusColor Gray { get; } = new("gray", "Gray", "#5F6B7A", "#FFFFFF");
    public static StatusColor LightGray { get; } = new("light-gray", "Light gray", "#E3E6EA", "#2B323B");
    public static StatusColor Blue { get; } = new("blue", "Blue", "#1F5FBF", "#FFFFFF");
    public static StatusColor Navy { get; } = new("navy", "Navy", "#263B73", "#FFFFFF");
    public static StatusColor Teal { get; } = new("teal", "Teal", "#00737A", "#FFFFFF");
    public static StatusColor Green { get; } = new("green", "Green", "#127A3E", "#FFFFFF");
    public static StatusColor Amber { get; } = new("amber", "Amber", "#FFC94D", "#3D2B00");
    public static StatusColor Orange { get; } = new("orange", "Orange", "#B34700", "#FFFFFF");
    public static StatusColor Red { get; } = new("red", "Red", "#C62828", "#FFFFFF");
    public static StatusColor Pink { get; } = new("pink", "Pink", "#B0306A", "#FFFFFF");
    public static StatusColor Purple { get; } = new("purple", "Purple", "#6B3FA0", "#FFFFFF");

    public static IReadOnlyList<StatusColor> All { get; } = [Gray, LightGray, Blue, Navy, Teal, Green, Amber, Orange, Red, Pink, Purple];

    /// <summary>The labels and colors a new status column starts with.</summary>
    public static IReadOnlyList<(string Label, StatusColor Color)> Defaults { get; } =
        [("Not started", LightGray), ("Working on it", Amber), ("Stuck", Red), ("Done", Green)];

    public static StatusColor? Find(string? key) => All.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The color a label gets until someone picks one: common status words get their usual color (Done green, Blocked red,
    /// In progress amber), anything else gray. Existing Status dropdowns therefore show sensible pills with no setup.
    /// </summary>
    public static StatusColor Suggest(string? label)
    {
        var text = label?.Trim().ToLowerInvariant() ?? string.Empty;
        if (text.Length == 0)
        {
            return LightGray;
        }

        return text switch
        {
            _ when text.StartsWith("not ", StringComparison.Ordinal) || Has(text, "incomplete", "undone") => LightGray,
            _ when Has(text, "done", "complete", "closed", "approved", "resolved", "finished", "shipped", "paid", "won") => Green,
            _ when Has(text, "stuck", "blocked", "late", "overdue", "rejected", "fail", "cancel", "lost", "critical") => Red,
            _ when Has(text, "not started", "to do", "todo", "new", "open", "planned", "backlog", "draft") => LightGray,
            _ when Has(text, "progress", "working", "doing", "active", "started", "underway", "in work") => Amber,
            _ when Has(text, "review", "waiting", "pending", "hold", "submitted", "testing") => Blue,
            _ when Has(text, "risk", "attention", "high") => Orange,
            _ => Gray,
        };

        static bool Has(string text, params string[] words) => words.Any(w => text.Contains(w, StringComparison.Ordinal));
    }
}
