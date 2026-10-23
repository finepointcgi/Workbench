using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Grid;

// Spreadsheet behavior: range selection (drag, Shift+click, Shift+arrows), copy and paste (Excel and Google Sheets
// blocks; paste past the end adds rows), fill down (Ctrl+D and the drag handle), Delete clears a range, and undo/redo.
public partial class SheetGrid
{
    /// <summary>One cell to set: the row, the column and the stored value (null clears it).</summary>
    private sealed record CellEdit(Guid ItemId, GridColumn Column, string? Value);

    /// <summary>A cell's value before and after a change, by column key (columns can be re-rendered between undo steps).</summary>
    private sealed record CellChange(Guid ItemId, string ColumnKey, string? Before, string? After);

    private readonly GridHistory _history = new();

    // The range is the rectangle between the anchor and the active cell (both visible-row indexes).
    private Guid? _anchorId;
    private int _anchorRow = -1;
    private int _anchorCol;

    private (int Top, int Left, int Bottom, int Right) Range
    {
        get
        {
            if (_activeRow < 0)
            {
                return (-1, -1, -1, -1);
            }

            var anchorRow = _anchorRow < 0 ? _activeRow : Math.Min(_anchorRow, _rows.Count - 1);
            var anchorCol = _anchorRow < 0 ? _activeCol : Math.Min(_anchorCol, _columns.Count - 1);
            return (Math.Min(anchorRow, _activeRow), Math.Min(anchorCol, _activeCol), Math.Max(anchorRow, _activeRow), Math.Max(anchorCol, _activeCol));
        }
    }

    private bool HasRange => Range is var r && r.Top >= 0 && (r.Top != r.Bottom || r.Left != r.Right);

    private int RangeCellCount => Range is var r && r.Top >= 0 ? (r.Bottom - r.Top + 1) * (r.Right - r.Left + 1) : 0;

    private bool InRange(int rowIndex, int colIndex) =>
        HasRange && Range is var r && rowIndex >= r.Top && rowIndex <= r.Bottom && colIndex >= r.Left && colIndex <= r.Right;

    private bool IsFillCorner(int rowIndex, int colIndex) =>
        CanEdit && !_editing && _activeRow >= 0 && Range is var r && rowIndex == r.Bottom && colIndex == r.Right;

    private void CollapseRange()
    {
        _anchorRow = -1;
        _anchorId = null;
    }

    /// <summary>Moves the active cell; with <paramref name="extend"/> the anchor stays, growing or shrinking the range.</summary>
    private void SelectTo(int rowIndex, int colIndex, bool extend)
    {
        if (extend && _activeRow >= 0)
        {
            if (_anchorRow < 0)
            {
                _anchorRow = _activeRow;
                _anchorCol = _activeCol;
                _anchorId = ActiveRow?.Item.Id;
            }

            Select(rowIndex, colIndex, keepRange: true);
        }
        else
        {
            Select(rowIndex, colIndex);
        }
    }

    /// <summary>Re-finds the anchor after the rows are rebuilt (sorting, collapsing, a reload).</summary>
    private void RestoreAnchor()
    {
        if (_anchorId is { } id && _rowIndex.TryGetValue(id, out var index))
        {
            _anchorRow = index;
        }
        else
        {
            CollapseRange();
        }
    }

