using System.Globalization;
using Workbench.Contracts.Scheduling;
using Workbench.Domain;

namespace Workbench.Application.Scheduling;

/// <summary>A directory user an import can assign.</summary>
public sealed record PlanUser(string Email, string DisplayName);

/// <summary>The plan as it is now, for comparing a file against it.</summary>
public sealed record PlanSnapshot(
    IReadOnlyList<Item> Items,
    IReadOnlyList<PlanLink> Links,
    WorkCalendar Calendar,
    Column? PercentColumn,
    Column? NotesColumn,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> Assignees,
    IReadOnlyList<PlanUser> Users,
    bool Scheduling = true,
    IReadOnlyDictionary<Guid, PlanConstraintDto>? Constraints = null);

/// <summary>What a file sets on one task. <see cref="ItemId"/> is new for added tasks.</summary>
public sealed record PlanImportTask(
    int Row,
    Guid ItemId,
    bool IsNew,
    Guid? ParentId,
    int Order,
    string Title,
    bool IsSummary,
    DateOnly? Start,
    DateOnly? Finish,
    bool IsMilestone,
    string? WbsCode,
    decimal? PercentComplete,
    string? Notes,
    IReadOnlyList<string> Assignees,
    PlanConstraintDto? Constraint = null);

/// <summary>
/// Compares an uploaded plan with the sheet: which tasks the file adds, changes and removes, and which rows have errors.
/// The file is the whole plan in order. Rows match tasks by Unique ID; predecessors refer to the file's ID column (or row
/// positions when it has none). Summary rows' dates and % complete are rolled up from their subtasks, so what the file says
/// for them is ignored. An unchanged export compares equal, field by field.
/// </summary>
public sealed class PlanImport
{
    private PlanImport(PlanImportPreview preview, IReadOnlyList<PlanImportTask> tasks, IReadOnlyList<PlanLink> links, IReadOnlyList<Guid> removed)
    {
        Preview = preview;
        Tasks = tasks;
        Links = links;
        Removed = removed;
    }

    public PlanImportPreview Preview { get; }

    /// <summary>Every row of the file as a task, in outline order.</summary>
    public IReadOnlyList<PlanImportTask> Tasks { get; }

    /// <summary>The links the plan will have.</summary>
    public IReadOnlyList<PlanLink> Links { get; }

    /// <summary>Existing tasks the file leaves out.</summary>
    public IReadOnlyList<Guid> Removed { get; }

