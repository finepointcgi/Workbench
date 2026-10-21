using Microsoft.EntityFrameworkCore;
using Workbench.Application.Sheets;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Sheets;
using Workbench.Domain;

namespace Workbench.Application.Identity;

/// <summary>
/// Restricted columns ("Finance and owners only"): Finance, administrators and the workspace's owners see them; for
/// everyone else the permission decorators leave the columns and their values out of every read and keep them out of
/// every write. Formula columns reading a restricted column are restricted too (<see cref="RestrictedColumns"/>).
/// </summary>
public sealed class RestrictedColumnAccess(IWorkbenchDbContextFactory dbFactory, AccessControl access)
{
    /// <summary>True for Finance, administrators and the owners of the sheet's workspace.</summary>
    public async Task<bool> CanSeeRestrictedAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
        await access.HasAsync(Permission.RestrictedColumnsView, cancellationToken)
        || await access.GetSheetRoleAsync(sheetId, cancellationToken) == WorkspaceRole.Owner;

    /// <summary>The ids of the columns hidden from the current user on the sheet (empty when they see everything).</summary>
    public async Task<IReadOnlySet<Guid>> HiddenColumnsAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        if (!columns.Any(c => c.IsRestricted))
        {
            return new HashSet<Guid>();
        }

        return await CanSeeRestrictedAsync(sheetId, cancellationToken) ? new HashSet<Guid>() : RestrictedColumns.Of(columns);
    }

    public async Task RequireCanSeeRestrictedAsync(Guid sheetId, string what, CancellationToken cancellationToken = default)
    {
        if (!await CanSeeRestrictedAsync(sheetId, cancellationToken))
        {
            throw new WorkbenchForbiddenException($"Only Finance, administrators and the workspace's owners can {what}.");
        }
    }

    public static SheetDetail Strip(SheetDetail sheet, IReadOnlySet<Guid> hidden) =>
        hidden.Count == 0 ? sheet : sheet with { Columns = sheet.Columns.Where(c => !hidden.Contains(c.Id)).ToList() };

    public static ItemDto Strip(ItemDto item, IReadOnlySet<Guid> hidden) =>
        hidden.Count == 0 ? item : item with { Values = Without(item.Values, hidden) };

    public static IReadOnlyDictionary<string, string?> Without(IReadOnlyDictionary<string, string?> values, IReadOnlySet<Guid> hidden) =>
        values.Where(v => !(Guid.TryParse(v.Key, out var id) && hidden.Contains(id))).ToDictionary(v => v.Key, v => v.Value);

    /// <summary>
    /// The values to save for someone who can't see the hidden columns: what they sent for the visible ones, and what
    /// the item already has for the hidden ones (they never saw those, so a full-set update must not erase them).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string?>> MergeHiddenAsync(
        Guid sheetId, Guid? itemId, IReadOnlyDictionary<string, string?>? sent, IReadOnlySet<Guid> hidden, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string?>(Without(sent ?? new Dictionary<string, string?>(), hidden));
        if (itemId is null)
        {
            return values;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var existing = await db.Items.AsNoTracking().Where(i => i.Id == itemId && i.SheetId == sheetId).Select(i => i.Values).FirstOrDefaultAsync(cancellationToken);
        foreach (var (key, value) in existing ?? [])
        {
            if (Guid.TryParse(key, out var id) && hidden.Contains(id))
            {
                values[key] = value;
            }
        }

        return values;
    }

    /// <summary>The restricted columns' names on the sheet, for rules that refer to columns by name (<c>{Budget}</c>).</summary>
    public async Task<IReadOnlyList<Column>> ColumnsAsync(Guid sheetId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
    }
}
