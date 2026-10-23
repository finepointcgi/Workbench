using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Grid;

// Sort, filter and hidden columns on any sheet, saved views (personal or shared) with a default per sheet, and exports
// of the sheet or the view on screen. The rules are SheetViewEngine's, shared with the server's export.
public partial class SheetGrid
{
    private SheetViewDefinition _view = SheetViewDefinition.Standard;
    private SheetViewDto? _savedView;
    private SheetViewsDto? _views;
    private Guid? _viewsFor;
    private IReadOnlyList<GridColumn> _allColumns = [];
    private IReadOnlySet<Guid>? _visible;

    /// <summary>Saved views and exports where the service is registered (null in tests and hosts without it).</summary>
    private ISheetViewService? ViewService => Services.GetService<ISheetViewService>();

    /// <summary>A sorted or filtered grid shows rows out of their real order, so indent, outdent and Insert wait for it to clear.</summary>
    private bool Rearranged => _view.HasSort || _view.HasFilters;

    private bool ViewChanged => Json(_view) != Json(_savedView?.Definition ?? SheetViewDefinition.Standard);

    private string ViewName => _savedView?.Name ?? "Standard view";

    /// <summary>The fields a view can sort and filter on: Title, Start and Finish, the sheet's columns, WBS.</summary>
    private IReadOnlyList<ViewField> Fields =>
        _allColumns
            .Where(c => c.Kind is GridColumnKind.Title or GridColumnKind.Start or GridColumnKind.Finish or GridColumnKind.Custom or GridColumnKind.Wbs)
            .Select(c => new ViewField(c.Key, c.Name, c.ValueKind, c.Column?.Options ?? []))
            .ToList();

    private static string Json(SheetViewDefinition definition) => JsonSerializer.Serialize(new
    {
        Sort = definition.Sort ?? [],
        Filters = definition.Filters ?? [],
        Hidden = (definition.HiddenColumns ?? []).Order(StringComparer.Ordinal),
        definition.BaselineId,
    });

    /// <summary>Shown columns: every column but the hidden ones (Title always shows).</summary>
    private void RebuildColumns()
    {
        _allColumns = GridColumn.For(Sheet, assignees: Tasks is not null, plan: Scheduling && Plan is not null,
            baseline: CanCompareBaselines && _view.BaselineId is not null);
        _columns = _allColumns.Where(c => c.Kind == GridColumnKind.Title || !_view.IsHidden(c.Key)).ToList();
        _activeCol = Math.Clamp(_activeCol, 0, Math.Max(0, _columns.Count - 1));
    }

    private void SetView(SheetViewDefinition definition)
    {
        var baselineChanged = definition.BaselineId != _view.BaselineId;
        _view = definition;
        if (baselineChanged)
        {
            _comparison = null;
            _ = RefreshComparisonAsync();
        }

        CollapseRange();
        RebuildColumns();
        _visible = SheetViewEngine.Visible(Items, Sheet.Columns, _view);
        RebuildRows();
    }

    private void UseView(SheetViewDto? view)
    {
        _savedView = view;
        SetView(view?.Definition ?? SheetViewDefinition.Standard);
    }

