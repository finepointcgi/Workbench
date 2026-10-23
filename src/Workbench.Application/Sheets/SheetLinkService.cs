using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using WorkspaceRole = Workbench.Contracts.Identity.WorkspaceRole;

namespace Workbench.Application.Sheets;

/// <summary>Link and lookup columns, read as the current user: sheets they can't see show nothing.</summary>
public sealed class SheetLinkService(
    IWorkbenchDbContextFactory dbFactory, AccessControl access, RestrictedColumnAccess restricted, SheetCalculator calculator) : ISheetLinkService
{
    /// <summary>The most rows of a linked sheet offered for picking.</summary>
    public const int MaxRows = 5_000;

    public async Task<SheetLinksDto> GetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        var hidden = await restricted.HiddenColumnsAsync(sheetId, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var columns = await db.Columns.AsNoTracking()
            .Where(c => c.SheetId == sheetId && (c.Type == ColumnType.LinkedRow || c.Type == ColumnType.Lookup))
            .ToListAsync(cancellationToken);
        columns = columns.Where(c => !hidden.Contains(c.Id)).ToList();
        if (columns.Count == 0)
        {
            return SheetLinksDto.Empty;
        }

        var links = new List<LinkColumnDto>();
        var targets = new Dictionary<Guid, ComputedSheet?>();
        foreach (var link in columns.Where(c => c.Type == ColumnType.LinkedRow))
        {
            if (SheetLinks.TargetOf(link) is not { } targetId)
            {
                continue;
            }

            if (!targets.TryGetValue(targetId, out var target))
            {
                target = targets[targetId] = await calculator.ReadVisibleAsync(db, targetId, cancellationToken);
            }

            links.Add(target is null
                ? new LinkColumnDto(link.Id, targetId, null, false, [])
                : new LinkColumnDto(link.Id, targetId, target.Name, true,
                    target.Outline().Take(MaxRows).Select(i => new LinkableRowDto(i.Id, i.Title, i.ParentId)).ToList()));
        }

        var lookups = new List<LookupColumnDto>();
        foreach (var lookup in columns.Where(c => c.Type == ColumnType.Lookup))
        {
            if (SheetLinks.LookupOf(lookup) is not { } spec)
            {
                continue;
            }

            var target = links.FirstOrDefault(l => l.ColumnId == spec.LinkColumnId) is { } l ? targets.GetValueOrDefault(l.TargetSheetId) : null;
            lookups.Add(new LookupColumnDto(lookup.Id, spec.LinkColumnId, spec.Field, target?.NameOf(spec.Field)));
        }

        return new SheetLinksDto(links, lookups);
    }

    public async Task<IReadOnlyList<LinkableSheetDto>> ListSheetsAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var visible = await access.GetVisibleWorkspaceIdsAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheets = await db.Sheets.AsNoTracking()
            .Where(s => visible == null || visible.Contains(s.WorkspaceId))
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, Workspace = s.Workspace!.Name, s.SchedulingEnabled })
            .ToListAsync(cancellationToken);
        var ids = sheets.Select(s => s.Id).ToList();
        var columns = (await db.Columns.AsNoTracking().Where(c => ids.Contains(c.SheetId)).ToListAsync(cancellationToken))
            .ToLookup(c => c.SheetId);

        var result = new List<LinkableSheetDto>(sheets.Count);
        foreach (var sheet in sheets)
        {
            var hidden = columns[sheet.Id].Any(c => c.IsRestricted) ? await restricted.HiddenColumnsAsync(sheet.Id, cancellationToken) : new HashSet<Guid>();
            var fields = new List<SheetFieldDto> { new("title", "Title", ColumnKind.Text) };
            if (sheet.SchedulingEnabled)
            {
                fields.Add(new("start", "Start", ColumnKind.Date));
                fields.Add(new("finish", "Finish", ColumnKind.Date));
            }

            fields.AddRange(columns[sheet.Id].Where(c => !hidden.Contains(c.Id)).OrderBy(c => c.Order)
                .Select(c => new SheetFieldDto(c.Id.ToString(), c.Name, c.Type.ToContract())));
            result.Add(new LinkableSheetDto(sheet.Id, sheet.Name, sheet.Workspace, fields));
        }

        return result;
    }
}
