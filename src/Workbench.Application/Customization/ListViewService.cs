using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Reports;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Reports;
using Model = Workbench.Domain.Customization;

namespace Workbench.Application.Customization;

/// <summary>
/// Saved list views. A person sees their own views, views shared with everyone and views shared with a role they hold.
/// Sharing (and changing someone else's shared view) is checked by <c>PermissionCheckingListViewService</c>; this service
/// only answers with what the caller may use. Views hold column keys, never data: the list page reads rows through its
/// own permission-checked services and drops columns the viewer can't see.
/// </summary>
public sealed class ListViewService(
    IWorkbenchDbContextFactory dbFactory,
    ICustomizationAccess access,
    SecurityEventLog securityLog,
    TimeProvider clock) : IListViewService
{
    public const string ExportEvent = "export.list_view";
    public const int MaxColumns = 100;
    public const int MaxFilters = 30;
    public const int MaxExportRows = 50_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ListViewsDto> ListAsync(string listKey, CancellationToken ct = default)
    {
        RequireList(listKey);
        var caller = await access.GetCallerAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var views = (await Visible(db, caller).Where(v => v.ListKey == listKey).ToListAsync(ct))
            .OrderBy(v => v.Visibility == Model.ListViewVisibility.Personal ? 1 : 0)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => ToDto(v, caller))
            .ToList();
        var preferred = await db.Set<Model.ListViewPreference>().AsNoTracking()
            .Where(p => p.UserEmail == caller.Email && p.ListKey == listKey)
            .Select(p => p.ViewId)
            .FirstOrDefaultAsync(ct);
        return new ListViewsDto(listKey, views, views.Any(v => v.Id == preferred) ? preferred : null, caller.CanShareViews);
    }

    public async Task<IReadOnlyList<SavedListViewDto>> ListAllAsync(CancellationToken ct = default)
    {
        var caller = await access.GetCallerAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        return (await Visible(db, caller).ToListAsync(ct))
            .OrderBy(v => v.ListKey, StringComparer.Ordinal).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => ToDto(v, caller))
            .ToList();
    }

    public async Task<SavedListViewDto> GetAsync(Guid viewId, CancellationToken ct = default)
    {
        var caller = await access.GetCallerAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var view = await Visible(db, caller).FirstOrDefaultAsync(v => v.Id == viewId, ct)
            ?? throw WorkbenchNotFoundException.For("List view", viewId);
        return ToDto(view, caller);
    }

    public async Task<SavedListViewDto> CreateAsync(SaveListViewRequest request, CancellationToken ct = default)
    {
        Validate(request);
        var caller = await access.GetCallerAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var view = new Model.SavedListView
        {
            ListKey = request.ListKey,
            Name = request.Name.Trim(),
            OwnerEmail = caller.Email,
            CreatedAtUtc = now,
        };
        Apply(view, request, now);
        db.Set<Model.SavedListView>().Add(view);
        await db.SaveChangesAsync(ct);
        return ToDto(view, caller);
    }

    public async Task<SavedListViewDto> UpdateAsync(Guid viewId, SaveListViewRequest request, CancellationToken ct = default)
    {
        Validate(request);
        var caller = await access.GetCallerAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var view = await FindVisibleAsync(db, caller, viewId, ct);
        if (view.ListKey != request.ListKey)
        {
            throw new ArgumentException("A view stays with its list.", nameof(request));
        }

        Apply(view, request, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return ToDto(view, caller);
    }

    public async Task DeleteAsync(Guid viewId, CancellationToken ct = default)
    {
        var caller = await access.GetCallerAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var view = await FindVisibleAsync(db, caller, viewId, ct);
        db.Set<Model.SavedListView>().Remove(view);
        var defaults = await db.Set<Model.ListViewPreference>().Where(p => p.ViewId == viewId).ToListAsync(ct);
        foreach (var preference in defaults)
        {
            preference.ViewId = null;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task SetDefaultAsync(string listKey, SetDefaultListViewRequest request, CancellationToken ct = default)
    {
        RequireList(listKey);
        var caller = await access.GetCallerAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        if (request.ViewId is { } id && !await Visible(db, caller).AnyAsync(v => v.Id == id && v.ListKey == listKey, ct))
        {
            throw WorkbenchNotFoundException.For("List view", id);
        }

        var preference = await db.Set<Model.ListViewPreference>().FirstOrDefaultAsync(p => p.UserEmail == caller.Email && p.ListKey == listKey, ct);
        if (preference is null)
        {
            preference = new Model.ListViewPreference { UserEmail = caller.Email, ListKey = listKey };
            db.Set<Model.ListViewPreference>().Add(preference);
        }

        preference.ViewId = request.ViewId;
        await db.SaveChangesAsync(ct);
    }

    public async Task<ReportFileDto> ExportAsync(ListExportRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireList(request.ListKey);
        if (request.Columns is null || request.Columns.Count == 0)
        {
            throw new ArgumentException("Choose at least one column to export.", nameof(request));
        }

        if (request.Rows.Count > MaxExportRows || request.Rows.Any(r => r.Count != request.Columns.Count))
        {
            throw new ArgumentException($"Each row needs one value per column (at most {MaxExportRows:N0} rows).", nameof(request));
        }

        var title = string.IsNullOrWhiteSpace(request.Title) ? request.ListKey : request.Title.Trim();
        var result = new ReportResultDto(title, null, request.Columns,
            request.Rows.Select(r => new ReportRowDto(ReportRowKind.Detail, 0, r)).ToList(), [], clock.GetUtcNow().UtcDateTime);
        var file = ReportFiles.Render(result, request.Format);
        await securityLog.RecordAsync(ExportEvent, request.ListKey, detail: $"{request.Rows.Count} rows, {request.Columns.Count} columns, {request.Format}", cancellationToken: ct);
        return file;
    }

    /// <summary>Views the caller may use: their own, everyone's, and those shared with a role they hold.</summary>
    private static IQueryable<Model.SavedListView> Visible(IWorkbenchDbContext db, CustomizationCaller caller)
    {
        var roles = caller.RoleKeys.ToList();
        return db.Set<Model.SavedListView>().AsNoTracking().Where(v =>
            v.OwnerEmail == caller.Email
            || v.Visibility == Model.ListViewVisibility.Everyone
            || (v.Visibility == Model.ListViewVisibility.Role && v.RoleKey != null && roles.Contains(v.RoleKey)));
    }

    private static async Task<Model.SavedListView> FindVisibleAsync(IWorkbenchDbContext db, CustomizationCaller caller, Guid viewId, CancellationToken ct)
    {
        var visible = await Visible(db, caller).AnyAsync(v => v.Id == viewId, ct);
        return (visible ? await db.Set<Model.SavedListView>().FirstOrDefaultAsync(v => v.Id == viewId, ct) : null)
            ?? throw WorkbenchNotFoundException.For("List view", viewId);
    }

    private static void Apply(Model.SavedListView view, SaveListViewRequest request, DateTime now)
    {
        view.Name = request.Name.Trim();
        view.Visibility = (Model.ListViewVisibility)request.Visibility;
        view.RoleKey = request.Visibility == ListViewVisibility.Role ? request.RoleKey!.Trim() : null;
        view.DefinitionJson = JsonSerializer.Serialize(Clean(request.Definition), Json);
        view.UpdatedAtUtc = now;
    }

    private static ListViewDefinition Clean(ListViewDefinition d) =>
        new(d.Columns.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).ToList(),
            d.Sort?.Where(s => !string.IsNullOrWhiteSpace(s.Field)).ToList() ?? [],
            d.Filters?.Where(f => !string.IsNullOrWhiteSpace(f.Field)).ToList() ?? [],
            string.IsNullOrWhiteSpace(d.GroupBy) ? null : d.GroupBy,
            string.IsNullOrWhiteSpace(d.Search) ? null : d.Search.Trim());

    private static void Validate(SaveListViewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireList(request.ListKey);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
        {
            throw new ArgumentException("Give the view a name of up to 120 characters.", nameof(request));
        }

        if (!Enum.IsDefined(request.Visibility))
        {
            throw new ArgumentException("Unknown sharing.", nameof(request));
        }

        if (request.Visibility == ListViewVisibility.Role && string.IsNullOrWhiteSpace(request.RoleKey))
        {
            throw new ArgumentException("Pick the role to share the view with.", nameof(request));
        }

        var definition = request.Definition ?? throw new ArgumentException("A view needs columns.", nameof(request));
        if (definition.Columns is null || definition.Columns.Count == 0 || definition.Columns.Count > MaxColumns)
        {
            throw new ArgumentException($"A view shows between 1 and {MaxColumns} columns.", nameof(request));
        }

        if ((definition.Filters?.Count ?? 0) > MaxFilters || definition.Filters?.Any(f => !Enum.IsDefined(f.Operator)) == true)
        {
            throw new ArgumentException($"A view has at most {MaxFilters} filter conditions, each with a known operator.", nameof(request));
        }
    }

    private static void RequireList(string listKey)
    {
        if (!CustomizableLists.All.Contains(listKey))
        {
            throw new ArgumentException($"Unknown list \"{listKey}\".", nameof(listKey));
        }
    }

    private static SavedListViewDto ToDto(Model.SavedListView v, CustomizationCaller caller)
    {
        var mine = string.Equals(v.OwnerEmail, caller.Email, StringComparison.OrdinalIgnoreCase);
        var canEdit = v.Visibility == Model.ListViewVisibility.Personal ? mine : caller.CanShareViews;
        return new SavedListViewDto(v.Id, v.ListKey, v.Name, v.OwnerEmail, (ListViewVisibility)v.Visibility, v.RoleKey, Read(v.DefinitionJson),
            mine, canEdit, v.UpdatedAtUtc);
    }

    internal static ListViewDefinition Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ListViewDefinition>(json, Json) ?? new ListViewDefinition([]);
        }
        catch (JsonException)
        {
            return new ListViewDefinition([]);
        }
    }
}
