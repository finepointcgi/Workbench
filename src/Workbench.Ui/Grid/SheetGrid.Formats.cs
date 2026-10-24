using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Grid;

// Conditional formatting ("when Status is Stuck, color the row red"), and on plans the total and free float columns with a
// "critical tasks only" filter. Rules come from ISheetFormatRuleService (restricted rules are already left out for people who
// can't see the column); float from IPlanFloatService, which reads the plan's critical-path analysis.
public partial class SheetGrid
{
    private SheetFormatRulesDto _rules = SheetFormatRulesDto.Empty;
    private SheetFormats _formats = SheetFormats.None;
    private PlanFloatDto? _float;
    private int _floatLoad;

    private ISheetFormatRuleService? RuleService => Services.GetService<ISheetFormatRuleService>();

    private IPlanFloatService? FloatService => Services.GetService<IPlanFloatService>();

    /// <summary>Float columns and the critical filter: plans only (the analysis needs dates and links).</summary>
    private bool CanShowFloat => Scheduling && FloatService is not null;

    /// <summary>The critical tasks while "critical only" is on (null when it's off or the analysis isn't loaded).</summary>
    private IReadOnlySet<Guid>? CriticalOnly =>
        CanShowFloat && _view.CriticalOnly && _float is not null ? _float.Tasks.Where(t => t.IsCritical).Select(t => t.ItemId).ToHashSet() : null;

    private async Task LoadRulesAsync()
    {
        if (RuleService is not { } service)
        {
            return;
        }

        try
        {
            _rules = await service.ListAsync(SheetId);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _rules = SheetFormatRulesDto.Empty;
        }

        RecomputeFormats();
    }

    /// <summary>Works the rules out again for the rows on hand (after a reload or a rule change).</summary>
    private void RecomputeFormats() =>
        _formats = _rules.Rules.Count == 0
            ? SheetFormats.None
            : SheetFormats.Evaluate(Items, Sheet.Columns, _rules.Rules.Select(r => r.Definition).ToList(), DateOnly.FromDateTime(DateTime.Now));

    /// <summary>A cell's inline style from the rules (rules on its column and on the whole row; td-level so sticky cells get it too).</summary>
    private string? CellStyle(GridRow row, GridColumn column) => _formats.Any ? _formats.Cell(row.Item.Id, column.Key).Css : null;

    /// <summary>Plain words for the rules a row matches (its row header's tooltip), so the formatting is explained, not only seen.</summary>
    private string? FormatReasons(GridRow row) => _formats.RulesFor(row.Item.Id) is { Count: > 0 } rules
        ? string.Join("; ", rules.Select(r => SheetFormatText.Describe(r, FieldName)))
        : null;

    private async Task FormatRulesDialogAsync()
    {
        if (RuleService is not { } service)
        {
            return;
        }

        var parameters = new DialogParameters<FormatRulesDialog>
        {
            { d => d.Fields, Fields },
            { d => d.Rules, _rules.Rules },
            { d => d.HiddenCount, _rules.HiddenCount },
            { d => d.CanEdit, CanEdit && _rules.CanEdit },
        };
        var dialog = await DialogService.ShowAsync<FormatRulesDialog>("Conditional formatting", parameters,
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        if (await dialog.Result is not { Canceled: false, Data: IReadOnlyList<SaveSheetFormatRule> rules })
        {
            _focusGrid = true;
            return;
        }

        try
        {
            _rules = await service.SaveAsync(SheetId, new SaveSheetFormatRulesRequest(rules));
            RecomputeFormats();
            Snackbar.Add(rules.Count == 0 ? "Removed the formatting rules." : $"Saved {rules.Count} formatting {(rules.Count == 1 ? "rule" : "rules")}.", Severity.Success);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Snackbar.Add($"Couldn't save the rules: {ex.Message}", Severity.Error);
        }

        _focusGrid = true;
    }

    // ---- Float ----

    private async Task LoadFloatAsync()
    {
        var load = ++_floatLoad;
        if (!CanShowFloat || !(_view.ShowFloat || _view.CriticalOnly))
        {
            _float = null;
            return;
        }

        try
        {
            var result = await FloatService!.GetAsync(SheetId);
            if (load == _floatLoad)
            {
                _float = result;
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (load == _floatLoad)
            {
                _float = PlanFloatDto.Empty;
            }
        }
    }

    private async Task RefreshFloatAsync()
    {
        await LoadFloatAsync();
        RebuildRows();
        await InvokeAsync(StateHasChanged);
    }

    private void ToggleFloatColumns() => SetView(_view with { ShowFloat = !_view.ShowFloat });

    private void ToggleCriticalOnly() => SetView(_view with { CriticalOnly = !_view.CriticalOnly });

    /// <summary>A float cell's stored value: whole working days.</summary>
    private string? FloatRaw(ItemDto item, GridColumn column) => _float?.For(item.Id) is { } task
        ? (column.Kind == GridColumnKind.TotalFloat ? task.TotalFloatDays : task.FreeFloatDays).ToString(CultureInfo.InvariantCulture)
        : null;

    /// <summary>"3 days", with "Critical" spelled out (not just colored) for a task with no total float.</summary>
    private static (string Text, bool Critical) FloatText(string? raw, GridColumn column)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
        {
            return (string.Empty, false);
        }

        var text = $"{days} {(Math.Abs(days) == 1 ? "day" : "days")}";
        return column.Kind == GridColumnKind.TotalFloat && days <= 0 ? (text, true) : (text, false);
    }

    // ---- Newer column types ----

    /// <summary>A symbol: its glyph (decorative) and its text label, so it never relies on color.</summary>
    private static SymbolOption? SymbolOf(string? raw) => SymbolSets.Find(raw);

    private static string ProgressWidth(string? raw) =>
        (ColumnValues.Percent(raw) ?? 0).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>Picks a multi-select cell's choices or a link's text and address in a dialog (typing in the cell works too).</summary>
    private async Task EditInDialogAsync(ItemDto item, GridColumn column)
    {
        var raw = Raw(item, column);
        string? value;
        if (column.ValueKind == ColumnKind.MultiSelect)
        {
            var parameters = new DialogParameters<MultiSelectDialog> { { d => d.Options, column.Column!.Options }, { d => d.Selected, ColumnValues.Selected(raw) } };
            var dialog = await DialogService.ShowAsync<MultiSelectDialog>(column.Name, parameters, new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true });
            if (await dialog.Result is not { Canceled: false, Data: IReadOnlyList<string> picked })
            {
                _focusGrid = true;
                return;
            }

            value = ColumnValues.JoinSelected(picked);
        }
        else
        {
            var link = ColumnValues.Link(raw);
            var parameters = new DialogParameters<LinkDialog> { { d => d.Url, link?.Url ?? string.Empty }, { d => d.Text, link is { } l && l.Text != l.Url ? l.Text : string.Empty } };
            var dialog = await DialogService.ShowAsync<LinkDialog>(column.Name, parameters, new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true });
            if (await dialog.Result is not { Canceled: false, Data: string stored })
            {
                _focusGrid = true;
                return;
            }

            value = stored.Length == 0 ? null : stored;
        }

        _focusGrid = true;
        await SaveAsync(item, column, value);
    }
}
