using MudBlazor;

namespace Workbench.Ui.Navigation;

public enum PaletteKind
{
    Recent,
    Page,
    Project,
    Sheet,
    Dashboard,
    Help,
}

/// <summary>One result in the command palette.</summary>
public sealed record PaletteItem(PaletteKind Kind, string Title, string Href, string Icon, string? Subtitle = null, string? Keywords = null);

/// <summary>Ranks command-palette results. Pure, so it is easy to test.</summary>
public static class PaletteSearch
{
    public const int MaxResults = 12;

    /// <summary>
    /// The pages a user may open, as palette items (subtitle: where they live in the menu). Entries outside the menu
    /// that only name records (breadcrumb parents such as billing/statements) aren't pages of their own.
    /// </summary>
    public static IEnumerable<PaletteItem> Pages(NavAccess access) =>
        NavCatalog.PaletteEntries(access)
            .Where(e => e.InMenu || e.Detail is null)
            .GroupBy(e => e.Href)
            .Select(g => g.First())
            .Select(e => new PaletteItem(PaletteKind.Page, e.Title, e.Href, e.Icon, Where(e, access), e.Keywords));

    private static string Where(NavEntry entry, NavAccess access)
    {
        var section = NavCatalog.Sections.First(s => s.Key == entry.Section);
        var title = NavCatalog.TitleFor(section, access);
        return entry.Group is null ? title : $"{title} › {entry.Group}";
    }

    /// <summary>
    /// Every word of <paramref name="query"/> must appear in the title, subtitle or keywords. Title matches rank first
    /// (whole title, then start, then start of a word); recent items break ties. A blank query lists recent items, then pages.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Search(string? query, IEnumerable<PaletteItem> items, int take = MaxResults)
    {
        var all = items.ToList();
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return all.Where(i => i.Kind == PaletteKind.Recent)
                .Concat(all.Where(i => i.Kind == PaletteKind.Page))
                .DistinctBy(i => i.Href)
                .Take(take)
                .ToList();
        }

        var q = string.Join(' ', words);
        return all
            .Select(item => (item, score: Score(item, q, words)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.item.Kind)
            .ThenBy(x => x.item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.item)
            .DistinctBy(i => i.Href)
            .Take(take)
            .ToList();
    }

    private static int Score(PaletteItem item, string query, string[] words)
    {
        var haystack = $"{item.Title} {item.Subtitle} {item.Keywords}";
        if (!words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        var title = item.Title;
        var score = 1;
        if (title.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            score = 100;
        }
        else if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            score = 80;
        }
        else if (title.Split(' ', '-', '/').Any(part => part.StartsWith(words[0], StringComparison.OrdinalIgnoreCase)))
        {
            score = 60;
        }
        else if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score = 40;
        }
        else if (words.All(w => title.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            score = 30;
        }

        return score + (item.Kind == PaletteKind.Recent ? 5 : 0) + (item.Kind == PaletteKind.Page ? 2 : 0);
    }

    public static string IconFor(PaletteKind kind) => kind switch
    {
        PaletteKind.Recent => Icons.Material.Filled.History,
        PaletteKind.Project => Icons.Material.Filled.Work,
        PaletteKind.Sheet => Icons.Material.Filled.TableChart,
        PaletteKind.Dashboard => Icons.Material.Filled.Dashboard,
        PaletteKind.Help => Icons.Material.Filled.HelpOutline,
        _ => Icons.Material.Filled.Article,
    };
}
