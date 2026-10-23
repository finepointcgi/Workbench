using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Reports;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Reports;
using Workbench.Contracts.Sheets;
using Workbench.Domain;

namespace Workbench.Application.Sheets;

/// <summary>
/// Saved views of a sheet's grid and sheet exports. A person sees their own views and the sheet's shared views; shared
/// views and the sheet's default need edit access (checked by <c>PermissionCheckingSheetViewService</c>, which also needs
/// the caller to see the sheet). Exports read the sheet through the permission-checked sheet and item services, so they
/// hold only rows and columns the caller may see.
/// </summary>
public sealed class SheetViewService(
    IWorkbenchDbContextFactory dbFactory,
    ICurrentUser currentUser,
    ISheetService sheets,
    IItemService items,
    TimeProvider clock,
    SecurityEventLog? securityLog = null,
    ISheetPermissions? permissions = null) : ISheetViewService
{
    public const string ExportEvent = "export.sheet";
    public const int MaxFilters = 30;
    public const int MaxSorts = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SheetViewsDto> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        var email = Caller();
        var canShare = await CanShareAsync(sheetId, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var views = (await Visible(db, sheetId, email).ToListAsync(cancellationToken))
            .OrderBy(v => v.IsShared ? 0 : 1)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mine = await db.Set<SheetViewPreference>().AsNoTracking()
            .Where(p => p.UserEmail == email && p.SheetId == sheetId)
            .Select(p => (Guid?)p.ViewId)
            .FirstOrDefaultAsync(cancellationToken);
        return new SheetViewsDto(
            sheetId,
            views.Select(v => ToDto(v, email, canShare)).ToList(),
            views.FirstOrDefault(v => v.IsShared && v.IsSheetDefault)?.Id,
            views.Any(v => v.Id == mine) ? mine : null,
            canShare);
    }

    public async Task<SheetViewDto> CreateAsync(Guid sheetId, SaveSheetViewRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var email = Caller();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Sheets.AnyAsync(s => s.Id == sheetId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Sheet", sheetId);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var view = new SheetView { SheetId = sheetId, Name = request.Name.Trim(), OwnerEmail = email, CreatedAtUtc = now };
        Apply(view, request, now);
        db.Set<SheetView>().Add(view);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(view, email, await CanShareAsync(sheetId, cancellationToken));
    }

    public async Task<SheetViewDto> UpdateAsync(Guid sheetId, Guid viewId, SaveSheetViewRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var email = Caller();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var view = await FindAsync(db, sheetId, viewId, email, cancellationToken);
        Apply(view, request, clock.GetUtcNow().UtcDateTime);
        if (!view.IsShared)
        {
            view.IsSheetDefault = false;
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(view, email, await CanShareAsync(sheetId, cancellationToken));
    }

    public async Task DeleteAsync(Guid sheetId, Guid viewId, CancellationToken cancellationToken = default)
    {
        var email = Caller();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var view = await FindAsync(db, sheetId, viewId, email, cancellationToken);
        db.Set<SheetViewPreference>().RemoveRange(await db.Set<SheetViewPreference>().Where(p => p.ViewId == viewId).ToListAsync(cancellationToken));
        db.Set<SheetView>().Remove(view);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetMyDefaultAsync(Guid sheetId, SetDefaultSheetViewRequest request, CancellationToken cancellationToken = default)
    {
        var email = Caller();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (request.ViewId is { } id)
        {
            await FindAsync(db, sheetId, id, email, cancellationToken);
        }

        var preference = await db.Set<SheetViewPreference>().FirstOrDefaultAsync(p => p.UserEmail == email && p.SheetId == sheetId, cancellationToken);
        if (request.ViewId is not { } viewId)
        {
            if (preference is not null)
            {
                db.Set<SheetViewPreference>().Remove(preference);
            }
        }
        else if (preference is null)
        {
            db.Set<SheetViewPreference>().Add(new SheetViewPreference { UserEmail = email, SheetId = sheetId, ViewId = viewId });
        }
        else
        {
            preference.ViewId = viewId;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetSheetDefaultAsync(Guid sheetId, SetDefaultSheetViewRequest request, CancellationToken cancellationToken = default)
    {
        var email = Caller();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (request.ViewId is { } id && !(await FindAsync(db, sheetId, id, email, cancellationToken)).IsShared)
        {
            throw new ArgumentException("Only a shared view can be the sheet's default. Share the view first.", nameof(request));
        }

        foreach (var view in await db.Set<SheetView>().Where(v => v.SheetId == sheetId && (v.IsSheetDefault || v.Id == request.ViewId)).ToListAsync(cancellationToken))
        {
            view.IsSheetDefault = view.Id == request.ViewId;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ReportFileDto> ExportAsync(Guid sheetId, SheetExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Format))
        {
            throw new ArgumentException("Unknown export format.", nameof(request));
        }

        var sheet = await sheets.GetAsync(sheetId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);
        var rows = SheetViewEngine.Apply(await items.ListAsync(sheetId, cancellationToken), sheet.Columns, request.View);
        var fields = ExportFields(sheet, request.View);
        var title = string.IsNullOrWhiteSpace(request.ViewName) ? sheet.Summary.Name : $"{sheet.Summary.Name} - {request.ViewName.Trim()}";
        var generated = clock.GetUtcNow().UtcDateTime;
        var result = new ReportResultDto(
            title,
            Describe(sheet, request.View),
            fields.Select(f => new ReportColumnDto(f.Key, f.Name, f.Type)).ToList(),
            rows.Select(r => new ReportRowDto(ReportRowKind.Detail, r.Depth, fields.Select(f => SheetViewEngine.Value(r.Item, f.Key)).ToList())).ToList(),
            [],
            generated);
        var file = request.Format == SheetExportFormat.Pdf
            ? new ReportFileDto(ReportFiles.FileName(result, "pdf"), "application/pdf", SheetPdf.Write(result, fields.Select(f => f.Kind).ToList()))
            : ReportFiles.Render(result, ReportFileFormat.Xlsx);
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(ExportEvent, sheetId.ToString(), detail: $"{rows.Count} rows, {fields.Count} columns, {request.Format}", cancellationToken: cancellationToken);
        }

        return file;
    }

    /// <summary>The fields an export holds, in grid order: Title, Start and Finish (scheduled sheets), the columns, WBS.</summary>
    private static List<(string Key, string Name, ReportColumnType Type, ColumnKind Kind)> ExportFields(SheetDetail sheet, SheetViewDefinition? view)
    {
        var fields = new List<(string Key, string Name, ReportColumnType Type, ColumnKind Kind)> { (SheetFields.Title, "Title", ReportColumnType.Text, ColumnKind.Text) };
        if (sheet.Summary.SchedulingEnabled)
        {
            fields.Add((SheetFields.Start, "Start", ReportColumnType.Date, ColumnKind.Date));
            fields.Add((SheetFields.Finish, "Finish", ReportColumnType.Date, ColumnKind.Date));
        }

        fields.AddRange(sheet.Columns.OrderBy(c => c.Order).Select(c => (c.Id.ToString(), c.Name, ReportType(c.Type), c.Type)));
        fields.Add((SheetFields.Wbs, "WBS", ReportColumnType.Text, ColumnKind.Text));
        return fields.Where(f => f.Key == SheetFields.Title || view?.IsHidden(f.Key) != true).ToList();
    }

    private static ReportColumnType ReportType(ColumnKind kind) => kind switch
    {
        ColumnKind.Number => ReportColumnType.Number,
        ColumnKind.Currency => ReportColumnType.Money,
        ColumnKind.Date => ReportColumnType.Date,
        ColumnKind.Checkbox => ReportColumnType.Boolean,
        _ => ReportColumnType.Text,
    };

    /// <summary>"Sorted by Status; filtered: Status is Done" under the title, so a file says what it holds.</summary>
    private static string? Describe(SheetDetail sheet, SheetViewDefinition? view)
    {
        if (view is null || (!view.HasSort && !view.HasFilters))
        {
            return null;
        }

        string Name(string field) => field switch
        {
            SheetFields.Title => "Title",
            SheetFields.Start => "Start",
            SheetFields.Finish => "Finish",
            SheetFields.Wbs => "WBS",
            _ => sheet.Columns.FirstOrDefault(c => c.Id.ToString() == field)?.Name ?? field,
        };

        var parts = new List<string>();
        if (view.HasSort)
        {
            parts.Add("Sorted by " + string.Join(", ", view.Sort!.Select(s => Name(s.Field) + (s.Descending ? " (descending)" : ""))));
        }

        if (view.HasFilters)
        {
            parts.Add("Filtered: " + string.Join("; ", view.Filters!.Select(f => $"{Name(f.Field)} {SheetFilterText.Operator(f.Operator)} {string.Join(" or ", SheetViewEngine.Values(f.Value))}".Trim())));
        }

        return string.Join(". ", parts);
    }

    private string Caller()
    {
        var email = currentUser.Email;
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new UnauthorizedAccessException("Sign in to use saved views.");
        }

        return email.Trim().ToLowerInvariant();
    }

    private async Task<bool> CanShareAsync(Guid sheetId, CancellationToken cancellationToken) =>
        permissions is null || await permissions.GetMyRoleAsync(sheetId, cancellationToken) is { } role && role >= WorkspaceRole.Editor;

    /// <summary>The caller's own views and the sheet's shared views.</summary>
    private static IQueryable<SheetView> Visible(IWorkbenchDbContext db, Guid sheetId, string email) =>
        db.Set<SheetView>().AsNoTracking().Where(v => v.SheetId == sheetId && (v.IsShared || v.OwnerEmail == email));

    private static async Task<SheetView> FindAsync(IWorkbenchDbContext db, Guid sheetId, Guid viewId, string email, CancellationToken cancellationToken) =>
        await db.Set<SheetView>().FirstOrDefaultAsync(v => v.Id == viewId && v.SheetId == sheetId && (v.IsShared || v.OwnerEmail == email), cancellationToken)
        ?? throw WorkbenchNotFoundException.For("Sheet view", viewId);

    private static void Apply(SheetView view, SaveSheetViewRequest request, DateTime now)
    {
        view.Name = request.Name.Trim();
        view.IsShared = request.IsShared;
        view.DefinitionJson = JsonSerializer.Serialize(Clean(request.Definition), Json);
        view.UpdatedAtUtc = now;
    }

    internal static SheetViewDefinition Clean(SheetViewDefinition d) => new(
        d.Sort?.Where(s => !string.IsNullOrWhiteSpace(s.Field)).DistinctBy(s => s.Field).ToList() ?? [],
        d.Filters?.Where(f => !string.IsNullOrWhiteSpace(f.Field)).ToList() ?? [],
        d.HiddenColumns?.Where(c => !string.IsNullOrWhiteSpace(c) && c != SheetFields.Title).Distinct(StringComparer.Ordinal).ToList() ?? []);

    private static void Validate(SaveSheetViewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
        {
            throw new ArgumentException("Give the view a name of up to 120 characters.", nameof(request));
        }

        var definition = request.Definition ?? throw new ArgumentException("A view needs a definition.", nameof(request));
        if ((definition.Sort?.Count ?? 0) > MaxSorts)
        {
            throw new ArgumentException($"A view sorts by at most {MaxSorts} fields.", nameof(request));
        }

        if ((definition.Filters?.Count ?? 0) > MaxFilters || definition.Filters?.Any(f => !Enum.IsDefined(f.Operator)) == true)
        {
            throw new ArgumentException($"A view has at most {MaxFilters} filter conditions, each with a known operator.", nameof(request));
        }
    }

    private static SheetViewDto ToDto(SheetView v, string email, bool canShare)
    {
        var mine = string.Equals(v.OwnerEmail, email, StringComparison.OrdinalIgnoreCase);
        return new SheetViewDto(v.Id, v.SheetId, v.Name, v.IsShared, v.OwnerEmail, Read(v.DefinitionJson), mine, v.IsShared ? canShare : mine, v.UpdatedAtUtc);
    }

    internal static SheetViewDefinition Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SheetViewDefinition>(json, Json) ?? SheetViewDefinition.Standard;
        }
        catch (JsonException)
        {
            return SheetViewDefinition.Standard;
        }
    }
}
