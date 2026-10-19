using Workbench.Ui.Navigation;

namespace Workbench.Ui.Help;

/// <summary>Help articles as command-palette results (they rank after pages, projects and sheets of equal relevance).</summary>
public static class HelpPalette
{
    public static IEnumerable<PaletteItem> Items() =>
        HelpLibrary.Ordered().Select(a => new PaletteItem(
            PaletteKind.Help, a.Title, a.Href, PaletteSearch.IconFor(PaletteKind.Help), $"Help · {a.Category}", $"{a.Summary} {a.Keywords}"));
}