    private async Task LoadViewsAsync(bool openDefault)
    {
        if (ViewService is not { } service)
        {
            return;
        }

        try
        {
            _views = await service.ListAsync(SheetId);
            if (openDefault)
            {
                UseView(_views.DefaultView);
            }
            else if (_savedView is { } current)
            {
                _savedView = _views.Views.FirstOrDefault(v => v.Id == current.Id);
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _views = null;
        }
    }

    // ---- Sort, filter, columns ----

    private void SortBy(GridColumn column, bool descending) => SetView(_view with { Sort = [new ListSort(column.Key, descending)] });

    private void ClearSort() => SetView(_view with { Sort = [] });

    private void ClearFilters() => SetView(_view with { Filters = [] });

    private void RemoveFilter(ListFilterCondition filter) => SetView(_view with { Filters = (_view.Filters ?? []).Where(f => f != filter).ToList() });

    private void HideColumn(GridColumn column) => SetView(_view with { HiddenColumns = [.. _view.HiddenColumns ?? [], column.Key] });

    private void ShowAllColumns() => SetView(_view with { HiddenColumns = [] });

    private void ResetView() => UseView(_savedView);

    private async Task SortDialogAsync()
    {
        var parameters = new DialogParameters<SortDialog> { { d => d.Fields, Fields }, { d => d.Sort, _view.Sort ?? [] } };
        var dialog = await DialogService.ShowAsync<SortDialog>("Sort rows", parameters, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        if (await dialog.Result is { Canceled: false, Data: IReadOnlyList<ListSort> sort })
        {
            SetView(_view with { Sort = sort });
        }

        _focusGrid = true;
    }

    private async Task FilterDialogAsync(GridColumn? column = null)
    {
        var filters = (_view.Filters ?? []).ToList();
        if (column is not null && filters.All(f => f.Field != column.Key))
        {
            var kind = column.ValueKind;
            filters.Add(new ListFilterCondition(column.Key, SheetFilterText.OperatorsFor(kind)[0]));
        }

        var parameters = new DialogParameters<FilterDialog> { { d => d.Fields, Fields }, { d => d.Filters, filters } };
        var dialog = await DialogService.ShowAsync<FilterDialog>("Filter rows", parameters, new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        if (await dialog.Result is { Canceled: false, Data: IReadOnlyList<ListFilterCondition> result })
        {
            SetView(_view with { Filters = result });
        }

        _focusGrid = true;
    }

    private async Task ColumnsDialogAsync()
    {
        var parameters = new DialogParameters<HiddenColumnsDialog>
        {
            { d => d.Columns, _allColumns.Where(c => c.Kind != GridColumnKind.Title).Select(c => (c.Key, c.Name)).ToList() },
            { d => d.Hidden, _view.HiddenColumns ?? [] },
        };
        var dialog = await DialogService.ShowAsync<HiddenColumnsDialog>("Show and hide columns", parameters, new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true });
        if (await dialog.Result is { Canceled: false, Data: IReadOnlyList<string> hidden })
        {
            SetView(_view with { HiddenColumns = hidden });
        }

        _focusGrid = true;
    }

    private string FieldName(string key) => Fields.FirstOrDefault(f => f.Key == key)?.Name ?? key;

    private string Describe(ListFilterCondition filter)
    {
        var field = Fields.FirstOrDefault(f => f.Key == filter.Field);
        var values = SheetViewEngine.Values(filter.Value).Select(v => field is null ? v : CellFormat.Display(v, field.Kind == ColumnKind.Checkbox ? ColumnKind.Text : field.Kind));
        var shown = field?.Kind == ColumnKind.Checkbox ? (filter.Value == "true" ? "checked" : "not checked") : string.Join(" or ", values);
        return field?.Kind == ColumnKind.Checkbox
            ? $"{FieldName(filter.Field)} is {shown}"
            : $"{FieldName(filter.Field)} {SheetFilterText.Operator(filter.Operator)} {shown}".TrimEnd();
    }

    // ---- Saved views ----

    private async Task SaveViewAsync(bool asNew)
    {
        if (ViewService is not { } service)
        {
            return;
        }

        var existing = asNew ? null : _savedView;
        var parameters = new DialogParameters<SaveViewDialog>
        {
            { d => d.Name, existing?.Name ?? string.Empty },
            { d => d.IsShared, existing?.IsShared ?? false },
            { d => d.CanShare, _views?.CanShare ?? false },
            { d => d.IsMyDefault, existing is not null && _views?.MyDefaultViewId == existing.Id },
            { d => d.IsSheetDefault, existing is not null && _views?.SheetDefaultViewId == existing.Id },
        };
        var dialog = await DialogService.ShowAsync<SaveViewDialog>(existing is null ? "Save view" : $"Save \"{existing.Name}\"", parameters,
            new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true });
        if (await dialog.Result is not { Canceled: false, Data: SaveViewResult result })
        {
            return;
        }

        try
        {
            var request = new SaveSheetViewRequest(result.Name, result.IsShared, _view);
            var saved = existing is null ? await service.CreateAsync(SheetId, request) : await service.UpdateAsync(SheetId, existing.Id, request);
            if (result.IsMyDefault != (_views?.MyDefaultViewId == saved.Id))
            {
                await service.SetMyDefaultAsync(SheetId, new SetDefaultSheetViewRequest(result.IsMyDefault ? saved.Id : null));
            }

            if (saved.IsShared && result.IsSheetDefault != (_views?.SheetDefaultViewId == saved.Id))
            {
                await service.SetSheetDefaultAsync(SheetId, new SetDefaultSheetViewRequest(result.IsSheetDefault ? saved.Id : null));
            }

            _savedView = saved;
            await LoadViewsAsync(openDefault: false);
            Snackbar.Add($"Saved the view \"{saved.Name}\"{(saved.IsShared ? " for everyone on this sheet" : "")}.", Severity.Success);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Snackbar.Add($"Couldn't save the view: {ex.Message}", Severity.Error);
        }
    }

    private async Task DeleteViewAsync(SheetViewDto view)
    {
        if (ViewService is not { } service)
        {
            return;
        }

        var parameters = new DialogParameters<ConfirmDialog>
        {
            { d => d.Message, view.IsShared ? $"Delete the shared view \"{view.Name}\" for everyone on this sheet?" : $"Delete your view \"{view.Name}\"?" },
        };
        var confirm = await DialogService.ShowAsync<ConfirmDialog>("Delete view", parameters);
        if (await confirm.Result is null or { Canceled: true })
        {
            return;
        }

        try
        {
            await service.DeleteAsync(SheetId, view.Id);
            if (_savedView?.Id == view.Id)
            {
                UseView(null);
            }

            await LoadViewsAsync(openDefault: false);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Snackbar.Add($"Couldn't delete the view: {ex.Message}", Severity.Error);
        }
    }

    private async Task SetMyDefaultAsync(SheetViewDto? view)
    {
        if (ViewService is not { } service)
        {
            return;
        }

        try
        {
            await service.SetMyDefaultAsync(SheetId, new SetDefaultSheetViewRequest(view?.Id));
            await LoadViewsAsync(openDefault: false);
            Snackbar.Add(view is null ? "This sheet opens with its default view for you." : $"This sheet opens with \"{view.Name}\" for you.", Severity.Success);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Snackbar.Add($"Couldn't set your default view: {ex.Message}", Severity.Error);
        }
    }

    private async Task SetSheetDefaultAsync(SheetViewDto? view)
    {
        if (ViewService is not { } service)
        {
            return;
        }

        try
        {
            await service.SetSheetDefaultAsync(SheetId, new SetDefaultSheetViewRequest(view?.Id));
            await LoadViewsAsync(openDefault: false);
            Snackbar.Add(view is null ? "The sheet opens with the standard view." : $"Everyone opens this sheet with \"{view.Name}\" unless they picked their own.", Severity.Success);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Snackbar.Add($"Couldn't set the sheet's default view: {ex.Message}", Severity.Error);
        }
    }

    // ---- Export ----

    private async Task ExportAsync(SheetExportFormat format, bool currentView)
    {
        if (ViewService is not { } service)
        {
            return;
        }

        try
        {
            var file = await service.ExportAsync(SheetId,
                currentView ? new SheetExportRequest(format, _view, _savedView?.Name ?? (_view.IsStandard ? null : "Current view")) : new SheetExportRequest(format));
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("downloadBase64", file.FileName, file.ContentType, Convert.ToBase64String(file.Content));
            }
        }
        catch (Exception ex) when (IsExpected(ex) || ex is JSException)
        {
            Snackbar.Add($"Couldn't export the sheet: {ex.Message}", Severity.Error);
        }
    }
}

/// <summary>A field a view can sort or filter on.</summary>
public sealed record ViewField(string Key, string Name, ColumnKind Kind, IReadOnlyList<string> Options);

/// <summary>What <see cref="SaveViewDialog"/> returns.</summary>
public sealed record SaveViewResult(string Name, bool IsShared, bool IsMyDefault, bool IsSheetDefault);
