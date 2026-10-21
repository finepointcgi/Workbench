using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Dashboards;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Dashboards;
using Workbench.Domain.Customization;
using Model = Workbench.Domain.Dashboards;

namespace Workbench.Application.Customization;

/// <summary>
/// Role home pages: each is an ordinary dashboard (same widgets, same widget data) linked to a role by
/// <see cref="RoleHomePage"/>. Widget edits go through <see cref="DashboardService"/> itself, so its validation applies.
/// Who may manage them is checked by <c>PermissionCheckingHomePageService</c>; reading your own is for everyone.
/// </summary>
public sealed class HomePageService(
    IWorkbenchDbContextFactory dbFactory,
    ICustomizationAccess access,
    ICurrentUser user,
    TimeProvider clock,
    IServiceProvider services) : IHomePageService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The built-in home page as widgets (fixed ids, so people can rearrange it too).</summary>
    public static IReadOnlyList<WidgetDto> BuiltInWidgets { get; } =
    [
        Fixed(1, WidgetType.MyTodo, "To do", 8, WidgetHeight.Large),
        Fixed(2, WidgetType.ThisWeek, "This week", 4, WidgetHeight.Medium),
        Fixed(3, WidgetType.WaitingOnYou, "Waiting on you", 4, WidgetHeight.Medium),
        Fixed(4, WidgetType.MySheetItems, "My sheet items", 12, WidgetHeight.Medium),
    ];

    private DashboardService Dashboards => new(dbFactory, user, clock);

    public async Task<HomePageDto> GetMineAsync(CancellationToken ct = default)
    {
        var caller = await access.GetCallerAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var (roleKey, dashboard) = await ForRolesAsync(db, caller.RoleKeys, ct);
        var widgets = dashboard is null ? BuiltInWidgets : WidgetsOf(dashboard);
        var preference = await db.Set<HomePagePreference>().AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserEmail == caller.Email && p.DashboardId == (dashboard == null ? Guid.Empty : dashboard.Id), ct);
        return Page(dashboard?.Id, roleKey, widgets, preference, caller.CanManageLayouts);
    }

    public async Task<HomePageDto> SavePreferencesAsync(SaveHomePreferencesRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = await access.GetCallerAsync(ct);
        var mine = await GetMineAsync(ct);
        var ids = mine.Widgets.Select(w => w.Widget.Id).ToHashSet();
        var order = (request.WidgetOrder ?? []).Distinct().ToList();
        if (order.Count != ids.Count || !order.All(ids.Contains))
        {
            throw new ArgumentException("List every widget on your home page exactly once.", nameof(request));
        }

        var pinned = mine.Widgets.Where(w => w.Pinned).Select(w => w.Widget.Id).ToHashSet();
        var hidden = (request.HiddenWidgetIds ?? []).Distinct().ToList();
        if (hidden.Any(id => !ids.Contains(id)))
        {
            throw new ArgumentException("That widget isn't on your home page.", nameof(request));
        }

        if (hidden.Any(pinned.Contains))
        {
            throw new ArgumentException("Your administrator pinned that widget, so it can't be hidden.", nameof(request));
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var key = mine.DashboardId ?? Guid.Empty;
        var preference = await db.Set<HomePagePreference>().FirstOrDefaultAsync(p => p.UserEmail == caller.Email && p.DashboardId == key, ct);
        if (preference is null)
        {
            preference = new HomePagePreference { UserEmail = caller.Email, DashboardId = key };
            db.Set<HomePagePreference>().Add(preference);
        }

        preference.OrderJson = JsonSerializer.Serialize(order, Json);
        preference.HiddenJson = JsonSerializer.Serialize(hidden, Json);
        preference.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return await GetMineAsync(ct);
    }

    public async Task<HomePageDto> ResetPreferencesAsync(CancellationToken ct = default)
    {
        var caller = await access.GetCallerAsync(ct);
        var mine = await GetMineAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var key = mine.DashboardId ?? Guid.Empty;
        db.Set<HomePagePreference>().RemoveRange(await db.Set<HomePagePreference>().Where(p => p.UserEmail == caller.Email && p.DashboardId == key).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return await GetMineAsync(ct);
    }

    public async Task<WidgetDataDto> GetWidgetDataAsync(Guid widgetId, CancellationToken ct = default)
    {
        var widget = (await GetMineAsync(ct)).Widgets.Select(w => w.Widget).FirstOrDefault(w => w.Id == widgetId);
        if (widget is null && (await access.GetCallerAsync(ct)).CanManageLayouts)
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var homeDashboards = db.Set<RoleHomePage>().Select(h => h.DashboardId);
            var row = await db.Set<Model.Widget>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == widgetId && homeDashboards.Contains(w.DashboardId), ct);
            widget = row is null ? BuiltInWidgets.FirstOrDefault(w => w.Id == widgetId) : DashboardService.ToDto(row);
        }

        if (widget is null)
        {
            throw WorkbenchNotFoundException.For("Widget", widgetId);
        }

        return await ActivatorUtilities.CreateInstance<WidgetDataService>(services).ComputeAsync(widget, ct);
    }

    public async Task<IReadOnlyList<RoleHomePageSummary>> ListRoleHomePagesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var rows = await (from h in db.Set<RoleHomePage>().AsNoTracking()
                          join d in db.Set<Model.Dashboard>().AsNoTracking() on h.DashboardId equals d.Id
                          select new { h.RoleKey, d.Id, Count = d.Widgets.Count, d.UpdatedAtUtc }).ToListAsync(ct);
        var roles = await access.ListRolesAsync(ct);
        return rows
            .OrderBy(r => roles.ToList().FindIndex(x => x.Key == r.RoleKey) is var i and >= 0 ? i : int.MaxValue)
            .Select(r => new RoleHomePageSummary(r.RoleKey, roles.FirstOrDefault(x => x.Key == r.RoleKey)?.Name ?? r.RoleKey, r.Id, r.Count, r.UpdatedAtUtc))
            .ToList();
    }

    public async Task<HomePageDto> GetRoleHomePageAsync(string roleKey, CancellationToken ct = default)
    {
        await RequireRoleAsync(roleKey, ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var dashboard = await FindForRoleAsync(db, roleKey, ct);
        return Page(dashboard?.Id, dashboard is null ? null : roleKey, dashboard is null ? BuiltInWidgets : WidgetsOf(dashboard), null, true);
    }

    public async Task<HomePageDto> PreviewAsync(string roleKey, CancellationToken ct = default)
    {
        await RequireRoleAsync(roleKey, ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var (key, dashboard) = await ForRolesAsync(db, [roleKey, CustomizationRoles.Everyone], ct);
        return Page(dashboard?.Id, key, dashboard is null ? BuiltInWidgets : WidgetsOf(dashboard), null, true);
    }

    public async Task<HomePageDto> CreateRoleHomePageAsync(string roleKey, CancellationToken ct = default)
    {
        await RequireRoleAsync(roleKey, ct);
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            if (await db.Set<RoleHomePage>().AnyAsync(h => h.RoleKey == roleKey, ct))
            {
                throw new ArgumentException($"{CustomizationRoles.NameOf(roleKey)} already has a home page.", nameof(roleKey));
            }
        }

        var dashboards = Dashboards;
        var created = await dashboards.CreateAsync(new CreateDashboardRequest($"Home · {CustomizationRoles.NameOf(roleKey)}", "Role home page"), ct);
        foreach (var widget in BuiltInWidgets)
        {
            await dashboards.AddWidgetAsync(created.Id, new SaveWidgetRequest(widget.Title, widget.Type, widget.Width, widget.Height, widget.Config), ct);
        }

        await using (var db = await dbFactory.CreateAsync(ct))
        {
            db.Set<RoleHomePage>().Add(new RoleHomePage { RoleKey = roleKey, DashboardId = created.Id });
            await db.SaveChangesAsync(ct);
        }

        return await GetRoleHomePageAsync(roleKey, ct);
    }

    public async Task DeleteRoleHomePageAsync(string roleKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var link = await db.Set<RoleHomePage>().FirstOrDefaultAsync(h => h.RoleKey == roleKey, ct)
            ?? throw new WorkbenchNotFoundException($"{CustomizationRoles.NameOf(roleKey)} has no home page.");
        db.Set<HomePagePreference>().RemoveRange(await db.Set<HomePagePreference>().Where(p => p.DashboardId == link.DashboardId).ToListAsync(ct));
        db.Set<RoleHomePage>().Remove(link);
        await db.SaveChangesAsync(ct);
        await Dashboards.DeleteAsync(link.DashboardId, ct);
    }

    public async Task<WidgetDto> AddWidgetAsync(string roleKey, SaveWidgetRequest request, CancellationToken ct = default) =>
        await Dashboards.AddWidgetAsync(await RequireDashboardAsync(roleKey, ct), request, ct);

    public async Task<WidgetDto> UpdateWidgetAsync(string roleKey, Guid widgetId, SaveWidgetRequest request, CancellationToken ct = default) =>
        await Dashboards.UpdateWidgetAsync(await RequireDashboardAsync(roleKey, ct), widgetId, request, ct);

    public async Task DeleteWidgetAsync(string roleKey, Guid widgetId, CancellationToken ct = default) =>
        await Dashboards.DeleteWidgetAsync(await RequireDashboardAsync(roleKey, ct), widgetId, ct);

    public async Task<HomePageDto> ReorderWidgetsAsync(string roleKey, ReorderWidgetsRequest request, CancellationToken ct = default)
    {
        await Dashboards.ReorderWidgetsAsync(await RequireDashboardAsync(roleKey, ct), request, ct);
        return await GetRoleHomePageAsync(roleKey, ct);
    }

    private async Task RequireRoleAsync(string roleKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(roleKey) || !(await access.ListRolesAsync(ct)).Any(r => r.Key == roleKey))
        {
            throw new ArgumentException($"There is no role \"{roleKey}\".", nameof(roleKey));
        }
    }

    private async Task<Guid> RequireDashboardAsync(string roleKey, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<RoleHomePage>().Where(h => h.RoleKey == roleKey).Select(h => (Guid?)h.DashboardId).FirstOrDefaultAsync(ct)
            ?? throw new WorkbenchNotFoundException($"{CustomizationRoles.NameOf(roleKey)} has no home page yet: create one first.");
    }

    /// <summary>The first of the roles that has a home page (roles most specific first, everyone last).</summary>
    private static async Task<(string? RoleKey, Model.Dashboard? Dashboard)> ForRolesAsync(IWorkbenchDbContext db, IReadOnlyList<string> roleKeys, CancellationToken ct)
    {
        var links = await db.Set<RoleHomePage>().AsNoTracking().Where(h => roleKeys.Contains(h.RoleKey)).ToListAsync(ct);
        foreach (var role in roleKeys)
        {
            if (links.FirstOrDefault(l => l.RoleKey == role) is { } link)
            {
                var dashboard = await db.Set<Model.Dashboard>().AsNoTracking().Include(d => d.Widgets).FirstOrDefaultAsync(d => d.Id == link.DashboardId, ct);
                if (dashboard is not null)
                {
                    return (role, dashboard);
                }
            }
        }

        return (null, null);
    }

    private static async Task<Model.Dashboard?> FindForRoleAsync(IWorkbenchDbContext db, string roleKey, CancellationToken ct)
    {
        var id = await db.Set<RoleHomePage>().Where(h => h.RoleKey == roleKey).Select(h => (Guid?)h.DashboardId).FirstOrDefaultAsync(ct);
        return id is null ? null : await db.Set<Model.Dashboard>().AsNoTracking().Include(d => d.Widgets).FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    private static IReadOnlyList<WidgetDto> WidgetsOf(Model.Dashboard dashboard) =>
        dashboard.Widgets.OrderBy(w => w.Order).Select(DashboardService.ToDto).ToList();

    /// <summary>The widgets in the person's order (new widgets after theirs, in the role's order), hidden ones marked.</summary>
    internal static HomePageDto Page(Guid? dashboardId, string? roleKey, IReadOnlyList<WidgetDto> widgets, HomePagePreference? preference, bool canManage)
    {
        var order = preference is null ? [] : Read(preference.OrderJson);
        var hidden = preference is null ? [] : Read(preference.HiddenJson).ToHashSet();
        var ordered = order.Select(id => widgets.FirstOrDefault(w => w.Id == id)).OfType<WidgetDto>()
            .Concat(widgets.Where(w => !order.Contains(w.Id)))
            .Select(w => new HomeWidgetDto(w, w.Config.Pinned, !w.Config.Pinned && hidden.Contains(w.Id)))
            .ToList();
        var source = roleKey is null ? "Built-in" : CustomizationRoles.NameOf(roleKey);
        return new HomePageDto(dashboardId, roleKey, source, ordered, preference is not null, canManage);
    }

    private static List<Guid> Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static WidgetDto Fixed(int n, WidgetType type, string title, int width, WidgetHeight height) =>
        new(new Guid($"00000000-0000-0000-0000-{n:D12}"), Guid.Empty, type, title, n - 1, width, height, new WidgetConfig());
}
