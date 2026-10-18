namespace Workbench.Ui.Help;

/// <summary>
/// Which help article explains which page, for the "?" panel. A route matches the page itself and every page below it
/// (time matches time/2026-10-12); the longest matching route wins. Pages with no route of their own fall back to
/// <see cref="Fallback"/>. Add a line here when a new page gets an article.
/// </summary>
public static class HelpRouteMap
{
    public const string Fallback = "getting-around";

    private static readonly (string Route, string Slug)[] Routes =
    [
        ("", "getting-around"),
        ("notifications", "getting-around"),

        ("time", "entering-time"),
        ("time/history", "correcting-time"),
        ("time/team", "approving"),
        ("time/approvals", "approving"),
        ("expenses", "expenses-per-diem"),
        ("leave", "requesting-leave"),
        ("approvals", "approving"),
        ("odcs/approvals", "approving"),
        ("purchasing/approvals", "approving"),

        ("projects", "project-page"),
        ("sheets", "sheets-boards-gantt"),
        ("dashboards", "dashboards"),

        ("accounting/close", "month-end-close"),
        ("accounting/revenue", "revenue-run"),
        ("accounting/incurred-cost", "ice-package"),
        ("billing", "billing-an-invoice"),
        ("purchasing/bills", "paying-bills"),
        ("purchasing/payments", "paying-bills"),
        ("purchasing/match-exceptions", "paying-bills"),
        ("cash", "bank-reconciliation"),

        ("setup", "admin-getting-started"),
        ("setup/people", "users-and-roles"),
        ("setup/approval-workflows", "approval-workflows"),
        ("setup/import", "importing-data"),
        ("admin/users", "users-and-roles"),

        ("support", "getting-help"),
        ("admin/support", "getting-help"),
        ("help", "getting-help"),
    ];

    public static IEnumerable<(string Route, string Slug)> All => Routes;

    /// <summary>The article slug for an app-relative path or a full URL; never null (unknown pages get <see cref="Fallback"/>).</summary>
    public static string SlugFor(string? pathOrUri)
    {
        var segments = Segments(pathOrUri);
        string? best = null;
        var bestLength = -1;
        foreach (var (route, slug) in Routes)
        {
            var routeSegments = Segments(route);
            if (routeSegments.Length > segments.Length || routeSegments.Length <= bestLength)
            {
                continue;
            }

            if (routeSegments.Select((s, i) => string.Equals(s, segments[i], StringComparison.OrdinalIgnoreCase)).All(x => x))
            {
                best = slug;
                bestLength = routeSegments.Length;
            }
        }

        // The home route ("") only answers for the home page itself, not for every page without a route.
        return bestLength == 0 && segments.Length > 0 ? Fallback : best ?? Fallback;
    }

    /// <summary>True when the page has an article of its own (not just the fallback).</summary>
    public static bool HasArticle(string? pathOrUri)
    {
        var segments = Segments(pathOrUri);
        return segments.Length == 0 || Routes.Any(r => Segments(r.Route) is { Length: > 0 } rs && rs.Length <= segments.Length
            && rs.Select((s, i) => string.Equals(s, segments[i], StringComparison.OrdinalIgnoreCase)).All(x => x));
    }

    private static string[] Segments(string? pathOrUri)
    {
        var value = pathOrUri ?? "";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            value = uri.AbsolutePath;
        }

        return value.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
    }
}
