using Workbench.Contracts.Sheets;

namespace Workbench.Application.Sheets;

/// <summary>Keeps a Dropdown column's status label colors in step with its options.</summary>
internal static class StatusLabels
{
    /// <summary>One known palette key per option: the given one where it's valid, else the suggestion for the label.</summary>
    public static List<string> Colors(IReadOnlyList<string> options, IReadOnlyList<string> colors) =>
        options.Select((option, i) => (i < colors.Count ? StatusColors.Find(colors[i]) : null) ?? StatusColors.Suggest(option)).Select(c => c.Key).ToList();

    /// <summary>The old colors carried over to the new options by label (when a request doesn't send colors).</summary>
    public static IReadOnlyList<string> ByName(IReadOnlyList<string> oldOptions, IReadOnlyList<string> oldColors, IReadOnlyList<string> newOptions) =>
        newOptions.Select(option =>
        {
            var i = oldOptions.ToList().FindIndex(o => string.Equals(o, option, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i < oldColors.Count ? oldColors[i] : string.Empty;
        }).ToList();
}
