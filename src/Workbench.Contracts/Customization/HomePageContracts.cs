using Workbench.Contracts.Dashboards;

namespace Workbench.Contracts.Customization;

// Role home pages. An administrator designs the home page for a role from dashboard widgets (to-dos, this week, KPI
// tiles, report and list view charts, shortcuts, announcements). A role's home page is an ordinary dashboard linked to the
// role, so it uses the same widgets and widget data. People rearrange and hide widgets on their own home page; a pinned
// widget can't be hidden. Without a home page for any of their roles, people get the built-in home page (to-dos, this
// week, waiting on you, my sheet items).

/// <summary>A widget on someone's home page. <see cref="Hidden"/>: hidden by them (never true for a pinned widget).</summary>
public sealed record HomeWidgetDto(WidgetDto Widget, bool Pinned, bool Hidden);

/// <summary>
/// A home page. <see cref="DashboardId"/> null is the built-in home page (its widgets have fixed ids). For a person,
/// <see cref="Widgets"/> are in their order, hidden ones included (marked).
/// </summary>
/// <param name="RoleKey">The role whose home page this is (null: built-in).</param>
/// <param name="Personalized">The person has rearranged or hidden widgets.</param>
/// <param name="CanManage">The caller may change role home pages.</param>
public sealed record HomePageDto(
    Guid? DashboardId,
    string? RoleKey,
    string Source,
    IReadOnlyList<HomeWidgetDto> Widgets,
    bool Personalized,
    bool CanManage)
{
    public bool IsBuiltIn => DashboardId is null;
}

public sealed record RoleHomePageSummary(string RoleKey, string RoleName, Guid DashboardId, int WidgetCount, DateTime UpdatedAtUtc);

/// <summary>The person's order of widgets (every widget id once) and the ones they hide.</summary>
public sealed record SaveHomePreferencesRequest(IReadOnlyList<Guid> WidgetOrder, IReadOnlyList<Guid> HiddenWidgetIds);

public interface IHomePageService
{
    /// <summary>The signed-in person's home page: their first role with a home page, else the built-in one, with their changes.</summary>
    Task<HomePageDto> GetMineAsync(CancellationToken ct = default);

    /// <summary>Saves the person's order and hidden widgets. Pinned widgets can't be hidden.</summary>
    Task<HomePageDto> SavePreferencesAsync(SaveHomePreferencesRequest request, CancellationToken ct = default);

    /// <summary>Back to the role's layout.</summary>
    Task<HomePageDto> ResetPreferencesAsync(CancellationToken ct = default);

    /// <summary>Data for a widget on the caller's home page (or, for managers, on any role's home page).</summary>
    Task<WidgetDataDto> GetWidgetDataAsync(Guid widgetId, CancellationToken ct = default);

    // Managing role home pages (CustomizationPermissions.ManageLayouts).

    Task<IReadOnlyList<RoleHomePageSummary>> ListRoleHomePagesAsync(CancellationToken ct = default);

    /// <summary>The role's own home page, or the built-in one (unsaved) when the role has none.</summary>
    Task<HomePageDto> GetRoleHomePageAsync(string roleKey, CancellationToken ct = default);

    /// <summary>What a person whose only role is <paramref name="roleKey"/> gets (their role's page, else everyone's, else built-in).</summary>
    Task<HomePageDto> PreviewAsync(string roleKey, CancellationToken ct = default);

    /// <summary>Starts a home page for the role from the built-in widgets. Refused when the role has one.</summary>
    Task<HomePageDto> CreateRoleHomePageAsync(string roleKey, CancellationToken ct = default);

    /// <summary>Removes the role's home page; its people fall back to the next one. Their personal changes for it go too.</summary>
    Task DeleteRoleHomePageAsync(string roleKey, CancellationToken ct = default);

    Task<WidgetDto> AddWidgetAsync(string roleKey, SaveWidgetRequest request, CancellationToken ct = default);

    Task<WidgetDto> UpdateWidgetAsync(string roleKey, Guid widgetId, SaveWidgetRequest request, CancellationToken ct = default);

    Task DeleteWidgetAsync(string roleKey, Guid widgetId, CancellationToken ct = default);

    Task<HomePageDto> ReorderWidgetsAsync(string roleKey, ReorderWidgetsRequest request, CancellationToken ct = default);
}
