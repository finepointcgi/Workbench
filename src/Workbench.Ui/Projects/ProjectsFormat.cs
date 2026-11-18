using MudBlazor;
using Workbench.Contracts.Modules;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Projects;
using Workbench.Ui.Financials;

namespace Workbench.Ui.Projects;

/// <summary>Labels, colors and tab slugs for the Projects hub.</summary>
public static class ProjectsFormat
{
    /// <summary>
    /// The project page's tabs in order, as URL slugs (<c>/projects/{id}/{slug}</c>) and labels. The first ones follow how a
    /// project is set up: the plan (schedule), the budget by labor category, then staffing people into it.
    /// </summary>
    public static readonly IReadOnlyList<(string Slug, string Label)> Tabs =
    [
        ("overview", "Overview"),
        ("plan", "Plan"),
        ("staffing", "Budget"),
        ("financials", "Financials"),
        ("costs", "Costs"),
        ("schedule-health", "Schedule health"),
        ("setup", "Setup"),
    ];

    /// <summary>Tabs used now and then, kept under "More" until opened.</summary>
    public static bool IsOccasional(string slug) => slug is "schedule-health";

    /// <summary>Whether a link's tab (the part before any "/section", e.g. <c>setup/charge-codes</c>) is open to the viewer (the built-in tabs; see <see cref="ProjectTabSet"/> for those a module adds).</summary>
    public static bool AllowsLink(ProjectHubTabsDto tabs, string link) => Allows(tabs, Normalize(link.Split('/')[0]));

    public static bool Allows(ProjectHubTabsDto tabs, string slug) => slug switch
    {
        "overview" => true,
        "plan" => tabs.Plan,
        "schedule-health" => tabs.Plan,
        "financials" => tabs.Financials,
        "budgets" => tabs.Budgets,
        "costs" => tabs.Costs,
        "staffing" => tabs.Team || tabs.Budgets, // the budget, staffing against it, and who charges to the project
        "setup" => tabs.Setup,
        _ => false,
    };

    /// <summary>The slug for a route segment, accepting the older names ("odcs" for Costs, "earned-value", "team" for Staffing).</summary>
    public static string Normalize(string? tab) => tab?.Trim().ToLowerInvariant() switch
    {
        null or "" => "overview",
        "odcs" => "costs",
        "earned-value" => "evm",
        "closeout" => "ceilings",
        "health" or "dcma" => "schedule-health",
        "team" => "staffing",
        "budget" or "budgets" => "staffing", // Budget and Staffing are one tab
        var t => t,
    };

    public static Color HealthColor(ProjectHealth health) => health switch
    {
        ProjectHealth.OnTrack => Color.Success,
        ProjectHealth.AtRisk => Color.Warning,
        ProjectHealth.Over => Color.Error,
        _ => Color.Default,
    };

    public static string Status(ProjectState state) => state switch
    {
        ProjectState.OnHold => "On hold",
        _ => state.ToString(),
    };

    public static Color StatusColor(ProjectState state) => state switch
    {
        ProjectState.Active => Color.Primary,
        ProjectState.OnHold => Color.Warning,
        ProjectState.Closed => Color.Dark,
        _ => Color.Default,
    };

    public static string Period(DateOnly? start, DateOnly? finish) => (start, finish) switch
    {
        (null, null) => "—",
        ({ } s, null) => $"From {Date(s)}",
        (null, { } f) => $"Until {Date(f)}",
        ({ } s, { } f) => $"{Date(s)} – {Date(f)}",
    };

    public static string Date(DateOnly date) => date.ToString("MMM d, yyyy", FinancialFormat.Culture);

    public static Severity Severity(ProjectAlertLevel level) => level switch
    {
        ProjectAlertLevel.Critical => MudBlazor.Severity.Error,
        ProjectAlertLevel.Warning => MudBlazor.Severity.Warning,
        _ => MudBlazor.Severity.Info,
    };

    /// <summary>A 0–100 value for a progress bar.</summary>
    public static double Bar(decimal? ratio) => ratio is { } r ? (double)Math.Clamp(r * 100m, 0m, 100m) : 0;

    public static Color BurnColor(decimal? ratio) => ratio > 1m ? Color.Error : ratio > 0.9m ? Color.Warning : Color.Primary;
}

/// <summary>One tab of the project page: a built-in one, or one a license module contributes (<see cref="Component"/> is then its content).</summary>
public sealed record ProjectTab(string Slug, string Label, bool Occasional, Func<ProjectHubTabsDto, bool> Allows, Type? Component);

/// <summary>The project page's tabs in order: the built-in ones and the tabs of the license modules loaded (<see cref="ProjectHubTabContribution"/>).</summary>
public sealed class ProjectTabSet
{
    private static readonly IReadOnlyDictionary<string, int> BuiltInOrder = new Dictionary<string, int>
    {
        ["overview"] = 10, ["plan"] = 20, ["staffing"] = 30, ["financials"] = 40, ["costs"] = 50, ["schedule-health"] = 80, ["setup"] = 200,
    };

    public ProjectTabSet(IEnumerable<ProjectHubTabContribution>? contributions = null)
    {
        var tabs = ProjectsFormat.Tabs
            .Select(t => (Order: BuiltInOrder[t.Slug], Tab: new ProjectTab(t.Slug, t.Label, ProjectsFormat.IsOccasional(t.Slug), tabs => ProjectsFormat.Allows(tabs, t.Slug), null)))
            .ToList();
        foreach (var c in contributions ?? [])
        {
            if (tabs.All(t => t.Tab.Slug != c.Slug))
            {
                tabs.Add((c.Order, new ProjectTab(c.Slug, c.Label, c.Occasional, c.Allows, c.Component)));
            }
        }

        All = tabs.OrderBy(t => t.Order).Select(t => t.Tab).ToList();
    }

    public IReadOnlyList<ProjectTab> All { get; }

    public ProjectTab? Find(string slug) => All.FirstOrDefault(t => t.Slug == slug);

    public string Label(string slug) => Find(slug)?.Label ?? "";

    public bool Allows(ProjectHubTabsDto tabs, string slug) => Find(slug)?.Allows(tabs) ?? false;

    public bool IsOccasional(string slug) => Find(slug)?.Occasional ?? false;
}
