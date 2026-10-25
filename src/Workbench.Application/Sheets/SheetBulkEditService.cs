using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Automations;
using Workbench.Application.Collaboration;
using Workbench.Application.Scheduling;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Collaboration;

namespace Workbench.Application.Sheets;

/// <summary>
/// Many cells in one save (the grid's paste, fill down and clear, and their undo). The same rules as
/// <see cref="ItemService"/> for each cell, but one database round, one plan reflow, one activity entry and live refresh,
/// and the sheet's automations run once as a batch (<see cref="SheetAutomations"/>, like the Excel plan import). Cells that
/// can't be set are skipped and reported; the others are saved. On a plan, <see cref="SheetBulkEditResult.Changes"/> also lists
/// dates the reflow moved (successors pushed by links), so undo puts the whole plan back. Permissions: <c>PermissionCheckingSheetBulkEditService</c>.
/// </summary>
public sealed class SheetBulkEditService(
    IWorkbenchDbContextFactory dbFactory,
    TimeProvider clock,
    SheetCalculator? calculator = null,
    ActivityRecorder? activity = null,
    SheetAutomations? automations = null,
    IBlobStore? blobs = null,
    ICurrentUser? currentUser = null) : ISheetBulkEditService
{
    public const string BulkEditedKind = "items.bulk_edited";
    public const int MaxCells = 50_000;
    public const int MaxNewRows = 5_000;
    private const string NewRowTitle = "New row";

    private readonly SheetCalculator _calculator = calculator ?? new SheetCalculator(dbFactory, clock);

    public async Task<SheetBulkEditResult> ApplyAsync(Guid sheetId, SheetBulkEditRequest request, CancellationToken cancellationToken = default)
    {
        using var author = SheetRowStamps.As(currentUser?.Email);
        ArgumentNullException.ThrowIfNull(request);
        var rowsIn = request.Rows ?? [];
        var newIn = request.NewRows ?? [];
        var deleteIn = request.DeleteItemIds ?? [];
        if (rowsIn.Sum(r => r.Cells?.Count ?? 0) + newIn.Sum(r => r.Cells?.Count ?? 0) > MaxCells || newIn.Count > MaxNewRows)
        {
            throw new ArgumentException($"Change at most {MaxCells:N0} cells and add at most {MaxNewRows:N0} rows at a time.", nameof(request));
        }

        var source = string.IsNullOrWhiteSpace(request.Source) ? "Bulk edit" : request.Source.Trim();
        var before = automations is null ? null : await automations.CaptureAsync(sheetId, cancellationToken);

        var skipped = new List<BulkSkippedCell>();
        var changes = new List<BulkCellChange>();
        var created = new List<Item>();
        var touched = new List<Item>();
        var deleted = 0;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var sheet = await db.Sheets.FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);
            var columns = (await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken)).ToDictionary(c => c.Id.ToString(), StringComparer.OrdinalIgnoreCase);
            var items = await db.Items.Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
            var byId = items.ToDictionary(i => i.Id);
            var parents = items.Where(i => i.ParentId is not null).Select(i => i.ParentId!.Value).ToHashSet();
            var original = items.ToDictionary(i => i.Id, Copy);

            foreach (var row in rowsIn)
            {
                if (!byId.TryGetValue(row.ItemId, out var item))
                {
                    skipped.AddRange((row.Cells ?? []).Select(c => new BulkSkippedCell(row.ItemId, null, c.Field, "The row isn't on the sheet any more.")));
                    continue;
                }

                var rolledUp = sheet.SchedulingEnabled && parents.Contains(item.Id);
                var rowChanges = Apply(item, row.Cells ?? [], columns, rolledUp, (field, reason) => skipped.Add(new BulkSkippedCell(item.Id, null, field, reason)));
                if (rowChanges.Count > 0)
                {
                    changes.AddRange(rowChanges.Select(c => c with { ItemId = item.Id }));
                    touched.Add(item);
                }
            }

            var order = items.Where(i => i.ParentId is null).Select(i => i.Order).DefaultIfEmpty(-1).Max() + 1;
            for (var index = 0; index < newIn.Count; index++)
            {
                var item = new Item { SheetId = sheetId, Title = NewRowTitle, Order = order++ };
                var at = index;
                Apply(item, newIn[index].Cells ?? [], columns, rolledUp: false, (field, reason) => skipped.Add(new BulkSkippedCell(null, at, field, reason)));
                db.Items.Add(item);
                created.Add(item);
            }

            deleted = await DeleteAsync(db, sheetId, items, deleteIn.ToHashSet(), cancellationToken);
            var direct = changes.Count;
            if (touched.Count > 0 || created.Count > 0 || deleted > 0)
            {
                sheet.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
                await PlanReflow.ReflowAndSaveAsync(db, sheetId, cancellationToken, clock);
                if (sheet.SchedulingEnabled)
                {
                    touched.AddRange(await MovedByThePlanAsync(db, sheetId, original, parents, touched, changes, cancellationToken));
                }
            }

            var computed = await ComputedAsync(db, sheetId, columns.Values.ToList(), touched.Concat(created).Select(i => i.Id).ToHashSet(), cancellationToken);
            var result = new SheetBulkEditResult(
                touched.Select(i => computed.GetValueOrDefault(i.Id) ?? i.ToDto()).ToList(),
                created.Select(i => computed.GetValueOrDefault(i.Id) ?? i.ToDto()).ToList(),
                changes,
                skipped,
                deleted);

            if (touched.Count == 0 && created.Count == 0 && deleted == 0)
            {
                return result;
            }

            // Automations run on every added or changed row, as one batch (one notification per automation and person).
            if (before is not null)
            {
                await automations!.RunBatchAsync(before, $"a {source.ToLowerInvariant()} in the grid", cancellationToken);
            }

            if (activity is not null)
            {
                var edited = changes.Take(direct).Select(c => c.ItemId).Distinct().ToList();
                if (edited.Count == 1 && created.Count == 0 && deleted == 0)
                {
                    // One row (an edit in a cell, a status pick): the same entry a single update records, naming each change.
                    // Stored values on both sides: formula columns are worked out on read, so they'd look changed.
                    var after = touched.First(i => i.Id == edited[0]).ToDto();
                    var described = ActivityRecordingItemService.Describe(original[edited[0]], after, columns.Values.ToList());
                    await activity.RecordAsync(sheetId, after.Id, ActivityKinds.ItemUpdated, $"Updated \"{after.Title}\": {string.Join("; ", described)}", SheetChangeKind.ItemUpdated);
                }
                else
                {
                    await activity.RecordAsync(sheetId, null, BulkEditedKind, Summary(source, changes.Take(direct).ToList(), edited.Count, created.Count, deleted), SheetChangeKind.ItemUpdated);
                }
            }

            return result;
        }
    }

    private static Item Copy(Item i) => new()
    {
        Id = i.Id, SheetId = i.SheetId, ParentId = i.ParentId, Order = i.Order, Title = i.Title, Start = i.Start, Finish = i.Finish,
        CostpointWbsId = i.CostpointWbsId, Values = new Dictionary<string, string?>(i.Values), IsMilestone = i.IsMilestone,
    };

    /// <summary>
    /// Dates the plan moved as a result of the edit (links push successors), added to the changes so that putting every
    /// <see cref="BulkCellChange.Before"/> back undoes the whole edit. Summary rows are left out: they follow their subtasks.
    /// </summary>
    private static async Task<List<Item>> MovedByThePlanAsync(IWorkbenchDbContext db, Guid sheetId, Dictionary<Guid, Item> original, HashSet<Guid> parents,
        List<Item> touched, List<BulkCellChange> changes, CancellationToken cancellationToken)
    {
        var now = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
        var direct = changes.Select(c => (c.ItemId, c.Field)).ToHashSet();
        var touchedIds = touched.Select(i => i.Id).ToHashSet();
        var moved = new List<Item>();
        foreach (var item in now.Where(i => original.ContainsKey(i.Id) && !parents.Contains(i.Id)))
        {
            var was = original[item.Id];
            var any = false;
            if (item.Start != was.Start && !direct.Contains((item.Id, SheetFields.Start)))
            {
                changes.Add(new BulkCellChange(item.Id, SheetFields.Start, Format(was.Start), Format(item.Start)));
                any = true;
            }

            if (item.Finish != was.Finish && !direct.Contains((item.Id, SheetFields.Finish)))
            {
                changes.Add(new BulkCellChange(item.Id, SheetFields.Finish, Format(was.Finish), Format(item.Finish)));
                any = true;
            }

            if (any && !touchedIds.Contains(item.Id))
            {
                moved.Add(item);
            }
        }

        return moved;
    }

    private static string Summary(string source, IReadOnlyList<BulkCellChange> changes, int rows, int added, int deleted)
    {
        var parts = new List<string>();
        if (changes.Count > 0)
        {
            parts.Add($"changed {changes.Count} {(changes.Count == 1 ? "cell" : "cells")} in {rows} {(rows == 1 ? "row" : "rows")}");
        }

        if (added > 0)
        {
            parts.Add($"added {added} {(added == 1 ? "row" : "rows")}");
        }

        if (deleted > 0)
        {
            parts.Add($"deleted {deleted} {(deleted == 1 ? "row" : "rows")}");
        }

        return $"{source}: {string.Join(", ", parts)}";
    }

    /// <summary>Sets the cells it can on one row; returns what changed (field, before, after).</summary>
    private static List<BulkCellChange> Apply(Item item, IReadOnlyList<BulkCellEdit> cells, IReadOnlyDictionary<string, Column> columns, bool rolledUp, Action<string, string> skip)
    {
        var changes = new List<BulkCellChange>();
        var values = new Dictionary<string, string?>(item.Values);
        var (start, finish) = (item.Start, item.Finish);
        foreach (var cell in cells.Where(c => !string.IsNullOrWhiteSpace(c.Field)))
        {
            var field = cell.Field.Trim();
            switch (field.ToLowerInvariant())
            {
                case SheetFields.Title:
                    if (string.IsNullOrWhiteSpace(cell.Value))
                    {
                        skip(field, "Title can't be blank.");
                    }
                    else if (cell.Value.Trim() != item.Title)
                    {
                        changes.Add(new BulkCellChange(item.Id, SheetFields.Title, item.Title, cell.Value.Trim()));
                        item.Title = cell.Value.Trim();
                    }

                    break;
                case SheetFields.Start or SheetFields.Finish:
                    var isStart = field.Equals(SheetFields.Start, StringComparison.OrdinalIgnoreCase);
                    if (rolledUp)
                    {
                        skip(field, "A summary task's dates come from its subtasks.");
                    }
                    else if (!TryDate(cell.Value, out var date))
                    {
                        skip(field, $"\"{cell.Value}\" isn't a date.");
                    }
                    else if (isStart)
                    {
                        start = date;
                    }
                    else
                    {
                        finish = date;
                    }

                    break;
                case SheetFields.Wbs:
                    var code = string.IsNullOrWhiteSpace(cell.Value) ? null : cell.Value.Trim();
                    if (code != item.CostpointWbsId)
                    {
                        changes.Add(new BulkCellChange(item.Id, SheetFields.Wbs, item.CostpointWbsId, code));
                        item.WbsCodeIsAuto = code is null;
                        item.CostpointWbsId = code;
                    }

                    break;
                default:
                    if (!columns.TryGetValue(field, out var column))
                    {
                        skip(field, "There's no such column on this sheet.");
                    }
                    else if (column.Formula is not null || column.Type is ColumnType.Lookup)
                    {
                        skip(field, $"{column.Name} is worked out from other cells.");
                    }
                    else if (column.Type is ColumnType.LinkedRow)
                    {
                        skip(field, $"Pick the rows {column.Name} links to instead of pasting them.");
                    }
                    else if (ColumnValues.IsAutomatic(column.Type.ToContract()))
                    {
                        skip(field, $"{column.Name} is filled in automatically.");
                    }
                    else if (!TryNormalize(cell.Value, column, out var value, out var error))
                    {
                        skip(field, error!);
                    }
                    else
                    {
                        var key = column.Id.ToString();
                        var old = values.GetValueOrDefault(key);
                        if (old != value)
                        {
                            changes.Add(new BulkCellChange(item.Id, key, old, value));
                            if (value is null)
                            {
                                values.Remove(key);
                            }
                            else
                            {
                                values[key] = value;
                            }
                        }
                    }

                    break;
            }
        }

        if (start is { } s && finish is { } f && f < s)
        {
            skip(start != item.Start ? SheetFields.Start : SheetFields.Finish, "Finish can't be before start.");
            (start, finish) = (item.Start, item.Finish);
        }

        if (start != item.Start)
        {
            changes.Add(new BulkCellChange(item.Id, SheetFields.Start, Format(item.Start), Format(start)));
            item.Start = start;
        }

        if (finish != item.Finish)
        {
            changes.Add(new BulkCellChange(item.Id, SheetFields.Finish, Format(item.Finish), Format(finish)));
            item.Finish = finish;
        }

        if (item.IsMilestone && (item.Finish ?? item.Start) is { } day)
        {
            (item.Start, item.Finish) = (day, day);
        }

        if (changes.Any(c => c.Field is not (SheetFields.Title or SheetFields.Start or SheetFields.Finish or SheetFields.Wbs)))
        {
            item.Values = values;
        }

        return changes;
    }

    /// <summary>Checks a value against its column's type and returns the stored form (null clears the cell).</summary>
    internal static bool TryNormalize(string? text, Column column, out string? value, out string? error)
    {
        value = null;
        error = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return true;
        }

        switch (column.Type)
        {
            case ColumnType.Number or ColumnType.Currency:
                var cleaned = trimmed.Replace("$", string.Empty).Replace(",", string.Empty).Replace(" ", string.Empty);
                if (cleaned.StartsWith('(') && cleaned.EndsWith(')'))
                {
                    cleaned = "-" + cleaned[1..^1];
                }

                if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    error = $"\"{trimmed}\" isn't a number.";
                    return false;
                }

                value = number.ToString("0.############################", CultureInfo.InvariantCulture);
                return true;
            case ColumnType.Date:
                if (!TryDate(trimmed, out var date))
                {
                    error = $"\"{trimmed}\" isn't a date.";
                    return false;
                }

                value = Format(date);
                return true;
            case ColumnType.Checkbox:
                value = trimmed.ToLowerInvariant() is "true" or "1" or "yes" or "y" or "x" or "checked" or "✓" or "✔" ? "true" : "false";
                return true;
            case ColumnType.Dropdown:
                value = column.Options.FirstOrDefault(o => string.Equals(o, trimmed, StringComparison.OrdinalIgnoreCase)) ?? trimmed;
                return true;
            case ColumnType.MultiSelect or ColumnType.Hyperlink or ColumnType.Symbol or ColumnType.Progress:
                return ColumnValues.TryNormalize(trimmed, column.Type.ToContract(), column.Options, out value, out error);
            default:
                value = trimmed;
                return true;
        }
    }

    private static bool TryDate(string? text, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (DateOnly.TryParseExact(text.Trim(), ["yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy", "M/d/yy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            date = d;
            return true;
        }

        return false;
    }

    private static string? Format(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Deletes rows with their subtasks, comments and attachments; returns how many rows went.</summary>
    private async Task<int> DeleteAsync(IWorkbenchDbContext db, Guid sheetId, List<Item> items, HashSet<Guid> ids, CancellationToken cancellationToken)
    {
        var doomed = items.Where(i => ids.Contains(i.Id)).Select(i => i.Id).ToHashSet();
        if (doomed.Count == 0)
        {
            return 0;
        }

        for (var added = true; added;)
        {
            var children = items.Where(i => i.ParentId is { } p && doomed.Contains(p) && !doomed.Contains(i.Id)).Select(i => i.Id).ToList();
            doomed.UnionWith(children);
            added = children.Count > 0;
        }

        await Scheduling.PlanWbsSync.BeforeRowsDeletedAsync(db, sheetId, doomed, cancellationToken);
        db.Items.RemoveRange(items.Where(i => doomed.Contains(i.Id)));
        foreach (var siblings in items.Where(i => !doomed.Contains(i.Id)).GroupBy(i => i.ParentId))
        {
            var order = 0;
            foreach (var sibling in siblings.OrderBy(s => s.Order))
            {
                sibling.Order = order++;
            }
        }

        var list = doomed.ToList();
        db.Set<Comment>().RemoveRange(await db.Set<Comment>().Where(c => c.SheetId == sheetId && list.Contains(c.ItemId)).ToListAsync(cancellationToken));
        var attachments = await db.Set<Attachment>().Where(a => a.SheetId == sheetId && list.Contains(a.ItemId)).ToListAsync(cancellationToken);
        db.Set<Attachment>().RemoveRange(attachments);
        if (blobs is not null)
        {
            foreach (var attachment in attachments)
            {
                await blobs.DeleteAsync(attachment.StorageKey);
            }
        }

        return doomed.Count;
    }

    /// <summary>Rows with formula, link and lookup columns worked out (only when the sheet has any).</summary>
    private async Task<Dictionary<Guid, ItemDto>> ComputedAsync(IWorkbenchDbContext db, Guid sheetId, List<Column> columns, HashSet<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0 || !columns.Any(c => c.Formula is not null || c.Type is ColumnType.LinkedRow or ColumnType.Lookup))
        {
            return [];
        }

        var all = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
        var computed = await _calculator.ComputeAsync(db, sheetId, columns, all, cancellationToken);
        return computed.Where(i => ids.Contains(i.Id)).ToDictionary(i => i.Id, i => i.ToDto());
    }
}