    private void SelectAll()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        Select(_rows.Count - 1, _columns.Count - 1);
        _anchorRow = 0;
        _anchorCol = 0;
        _anchorId = _rows[0].Item.Id;
        _scrollToActive = false;
    }

    /// <summary>A mouse drag across cells (sheetGrid.js): selects from where it started to the cell under the pointer.</summary>
    [JSInvokable]
    public void DragSelect(int fromRow, int fromCol, int toRow, int toCol)
    {
        if (_editing || _rows.Count == 0)
        {
            return;
        }

        fromRow = Math.Clamp(fromRow, 0, _rows.Count - 1);
        Select(toRow, toCol);
        _anchorRow = fromRow;
        _anchorCol = Math.Clamp(fromCol, 0, _columns.Count - 1);
        _anchorId = _rows[fromRow].Item.Id;
        _scrollToActive = false;
        _focusGrid = true;
        StateHasChanged();
    }

    // ---- Copy and paste ----

    /// <summary>The selected cells as tab-separated text (sheetGrid.js puts it on the clipboard).</summary>
    [JSInvokable]
    public string CopySelection()
    {
        var (top, left, bottom, right) = Range;
        if (top < 0)
        {
            return string.Empty;
        }

        return ClipboardText.Format(Enumerable.Range(top, bottom - top + 1)
            .Select(r => Enumerable.Range(left, right - left + 1).Select(c => CellText(_rows[r], _columns[c]))));
    }

    [JSInvokable]
    public async Task<string> CutSelectionAsync()
    {
        var text = CopySelection();
        if (CanEdit)
        {
            await ClearRangeAsync("Cut");
        }

        return text;
    }

    /// <summary>What a cell holds as text: what the grid shows.</summary>
    private string CellText(GridRow row, GridColumn column) => column.Kind switch
    {
        GridColumnKind.Title => row.Item.Title,
        GridColumnKind.Assignees => string.Join(", ", (Tasks?.For(row.Item.Id) ?? []).Select(a => a.DisplayName)),
        GridColumnKind.Duration => int.TryParse(Raw(row.Item, column), out var days) ? PlanDuration.Format(days) : string.Empty,
        _ => CellFormat.CopyText(Raw(row.Item, column), column.ValueKind),
    };

    /// <summary>
    /// Pastes a block copied from Excel, Google Sheets or this grid, starting at the top-left of the selection. One value
    /// pasted into a range fills it. Rows past the end are added. Read-only cells and values that don't fit a column (a
    /// word in a number column) are skipped and counted.
    /// </summary>
    [JSInvokable]
    public async Task PasteAsync(string text)
    {
        if (!CanEdit)
        {
            Snackbar.Add("You can view this sheet but not change it.", Severity.Info);
            return;
        }

        var block = ClipboardText.Parse(text);
        if (block.Count == 0 || _columns.Count == 0)
        {
            return;
        }

        if (_editing && !await CommitEditAsync())
        {
            return;
        }

        if (_activeRow < 0 && _rows.Count > 0)
        {
            Select(0, 0);
        }

        var (top, left, bottom, right) = _rows.Count == 0 ? (0, 0, 0, 0) : Range;
        if (block.Count == 1 && block[0].Count == 1 && HasRange)
        {
            // One value into a range: fill it, like Excel.
            block = Enumerable.Range(0, bottom - top + 1).Select(_ => (IReadOnlyList<string>)Enumerable.Repeat(block[0][0], right - left + 1).ToList()).ToList();
        }

        var edits = new List<CellEdit>();
        var newRows = new List<IReadOnlyList<string>>();
        var skipped = 0;
        var outside = 0;
        for (var r = 0; r < block.Count; r++)
        {
            if (top + r >= _rows.Count)
            {
                newRows.Add(block[r]);
                continue;
            }

            var row = _rows[top + r];
            for (var c = 0; c < block[r].Count; c++)
            {
                if (left + c >= _columns.Count)
                {
                    outside += block[r][c].Length > 0 ? 1 : 0;
                    continue;
                }

                var column = _columns[left + c];
                if (!CanSet(row, column))
                {
                    skipped += block[r][c].Length > 0 || Raw(row.Item, column) is not null ? 1 : 0;
                    continue;
                }

                if (Normalize(block[r][c], column, out var value))
                {
                    edits.Add(new CellEdit(row.Item.Id, column, value));
                }
                else
                {
                    skipped++;
                }
            }
        }

        var changes = await ApplyAsync(edits);
        var created = await CreatePastedRowsAsync(newRows, left);
        skipped += created.Skipped;
        outside += created.Outside;
        if (changes.Count > 0 || created.Ids.Count > 0)
        {
            RecordCells("Paste", changes, created.Ids, created.Requests);
            var rowsTouched = changes.Select(c => c.ItemId).Distinct().Count();
            Snackbar.Add($"Pasted into {rowsTouched + created.Ids.Count} {(rowsTouched + created.Ids.Count == 1 ? "row" : "rows")}"
                + (created.Ids.Count > 0 ? $" (added {created.Ids.Count})" : "") + ". Ctrl+Z undoes it.", Severity.Success);
            // Select what was pasted. Added rows aren't on screen until the reload, so they're found again by id.
            var lastRow = Math.Min(top + block.Count - 1, _rows.Count - 1);
            if (_rows.Count > 0)
            {
                Select(lastRow, Math.Min(left + block.Max(b => b.Count) - 1, _columns.Count - 1), keepRange: true);
                _anchorRow = Math.Min(top, _rows.Count - 1);
                _anchorCol = left;
                _anchorId = _rows[_anchorRow].Item.Id;
            }

            if (created.Ids.Count > 0)
            {
                _activeId = created.Ids[^1];
                _anchorId ??= created.Ids[0];
                _anchorCol = left;
            }

            await Changed.InvokeAsync();
        }

        if (skipped > 0 || outside > 0)
        {
            Snackbar.Add(Skipped(skipped, outside), Severity.Warning);
        }

        _focusGrid = true;
    }

    private static string Skipped(int skipped, int outside) =>
        string.Join(" ", new[]
        {
            skipped > 0 ? $"{skipped} {(skipped == 1 ? "value was" : "values were")} skipped (read-only cells, or text that doesn't fit the column)." : null,
            outside > 0 ? $"{outside} {(outside == 1 ? "value" : "values")} fell past the last column." : null,
        }.OfType<string>());

    /// <summary>Rows pasted below the last row become new top-level rows, created with their values in one step each.</summary>
    private async Task<(List<Guid> Ids, List<CreateItemRequest> Requests, int Skipped, int Outside)> CreatePastedRowsAsync(List<IReadOnlyList<string>> block, int left)
    {
        var ids = new List<Guid>();
        var requests = new List<CreateItemRequest>();
        var skipped = 0;
        var outside = 0;
        foreach (var cells in block)
        {
            var request = new CreateItemRequest("New row");
            var values = new Dictionary<string, string?>();
            for (var c = 0; c < cells.Count; c++)
            {
                if (left + c >= _columns.Count)
                {
                    outside += cells[c].Length > 0 ? 1 : 0;
                    continue;
                }

                var column = _columns[left + c];
                if (cells[c].Length == 0)
                {
                    continue;
                }

                if (column.ReadOnly || column.Kind is GridColumnKind.Assignees or GridColumnKind.Duration or GridColumnKind.Predecessors or GridColumnKind.Wbs
                    || !Normalize(cells[c], column, out var value))
                {
                    skipped++;
                    continue;
                }

                switch (column.Kind)
                {
                    case GridColumnKind.Title when value is not null:
                        request = request with { Title = value };
                        break;
                    case GridColumnKind.Start:
                        request = request with { Start = CellFormat.ToDateTime(value) is { } s ? DateOnly.FromDateTime(s) : null };
                        break;
                    case GridColumnKind.Finish:
                        request = request with { Finish = CellFormat.ToDateTime(value) is { } f ? DateOnly.FromDateTime(f) : null };
                        break;
                    case GridColumnKind.Custom:
                        values[column.Key] = value;
                        break;
                }
            }

            request = request with { Values = values };
            try
            {
                var created = await ItemService.CreateAsync(SheetId, request);
                ids.Add(created.Id);
                requests.Add(request);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                Snackbar.Add($"Couldn't add a pasted row: {ex.Message}", Severity.Error);
                break;
            }
        }

        return (ids, requests, skipped, outside);
    }

    /// <summary>Whether a paste, fill or clear may set this cell.</summary>
    private bool CanSet(GridRow row, GridColumn column) =>
        CanEdit && !column.ReadOnly && column.Kind != GridColumnKind.Assignees && !IsRolledUp(row, column)
        && (column.Kind is not (GridColumnKind.Duration or GridColumnKind.Predecessors) || PlanService is not null);

    /// <summary>Text from the clipboard as the column stores it; false when it doesn't fit (and Title can't be blank).</summary>
    private static bool Normalize(string text, GridColumn column, out string? value)
    {
        value = null;
        if (column.Kind == GridColumnKind.Duration)
        {
            if (!PlanDuration.TryParse(text, out var days))
            {
                return false;
            }

            value = days.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        if (column.Kind == GridColumnKind.Predecessors)
        {
            value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            return true;
        }

        return CellFormat.TryNormalize(text, column, out value, out _) && !(column.Kind == GridColumnKind.Title && value is null);
    }

    // ---- Fill and clear ----

    /// <summary>
    /// Ctrl+D: copies the top row of the selection into the rows below it; with one row selected, copies the row above
    /// into it (like Excel).
    /// </summary>
    private async Task FillDownAsync()
    {
        var (top, left, bottom, right) = Range;
        if (!CanEdit || top < 0)
        {
            return;
        }

        var source = top == bottom ? top - 1 : top;
        var first = top == bottom ? top : top + 1;
        if (source < 0)
        {
            return;
        }

        await FillAsync([source], first, bottom, left, right);
    }

    /// <summary>The fill handle was dragged to <paramref name="toRow"/>: repeats the selected rows down to it.</summary>
    [JSInvokable]
    public async Task FillToAsync(int toRow)
    {
        var (top, left, bottom, right) = Range;
        if (!CanEdit || top < 0 || _editing)
        {
            return;
        }

        toRow = Math.Clamp(toRow, 0, _rows.Count - 1);
        if (toRow <= bottom)
        {
            return;
        }

        await FillAsync(Enumerable.Range(top, bottom - top + 1).ToList(), bottom + 1, toRow, left, right);
        Select(toRow, right, keepRange: true);
        _anchorRow = top;
        _anchorCol = left;
        _anchorId = _rows[top].Item.Id;
        StateHasChanged();
    }

    private async Task FillAsync(IReadOnlyList<int> sourceRows, int first, int last, int left, int right)
    {
        var edits = new List<CellEdit>();
        var skipped = 0;
        for (var r = first; r <= last; r++)
        {
            var source = _rows[sourceRows[(r - first) % sourceRows.Count]];
            for (var c = left; c <= right; c++)
            {
                var column = _columns[c];
                if (column.Kind == GridColumnKind.Title && !column.ReadOnly)
                {
                    // Filling titles is rarely wanted (every row the same name), but it's what was asked for.
                    edits.Add(new CellEdit(_rows[r].Item.Id, column, source.Item.Title));
                }
                else if (CanSet(_rows[r], column))
                {
                    edits.Add(new CellEdit(_rows[r].Item.Id, column, Raw(source.Item, column)));
                }
                else
                {
                    skipped++;
                }
            }
        }

        var changes = await ApplyAsync(edits);
        if (changes.Count > 0)
        {
            RecordCells("Fill down", changes);
            await Changed.InvokeAsync();
        }

        if (skipped > 0)
        {
            Snackbar.Add(Skipped(skipped, 0), Severity.Info);
        }

        _focusGrid = true;
    }

    /// <summary>Delete or Backspace: clears every cell of the selection that can be cleared (not titles or rolled-up dates).</summary>
    private async Task ClearRangeAsync(string label = "Clear")
    {
        var (top, left, bottom, right) = Range;
        if (!CanEdit || top < 0)
        {
            return;
        }

        var edits = new List<CellEdit>();
        for (var r = top; r <= bottom; r++)
        {
            for (var c = left; c <= right; c++)
            {
                var column = _columns[c];
                if (column.Kind is not (GridColumnKind.Title or GridColumnKind.Duration) && CanSet(_rows[r], column))
                {
                    edits.Add(new CellEdit(_rows[r].Item.Id, column, null));
                }
            }
        }

        var changes = await ApplyAsync(edits);
        if (changes.Count > 0)
        {
            RecordCells(label, changes);
            await Changed.InvokeAsync();
        }

        _focusGrid = true;
    }

    // ---- Saving several cells ----

    /// <summary>
    /// Saves cell values, one update per row (Duration and Predecessors through the plan). Unchanged cells are left alone.
    /// Returns what changed, for undo.
    /// </summary>
    private async Task<List<CellChange>> ApplyAsync(IReadOnlyList<CellEdit> edits)
    {
        var changes = new List<CellChange>();
        foreach (var group in edits.GroupBy(e => e.ItemId))
        {
            if (FindItem(group.Key) is not { } item)
            {
                continue;
            }

            var updated = item;
            var rowChanges = new List<CellChange>();
            var planEdits = new List<CellEdit>();
            foreach (var edit in group)
            {
                var before = Raw(item, edit.Column);
                if (before == edit.Value || (edit.Column.Kind == GridColumnKind.Title && string.IsNullOrWhiteSpace(edit.Value)))
                {
                    continue;
                }

                rowChanges.Add(new CellChange(item.Id, edit.Column.Key, before, edit.Value));
                if (edit.Column.Kind is GridColumnKind.Duration or GridColumnKind.Predecessors)
                {
                    planEdits.Add(edit);
                }
                else
                {
                    updated = WithValue(updated, edit.Column, edit.Value);
                }
            }

            if (rowChanges.Count == 0)
            {
                continue;
            }

            try
            {
                if (!ReferenceEquals(updated, item))
                {
                    ReplaceItem(await ItemService.UpdateAsync(SheetId, item.Id, new UpdateItemRequest(updated.Title, updated.Start, updated.Finish, updated.CostpointWbsId, updated.Values)));
                }

                foreach (var edit in planEdits)
                {
                    await SavePlanCellAsync(item, edit.Column, edit.Value);
                }

                changes.AddRange(rowChanges);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                Snackbar.Add($"Couldn't save \"{item.Title}\": {ex.Message}", Severity.Error);
            }
        }

        return changes;
    }

    private async Task SavePlanCellAsync(ItemDto item, GridColumn column, string? value)
    {
        if (PlanService is not { } plan)
        {
            return;
        }

        if (column.Kind == GridColumnKind.Duration && PlanDuration.TryParse(value, out var days))
        {
            await plan.SetDurationAsync(SheetId, item.Id, days);
        }
        else if (column.Kind == GridColumnKind.Predecessors)
        {
            await plan.SetPredecessorsAsync(SheetId, item.Id, value);
        }
    }

    /// <summary>The item with one cell changed (the same fields <see cref="CellFormat.WithValue"/> sets).</summary>
    private static ItemDto WithValue(ItemDto item, GridColumn column, string? value)
    {
        var request = CellFormat.WithValue(item, column, value);
        return item with { Title = request.Title, Start = request.Start, Finish = request.Finish, CostpointWbsId = request.CostpointWbsId, Values = request.Values };
    }

    /// <summary>The latest copy of a row: the grid's (saved edits swap in at once), else the page's.</summary>
    private ItemDto? FindItem(Guid id) =>
        _rowIndex.TryGetValue(id, out var index) ? _rows[index].Item : Items.FirstOrDefault(i => i.Id == id);

    // ---- Undo and redo ----

    private async Task UndoAsync()
    {
        if (_editing && !await CommitEditAsync())
        {
            return;
        }

        await ReplayAsync(_history.UndoAsync, "Undid");
    }

    private async Task RedoAsync()
    {
        if (_editing && !await CommitEditAsync())
        {
            return;
        }

        await ReplayAsync(_history.RedoAsync, "Redid");
    }

    private async Task ReplayAsync(Func<Task<string?>> step, string verb)
    {
        try
        {
            if (await step() is { } label)
            {
                Snackbar.Add($"{verb}: {label.ToLowerInvariant()}", Severity.Normal, o => o.VisibleStateDuration = 2000);
                await Changed.InvokeAsync();
            }
            else
            {
                Snackbar.Add(verb == "Undid" ? "Nothing to undo." : "Nothing to redo.", Severity.Info, o => o.VisibleStateDuration = 1500);
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Snackbar.Add($"Couldn't {(verb == "Undid" ? "undo" : "redo")} that: {ex.Message}", Severity.Error);
            await Changed.InvokeAsync();
        }

        _focusGrid = true;
    }

    /// <summary>Records cell changes (and rows a paste added) as one undo step.</summary>
    private void RecordCells(string label, IReadOnlyList<CellChange> changes, IReadOnlyList<Guid>? createdIds = null, IReadOnlyList<CreateItemRequest>? createdRequests = null)
    {
        var created = createdIds?.ToList() ?? [];
        _history.Record(label,
            async () =>
            {
                foreach (var id in created)
                {
                    await DeleteQuietlyAsync(_history.Resolve(id));
                }

                await ApplyChangesAsync(changes, after: false);
            },
            async () =>
            {
                await ApplyChangesAsync(changes, after: true);
                for (var i = 0; i < created.Count; i++)
                {
                    var again = await ItemService.CreateAsync(SheetId, createdRequests![i]);
                    _history.Remap(created[i], again.Id);
                    created[i] = again.Id;
                }
            });
    }

    private async Task ApplyChangesAsync(IReadOnlyList<CellChange> changes, bool after)
    {
        var columns = _allColumns.ToDictionary(c => c.Key);
        await ApplyAsync(changes
            .Where(c => columns.ContainsKey(c.ColumnKey))
            .Select(c => new CellEdit(_history.Resolve(c.ItemId), columns[c.ColumnKey], after ? c.After : c.Before))
            .ToList());
    }

    private async Task DeleteQuietlyAsync(Guid id)
    {
        try
        {
            await ItemService.DeleteAsync(SheetId, id);
        }
        catch (Workbench.Contracts.WorkbenchNotFoundException)
        {
            // Already gone (someone else deleted it).
        }
    }

    /// <summary>Records a single cell's change made through the editor, a checkbox, a date or a status pick.</summary>
    private void RecordCell(ItemDto item, GridColumn column, string? before, string? after) =>
        RecordCells($"Edit {column.Name}", [new CellChange(item.Id, column.Key, before, after)]);

    /// <summary>Records added rows: undo deletes them, redo adds them again (with new ids).</summary>
    private void RecordCreate(string label, ItemDto created, CreateItemRequest request) =>
        RecordCells(label, [], [created.Id], [request]);

    /// <summary>
    /// The sheet's structure (parent and position of every row) before an indent, outdent or move; undo puts every row
    /// back where it was, redo repeats the action.
    /// </summary>
    private Dictionary<Guid, (Guid? ParentId, int Order)> Structure() => Items.ToDictionary(i => i.Id, i => (i.ParentId, i.Order));

    private void RecordStructure(string label, Dictionary<Guid, (Guid? ParentId, int Order)> before, Func<Task> redo) =>
        _history.Record(label, () => RestoreStructureAsync(before), redo);

    private async Task RestoreStructureAsync(Dictionary<Guid, (Guid? ParentId, int Order)> before)
    {
        var current = Items.ToDictionary(i => i.Id);
        var groups = before
            .Select(kv => (Id: _history.Resolve(kv.Key), Parent: kv.Value.ParentId is { } p ? _history.Resolve(p) : (Guid?)null, kv.Value.Order))
            .Where(x => current.ContainsKey(x.Id))
            .GroupBy(x => x.Parent);
        foreach (var group in groups)
        {
            var wanted = group.OrderBy(x => x.Order).Select(x => x.Id).ToList();
            var now = current.Values.Where(i => i.ParentId == group.Key).OrderBy(i => i.Order).Select(i => i.Id).ToList();
            if (wanted.SequenceEqual(now))
            {
                continue;
            }

            for (var i = 0; i < wanted.Count; i++)
            {
                await ItemService.MoveAsync(SheetId, wanted[i], new MoveItemRequest(group.Key, i));
            }
        }
    }

    /// <summary>A deleted row and everything under it, enough to create them again.</summary>
    private sealed record RowSnapshot(ItemDto Item, IReadOnlyList<RowSnapshot> Children);

    private RowSnapshot Snapshot(ItemDto item) =>
        new(item, _children[item.Id].OrderBy(i => i.Order).Select(Snapshot).ToList());

    private void RecordDelete(RowSnapshot snapshot) =>
        _history.Record("Delete row",
            () => RecreateAsync(snapshot, snapshot.Item.ParentId is { } p ? _history.Resolve(p) : null),
            () => DeleteQuietlyAsync(_history.Resolve(snapshot.Item.Id)));

    private async Task RecreateAsync(RowSnapshot snapshot, Guid? parentId)
    {
        var item = snapshot.Item;
        var created = await ItemService.CreateAsync(SheetId,
            new CreateItemRequest(item.Title, parentId, item.Order, item.Start, item.Finish, item.Values, item.IsMilestone));
        if (item.CostpointWbsId is not null)
        {
            await ItemService.UpdateAsync(SheetId, created.Id, new UpdateItemRequest(created.Title, created.Start, created.Finish, item.CostpointWbsId, created.Values));
        }

        _history.Remap(item.Id, created.Id);
        foreach (var child in snapshot.Children)
        {
            await RecreateAsync(child, created.Id);
        }
    }

    // ---- Keys ----

    /// <summary>Ctrl (Cmd on a Mac) shortcuts and Shift+arrows; returns true when the key was handled.</summary>
    private async Task<bool> OnRangeKeyAsync(KeyboardEventArgs e)
    {
        var command = e.CtrlKey || e.MetaKey;
        var key = e.Key.Length == 1 ? e.Key.ToLowerInvariant() : e.Key;
        if (command && !e.AltKey)
        {
            switch (key)
            {
                case "z" when e.ShiftKey:
                case "y":
                    await RedoAsync();
                    return true;
                case "z":
                    await UndoAsync();
                    return true;
                case "d":
                    await FillDownAsync();
                    return true;
                case "a":
                    SelectAll();
                    return true;
            }
        }

        if (e.ShiftKey && !command && !e.AltKey)
        {
            switch (e.Key)
            {
                case "ArrowUp":
                    SelectTo(_activeRow - 1, _activeCol, extend: true);
                    return true;
                case "ArrowDown":
                    SelectTo(_activeRow + 1, _activeCol, extend: true);
                    return true;
                case "ArrowLeft":
                    SelectTo(_activeRow, _activeCol - 1, extend: true);
                    return true;
                case "ArrowRight":
                    SelectTo(_activeRow, _activeCol + 1, extend: true);
                    return true;
            }
        }

        if (e.Key == "Escape" && HasRange)
        {
            CollapseRange();
            return true;
        }

        if (e.Key is "Delete" or "Backspace" && HasRange)
        {
            await ClearRangeAsync();
            return true;
        }

        return false;
    }
}