    /// <param name="hasConstraints">
    /// The file sets constraints (MS Project XML): a task without one is As Soon As Possible. Otherwise (Excel) the plan's
    /// constraints are kept as they are.
    /// </param>
    /// <param name="unknownResourcesAreWarnings">
    /// Resources that aren't Workbench users are left off with a warning rather than blocking the import (MS Project files
    /// often name roles or people outside the company).
    /// </param>
    public static PlanImport Build(
        string fileName, IReadOnlyList<PlanFileRow> rows, bool hasIdColumn, PlanSnapshot plan, bool hasConstraints = false, bool unknownResourcesAreWarnings = false)
    {
        var issues = new List<PlanImportIssue>();
        foreach (var row in rows)
        {
            issues.AddRange(row.Errors.Select(e => new PlanImportIssue(row.Row, e)));
            issues.AddRange((row.Warnings ?? []).Select(w => new PlanImportIssue(row.Row, w, IsError: false)));
        }

        var constraintsBefore = plan.Constraints ?? new Dictionary<Guid, PlanConstraintDto>();

        if (rows.Count == 0)
        {
            issues.Add(new PlanImportIssue(1, "The file has no tasks. Importing it would remove every task, so it isn't allowed."));
        }

        var existing = plan.Items.ToDictionary(i => i.Id);
        var outline = new PlanOutline(plan.Items);
        var calendar = plan.Calendar;

        // Match rows to tasks by Unique ID.
        var ids = new Guid[rows.Count];
        var isNew = new bool[rows.Count];
        var claimed = new HashSet<Guid>();
        for (var i = 0; i < rows.Count; i++)
        {
            var uid = rows[i].Task.UniqueId;
            if (uid is { } u && claimed.Contains(u))
            {
                issues.Add(new PlanImportIssue(rows[i].Row, "This Unique ID is on an earlier row too. Clear it on copied rows."));
                uid = null;
            }

            if (uid is { } known && existing.ContainsKey(known))
            {
                ids[i] = known;
                claimed.Add(known);
            }
            else
            {
                if (uid is not null)
                {
                    issues.Add(new PlanImportIssue(rows[i].Row, "Its Unique ID isn't a task on this plan, so it's added as a new task.", IsError: false));
                }

                ids[i] = Guid.CreateVersion7();
                isNew[i] = true;
            }
        }

        // The outline: each row's parent is the nearest row above it one level up.
        var parents = new Guid?[rows.Count];
        var orders = new int[rows.Count];
        var stack = new List<int>();
        var childCount = new Dictionary<Guid, int>();
        var topCount = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var level = rows[i].Task.Level;
            if (level > stack.Count + 1)
            {
                issues.Add(new PlanImportIssue(rows[i].Row, stack.Count == 0
                    ? $"The first task must be at outline level 1, not {level}."
                    : $"Outline level {level} is more than one level below the row above ({stack.Count})."));
                level = stack.Count + 1;
            }

            while (stack.Count >= level)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            if (stack.Count > 0)
            {
                var parent = ids[stack[^1]];
                parents[i] = parent;
                orders[i] = childCount.GetValueOrDefault(parent);
                childCount[parent] = orders[i] + 1;
            }
            else
            {
                orders[i] = topCount++;
            }

            stack.Add(i);
        }

        var summaries = parents.OfType<Guid>().ToHashSet();

        // Task IDs that predecessors refer to: the ID column, or row positions when it's missing or left blank.
        hasIdColumn = hasIdColumn && rows.Any(r => r.Task.Id is not null);
        var byNumber = new Dictionary<int, int>();
        for (var i = 0; i < rows.Count; i++)
        {
            var number = hasIdColumn ? rows[i].Task.Id : i + 1;
            if (number is not { } n)
            {
                continue;
            }

            if (!byNumber.TryAdd(n, i))
            {
                issues.Add(new PlanImportIssue(rows[i].Row, $"ID {n} is used by an earlier row too."));
            }
        }

        // Resources, by email or name.
        var users = plan.Users;
        var tasks = new List<PlanImportTask>(rows.Count);
        var links = new List<PlanLink>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var task = row.Task;
            var summary = summaries.Contains(ids[i]);
            existing.TryGetValue(ids[i], out var before);
            if (isNew[i])
            {
                before = null;
            }

            var (start, finish, milestone) = summary
                ? (before?.Start, before?.Finish, false)
                : Dates(task, before, calendar, row.Row, issues);

