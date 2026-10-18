using System.Globalization;
using Workbench.Application.MyWork;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Dashboards;

/// <summary>A widget's configuration doesn't fit its sheet any more (e.g. a column was deleted). Shown on the widget.</summary>
public sealed class WidgetConfigException(string message) : Exception(message);

/// <summary>
/// Pure widget calculations over data already read through the contracts. Only leaf items count: parent rows are
/// summaries of their children, so including them would double count.
/// </summary>
public static class WidgetCalculator
{
    public const string BlankLabel = "(none)";

    public static MetricData Metric(SheetDetail sheet, IReadOnlyList<ItemDto> items, WidgetConfig config)
    {
        var matching = Filtered(sheet, items, config).ToList();
        if (config.Aggregate == MetricAggregate.Count)
        {
            return new MetricData(matching.Count, "Items" + FilterSuffix(sheet, config), matching.Count, false);
        }

        var column = NumericColumn(sheet, config.ValueColumnId, "Pick a Number or Currency column to " + config.Aggregate.ToString().ToLowerInvariant());
        var numbers = matching.Select(i => Number(i, column)).OfType<decimal>().ToList();
        var value = config.Aggregate == MetricAggregate.Sum ? numbers.Sum()
            : numbers.Count == 0 ? 0m : Math.Round(numbers.Average(), 2);
        var label = $"{(config.Aggregate == MetricAggregate.Sum ? "Sum" : "Average")} of {column.Name}{FilterSuffix(sheet, config)}";
        return new MetricData(value, label, matching.Count, column.Type == ColumnKind.Currency);
    }

    public static ChartData Chart(SheetDetail sheet, IReadOnlyList<ItemDto> items, WidgetConfig config)
    {
        var group = Column(sheet, config.GroupColumnId, "The group column")
            ?? throw new WidgetConfigException("Pick a column to group by.");
        if (group.Type is not (ColumnKind.Dropdown or ColumnKind.Person))
        {
            throw new WidgetConfigException($"Charts group by a Dropdown or Person column; \"{group.Name}\" is {group.Type}.");
        }

        var sum = config.ValueColumnId is null ? null : NumericColumn(sheet, config.ValueColumnId, "Pick a Number or Currency column to sum");
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Filtered(sheet, items, config))
        {
            var key = MyWorkCalculator.ValueOf(item, group) ?? BlankLabel;
            var amount = sum is null ? 1d : (double)(Number(item, sum) ?? 0m);
            totals[key] = totals.GetValueOrDefault(key) + amount;
        }

        // Dropdowns keep their option order (even empty options, so a bar chart shows the zero); people sort by name.
        var labels = group.Type == ColumnKind.Dropdown
            ? group.Options.Concat(totals.Keys.Where(k => k != BlankLabel && !group.Options.Contains(k, StringComparer.OrdinalIgnoreCase)).Order())
            : totals.Keys.Where(k => k != BlankLabel).Order(StringComparer.OrdinalIgnoreCase);
        var ordered = labels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (totals.ContainsKey(BlankLabel))
        {
            ordered.Add(BlankLabel);
        }

