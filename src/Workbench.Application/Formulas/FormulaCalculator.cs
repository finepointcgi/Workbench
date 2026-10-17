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
        IReadOnlyList<Column> columns, IReadOnlyList<Item> items, DateOnly today)
    {
        var plan = Plan.Create(columns);
        var results = new Dictionary<Guid, Dictionary<Guid, FormulaValue>>();
        if (plan.FormulaColumns.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyDictionary<Guid, string?>>();
        }

        var tree = new ItemTree(items);
        foreach (var item in tree.PostOrder())
        {
            var row = new Dictionary<Guid, FormulaValue>();
            results[item.Id] = row;
            var context = new RowContext(item, plan, tree, results, today);
            foreach (var column in plan.Circular)
            {
                row[column.Id] = FormulaValue.FromError(FormulaErrors.Circular);
            }

            foreach (var column in plan.Order)
            {
                row[column.Id] = plan.Parsed.TryGetValue(column.Id, out var formula)
                    ? formula.Evaluate(context)
                    : FormulaValue.FromError(FormulaErrors.Name);
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

    private sealed class Plan
    {
        public required Dictionary<string, Column> ByName { get; init; }

        public required List<Column> FormulaColumns { get; init; }

        /// <summary>Successfully parsed formulas; columns missing here have unparseable formulas (#NAME?).</summary>
        public required Dictionary<Guid, Formula> Parsed { get; init; }

        public required List<Column> Order { get; init; }

        public required List<Column> Circular { get; init; }

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
            var order = TopologicalOrder(formulaColumns.Where(c => !circular.Contains(c.Id)).ToList(), dependencies);
            return new Plan
            {
                ByName = byName,
                FormulaColumns = formulaColumns,
                Parsed = parsed,
                Order = order,
                Circular = formulaColumns.Where(c => circular.Contains(c.Id)).ToList(),
            };
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

    private sealed class RowContext(
        Item item,
        Plan plan,
        ItemTree tree,
        Dictionary<Guid, Dictionary<Guid, FormulaValue>> results,
        DateOnly today) : IFormulaContext
    {
        public DateOnly Today => today;

        public FormulaValue GetValue(string name) => ValueOf(item, name);

        public FormulaValue GetChildValues(string name)
        {
            if (!Exists(name))
            {
                return FormulaValue.FromError(FormulaErrors.Ref);
            }

            return FormulaValue.FromList(tree.ChildrenOf(item.Id).Select(child => ValueOf(child, name)).ToList());
        }

        private bool Exists(string name) =>
            plan.ByName.ContainsKey(name) || Formula.BuiltInReferences.Contains(name, StringComparer.OrdinalIgnoreCase);

        private FormulaValue ValueOf(Item target, string name)
        {
            if (plan.ByName.TryGetValue(name, out var column))
            {
                if (!string.IsNullOrWhiteSpace(column.Formula))
                {
                    // Not computed yet only when parent links loop back on themselves.
                    return results.TryGetValue(target.Id, out var row) && row.TryGetValue(column.Id, out var computed)
                        ? computed
                        : FormulaValue.FromError(FormulaErrors.Circular);
                }

                return FormulaValue.FromCell(target.Values.GetValueOrDefault(column.Id.ToString()), column.Type);
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