            var emails = new List<string>();
            foreach (var name in (task.Resources ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var matches = users.Where(u => string.Equals(u.Email, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(u.DisplayName, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1)
                {
                    if (!emails.Contains(matches[0].Email))
                    {
                        emails.Add(matches[0].Email);
                    }
                }
                else
                {
                    issues.Add(new PlanImportIssue(row.Row, matches.Count == 0
                        ? unknownResourcesAreWarnings
                            ? $"Resource \"{name}\" isn't an active Workbench user, so the task is imported without them."
                            : $"\"{name}\" isn't an active Workbench user. Use their name or email."
                        : $"More than one person is called \"{name}\". Use their email.", IsError: !unknownResourcesAreWarnings || matches.Count > 1));
                }
            }

            var references = ScheduleEngine.ParsePredecessors(task.Predecessors, out var error);
            foreach (var reference in references)
            {
                if (!byNumber.TryGetValue(reference.TaskNumber, out var p))
                {
                    issues.Add(new PlanImportIssue(row.Row, $"Predecessor {reference.TaskNumber} isn't an ID in the file."));
                }
                else if (p == i)
                {
                    issues.Add(new PlanImportIssue(row.Row, "A task can't be its own predecessor."));
                }
                else if (IsAncestorRow(parents, ids, p, i) || IsAncestorRow(parents, ids, i, p))
                {
                    issues.Add(new PlanImportIssue(row.Row, $"Task {reference.TaskNumber} is this task's summary or subtask; they can't be linked."));
                }
                else
                {
                    links.Add(new PlanLink(ids[p], ids[i], reference.Type, reference.LagDays));
                }
            }

            if (error is not null)
            {
                issues.Add(new PlanImportIssue(row.Row, error));
            }

            var constraint = hasConstraints ? task.Constraint : isNew[i] ? null : constraintsBefore.GetValueOrDefault(ids[i]);
            if (summary && hasConstraints && constraint is not null)
            {
                issues.Add(new PlanImportIssue(row.Row, $"Its {PlanConstraints.Label(constraint.Kind)} constraint is left off: it's a summary task, whose dates come from its subtasks.", IsError: false));
                constraint = null;
            }

            tasks.Add(new PlanImportTask(
                row.Row, ids[i], isNew[i], parents[i], orders[i], task.Name.Trim(), summary, start, finish, milestone,
                task.WbsCode?.Trim(), summary ? null : task.PercentComplete, task.Notes, emails, constraint));
        }

        // Circular links.
        var edges = new List<(Guid, Guid)>();
        foreach (var link in links)
        {
            if (DependencyService.CreatesCycle(edges, link.PredecessorId, link.SuccessorId))
            {
                var at = tasks.First(t => t.ItemId == link.SuccessorId);
                issues.Add(new PlanImportIssue(at.Row, "Its predecessors make a loop (a task ends up waiting on itself)."));
            }
            else
            {
                edges.Add((link.PredecessorId, link.SuccessorId));
            }
        }

        // The dates the plan will end up with: the rollup and link rules run after an import too, so the preview shows
        // their result (a task its predecessors push later, a summary's subtasks moved with it), not only the typed dates.
        if (plan.Scheduling && !issues.Any(i => i.IsError))
        {
            var simulated = tasks.Select(t => new Item
            {
                Id = t.ItemId, ParentId = t.ParentId, Order = t.Order, Title = t.Title, Start = t.Start, Finish = t.Finish, IsMilestone = t.IsMilestone,
            }).ToList();
            var constraints = tasks.Where(t => t.Constraint is not null).ToDictionary(t => t.ItemId, t => t.Constraint!);
            ScheduleEngine.Reflow(simulated, links, calendar, null, constraints);
            foreach (var conflict in ScheduleEngine.Conflicts(simulated, links, calendar, constraints))
            {
                var at = tasks.First(t => t.ItemId == conflict.ItemId);
                issues.Add(new PlanImportIssue(at.Row, "After importing: " + conflict.Message, IsError: false));
            }

            var result = simulated.ToDictionary(i => i.Id);
            tasks = tasks.Select(t => t with { Start = result[t.ItemId].Start, Finish = result[t.ItemId].Finish, IsMilestone = result[t.ItemId].IsMilestone }).ToList();
        }

        var removed = plan.Items.Where(i => !claimed.Contains(i.Id)).ToList();
        var changes = new List<PlanImportChange>();
        var unchanged = 0;
        var titles = tasks.ToDictionary(t => t.ItemId, t => t.Title);
        foreach (var task in tasks)
        {
            if (task.IsNew)
            {
                changes.Add(new PlanImportChange(PlanImportChangeKind.Added, task.Row, null, task.Title, Describe(task, calendar)));
                continue;
            }

            var details = Compare(task, existing[task.ItemId], plan, outline, titles, links, tasks);
            if (details.Count == 0)
            {
                unchanged++;
            }
            else
            {
                changes.Add(new PlanImportChange(PlanImportChangeKind.Changed, task.Row, task.ItemId, task.Title, details));
            }
        }

        foreach (var item in outline.Ordered.Where(i => removed.Contains(i)))
        {
            var detail = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.CostpointWbsId))
            {
                detail.Add($"Linked to WBS {item.CostpointWbsId}: the charge code stays, without a plan task.");
            }

            changes.Add(new PlanImportChange(PlanImportChangeKind.Removed, null, item.Id, item.Title, detail));
        }