        return new ChartData(config.Chart, ordered, ordered.Select(l => totals.GetValueOrDefault(l)).ToList(),
            sum is null ? "Items" : sum.Name);
    }

    public static ItemListData ItemList(SheetDetail sheet, IReadOnlyList<ItemDto> items, WidgetConfig config)
    {
        var status = MyWorkCalculator.StatusColumn(sheet);
        var matching = Filtered(sheet, items, config)
            .OrderBy(i => i.Finish ?? DateOnly.MaxValue)
            .ThenBy(i => i.Order)
            .ToList();
        var rows = matching
            .Take(Math.Clamp(config.MaxItems, 1, 100))
            .Select(i => new WidgetItemRow(i.Id, i.Title, i.Finish, status is null ? null : MyWorkCalculator.ValueOf(i, status)))
            .ToList();
        return new ItemListData(rows, matching.Count);
    }

    public static ScheduleHealthData ScheduleHealth(SheetDetail sheet, IReadOnlyList<ItemDto> items, ScheduleAnalysis analysis, DateOnly today)
    {
        var status = MyWorkCalculator.StatusColumn(sheet);
        var leaves = Leaves(items).ToList();
        var late = leaves.Count(i => i.Finish < today && !MyWorkCalculator.IsDoneStatus(status is null ? null : MyWorkCalculator.ValueOf(i, status)));
        var milestones = leaves
            .Where(i => i.Finish is { } f && f >= today && (i.Start is null || i.Start == i.Finish))
            .OrderBy(i => i.Finish)
            .ThenBy(i => i.Order)
            .Take(5)
            .Select(i => new MilestoneRow(i.Id, i.Title, i.Finish!.Value))
            .ToList();
        return new ScheduleHealthData(analysis.Items.Count, analysis.CriticalPath.Count, late, analysis.ProjectFinish, milestones);
    }

    /// <summary>Contract value and period of performance, with the projects charged against it.</summary>
    public static ContractFundingData ContractFunding(ContractDto contract, IEnumerable<ProjectDto> projects, DateOnly today)
    {
        var totalDays = contract.PeriodEnd.DayNumber - contract.PeriodStart.DayNumber;
        var elapsed = totalDays <= 0 ? (today >= contract.PeriodEnd ? 1d : 0d)
            : Math.Clamp((today.DayNumber - contract.PeriodStart.DayNumber) / (double)totalDays, 0d, 1d);
        var codes = projects.Where(p => p.ContractId == contract.Id).Select(p => p.Code).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new ContractFundingData(contract.Id, contract.ContractNumber, contract.Name, contract.CustomerName, contract.Status.ToString(),
            contract.CeilingValue, contract.FundedValue, contract.PeriodStart, contract.PeriodEnd,
            Math.Max(0, contract.PeriodEnd.DayNumber - Math.Max(today.DayNumber, contract.PeriodStart.DayNumber)), elapsed, codes);
    }

    /// <summary>Leaf items that pass the optional "column equals value" filter (case-insensitive; blank matches empty cells).</summary>
    public static IEnumerable<ItemDto> Filtered(SheetDetail sheet, IReadOnlyList<ItemDto> items, WidgetConfig config)
    {
        var leaves = Leaves(items);
        if (config.FilterColumnId is null)
        {
            return leaves;
        }

        var column = Column(sheet, config.FilterColumnId, "The filter column")!;
        var wanted = string.IsNullOrWhiteSpace(config.FilterValue) ? null : config.FilterValue.Trim();
        return leaves.Where(i => string.Equals(MyWorkCalculator.ValueOf(i, column), wanted, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<ItemDto> Leaves(IReadOnlyList<ItemDto> items)
    {
        var parents = items.Where(i => i.ParentId is not null).Select(i => i.ParentId!.Value).ToHashSet();
        return items.Where(i => !parents.Contains(i.Id));
    }

    private static ColumnDto? Column(SheetDetail sheet, Guid? columnId, string what)
    {
        if (columnId is null)
        {
            return null;
        }

        return sheet.Columns.FirstOrDefault(c => c.Id == columnId)
            ?? throw new WidgetConfigException($"{what} no longer exists on \"{sheet.Summary.Name}\". Edit the widget to pick another.");
    }

    private static ColumnDto NumericColumn(SheetDetail sheet, Guid? columnId, string missing)
    {
        var column = Column(sheet, columnId, "The value column") ?? throw new WidgetConfigException(missing + ".");
        return column.Type is ColumnKind.Number or ColumnKind.Currency || column.Formula is not null
            ? column
            : throw new WidgetConfigException($"\"{column.Name}\" isn't a Number or Currency column.");
    }

    private static decimal? Number(ItemDto item, ColumnDto column) =>
        decimal.TryParse(MyWorkCalculator.ValueOf(item, column), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string FilterSuffix(SheetDetail sheet, WidgetConfig config)
    {
        var column = config.FilterColumnId is null ? null : sheet.Columns.FirstOrDefault(c => c.Id == config.FilterColumnId);
        return column is null ? string.Empty : $" where {column.Name} = {(string.IsNullOrWhiteSpace(config.FilterValue) ? BlankLabel : config.FilterValue.Trim())}";
    }
}
