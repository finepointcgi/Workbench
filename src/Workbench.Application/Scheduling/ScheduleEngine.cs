using System.Globalization;
using System.Text.RegularExpressions;
using Workbench.Contracts.Scheduling;
using Workbench.Domain;

namespace Workbench.Application.Scheduling;

/// <summary>A predecessor link as the engine sees it. <see cref="LagDays"/> is in working days.</summary>
public sealed record PlanLink(Guid PredecessorId, Guid SuccessorId, DependencyKind Type, int LagDays);

/// <summary>
/// The item tree of a sheet in outline order: MS Project's task IDs (1-based, depth first), outline numbers and levels.
/// Items whose parent is missing are treated as top level, as the views do.
/// </summary>
public sealed class PlanOutline
{
    private readonly Dictionary<Guid, Item> _byId;
    private readonly Dictionary<Guid, List<Item>> _children;
    private readonly List<Item> _roots;

    public PlanOutline(IEnumerable<Item> items)
    {
        var list = items.ToList();
        _byId = list.ToDictionary(i => i.Id);
        var groups = list
            .GroupBy(i => i.ParentId is { } p && _byId.ContainsKey(p) ? p : Guid.Empty)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Order).ThenBy(i => i.Id).ToList());
        _roots = groups.Remove(Guid.Empty, out var roots) ? roots : [];
        _children = groups;

        var ordered = new List<Item>(list.Count);
        void Visit(Guid? parentId, string prefix, int level)
        {
            var n = 0;
            foreach (var child in ChildrenOf(parentId))
            {
                n++;
                var number = prefix.Length == 0 ? n.ToString(CultureInfo.InvariantCulture) : $"{prefix}.{n}";
                ordered.Add(child);
                TaskNumber[child.Id] = ordered.Count;
                OutlineNumber[child.Id] = number;
                Level[child.Id] = level;
                Visit(child.Id, number, level + 1);
            }
        }

        Visit(null, "", 1);
        Ordered = ordered;
    }

    /// <summary>Every item, depth first in sibling order.</summary>
    public IReadOnlyList<Item> Ordered { get; }

    public Dictionary<Guid, int> TaskNumber { get; } = [];

    public Dictionary<Guid, string> OutlineNumber { get; } = [];

    /// <summary>1 for top-level tasks.</summary>
    public Dictionary<Guid, int> Level { get; } = [];

    public Item? Find(Guid id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<Item> ChildrenOf(Guid? parentId) =>
        parentId is not { } id ? _roots : _children.TryGetValue(id, out var kids) ? kids : [];

    public bool IsSummary(Guid id) => ChildrenOf(id).Count > 0;

    public Guid? ParentOf(Item item) => item.ParentId is { } p && _byId.ContainsKey(p) ? p : null;

    public IEnumerable<Item> Descendants(Guid id)
    {
        foreach (var child in ChildrenOf(id))
        {
            yield return child;
            foreach (var grandchild in Descendants(child.Id))
            {
                yield return grandchild;
            }
        }
    }

    /// <summary>True when <paramref name="ancestorId"/> is above <paramref name="id"/> in the tree.</summary>
    public bool IsAncestor(Guid ancestorId, Guid id)
    {
        for (var cursor = _byId.GetValueOrDefault(id); cursor is not null && ParentOf(cursor) is { } p; cursor = _byId.GetValueOrDefault(p))
        {
            if (p == ancestorId)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// MS Project's scheduling rules over a sheet's items, as pure functions of tracked entities:
/// <list type="bullet">
/// <item>A summary task (one with subtasks) spans its subtasks: earliest start to latest finish, never a milestone, and
/// its % complete is its subtasks' average weighted by duration. Its own dates aren't edited directly.</item>
/// <item>Links push successors later, never earlier: a planned start is a "start no earlier than" (as in
/// <see cref="CriticalPathCalculator"/>). Durations are kept when a task moves.</item>
/// <item>Indent and outdent restructure the outline the way MS Project does; see <see cref="Indent"/> and <see cref="Outdent"/>.</item>
/// </list>
/// </summary>
public static class ScheduleEngine
{
    /// <summary>Rolls summaries up and pushes successors until nothing moves. True when anything changed.</summary>
    public static bool Reflow(IReadOnlyCollection<Item> items, IReadOnlyCollection<PlanLink> links, WorkCalendar calendar, Guid? percentColumnId)
    {
        var outline = new PlanOutline(items);
        var changed = false;
        var maxPasses = items.Count + links.Count + 10;
        for (var pass = 0; pass < maxPasses; pass++)
        {
            changed |= Rollup(outline, calendar, percentColumnId);
            if (!Push(outline, links, calendar))
            {
                break;
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>Sets every summary task's dates (and % complete, with a percent column) from its subtasks. True when anything changed.</summary>
    public static bool Rollup(PlanOutline outline, WorkCalendar calendar, Guid? percentColumnId)
    {
        var changed = false;
        foreach (var item in outline.Ordered.Reverse())
        {
            var kids = outline.ChildrenOf(item.Id);
            if (kids.Count == 0)
            {
                continue;
            }

            if (item.IsMilestone)
            {
                item.IsMilestone = false;
                changed = true;
            }

            var dated = kids.Where(k => (k.Start ?? k.Finish) is not null).ToList();
            if (dated.Count > 0)
            {
                var start = dated.Min(k => (k.Start ?? k.Finish)!.Value);
                var finish = dated.Max(k => (k.Finish ?? k.Start)!.Value);
                if (item.Start != start || item.Finish != finish)
                {
                    item.Start = start;
                    item.Finish = finish;
                    changed = true;
                }
            }

            if (percentColumnId is { } columnId)
            {
                changed |= RollupPercent(item, kids, calendar, columnId.ToString());
            }
        }

        return changed;
    }

    private static bool RollupPercent(Item summary, IReadOnlyList<Item> kids, WorkCalendar calendar, string key)
    {
        if (kids.All(k => Percent(k.Values.GetValueOrDefault(key)) is null) && summary.Values.GetValueOrDefault(key) is null)
        {
            return false;
        }

        decimal weighted = 0, weights = 0;
        foreach (var kid in kids)
        {
            var weight = kid.Start is { } s && kid.Finish is { } f ? Math.Max(calendar.Duration(s, f, kid.IsMilestone), 0) : 0;
            weight = Math.Max(weight, 1); // undated tasks and milestones still count, lightly
            weighted += weight * (Percent(kid.Values.GetValueOrDefault(key)) ?? 0);
            weights += weight;
        }

        var value = weights == 0 ? null : Math.Round(weighted / weights, 0, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        if (summary.Values.GetValueOrDefault(key) == value)
        {
            return false;
        }

        summary.Values = new Dictionary<string, string?>(summary.Values) { [key] = value };
        return true;
    }

    /// <summary>A stored % complete value (invariant number), or null.</summary>
    public static decimal? Percent(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : null;

    /// <summary>One pass over the links. True when a successor moved.</summary>
    private static bool Push(PlanOutline outline, IReadOnlyCollection<PlanLink> links, WorkCalendar calendar)
    {
        var moved = false;
        foreach (var link in links.OrderBy(l => outline.TaskNumber.GetValueOrDefault(l.PredecessorId)))
        {
            if (outline.Find(link.PredecessorId) is not { } pred || outline.Find(link.SuccessorId) is not { } succ
                || pred.Id == succ.Id || outline.IsAncestor(pred.Id, succ.Id) || outline.IsAncestor(succ.Id, pred.Id))
            {
                continue;
            }

            if ((pred.Start ?? pred.Finish) is not { } ps || (pred.Finish ?? pred.Start) is not { } pf
                || (succ.Start ?? succ.Finish) is not { } ss || (succ.Finish ?? succ.Start) is not { } sf)
            {
                continue;
            }

            var predMilestone = pred.IsMilestone && !outline.IsSummary(pred.Id);
            var succMilestone = succ.IsMilestone && !outline.IsSummary(succ.Id);
            var required = RequiredStart(link, ps, pf, ss, sf, predMilestone, succMilestone, calendar);
            if (required > ss)
            {
                ShiftTo(outline, succ, required, calendar);
                moved = true;
            }
        }

        return moved;
    }

    /// <summary>The earliest start a link allows its successor. A milestone takes no time: its successor may start that day.</summary>
    public static DateOnly RequiredStart(
        PlanLink link, DateOnly ps, DateOnly pf, DateOnly ss, DateOnly sf, bool predMilestone, bool succMilestone, WorkCalendar calendar)
    {
        DateOnly Lag(DateOnly date) => link.LagDays == 0 ? date : calendar.AddWorkingDays(calendar.OnOrAfter(date), link.LagDays);
        var duration = calendar.Duration(ss, sf, succMilestone);
        DateOnly StartFor(DateOnly finish) => duration <= 1 ? finish : calendar.AddWorkingDays(calendar.OnOrAfter(finish), -(duration - 1));

        return link.Type switch
        {
            DependencyKind.StartToStart => Lag(calendar.OnOrAfter(ps)),
            DependencyKind.FinishToFinish => StartFor(Lag(pf)),
            DependencyKind.StartToFinish => StartFor(Lag(ps)),
            _ => Lag(predMilestone || succMilestone ? pf : calendar.OnOrAfter(pf.AddDays(1))),
        };
    }

    /// <summary>
    /// Moves a task so it starts on <paramref name="start"/>, keeping its duration in working days. A summary task moves all
    /// of its subtasks by the same number of working days; undated tasks stay undated.
    /// </summary>
    public static void ShiftTo(PlanOutline outline, Item item, DateOnly start, WorkCalendar calendar)
    {
        if (!outline.IsSummary(item.Id))
        {
            MoveLeaf(item, start, calendar);
            return;
        }

        var leaves = outline.Descendants(item.Id).Where(d => !outline.IsSummary(d.Id) && (d.Start ?? d.Finish) is not null).ToList();
        if (leaves.Count == 0)
        {
            return;
        }

        var from = leaves.Min(l => (l.Start ?? l.Finish)!.Value);
        var delta = calendar.Between(from, start);
        foreach (var leaf in leaves)
        {
            var leafStart = (leaf.Start ?? leaf.Finish)!.Value;
            MoveLeaf(leaf, delta == 0 ? leafStart : calendar.AddWorkingDays(calendar.OnOrAfter(leafStart), delta), calendar);
        }

        Rollup(outline, calendar, null);
    }

    private static void MoveLeaf(Item item, DateOnly start, WorkCalendar calendar)
    {
        if (item.Start is { } s && item.Finish is { } f)
        {
            var days = calendar.Duration(s, f, item.IsMilestone);
            item.Start = start;
            item.Finish = item.IsMilestone ? start : calendar.Finish(start, days);
        }
        else if (item.Start is not null)
        {
            item.Start = start;
        }
        else if (item.Finish is not null)
        {
            item.Finish = start;
        }
    }

    /// <summary>Earliest start of a task: its own, or for a summary its subtasks' (null when nothing is dated).</summary>
    public static DateOnly? StartOf(PlanOutline outline, Item item)
    {
        var kids = outline.ChildrenOf(item.Id);
        if (kids.Count == 0)
        {
            return item.Start ?? item.Finish;
        }

        var starts = kids.Select(k => StartOf(outline, k)).OfType<DateOnly>().ToList();
        return starts.Count > 0 ? starts.Min() : item.Start ?? item.Finish;
    }

    /// <summary>
    /// Makes <paramref name="item"/> the last subtask of the task above it at its level. When <paramref name="schedule"/>
    /// is set the task takes the start of the task now just above it among its new siblings (its new parent's start when
    /// it's the first subtask), keeping its duration: an undated task gets that day. The new parent becomes a summary.
    /// Returns false when there's no task above to indent under.
    /// </summary>
    public static bool Indent(IReadOnlyCollection<Item> items, Item item, bool schedule, WorkCalendar calendar)
    {
        var outline = new PlanOutline(items);
        var siblings = outline.ChildrenOf(outline.ParentOf(item)).ToList();
        var index = siblings.IndexOf(item);
        if (index <= 0)
        {
            return false;
        }

        var parent = siblings[index - 1];
        var newSiblings = outline.ChildrenOf(parent.Id);
        var anchor = newSiblings.Count > 0 ? newSiblings[^1] : parent;
        var anchorStart = StartOf(outline, anchor);

        siblings.RemoveAt(index);
        Renumber(siblings);
        item.ParentId = parent.Id;
        item.Order = newSiblings.Count;
        parent.IsMilestone = false;

        if (schedule && anchorStart is { } start)
        {
            outline = new PlanOutline(items);
            if (StartOf(outline, item) is null)
            {
                item.Start = start;
                item.Finish = start;
            }
            else
            {
                ShiftTo(outline, item, start, calendar);
            }
        }

        return true;
    }

    /// <summary>
    /// Moves <paramref name="item"/> up a level, keeping its place in the outline: the tasks after it under the same parent
    /// become its last subtasks (MS Project's outdent). Dates don't change here; the reflow rolls the summaries up.
    /// Returns false for a top-level task.
    /// </summary>
    public static bool Outdent(IReadOnlyCollection<Item> items, Item item)
    {
        var outline = new PlanOutline(items);
        if (outline.ParentOf(item) is not { } parentId)
        {
            return false;
        }

        var parent = outline.Find(parentId)!;
        var siblings = outline.ChildrenOf(parentId).ToList();
        var index = siblings.IndexOf(item);
        var following = siblings.Skip(index + 1).ToList();
        var ownChildren = outline.ChildrenOf(item.Id).Count;
        for (var i = 0; i < following.Count; i++)
        {
            following[i].ParentId = item.Id;
            following[i].Order = ownChildren + i;
        }

        if (following.Count > 0)
        {
            item.IsMilestone = false;
        }

        Renumber(siblings.Take(index));

        var grandparentId = outline.ParentOf(parent);
        var uncles = outline.ChildrenOf(grandparentId).ToList();
        uncles.Insert(uncles.IndexOf(parent) + 1, item);
        item.ParentId = grandparentId;
        Renumber(uncles);
        return true;
    }

    private static void Renumber(IEnumerable<Item> siblings)
    {
        var order = 0;
        foreach (var sibling in siblings)
        {
            sibling.Order = order++;
        }
    }

    // ---- Predecessor text (MS Project): "3", "3FS", "5SS+2d", "7FF-1 day", "4SF+1w" ----

    private static readonly Regex PredecessorPattern = new(
        @"^\s*(?<id>\d+)\s*(?<type>FS|SS|FF|SF)?\s*(?:(?<sign>[+-])\s*(?<lag>\d+)\s*(?<unit>d|dy|dys|day|days|w|wk|wks|week|weeks)?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>One parsed predecessor: the task ID it refers to, the link type and the lag in working days.</summary>
    public sealed record PredecessorRef(int TaskNumber, DependencyKind Type, int LagDays);

    /// <summary>Parses MS Project predecessor text. Entries are separated by commas or semicolons; returns an error for the first bad one.</summary>
    public static IReadOnlyList<PredecessorRef> ParsePredecessors(string? text, out string? error)
    {
        error = null;
        var refs = new List<PredecessorRef>();
        foreach (var part in (text ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = PredecessorPattern.Match(part);
            if (!match.Success || !int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
            {
                error = $"\"{part}\" isn't a predecessor. Use task IDs like 3, 5SS or 7FS+2d.";
                return [];
            }

            var type = match.Groups["type"].Value.ToUpperInvariant() switch
            {
                "SS" => DependencyKind.StartToStart,
                "FF" => DependencyKind.FinishToFinish,
                "SF" => DependencyKind.StartToFinish,
                _ => DependencyKind.FinishToStart,
            };
            var lag = 0;
            if (match.Groups["lag"].Success)
            {
                if (!int.TryParse(match.Groups["lag"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out lag) || lag > 3650)
                {
                    error = $"The lag in \"{part}\" is too long.";
                    return [];
                }

                if (match.Groups["unit"].Value.StartsWith('w') || match.Groups["unit"].Value.StartsWith('W'))
                {
                    lag *= 5;
                }

                if (match.Groups["sign"].Value == "-")
                {
                    lag = -lag;
                }
            }

            if (refs.Any(r => r.TaskNumber == id))
            {
                error = $"Task {id} is listed twice.";
                return [];
            }

            refs.Add(new PredecessorRef(id, type, lag));
        }

        return refs;
    }

    /// <summary>MS Project text for one link: <c>3</c>, <c>5SS</c>, <c>7FS+2d</c>.</summary>
    public static string FormatPredecessor(int taskNumber, DependencyKind type, int lagDays)
    {
        var code = type switch
        {
            DependencyKind.StartToStart => "SS",
            DependencyKind.FinishToFinish => "FF",
            DependencyKind.StartToFinish => "SF",
            _ => "FS",
        };
        var text = taskNumber.ToString(CultureInfo.InvariantCulture);
        if (lagDays != 0)
        {
            return text + code + lagDays.ToString("+0;-0", CultureInfo.InvariantCulture) + "d";
        }

        return type == DependencyKind.FinishToStart ? text : text + code;
    }

    /// <summary>The predecessors of each task as MS Project text, in task-ID order.</summary>
    public static Dictionary<Guid, string> PredecessorText(PlanOutline outline, IEnumerable<PlanLink> links) =>
        links
            .Where(l => outline.TaskNumber.ContainsKey(l.PredecessorId) && outline.TaskNumber.ContainsKey(l.SuccessorId))
            .GroupBy(l => l.SuccessorId)
            .ToDictionary(
                g => g.Key,
                g => string.Join(", ", g.OrderBy(l => outline.TaskNumber[l.PredecessorId])
                    .Select(l => FormatPredecessor(outline.TaskNumber[l.PredecessorId], l.Type, l.LagDays))));

    /// <summary>Parses a duration in working days; see <see cref="PlanDuration.TryParse"/>.</summary>
    public static bool TryParseDuration(string? text, out int days) => PlanDuration.TryParse(text, out days);
}
