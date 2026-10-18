using MudBlazor;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Projects;
using Workbench.Ui.Financials;

namespace Workbench.Ui.Projects;

/// <summary>Labels, colors and tab slugs for the Projects hub.</summary>
public static class ProjectsFormat
{
    /// <summary>The project page's tabs in order, as URL slugs (<c>/projects/{id}/{slug}</c>) and labels.</summary>
    public static readonly IReadOnlyList<(string Slug, string Label)> Tabs =
    [
        ("overview", "Overview"),
        ("plan", "Plan"),
        ("financials", "Financials"),
        ("budgets", "Budgets"),
        ("revenue", "Revenue"),
        ("evm", "Earned value"),
        ("ceilings", "Ceilings & closeout"),
        ("costs", "Costs"),
        ("team", "Team"),
        ("setup", "Setup"),
    ];

    public static bool Allows(ProjectHubTabsDto tabs, string slug) => slug switch
    {
        "overview" => true,
        "plan" => tabs.Plan,
        "financials" => tabs.Financials,
        "budgets" => tabs.Budgets,
        "revenue" => tabs.Revenue,
        "evm" => tabs.EarnedValue,
        "ceilings" => tabs.Ceilings,
        "costs" => tabs.Costs,
        "team" => tabs.Team,
        "setup" => tabs.Setup,
        _ => false,
    };

    /// <summary>The slug for a route segment, accepting the older names ("odcs" for Costs, "earned-value").</summary>
    public static string Normalize(string? tab) => tab?.Trim().ToLowerInvariant() switch
    {
        null or "" => "overview",
        "odcs" => "costs",
        "earned-value" => "evm",
        "closeout" => "ceilings",
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
