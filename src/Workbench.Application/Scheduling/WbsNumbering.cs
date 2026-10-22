using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain;

namespace Workbench.Application.Scheduling;

/// <summary>
/// Automatic WBS codes on a project's plan: a task's code is its parent's code (the project code at the top) plus its
/// two-digit position, e.g. the third subtask of 1001.02 is 1001.02.03. Codes follow the outline when tasks are added,
/// indented, outdented, moved or removed. Two kinds of code are left alone and numbering works around them:
/// <list type="bullet">
/// <item>codes of tasks that are already charge codes (linked to a WBS element), so timesheets and reports keep theirs;</item>
/// <item>codes someone typed (<see cref="Item.WbsCodeIsAuto"/> false). Clearing a typed code gives the task an automatic one.</item>
/// </list>
/// </summary>
public static class WbsNumbering
{
    /// <summary>
    /// Numbers the outline. <paramref name="locked"/>: tasks linked to WBS elements. <paramref name="taken"/>: codes the
    /// project's WBS already uses, which an automatic code never reuses. True when anything changed.
    /// </summary>
    public static bool Apply(PlanOutline outline, string projectCode, IReadOnlySet<Guid> locked, IReadOnlySet<string> taken)
    {
        var changed = false;

        void Number(Guid? parentId, string prefix)
        {
            var children = outline.ChildrenOf(parentId);
            var kept = children.Where(c => !IsAuto(c, locked)).Select(c => c.CostpointWbsId!).ToHashSet(StringComparer.Ordinal);
            var n = 0;
            foreach (var child in children)
            {
                if (IsAuto(child, locked))
                {
                    string code;
                    do
                    {
                        code = Code(prefix, ++n);
                    }
                    while (kept.Contains(code) || (taken.Contains(code) && code != child.CostpointWbsId));

                    if (child.CostpointWbsId != code || !child.WbsCodeIsAuto)
                    {
                        child.CostpointWbsId = code;
                        child.WbsCodeIsAuto = true;
                        changed = true;
                    }
                }
                else if (Position(prefix, child.CostpointWbsId) is { } position)
                {
                    n = Math.Max(n, position);
                }

                Number(child.Id, child.CostpointWbsId ?? Code(prefix, n));
            }
        }

        Number(null, projectCode);
        return changed;
    }

    /// <summary>The next automatic code under <paramref name="prefix"/> given the codes already used there.</summary>
    public static string Next(string prefix, IEnumerable<string> siblingCodes) => WbsCodes.Next(prefix, siblingCodes);

    public static string Code(string prefix, int position) => WbsCodes.Code(prefix, position);

    private static bool IsAuto(Item item, IReadOnlySet<Guid> locked) =>
        !locked.Contains(item.Id) && (item.WbsCodeIsAuto || string.IsNullOrWhiteSpace(item.CostpointWbsId));

    private static int? Position(string prefix, string? code) => WbsCodes.Position(prefix, code);
}
