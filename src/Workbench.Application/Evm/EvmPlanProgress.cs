using System.Globalization;
using Workbench.Domain;

namespace Workbench.Application.Evm;

/// <summary>Progress read from one plan-sheet task (or rolled up from its subtasks).</summary>
public sealed record PlanTaskProgress(bool Started, bool Complete, decimal Percent, string Source);

/// <summary>
/// Reads progress from the project's plan sheet: a "% complete" number column (0–100) when the sheet has one, otherwise the
/// "Status" dropdown (Done = 100%, anything past "Not started" = 50%). A task with subtasks takes the average of its leaf
/// subtasks; it has started when any has, and is complete when all are.
/// </summary>
public sealed class EvmPlanProgress
{
    private static readonly string[] PercentColumnNames = ["% complete", "percent complete", "% done", "progress"];
    private static readonly string[] DoneValues = ["done", "complete", "completed", "closed"];
    private static readonly string[] NotStartedValues = ["", "not started", "to do", "todo", "planned", "new"];

    private readonly Dictionary<Guid, Item> _items;
    private readonly ILookup<Guid, Item> _children;
    private readonly string? _statusColumn;
    private readonly string? _percentColumn;

    public EvmPlanProgress(IEnumerable<Item> items, IEnumerable<Column> columns)
    {
        _items = items.ToDictionary(i => i.Id);
        _children = _items.Values.Where(i => i.ParentId is not null).ToLookup(i => i.ParentId!.Value);
        var cols = columns.ToList();
        _statusColumn = cols.FirstOrDefault(c => c.Type == ColumnType.Dropdown && string.Equals(c.Name.Trim(), "Status", StringComparison.OrdinalIgnoreCase))?.Id.ToString();
        _percentColumn = cols.FirstOrDefault(c => c.Type == ColumnType.Number && c.Formula is null
            && PercentColumnNames.Contains(c.Name.Trim(), StringComparer.OrdinalIgnoreCase))?.Id.ToString();
    }

    public static EvmPlanProgress Empty { get; } = new([], []);

    public bool Has(Guid itemId) => _items.ContainsKey(itemId);

    public Item? Find(Guid itemId) => _items.GetValueOrDefault(itemId);

    public IReadOnlyList<Item> ChildrenOf(Guid itemId) => _children[itemId].OrderBy(i => i.Order).ToList();

    /// <summary>True when the task (all of its subtasks, if it has any) is done.</summary>
    public bool IsDone(Guid itemId) => Of(itemId)?.Complete == true;

    public PlanTaskProgress? Of(Guid itemId)
    {
        if (!_items.TryGetValue(itemId, out var item))
        {
            return null;
        }

        var leaves = Leaves(item, []).ToList();
        if (leaves.Count == 1 && leaves[0].Id == item.Id)
        {
            return OfLeaf(item);
        }

        var parts = leaves.Select(OfLeaf).ToList();
        var percent = Math.Round(parts.Average(p => p.Percent), 2, MidpointRounding.AwayFromZero);
        var complete = parts.All(p => p.Complete);
        return new PlanTaskProgress(parts.Any(p => p.Started), complete, complete ? 100m : percent,
            $"{parts.Count(p => p.Complete)} of {parts.Count} plan subtasks done");
    }

    private IEnumerable<Item> Leaves(Item item, HashSet<Guid> seen)
    {
        if (!seen.Add(item.Id))
        {
            yield break;
        }

        var children = _children[item.Id].ToList();
        if (children.Count == 0)
        {
            yield return item;
            yield break;
        }

        foreach (var leaf in children.SelectMany(c => Leaves(c, seen)))
        {
            yield return leaf;
        }
    }

    private PlanTaskProgress OfLeaf(Item item)
    {
        var status = _statusColumn is null ? null : item.Values.GetValueOrDefault(_statusColumn)?.Trim();
        var done = status is not null && DoneValues.Contains(status, StringComparer.OrdinalIgnoreCase);
        var started = done || (status is not null && !NotStartedValues.Contains(status, StringComparer.OrdinalIgnoreCase));
        if (_percentColumn is not null
            && item.Values.GetValueOrDefault(_percentColumn) is { } raw
            && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var pct))
        {
            pct = Math.Clamp(pct, 0m, 100m);
            var complete = done || pct >= 100m;
            return new PlanTaskProgress(started || pct > 0, complete, complete ? 100m : pct, "Plan sheet % complete");
        }

        return new PlanTaskProgress(started, done, done ? 100m : started ? 50m : 0m,
            status is null or "" ? "Plan sheet (no status)" : $"Plan sheet status: {status}");
    }
}
