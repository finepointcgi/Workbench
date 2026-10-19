using System.Reflection;
using Workbench.Ui.Help.Markdown;

namespace Workbench.Ui.Help;

/// <summary>One help center article, from <c>Help/Articles/{slug}.md</c> (front matter: title, summary, category, audience, keywords, order).</summary>
public sealed record HelpArticle(
    string Slug,
    string Title,
    string Summary,
    string Category,
    string Audience,
    string Keywords,
    int Order,
    MarkdownDocument Document)
{
    public string Href => $"help/{Slug}";

    /// <summary>The article's text without markup, lower-cased once for search.</summary>
    internal string SearchText { get; } = $"{Title} {Summary} {Keywords} {Document.PlainText()}".ToLowerInvariant();
}

/// <summary>One "What's new" entry: a <c>## version · date</c> heading in <c>Help/WhatsNew.md</c> and what follows it.</summary>
public sealed record WhatsNewEntry(string Version, string Date, MarkdownDocument Document);

/// <summary>
/// The help articles and the What's new list shipped inside this assembly. Read once and never changed, so they are
/// safe to share between users and tenants.
/// </summary>
public static class HelpLibrary
{
    public static readonly string[] Categories =
        ["Getting started", "Time and expenses", "Approvals and projects", "Sheets and dashboards", "Finance", "Administration"];

    private static readonly Lazy<IReadOnlyList<HelpArticle>> LazyArticles = new(LoadArticles);
    private static readonly Lazy<IReadOnlyList<WhatsNewEntry>> LazyWhatsNew = new(() => ParseWhatsNew(Read("Workbench.Ui.Help.WhatsNew.md") ?? ""));

    public static IReadOnlyList<HelpArticle> Articles => LazyArticles.Value;

    public static IReadOnlyList<WhatsNewEntry> WhatsNew => LazyWhatsNew.Value;

    /// <summary>The newest What's new version, or null when there are no entries.</summary>
    public static string? LatestVersion => WhatsNew.Count > 0 ? WhatsNew[0].Version : null;

    public static HelpArticle? Find(string? slug) =>
        slug is null ? null : Articles.FirstOrDefault(a => string.Equals(a.Slug, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Articles containing every word of <paramref name="query"/> (title, summary, keywords or text). Title matches rank
    /// first, then summary and keyword matches; ties keep the help center's order. A blank query returns nothing.
    /// </summary>
    public static IReadOnlyList<HelpArticle> Search(string? query, int take = 20)
    {
        var words = (query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return [];
        }

        return Articles
            .Where(a => words.All(w => a.SearchText.Contains(w, StringComparison.Ordinal)))
            .Select(a => (a, score: words.Sum(w => Score(a, w))))
            .OrderByDescending(x => x.score)
            .ThenBy(x => Array.IndexOf(Categories, x.a.Category))
            .ThenBy(x => x.a.Order)
            .Select(x => x.a)
            .Take(take)
            .ToList();
    }

    /// <summary>Articles in help center order: by category, then their own order.</summary>
    public static IEnumerable<HelpArticle> Ordered() =>
        Articles.OrderBy(a => Array.IndexOf(Categories, a.Category) is var i and >= 0 ? i : int.MaxValue).ThenBy(a => a.Order).ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase);

    private static int Score(HelpArticle article, string word) =>
        (article.Title.Contains(word, StringComparison.OrdinalIgnoreCase) ? 10 : 0)
        + (article.Keywords.Contains(word, StringComparison.OrdinalIgnoreCase) ? 4 : 0)
        + (article.Summary.Contains(word, StringComparison.OrdinalIgnoreCase) ? 3 : 0)
        + 1;

    /// <summary>Parses one article. Public for tests; the slug comes from the file name.</summary>
    public static HelpArticle ParseArticle(string slug, string markdown)
    {
        var document = MarkdownDocument.Parse(markdown);
        string Get(string key, string fallback = "") => document.FrontMatter.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;
        return new HelpArticle(
            slug,
            Get("title", slug),
            Get("summary"),
            Get("category", "Getting started"),
            Get("audience", "everyone"),
            Get("keywords"),
            int.TryParse(Get("order", "100"), out var order) ? order : 100,
            document);
    }

    /// <summary>Splits the What's new file at its <c>## version · date</c> headings. Public for tests.</summary>
    public static IReadOnlyList<WhatsNewEntry> ParseWhatsNew(string markdown)
    {
        var entries = new List<WhatsNewEntry>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var start = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            start = Array.FindIndex(lines, 1, l => l.Trim() == "---") + 1;
        }

        string? heading = null;
        var body = new List<string>();
        void Flush()
        {
            if (heading is null)
            {
                return;
            }

            var parts = heading.Split('·', 2, StringSplitOptions.TrimEntries);
            var version = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (version.Length is > 0 and <= 32)
            {
                entries.Add(new WhatsNewEntry(version, parts.Length > 1 ? parts[1] : "", MarkdownDocument.Parse(string.Join('\n', body))));
            }
        }

        foreach (var line in lines.Skip(start))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                heading = line[3..].Trim();
                body.Clear();
            }
            else
            {
                body.Add(line);
            }
        }

        Flush();
        return entries;
    }

    private static IReadOnlyList<HelpArticle> LoadArticles()
    {
        const string prefix = "Workbench.Ui.Help.Articles.";
        return typeof(HelpLibrary).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal))
            .Select(n => ParseArticle(n[prefix.Length..^3], Read(n) ?? ""))
            .ToList();
    }

    private static string? Read(string resource)
    {
        using var stream = typeof(HelpLibrary).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>The running app's version, for support reports and the What's new panel.</summary>
public static class AppInfo
{
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        // "1.0.0+3f2c1d9e..." → "1.0.0+3f2c1d9" (the commit is useful to support, its full length isn't).
        var plus = version.IndexOf('+');
        if (plus >= 0 && version.Length > plus + 8)
        {
            version = version[..(plus + 8)];
        }

        return version.Length <= 64 ? version : version[..64];
    }
}
