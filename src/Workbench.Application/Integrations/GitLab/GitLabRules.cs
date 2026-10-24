using System.Globalization;
using Workbench.Application.Scheduling;
using Workbench.Domain;

namespace Workbench.Application.Integrations.GitLab;

/// <summary>Who owns each synced field, so the two sides never fight over it.</summary>
public enum GitLabFieldOwner
{
    /// <summary>The schedule: start and due dates, dependencies, WBS codes and structure, hours.</summary>
    Workbench,

    /// <summary>Work state: open/closed and progress.</summary>
    GitLab,

    /// <summary>Title, description and assignees: the later edit wins and the other value is noted.</summary>
    Shared,
}

/// <summary>The sync's pure rules: what each task becomes, field ownership, progress and the description block.</summary>
public static class GitLabRules
{
    public const string WbsLabelPrefix = "WBS::";

    public static string WbsLabel(string code) => WbsLabelPrefix + code.Trim();

    public static bool IsWbsLabel(string label) => label.StartsWith(WbsLabelPrefix, StringComparison.OrdinalIgnoreCase);

    public static string? WbsCodeOf(IEnumerable<string>? labels) =>
        labels?.FirstOrDefault(IsWbsLabel) is { } label ? label[WbsLabelPrefix.Length..].Trim() : null;

    public static GitLabFieldOwner OwnerOf(string field) => field switch
    {
        "title" or "description" or "assignees" => GitLabFieldOwner.Shared,
        "state" or "percent" => GitLabFieldOwner.GitLab,
        _ => GitLabFieldOwner.Workbench,
    };

    public static string FieldLabel(string field) => field switch
    {
        "title" => "Title",
        "description" => "Description",
        "assignees" => "Assignees",
        "start" => "Start date",
        "due" => "Due date",
        "state" => "Open or closed",
        "percent" => "% complete",
        "wbs" => "WBS code",
        "estimate" => "Hours",
        "parent" => "Phase",
        "blocks" => "Dependencies",
        "person" => "Person",
        "sync" => "Sync",
        _ => field,
    };

    // ---- What each task becomes ----

    public enum Role
    {
        /// <summary>Not synced (a summary task below the phase levels).</summary>
        None,

        /// <summary>A detail task: an issue.</summary>
        Task,

        /// <summary>A summary task at a phase level: an epic or a milestone.</summary>
        Phase,
    }

    /// <summary>The plan's shape for GitLab: each task's role and nearest phase above it.</summary>
    public sealed class PlanShape
    {
        private readonly PlanOutline _outline;
        private readonly int _phaseLevels;

        public PlanShape(IEnumerable<Item> items, int phaseLevels)
        {
            _outline = new PlanOutline(items);
            _phaseLevels = Math.Max(0, phaseLevels);
        }

        public PlanOutline Outline => _outline;

        public IEnumerable<Item> Items => _outline.Ordered;

        public Role RoleOf(Guid itemId)
        {
            if (_outline.Find(itemId) is null)
            {
                return Role.None;
            }

            if (!_outline.IsSummary(itemId))
            {
                return Role.Task;
            }

            return _phaseLevels == 0 || _outline.Level.GetValueOrDefault(itemId, 1) <= _phaseLevels ? Role.Phase : Role.None;
        }

        /// <summary>The nearest phase above the task, if any.</summary>
        public Item? PhaseOf(Guid itemId)
        {
            for (var cursor = _outline.Find(itemId); cursor is not null && _outline.ParentOf(cursor) is { } parentId; cursor = _outline.Find(parentId))
            {
                if (RoleOf(parentId) == Role.Phase)
                {
                    return _outline.Find(parentId);
                }
            }

            return null;
        }

        /// <summary>Phases parents first, then tasks, in plan order: the order to create them in GitLab.</summary>
        public IEnumerable<Item> InCreateOrder() =>
            _outline.Ordered.Where(i => RoleOf(i.Id) == Role.Phase).Concat(_outline.Ordered.Where(i => RoleOf(i.Id) == Role.Task));
    }

    // ---- Progress (GitLab owns it) ----

