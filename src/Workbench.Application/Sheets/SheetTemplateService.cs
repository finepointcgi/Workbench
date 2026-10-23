using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Scheduling;
using Workbench.Domain.Sheets;
using WorkspaceRole = Workbench.Contracts.Identity.WorkspaceRole;

namespace Workbench.Application.Sheets;

/// <summary>The New sheet gallery (built-in and workspace templates), "Save as template" and "Copy sheet".</summary>
public sealed class SheetTemplateService(
    IWorkbenchDbContextFactory dbFactory,
    TimeProvider clock,
    AccessControl access,
    RestrictedColumnAccess restricted,
    SheetCalculator calculator,
    ISheetWorkspacePicker? workspacePicker = null) : ISheetTemplateService
{
    private const string DayPrefix = "day:";

    public async Task<IReadOnlyList<SheetTemplateDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var list = BuiltInSheetTemplates.All
            .Select(t => new SheetTemplateDto(t.Id, t.Name, t.Description, t.Category, true, null, t.Content.SchedulingEnabled,
                t.Content.Columns.Select(c => c.Name).ToList(), t.Content.Rows.Count, null, false))
            .ToList();

        var visible = await access.GetVisibleWorkspaceIdsAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var saved = await db.Set<SheetTemplate>().AsNoTracking()
            .Where(t => visible == null || visible.Contains(t.WorkspaceId))
            .ToListAsync(cancellationToken);
        var workspaces = await db.Workspaces.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, cancellationToken);
        foreach (var template in saved.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(await ToDtoAsync(db, template, workspaces.GetValueOrDefault(template.WorkspaceId), cancellationToken));
        }

        return list;
    }

    public async Task<SheetSummary> CreateSheetAsync(CreateSheetFromTemplateRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        SheetTemplateContent content;
        if (request.TemplateId.StartsWith(BuiltInSheetTemplates.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            content = (BuiltInSheetTemplates.Find(request.TemplateId) ?? throw new WorkbenchNotFoundException($"Template {request.TemplateId} was not found.")).Content;
        }
        else
        {
            if (!Guid.TryParse(request.TemplateId, out var id))
            {
                throw new WorkbenchNotFoundException($"Template {request.TemplateId} was not found.");
            }

            await using var read = await dbFactory.CreateAsync(cancellationToken);
            var template = await read.Set<SheetTemplate>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
            if (template is null || await access.GetWorkspaceRoleAsync(template.WorkspaceId, cancellationToken) is null)
            {
                throw new WorkbenchNotFoundException($"Template {request.TemplateId} was not found.");
            }

            content = template.Content;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var workspace = await PickWorkspaceAsync(db, null, cancellationToken);
        var (sheet, _) = await MaterializeAsync(db, workspace, request.Name, request.Description, content, request.IncludeRows, Today(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Summary(sheet, workspace, request.IncludeRows ? content.Rows.Count : 0);
    }

    public async Task<SheetTemplateDto> SaveAsTemplateAsync(Guid sheetId, SaveSheetTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheet = await db.Sheets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);
        var (content, _) = await BuildContentAsync(db, sheet, request.IncludeRows, relativeDates: true, cancellationToken);
        var template = new SheetTemplate
        {
            WorkspaceId = sheet.WorkspaceId,
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? sheet.Description : request.Description.Trim(),
            CreatedBy = access.Email,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            Content = content,
        };
        db.Set<SheetTemplate>().Add(template);
        await db.SaveChangesAsync(cancellationToken);
        var workspace = await db.Workspaces.Where(w => w.Id == sheet.WorkspaceId).Select(w => w.Name).FirstOrDefaultAsync(cancellationToken);
        return await ToDtoAsync(db, template, workspace, cancellationToken);
    }

    public async Task DeleteAsync(Guid templateId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var template = await db.Set<SheetTemplate>().FirstOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        var role = template is null ? null : await access.GetWorkspaceRoleAsync(template.WorkspaceId, cancellationToken);
        if (template is null || role is null)
        {
            throw WorkbenchNotFoundException.For("Template", templateId);
        }

        if (role != WorkspaceRole.Owner && template.CreatedBy != access.Email)
        {
            throw await access.DenyAsync("Only the person who saved a template, or an owner of its workspace, can delete it.", cancellationToken);
        }

        db.Set<SheetTemplate>().Remove(template);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SheetSummary> CopyAsync(Guid sheetId, CopySheetRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var source = await db.Sheets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);
        var (content, anchor) = await BuildContentAsync(db, source, request.IncludeRows, relativeDates: false, cancellationToken);
        var workspace = await PickWorkspaceAsync(db, source.WorkspaceId, cancellationToken);
        var (sheet, ids) = await MaterializeAsync(db, workspace, request.Name, source.Description, content, request.IncludeRows, anchor, cancellationToken);
        if (request.IncludeRows)
        {
            var dependencies = await db.Set<Dependency>().AsNoTracking().Where(d => d.SheetId == sheetId).ToListAsync(cancellationToken);
            foreach (var d in dependencies)
            {
                if (ids.TryGetValue(d.PredecessorId.ToString(), out var predecessor) && ids.TryGetValue(d.SuccessorId.ToString(), out var successor))
                {
                    db.Set<Dependency>().Add(new Dependency { SheetId = sheet.Id, PredecessorId = predecessor, SuccessorId = successor, Type = d.Type, LagDays = d.LagDays });
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return Summary(sheet, workspace, request.IncludeRows ? content.Rows.Count : 0);
    }

    /// <summary>
    /// The sheet as template content: the columns the user can see, references, and (optionally) rows. Dates are days
    /// from the earliest Start (or Finish); <paramref name="relativeDates"/> also turns date cells into <c>day:N</c>.
    /// </summary>
    private async Task<(SheetTemplateContent Content, DateOnly Anchor)> BuildContentAsync(
        IWorkbenchDbContext db, Sheet sheet, bool includeRows, bool relativeDates, CancellationToken cancellationToken)
    {
        var hidden = await restricted.HiddenColumnsAsync(sheet.Id, cancellationToken);
        var columns = (await db.Columns.AsNoTracking().Where(c => c.SheetId == sheet.Id).OrderBy(c => c.Order).ToListAsync(cancellationToken))
            .Where(c => !hidden.Contains(c.Id)).ToList();
        var content = new SheetTemplateContent
        {
            SchedulingEnabled = sheet.SchedulingEnabled,
            Columns = columns.Select(c => new TemplateColumn
            {
                Key = c.Id.ToString(),
                Name = c.Name,
                Type = c.Type,
                Options = c.Options.ToList(),
                Formula = c.Formula,
                IsRestricted = c.IsRestricted,
            }).ToList(),
            References = (await db.Set<SheetReference>().AsNoTracking().Where(r => r.SheetId == sheet.Id).ToListAsync(cancellationToken))
                .Select(r => new TemplateReference { Name = r.Name, SourceSheetId = r.SourceSheetId, Fields = r.Fields.ToList() }).ToList(),
        };

        var anchor = Today();
        if (!includeRows)
        {
            return (content, anchor);
        }

        var items = await db.Items.AsNoTracking().Where(i => i.SheetId == sheet.Id).ToListAsync(cancellationToken);
        var dates = items.SelectMany(i => new[] { i.Start, i.Finish }).Where(d => d.HasValue).Select(d => d!.Value).ToList();
        if (dates.Count > 0)
        {
            anchor = dates.Min();
        }

        var stored = columns.Where(c => c.Formula is null && c.Type != ColumnType.Lookup).ToDictionary(c => c.Id.ToString(), StringComparer.OrdinalIgnoreCase);
        var computed = new ComputedSheet(sheet.Id, sheet.Name, columns, items, new Dictionary<Guid, ColumnType>(), hidden);
        foreach (var item in computed.Outline())
        {
            content.Rows.Add(new TemplateRow
            {
                Key = item.Id.ToString(),
                ParentKey = item.ParentId?.ToString(),
                Title = item.Title,
                StartDay = item.Start is { } s ? s.DayNumber - anchor.DayNumber : null,
                FinishDay = item.Finish is { } f ? f.DayNumber - anchor.DayNumber : null,
                IsMilestone = item.IsMilestone,
                Values = item.Values
                    .Where(v => stored.ContainsKey(v.Key) && !string.IsNullOrEmpty(v.Value))
                    .ToDictionary(
                        v => stored[v.Key].Id.ToString(),
                        v => relativeDates && stored[v.Key].Type == ColumnType.Date && DateOnly.TryParseExact(v.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                            ? DayPrefix + (d.DayNumber - anchor.DayNumber).ToString(CultureInfo.InvariantCulture)
                            : v.Value),
            });
        }

        return (content, anchor);
    }

    /// <summary>
    /// Adds a sheet built from <paramref name="content"/> to <paramref name="db"/> (not saved). Link columns and references
    /// to sheets the user can't see are left out (with the lookups that follow them). Returns row keys → new item ids.
    /// </summary>
    private async Task<(Sheet Sheet, Dictionary<string, Guid> ItemIds)> MaterializeAsync(
        IWorkbenchDbContext db, Workspace workspace, string name, string? description, SheetTemplateContent content, bool includeRows, DateOnly anchor,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var sheet = new Sheet
        {
            WorkspaceId = workspace.Id,
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            SchedulingEnabled = content.SchedulingEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Sheets.Add(sheet);

        var columnIds = new Dictionary<string, Column>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var source in content.Columns)
        {
            if (source.Type == ColumnType.LinkedRow
                && (source.Options.Count == 0 || !Guid.TryParse(source.Options[0], out var target) || !await calculator.CanSeeSheetAsync(target, cancellationToken)))
            {
                continue;
            }

            var options = source.Options.ToList();
            if (source.Type == ColumnType.Lookup)
            {
                if (options.Count < 2 || !columnIds.TryGetValue(options[0], out var link) || link.Type != ColumnType.LinkedRow)
                {
                    continue;
                }

                options[0] = link.Id.ToString();
            }

            var column = new Column
            {
                SheetId = sheet.Id,
                Name = source.Name,
                Type = source.Type,
                Order = order++,
                Options = options,
                Formula = source.Formula,
                IsRestricted = source.IsRestricted, // only columns the user could see were taken
            };
            columnIds[source.Key] = column;
            db.Columns.Add(column);
        }

        foreach (var reference in content.References)
        {
            if (await calculator.CanSeeSheetAsync(reference.SourceSheetId, cancellationToken))
            {
                db.Set<SheetReference>().Add(new SheetReference
                {
                    SheetId = sheet.Id,
                    Name = reference.Name,
                    SourceSheetId = reference.SourceSheetId,
                    Fields = reference.Fields.ToList(),
                    CreatedBy = access.Email,
                    CreatedAtUtc = now,
                });
            }
        }

        var itemIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (!includeRows)
        {
            return (sheet, itemIds);
        }

        foreach (var row in content.Rows)
        {
            itemIds[row.Key] = Guid.CreateVersion7();
        }

        var siblings = new Dictionary<Guid, int>();
        foreach (var row in content.Rows)
        {
            Guid? parent = row.ParentKey is { } p && itemIds.TryGetValue(p, out var parentId) ? parentId : null;
            var position = siblings.GetValueOrDefault(parent ?? Guid.Empty);
            siblings[parent ?? Guid.Empty] = position + 1;
            var values = new Dictionary<string, string?>();
            foreach (var (key, value) in row.Values)
            {
                if (columnIds.TryGetValue(key, out var column) && column.Formula is null && column.Type != ColumnType.Lookup)
                {
                    values[column.Id.ToString()] = value is not null && value.StartsWith(DayPrefix, StringComparison.Ordinal)
                        && int.TryParse(value[DayPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
                        ? anchor.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : value;
                }
            }

            db.Items.Add(new Item
            {
                Id = itemIds[row.Key],
                SheetId = sheet.Id,
                ParentId = parent,
                Order = position,
                Title = row.Title,
                Start = row.StartDay is { } s ? anchor.AddDays(s) : null,
                Finish = row.FinishDay is { } f ? anchor.AddDays(f) : null,
                IsMilestone = row.IsMilestone,
                Values = values,
            });
        }

        return (sheet, itemIds);
    }

    /// <summary>The preferred workspace when the user can edit it, otherwise the one the picker chooses (it checks Editor).</summary>
    private async Task<Workspace> PickWorkspaceAsync(IWorkbenchDbContext db, Guid? preferred, CancellationToken cancellationToken)
    {
        if (preferred is { } id && await access.GetWorkspaceRoleAsync(id, cancellationToken) is WorkspaceRole.Editor or WorkspaceRole.Owner)
        {
            return await db.Workspaces.FirstAsync(w => w.Id == id, cancellationToken);
        }

        return (workspacePicker is null ? null : await workspacePicker.PickAsync(db, cancellationToken))
            ?? await db.Workspaces.OrderBy(w => w.Name).FirstOrDefaultAsync(cancellationToken)
            ?? db.Workspaces.Add(new Workspace { Name = SheetService.DefaultWorkspaceName }).Entity;
    }

    private async Task<SheetTemplateDto> ToDtoAsync(IWorkbenchDbContext db, SheetTemplate template, string? workspace, CancellationToken cancellationToken)
    {
        var canDelete = template.CreatedBy == access.Email || await access.GetWorkspaceRoleAsync(template.WorkspaceId, cancellationToken) == WorkspaceRole.Owner;
        return new SheetTemplateDto(template.Id.ToString(), template.Name, template.Description ?? "", "Workspace templates", false, workspace,
            template.Content.SchedulingEnabled, template.Content.Columns.Select(c => c.Name).ToList(), template.Content.Rows.Count, template.CreatedBy, canDelete);
    }

    private static SheetSummary Summary(Sheet sheet, Workspace workspace, int items) =>
        new(sheet.Id, sheet.Name, sheet.Description, workspace.Name, items, sheet.SchedulingEnabled, sheet.CostpointProjectId, sheet.UpdatedAtUtc);

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
}
