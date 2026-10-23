using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Grid;

// Status labels like monday.com: a Dropdown cell shows its label as a colored pill (the text is always there, so color
// is never the only signal) and one click opens a small list of the column's labels to pick from.
public partial class SheetGrid
{
    private sealed record StatusPop(Guid ItemId, string ColumnKey, double Left, double Top);

    private StatusPop? _statusPop;
    private int _statusFocus;
    private bool _focusStatus;
    private ElementReference _statusRef;

    private GridColumn? StatusColumn => _statusPop is { } pop ? _columns.FirstOrDefault(c => c.Key == pop.ColumnKey) : null;

    private ItemDto? StatusItem => _statusPop is { } pop ? FindItem(pop.ItemId) : null;

    private static bool IsStatus(GridColumn column) => column.ValueKind == ColumnKind.Dropdown && column.Column is not null;

    private async Task OpenStatusAsync(GridRow row, int colIndex, MouseEventArgs? click = null)
    {
        var column = _columns[colIndex];
        if (!IsStatus(column) || !CanSet(row, column))
        {
            return;
        }

        double left = click?.ClientX ?? 0, top = click?.ClientY ?? 0;
        if (_module is not null)
        {
            try
            {
                // Under the cell, so the list doesn't cover the label being changed.
                if (await _module.InvokeAsync<double[]?>("cellRect", _container, IndexOf(row), colIndex) is [var x, var y, ..])
                {
                    (left, top) = (x, y);
                }
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException)
            {
            }
        }

        var options = column.Column!.Options;
        var current = Raw(row.Item, column);
        _statusFocus = Math.Max(0, options.ToList().FindIndex(o => string.Equals(o, current, StringComparison.OrdinalIgnoreCase)));
        _statusPop = new StatusPop(row.Item.Id, column.Key, left, top);
        _focusStatus = true;
    }

    private void CloseStatus()
    {
        _statusPop = null;
        _focusGrid = true;
    }

    private async Task PickStatusAsync(string? option)
    {
        if (StatusItem is not { } item || StatusColumn is not { } column)
        {
            CloseStatus();
            return;
        }

        CloseStatus();
        await SaveAsync(item, column, option);
    }

    private async Task OnStatusKeyAsync(KeyboardEventArgs e)
    {
        if (StatusColumn?.Column is not { } column)
        {
            CloseStatus();
            return;
        }

        var count = column.Options.Count + 1; // the labels and "Clear"
        switch (e.Key)
        {
            case "Escape" or "Tab":
                CloseStatus();
                break;
            case "ArrowDown":
                _statusFocus = (_statusFocus + 1) % count;
                _focusStatus = true;
                break;
            case "ArrowUp":
                _statusFocus = (_statusFocus - 1 + count) % count;
                _focusStatus = true;
                break;
            case "Home":
                _statusFocus = 0;
                _focusStatus = true;
                break;
            case "End":
                _statusFocus = count - 1;
                _focusStatus = true;
                break;
            case { Length: 1 } key when char.IsDigit(key[0]) && key[0] != '0' && key[0] - '1' < column.Options.Count:
                await PickStatusAsync(column.Options[key[0] - '1']);
                break;
        }
    }

    private async Task FocusStatusAsync()
    {
        if (!_focusStatus || _statusPop is null || _module is null)
        {
            return;
        }

        _focusStatus = false;
        try
        {
            await _module.InvokeVoidAsync("focusIn", _statusRef, $"[data-index='{_statusFocus}']");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException)
        {
        }
    }

    private async Task EditLabelsAsync()
    {
        var column = StatusColumn?.Column;
        CloseStatus();
        if (column is not null)
        {
            await EditColumnAsync(column);
        }
    }

    private static string PillStyle(StatusColor color) => $"background:{color.Background};color:{color.Text}";
}
