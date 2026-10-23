using Workbench.Domain;
using Workbench.Domain.Formulas;

namespace Workbench.Application.Formulas;

/// <summary>
/// Computes formula columns for a whole sheet. Values are never stored; they are recalculated on read.
/// <para>
/// Order: formula columns that read each other on the same row (<c>[Other formula]</c>) are sorted topologically,
/// and items are visited children-first so <c>CHILDREN([Column])</c> always sees finished values. Columns in a
/// same-row reference cycle evaluate to #CIRCULAR! on every row. A column may roll up its own children
/// (<c>[Hours] + SUM(CHILDREN([Total]))</c>), since the item tree has no cycles.
/// </para>
/// </summary>
public static class FormulaCalculator
{
    /// <summary>Returns computed cell strings keyed by item id, then formula column id.</summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string?>> Calculate(
        IReadOnlyList<Column> columns, IReadOnlyList<Item> items, DateOnly today) => Calculate(columns, items, today, null);

    /// <summary>
    /// Like <see cref="Calculate(IReadOnlyList{Column}, IReadOnlyList{Item}, DateOnly)"/>, with the values of the sheet's
    /// cross-sheet references and the value types of lookup columns from <paramref name="inputs"/>.
    /// </summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string?>> Calculate(
        IReadOnlyList<Column> columns, IReadOnlyList<Item> items, DateOnly today, FormulaInputs? inputs)
    {
        var plan = Plan.Create(columns);
        var results = new Dictionary<Guid, Dictionary<Guid, FormulaValue>>();
        if (plan.FormulaColumns.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyDictionary<Guid, string?>>();
        }

        var tree = new ItemTree(items);
        var order = tree.PostOrder().ToList();
        var shared = new SheetState(plan, tree, results, today, inputs ?? new FormulaInputs());
        foreach (var item in order)
        {
            var row = new Dictionary<Guid, FormulaValue>();
            results[item.Id] = row;
            foreach (var column in plan.Circular)
            {
                row[column.Id] = FormulaValue.FromError(FormulaErrors.Circular);
            }
        }

        // A column reading a whole column of formulas ([Done]:[Done]) waits until that column is done on every row.
        foreach (var level in plan.Levels)
        {
            foreach (var item in order)
            {
                var row = results[item.Id];
                var context = new RowContext(item, shared);
                foreach (var column in level)
                {
                    row[column.Id] = plan.Parsed.TryGetValue(column.Id, out var formula)
                        ? formula.Evaluate(context)
                        : FormulaValue.FromError(FormulaErrors.Name);
                }
            }
        }

        return results.ToDictionary(
            r => r.Key,
            r => (IReadOnlyDictionary<Guid, string?>)r.Value.ToDictionary(v => v.Key, v => v.Value.ToCellString()));
    }

    /// <summary>
    /// The order formula columns are evaluated in on each row, and the columns that are part of a circular reference.
    /// </summary>
    public static (IReadOnlyList<Column> Order, IReadOnlyList<Column> Circular) EvaluationOrder(IReadOnlyList<Column> columns)
    {
        var plan = Plan.Create(columns);
        return (plan.Order, plan.Circular);
    }

    /// <summary>
    /// The columns a range <c>[From]:[To]</c> covers, in the order the grid shows them (Title, Start, Finish, then the
    /// sheet's columns); null when an end doesn't exist. Built-in fields come back as null columns with their name.
    /// </summary>
    public static IReadOnlyList<(string Name, Column? Column)>? RangeColumns(IReadOnlyList<Column> columns, string from, string to)
    {
        var layout = Layout(columns);
        var start = layout.FindIndex(l => string.Equals(l.Name, from.Trim(), StringComparison.OrdinalIgnoreCase));
        var end = layout.FindIndex(l => string.Equals(l.Name, to.Trim(), StringComparison.OrdinalIgnoreCase));
        if (start < 0 || end < 0)
        {
            return null;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }

        return layout.GetRange(start, end - start + 1);
    }

    /// <summary>Built-in fields first, then each name's first column by order (later duplicates are unreachable, as for [Name]).</summary>
    private static List<(string Name, Column? Column)> Layout(IReadOnlyList<Column> columns)
    {
        var byName = new Dictionary<string, Column>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns.OrderBy(c => c.Order))
        {
            byName.TryAdd(column.Name.Trim(), column);
        }

        var layout = new List<(string, Column?)>();
        foreach (var builtIn in Formula.BuiltInReferences.Where(b => !byName.ContainsKey(b)))
        {
            layout.Add((builtIn, null));
        }

        layout.AddRange(columns.OrderBy(c => c.Order).Where(c => byName[c.Name.Trim()] == c).Select(c => (c.Name.Trim(), (Column?)c)));
        return layout;
    }

    private sealed class Plan
    {
        public required Dictionary<string, Column> ByName { get; init; }

        public required List<Column> FormulaColumns { get; init; }

        /// <summary>Successfully parsed formulas; columns missing here have unparseable formulas (#NAME?).</summary>
        public required Dictionary<Guid, Formula> Parsed { get; init; }

        public required List<Column> Order { get; init; }

        public required List<Column> Circular { get; init; }

        /// <summary><see cref="Order"/> split into levels: a column reading whole formula columns comes after them.</summary>
        public required List<List<Column>> Levels { get; init; }

        public required IReadOnlyList<Column> Columns { get; init; }

        public static Plan Create(IReadOnlyList<Column> columns)
        {
            var ordered = columns.OrderBy(c => c.Order).ToList();

            // The first column with a name wins; columns can shadow the built-in Title/Start/Finish fields.
            var byName = new Dictionary<string, Column>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in ordered)
            {
                byName.TryAdd(column.Name.Trim(), column);
            }

            var formulaColumns = ordered.Where(c => !string.IsNullOrWhiteSpace(c.Formula)).ToList();
            var parsed = new Dictionary<Guid, Formula>();
            foreach (var column in formulaColumns)
            {
                try
                {
                    parsed[column.Id] = Formula.Parse(column.Formula!);
                }
                catch (FormulaParseException)
                {
                    // Left out of Parsed; evaluates to #NAME?.
                }
            }

            // Same-row edges only: column -> formula columns it reads with [Name].
            var dependencies = formulaColumns.ToDictionary(
                c => c.Id,
                c => parsed.TryGetValue(c.Id, out var f)
                    ? f.References
                        .Select(n => byName.GetValueOrDefault(n))
                        .Where(d => d is not null && !string.IsNullOrWhiteSpace(d.Formula))
                        .Select(d => d!.Id)
                        .Distinct()
                        .ToList()
                    : []);

            var circular = FindCycles(formulaColumns.Select(c => c.Id).ToList(), dependencies);
            var levels = AssignLevels(columns, formulaColumns, parsed, byName, circular);
            var order = TopologicalOrder(formulaColumns.Where(c => !circular.Contains(c.Id)).ToList(), dependencies);
            return new Plan
            {
                ByName = byName,
                FormulaColumns = formulaColumns,
                Parsed = parsed,
                Order = order,
                Circular = formulaColumns.Where(c => circular.Contains(c.Id)).ToList(),
                Levels = order.GroupBy(c => levels[c.Id]).OrderBy(g => g.Key).Select(g => g.ToList()).ToList(),
                Columns = columns,
            };
        }

        /// <summary>
        /// Level 0 unless a column reads whole formula columns through a range: then one above them. Same-row and
        /// CHILDREN reads keep a column at or above what it reads. A column that can never settle (it ranges over
        /// itself, directly or through others) joins <paramref name="circular"/>.
        /// </summary>
        private static Dictionary<Guid, int> AssignLevels(IReadOnlyList<Column> columns, List<Column> formulaColumns, Dictionary<Guid, Formula> parsed,
            Dictionary<string, Column> byName, HashSet<Guid> circular)
        {
            var formulaIds = formulaColumns.Where(c => !circular.Contains(c.Id)).Select(c => c.Id).ToHashSet();
            var atLeast = new Dictionary<Guid, List<Guid>>();
            var above = new Dictionary<Guid, List<Guid>>();
            foreach (var column in formulaColumns.Where(c => formulaIds.Contains(c.Id)))
            {
                atLeast[column.Id] = [];
                above[column.Id] = [];
                if (!parsed.TryGetValue(column.Id, out var formula))
                {
                    continue;
                }

                atLeast[column.Id].AddRange(formula.References.Concat(formula.ChildReferences)
                    .Select(n => byName.GetValueOrDefault(n)).Where(d => d is not null && formulaIds.Contains(d.Id)).Select(d => d!.Id));
                above[column.Id].AddRange(formula.ColumnRanges
                    .SelectMany(r => RangeColumns(columns, r.From, r.To) ?? [])
                    .Where(r => r.Column is not null && formulaIds.Contains(r.Column.Id)).Select(r => r.Column!.Id));
            }

            var levels = formulaIds.ToDictionary(id => id, _ => 0);
            var limit = formulaIds.Count + 1;
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var id in formulaIds)
                {
                    var level = Math.Max(
                        atLeast[id].Select(d => levels[d]).DefaultIfEmpty(0).Max(),
                        above[id].Select(d => levels[d] + 1).DefaultIfEmpty(0).Max());
                    if (level > levels[id] && level <= limit)
                    {
                        levels[id] = level;
                        changed = true;
                    }
                }
            }

            foreach (var id in formulaIds.Where(id => levels[id] >= limit || above[id].Any(d => levels[d] >= levels[id])))
            {
                circular.Add(id);
            }

            return levels;
        }

        /// <summary>Tarjan's strongly connected components; returns nodes in a component with a cycle.</summary>
        private static HashSet<Guid> FindCycles(List<Guid> nodes, Dictionary<Guid, List<Guid>> edges)
        {
            var index = 0;
            var indices = new Dictionary<Guid, int>();
            var lowLinks = new Dictionary<Guid, int>();
            var stack = new Stack<Guid>();
            var onStack = new HashSet<Guid>();
            var cyclic = new HashSet<Guid>();

            foreach (var node in nodes.Where(n => !indices.ContainsKey(n)))
            {
                Visit(node);
            }

            return cyclic;

            void Visit(Guid node)
            {
                indices[node] = lowLinks[node] = index++;
                stack.Push(node);
                onStack.Add(node);
                foreach (var next in edges[node])
                {
                    if (!indices.ContainsKey(next))
                    {
                        Visit(next);
                        lowLinks[node] = Math.Min(lowLinks[node], lowLinks[next]);
                    }
                    else if (onStack.Contains(next))
                    {
                        lowLinks[node] = Math.Min(lowLinks[node], indices[next]);
                    }
                }

                if (lowLinks[node] != indices[node])
                {
                    return;
                }

                var component = new List<Guid>();
                Guid member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member);
                    component.Add(member);
                }
                while (member != node);

                if (component.Count > 1 || edges[node].Contains(node))
                {
                    cyclic.UnionWith(component);
                }
            }
        }

        /// <summary>Dependencies first; ties keep sheet column order. Edges to circular columns are already satisfied.</summary>
        private static List<Column> TopologicalOrder(List<Column> columns, Dictionary<Guid, List<Guid>> edges)
        {
            var pending = columns.ToList();
            var done = new HashSet<Guid>();
            var included = columns.Select(c => c.Id).ToHashSet();
            var order = new List<Column>();
            while (pending.Count > 0)
            {
                var ready = pending.First(c => edges[c.Id].All(d => done.Contains(d) || !included.Contains(d)));
                pending.Remove(ready);
                done.Add(ready.Id);
                order.Add(ready);
            }

            return order;
        }
    }

    private sealed class ItemTree
    {
        private readonly IReadOnlyList<Item> _items;
        private readonly Dictionary<Guid, List<Item>> _children;

        public ItemTree(IReadOnlyList<Item> items)
        {
            _items = items;
            var ids = items.Select(i => i.Id).ToHashSet();
            _children = items
                .Where(i => i.ParentId is { } p && ids.Contains(p))
                .GroupBy(i => i.ParentId!.Value)
                .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Order).ToList());
        }

        public IReadOnlyList<Item> ChildrenOf(Guid itemId) => _children.TryGetValue(itemId, out var children) ? children : [];

        /// <summary>Every item in outline order (as the grid shows them): each before its children.</summary>
        public IReadOnlyList<Item> PreOrder()
        {
            var ids = _items.Select(i => i.Id).ToHashSet();
            var visited = new HashSet<Guid>();
            var list = new List<Item>();
            foreach (var root in _items.Where(i => i.ParentId is not { } p || !ids.Contains(p)).OrderBy(i => i.Order).Concat(_items))
            {
                var stack = new Stack<Item>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    var item = stack.Pop();
                    if (!visited.Add(item.Id))
                    {
                        continue;
                    }

                    list.Add(item);
                    foreach (var child in ChildrenOf(item.Id).Reverse())
                    {
                        stack.Push(child);
                    }
                }
            }

            return list;
        }

        /// <summary>Every item, each after all of its descendants.</summary>
        public IEnumerable<Item> PostOrder()
        {
            var visited = new HashSet<Guid>();
            var ids = _items.Select(i => i.Id).ToHashSet();
            var roots = _items.Where(i => i.ParentId is not { } p || !ids.Contains(p)).OrderBy(i => i.Order);

            // Roots first, then anything left over (only possible if parent links form a loop).
            foreach (var start in roots.Concat(_items))
            {
                if (visited.Contains(start.Id))
                {
                    continue;
                }

                var stack = new Stack<(Item Item, bool Expanded)>();
                stack.Push((start, false));
                while (stack.Count > 0)
                {
                    var (item, expanded) = stack.Pop();
                    if (expanded)
                    {
                        yield return item;
                        continue;
                    }

                    if (!visited.Add(item.Id))
                    {
                        continue;
                    }

                    stack.Push((item, true));
                    foreach (var child in ChildrenOf(item.Id).Reverse())
                    {
                        if (!visited.Contains(child.Id))
                        {
                            stack.Push((child, false));
                        }
                    }
                }
            }
        }
    }

    /// <summary>What every row's context shares: the plan, the tree, results so far, cached ranges and the inputs.</summary>
    private sealed class SheetState(
        Plan plan, ItemTree tree, Dictionary<Guid, Dictionary<Guid, FormulaValue>> results, DateOnly today, FormulaInputs inputs)
    {
        private readonly Dictionary<(string, string), FormulaValue> _ranges = new();
        private IReadOnlyList<Item>? _outline;

        public Plan Plan => plan;

        public ItemTree Tree => tree;

        public DateOnly Today => today;

        public FormulaInputs Inputs => inputs;

        public Dictionary<Guid, Dictionary<Guid, FormulaValue>> Results => results;

        public FormulaValue Range(string from, string to, Func<Item, string, FormulaValue> valueOf)
        {
            var key = (from.Trim().ToUpperInvariant(), to.Trim().ToUpperInvariant());
            if (_ranges.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var span = RangeColumns(plan.Columns, from, to);
            FormulaValue value;
            if (span is null)
            {
                value = FormulaValue.FromError(FormulaErrors.Ref);
            }
            else
            {
                _outline ??= tree.PreOrder();
                var cells = new List<FormulaValue>(_outline.Count * span.Count);
                foreach (var item in _outline)
                {
                    cells.AddRange(span.Select(c => valueOf(item, c.Name)));
                }

                value = span.Count == 1 ? FormulaValue.FromList(cells) : FormulaValue.FromTable(cells, span.Count);
            }

            _ranges[key] = value;
            return value;
        }
    }

    private sealed class RowContext(Item item, SheetState state) : IFormulaContext
    {
        public DateOnly Today => state.Today;

        public FormulaValue GetValue(string name) => ValueOf(item, name);

        public FormulaValue GetChildValues(string name)
        {
            if (!Exists(name))
            {
                return FormulaValue.FromError(FormulaErrors.Ref);
            }

            return FormulaValue.FromList(state.Tree.ChildrenOf(item.Id).Select(child => ValueOf(child, name)).ToList());
        }

        public FormulaValue GetColumnRange(string from, string to) => state.Range(from, to, ValueOf);

        public FormulaValue GetSheetReference(string name) =>
            state.Inputs.SheetReferences is { } references && references.TryGetValue(name.Trim(), out var value)
                ? value
                : FormulaValue.FromError(FormulaErrors.Ref);

        private bool Exists(string name) =>
            state.Plan.ByName.ContainsKey(name) || Formula.BuiltInReferences.Contains(name, StringComparer.OrdinalIgnoreCase);

        private FormulaValue ValueOf(Item target, string name)
        {
            if (state.Plan.ByName.TryGetValue(name, out var column))
            {
                if (!string.IsNullOrWhiteSpace(column.Formula))
                {
                    // Not computed yet only when parent links loop back on themselves.
                    return state.Results.TryGetValue(target.Id, out var row) && row.TryGetValue(column.Id, out var computed)
                        ? computed
                        : FormulaValue.FromError(FormulaErrors.Circular);
                }

                var type = state.Inputs.ValueTypes is { } types && types.TryGetValue(column.Id, out var effective) ? effective : column.Type;
                return FormulaValue.FromCell(target.Values.GetValueOrDefault(column.Id.ToString()), type);
            }

            return name.ToUpperInvariant() switch
            {
                "TITLE" => FormulaValue.FromText(target.Title),
                "START" => target.Start is { } start ? FormulaValue.FromDate(start) : FormulaValue.Blank,
                "FINISH" => target.Finish is { } finish ? FormulaValue.FromDate(finish) : FormulaValue.Blank,
                _ => FormulaValue.FromError(FormulaErrors.Ref),
            };
        }
    }
}

/// <summary>
/// What a sheet's formulas read from outside the sheet: the values of its cross-sheet references by name (already
/// filtered for the viewer: #NO ACCESS when hidden), and the value type of lookup columns (that of the looked-up field).
/// </summary>
public sealed record FormulaInputs(
    IReadOnlyDictionary<string, FormulaValue>? SheetReferences = null,
    IReadOnlyDictionary<Guid, ColumnType>? ValueTypes = null);