    /// <summary>
    /// A task's % complete from its issue: a closed issue is 100%. Otherwise its child tasks, weighted by their weight (1 when
    /// not set), then its description's checklist. Null means GitLab says nothing about progress, so Workbench's value stays.
    /// </summary>
    public static decimal? Progress(bool closed, IReadOnlyList<GitLabChildTask>? children, GitLabTaskCompletion? checklist)
    {
        if (closed)
        {
            return 100m;
        }

        if (children is { Count: > 0 })
        {
            var total = children.Sum(c => (decimal)Math.Max(c.Weight ?? 1, 0));
            if (total > 0)
            {
                var done = children.Where(c => c.Closed).Sum(c => (decimal)Math.Max(c.Weight ?? 1, 0));
                return Math.Round(done * 100m / total, 0, MidpointRounding.AwayFromZero);
            }
        }

        if (checklist is { Count: > 0 } list)
        {
            return Math.Round(list.CompletedCount * 100m / list.Count, 0, MidpointRounding.AwayFromZero);
        }

        return null;
    }

    public static string Percent(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    // ---- The description block Workbench keeps at the end of each issue ----

    private const string BlockStart = "<!-- workbench:begin";
    private const string BlockEnd = "<!-- workbench:end -->";

    /// <summary>The marker that finds a task's issue again (e.g. when a create is retried).</summary>
    public static string Marker(Guid itemId) => $"workbench-task:{itemId:N}";

    /// <summary>
    /// The issue description: the task's notes (shared) followed by a block Workbench owns, with what GitLab issues can't
    /// hold (the planned start, the WBS code, hours). The block is rewritten on every push and ignored when reading.
    /// </summary>
    public static string Compose(string? notes, Guid itemId, string? wbsCode, DateOnly? start, DateOnly? finish, decimal? hours, string? planUrl)
    {
        var facts = new List<string>();
        if (!string.IsNullOrWhiteSpace(wbsCode))
        {
            facts.Add($"WBS {wbsCode.Trim()}");
        }

        if (start is not null || finish is not null)
        {
            facts.Add($"planned {DateText(start)} to {DateText(finish)}");
        }

        if (hours is > 0)
        {
            facts.Add($"{hours.Value.ToString("0.##", CultureInfo.InvariantCulture)} h");
        }

        var link = string.IsNullOrWhiteSpace(planUrl) ? "" : $" ([open the plan]({planUrl}))";
        var block = $"{BlockStart} {Marker(itemId)} -->\n---\n_From the Workbench plan{link}: {(facts.Count == 0 ? "no dates yet" : string.Join(" · ", facts))}. " +
            $"Change dates, hours and the WBS on the plan; Workbench sets them here._\n{BlockEnd}";
        var body = (notes ?? "").TrimEnd();
        return body.Length == 0 ? block : $"{body}\n\n{block}";
    }

    /// <summary>The description without Workbench's block (the shared part), or null when that's empty.</summary>
    public static string? Strip(string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return null;
        }

        var text = description.Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = text.IndexOf(BlockStart, StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = text.IndexOf(BlockEnd, start, StringComparison.Ordinal);
            text = end < 0 ? text[..start] : text[..start] + text[(end + BlockEnd.Length)..];
        }

        text = text.Trim();
        return text.Length == 0 ? null : text;
    }

    public static string? NormalizeNotes(string? notes)
    {
        var text = notes?.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string DateText(DateOnly? date) => date?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) ?? "(not set)";

    // ---- Small helpers ----

    /// <summary>A milestone's title: GitLab needs them unique in a project, so the WBS code leads (<c>1001.01 Design</c>).</summary>
    public static string MilestoneTitle(string title, string? wbsCode) =>
        string.IsNullOrWhiteSpace(wbsCode) ? title.Trim() : $"{wbsCode.Trim()} {title.Trim()}";

    /// <summary>The task title from a milestone title, without the WBS code in front.</summary>
    public static string TitleFromMilestone(string title, string? wbsCode) =>
        !string.IsNullOrWhiteSpace(wbsCode) && title.StartsWith(wbsCode.Trim() + " ", StringComparison.Ordinal) ? title[(wbsCode.Trim().Length + 1)..].Trim() : title.Trim();

    public static string Ids(IEnumerable<long> ids) => string.Join(",", ids.Distinct().Order());

    public static IReadOnlySet<long> ParseIds(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0).Where(v => v != 0).ToHashSet();

    public static string Label(Domain.Integrations.GitLab.GitLabObjectKind kind, long iid) => kind switch
    {
        Domain.Integrations.GitLab.GitLabObjectKind.Epic => $"epic &{iid}",
        Domain.Integrations.GitLab.GitLabObjectKind.Milestone => $"milestone %{iid}",
        _ => $"issue #{iid}",
    };

    public static string Clip(string? text, int max) => text is null ? "" : text.Length <= max ? text : text[..(max - 1)] + "…";
}
