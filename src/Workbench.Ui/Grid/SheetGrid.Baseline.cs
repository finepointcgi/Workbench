using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Grid;

// Compare to a baseline: on a scheduled sheet, the grid can show each task's baseline start and finish and how far its finish
// has moved (in the plan's working days, worked out by IBaselineService). The baseline is part of the view, so a saved view can
// be "the plan against the approved baseline".
public partial class SheetGrid
{
    private IReadOnlyList<BaselineSummary> _baselines = [];
    private BaselineComparison? _comparison;
    private int _comparisonLoad;

    /// <summary>Saved baselines where the service is registered (null in tests and hosts without it).</summary>
    private IBaselineService? BaselineService => Services.GetService<IBaselineService>();

    private bool CanCompareBaselines => Scheduling && BaselineService is not null;

    private BaselineSummary? ComparedBaseline => _view.BaselineId is { } id ? _baselines.FirstOrDefault(b => b.Id == id) ?? _comparison?.Baseline : null;

    private async Task LoadBaselinesAsync()
    {
        if (!CanCompareBaselines)
        {
            return;
        }

        try
        {
            _baselines = await BaselineService!.ListAsync(SheetId);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _baselines = [];
        }
    }

    /// <summary>Loads the comparison for the view's baseline (again after the plan changes). Late answers are dropped.</summary>
    private async Task LoadComparisonAsync()
    {
        var load = ++_comparisonLoad;
        if (!CanCompareBaselines || _view.BaselineId is not { } baselineId)
        {
            _comparison = null;
            return;
        }

        try
        {
            var comparison = await BaselineService!.CompareAsync(SheetId, baselineId);
            if (load == _comparisonLoad)
            {
                _comparison = comparison;
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (load == _comparisonLoad)
            {
                // The baseline was deleted (or can't be read): stop comparing rather than show empty columns.
                _comparison = null;
                _view = _view with { BaselineId = null };
                RebuildColumns();
                RebuildRows();
                Snackbar.Add("That baseline isn't available any more.", Severity.Info);
            }
        }
    }

    private async Task RefreshComparisonAsync()
    {
        await LoadComparisonAsync();
        await InvokeAsync(StateHasChanged);
    }

    private void CompareTo(BaselineSummary? baseline) => SetView(_view with { BaselineId = baseline?.Id });

    private async Task SaveBaselineAsync()
    {
        if (BaselineService is not { } service || !CanEdit)
        {
            return;
        }

        try
        {
            var saved = await service.CaptureAsync(SheetId, new CaptureBaselineRequest(null));
            await LoadBaselinesAsync();
            CompareTo(saved);
            Snackbar.Add($"Saved \"{saved.Name}\". The grid now compares the plan to it.", Severity.Success);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Snackbar.Add($"Couldn't save a baseline: {ex.Message}", Severity.Error);
        }
    }

    /// <summary>The stored value of a baseline cell (dates as yyyy-MM-dd, the variance as whole days).</summary>
    private string? BaselineRaw(ItemDto item, GridColumn column)
    {
        if (_comparison?.For(item.Id) is not { } task)
        {
            return null;
        }

        return column.Kind switch
        {
            GridColumnKind.BaselineStart => task.BaselineStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            GridColumnKind.BaselineFinish => task.BaselineFinish?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => task.InBaseline ? task.FinishVarianceDays?.ToString(CultureInfo.InvariantCulture) : null,
        };
    }

    /// <summary>"+2 days", "−1 day" or "On time": the sign and words carry the meaning, the color only repeats it.</summary>
    private static (string Text, string Css, string Title) Variance(string? raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
        {
            return (string.Empty, string.Empty, "Not in the baseline");
        }

        var unit = Math.Abs(days) == 1 ? "day" : "days";
        return days switch
        {
            > 0 => ($"+{days} {unit}", "wb-late", $"Finishes {days} working {unit} later than the baseline"),
            < 0 => ($"−{-days} {unit}", "wb-early", $"Finishes {-days} working {unit} earlier than the baseline"),
            _ => ("On time", "wb-ontime", "Finishes when the baseline said"),
        };
    }
}