        var preview = new PlanImportPreview(fileName, rows.Count, unchanged, changes, issues.OrderBy(i => i.Row).ThenBy(i => i.IsError ? 0 : 1).ToList());
        return new PlanImport(preview, tasks, links, removed.Select(r => r.Id).ToList());
    }

    /// <summary>True when the row at <paramref name="ancestor"/> is above the row at <paramref name="row"/> in the file's outline.</summary>
    private static bool IsAncestorRow(Guid?[] parents, Guid[] ids, int ancestor, int row)
    {
        for (var p = parents[row]; p is { } id; p = parents[Array.IndexOf(ids, id)])
        {
            if (id == ids[ancestor])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The dates a row sets, MS Project style: a changed Finish wins; otherwise a changed Duration sets the finish from the
    /// start; otherwise a moved Start keeps the duration. Duration 0 is a milestone. New rows: Finish, or Start + Duration.
    /// </summary>
    private static (DateOnly? Start, DateOnly? Finish, bool Milestone) Dates(
        PlanSheetRow row, Item? before, WorkCalendar calendar, int rowNumber, List<PlanImportIssue> issues)
    {
        var start = row.Start;
        var finish = row.Finish;
        var duration = row.DurationDays;
        var milestone = duration == 0 || (duration is null && before?.IsMilestone == true && start == finish);
        if (milestone)
        {
            var day = start ?? finish;
            return (day, day, true);
        }

        if (before is not null)
        {
            int? beforeDays = before.Start is { } bs && before.Finish is { } bf ? calendar.Duration(bs, bf, before.IsMilestone) : null;
            if (finish != before.Finish)
            {
                // Finish typed: keep it.
            }
            else if (duration is { } d && d != beforeDays && start is { } s)
            {
                finish = calendar.Finish(s, d);
            }
            else if (start != before.Start && start is { } moved && beforeDays is { } keep && finish is not null)
            {
                finish = calendar.Finish(moved, keep);
            }
        }
        else if (finish is null && start is { } s)
        {
            finish = duration is { } d ? calendar.Finish(s, d) : s;
        }

        if (start is { } a && finish is { } b && b < a)
        {
            issues.Add(new PlanImportIssue(rowNumber, $"Finish ({b:MM/dd/yyyy}) is before Start ({a:MM/dd/yyyy})."));
        }

        return (start, finish, false);
    }

    private static List<string> Describe(PlanImportTask task, WorkCalendar calendar)
    {
        var details = new List<string>();
        if (task.IsSummary)
        {
            details.Add("Summary task");
        }
        else if (task.IsMilestone && task.Start is { } m)
        {
            details.Add($"Milestone {Day(m)}");
        }
        else if (task.Start is { } s && task.Finish is { } f)
        {
            details.Add($"{Day(s)} – {Day(f)} ({calendar.Duration(s, f)} d)");
        }

        if (task.Assignees.Count > 0)
        {
            details.Add($"Resources: {string.Join(", ", task.Assignees)}");
        }

        if (task.Constraint is { } constraint)
        {
            details.Add($"{constraint.Label} {Day(constraint.Date)}");
        }

        return details;
    }

    private static List<string> Compare(
        PlanImportTask task, Item before, PlanSnapshot plan, PlanOutline outline, IReadOnlyDictionary<Guid, string> titles,
        IReadOnlyList<PlanLink> links, IReadOnlyList<PlanImportTask> tasks)
    {
        var details = new List<string>();
        if (task.Title != before.Title)
        {
            details.Add($"Renamed from \"{before.Title}\"");
        }

        var beforeParent = outline.ParentOf(before);
        if (task.ParentId != beforeParent)
        {
            details.Add(task.ParentId is { } p ? $"Moved under \"{titles.GetValueOrDefault(p) ?? "?"}\"" : "Moved to the top level");
        }
        else if (RankAmongKept(before.Id, outline.ChildrenOf(beforeParent).Select(c => c.Id), tasks)
            != RankAmongKept(before.Id, tasks.Where(t => t.ParentId == task.ParentId).OrderBy(t => t.Order).Select(t => t.ItemId), tasks))
        {
            details.Add("Moved");
        }

        if (!task.IsSummary)
        {
            if (task.IsMilestone != before.IsMilestone)
            {
                details.Add(task.IsMilestone ? "Now a milestone" : "No longer a milestone");
            }

            if (task.Start != before.Start)
            {
                details.Add($"Start {Day(before.Start)} → {Day(task.Start)}");
            }

            if (task.Finish != before.Finish)
            {
                details.Add($"Finish {Day(before.Finish)} → {Day(task.Finish)}");
            }

            if (plan.PercentColumn is { } percent)
            {
                var old = ScheduleEngine.Percent(before.Values.GetValueOrDefault(percent.Id.ToString()));
                if (old != task.PercentComplete)
                {
                    details.Add($"% complete {Pct(old)} → {Pct(task.PercentComplete)}");
                }
            }
            else if (task.PercentComplete is { } p)
            {
                details.Add($"% complete {Pct(p)}");
            }
        }

        if ((task.WbsCode ?? "") != (before.CostpointWbsId ?? ""))
        {
            details.Add($"WBS code {Text(before.CostpointWbsId)} → {Text(task.WbsCode)}");
        }

        var oldNotes = plan.NotesColumn is { } notes ? before.Values.GetValueOrDefault(notes.Id.ToString()) : null;
        if (Normalize(oldNotes) != Normalize(task.Notes))
        {
            details.Add("Notes changed");
        }

        var oldPeople = plan.Assignees.GetValueOrDefault(before.Id) ?? [];
        if (!oldPeople.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(task.Assignees))
        {
            details.Add($"Resources {List(oldPeople)} → {List(task.Assignees)}");
        }

        var oldConstraint = plan.Constraints?.GetValueOrDefault(before.Id);
        if (oldConstraint != task.Constraint)
        {
            details.Add($"Constraint {ConstraintText(oldConstraint)} → {ConstraintText(task.Constraint)}");
        }

        var oldLinks = plan.Links.Where(l => l.SuccessorId == before.Id).Select(l => (l.PredecessorId, l.Type, l.LagDays)).ToHashSet();
        var newLinks = links.Where(l => l.SuccessorId == before.Id).Select(l => (l.PredecessorId, l.Type, l.LagDays)).ToHashSet();
        if (!oldLinks.SetEquals(newLinks))
        {
            details.Add("Predecessors changed");
        }

        return details;
    }

    /// <summary>Position among the siblings that exist both before and after, so inserted or removed rows don't count as moves.</summary>
    private static int RankAmongKept(Guid id, IEnumerable<Guid> siblings, IReadOnlyList<PlanImportTask> tasks)
    {
        var kept = tasks.Where(t => !t.IsNew).Select(t => t.ItemId).ToHashSet();
        return siblings.Where(kept.Contains).ToList().IndexOf(id);
    }

    private static string ConstraintText(PlanConstraintDto? constraint) =>
        constraint is null ? "As Soon As Possible" : $"{constraint.Label} {Day(constraint.Date)}";

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();

    private static string Day(DateOnly? date) => date?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? "(none)";

    private static string Pct(decimal? value) => value is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) + "%" : "(none)";

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    private static string List(IEnumerable<string> people) => people.Any() ? string.Join(", ", people) : "(none)";
}
