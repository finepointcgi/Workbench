using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Assistant;
using Workbench.Contracts;
using Workbench.Contracts.Resourcing;
using Workbench.Domain.Assistant;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;

namespace Workbench.Application.Resourcing;

/// <summary>One person weighed for a task: what the rules know about them.</summary>
public sealed record SuggestionCandidate(
    Guid EmployeeId,
    string Name,
    decimal FreeHours,
    decimal CapacityHours,
    Guid? LaborCategoryId,
    string? LaborCategory,
    bool Qualifies,
    IReadOnlyList<EmployeeSkillDto> Skills);

/// <summary>
/// The rules half of staffing suggestions: free hours in the window (45 points), skills the task text names (40) and the
/// labor category (15). Deterministic, so suggestions work with the assistant off and the assistant has a baseline.
/// </summary>
public static partial class StaffingSuggestionRules
{
    public static StaffingSuggestionDto Score(SuggestionCandidate person, string taskText, decimal? hoursNeeded, Guid? laborCategoryId)
    {
        var reasons = new List<string>();
        var concerns = new List<string>();

        // Free hours.
        var free = Math.Round(person.FreeHours, 1);
        double availability;
        if (hoursNeeded is > 0)
        {
            availability = (double)Math.Min(1m, person.FreeHours / hoursNeeded.Value);
            if (person.FreeHours >= hoursNeeded)
            {
                reasons.Add($"{Hours(free)} h free, enough for the {Hours(hoursNeeded.Value)} h");
            }
            else
            {
                reasons.Add($"{Hours(free)} h free");
                concerns.Add($"only {Hours(free)} of the {Hours(hoursNeeded.Value)} h would fit in these dates");
            }
        }
        else
        {
            // No hours given: a quarter of their capacity free in the window counts as plenty.
            availability = person.CapacityHours > 0 ? (double)Math.Clamp(person.FreeHours / (person.CapacityHours * 0.25m), 0m, 1m) : 0;
            reasons.Add($"{Hours(free)} h free in these dates");
        }

        var score = (int)Math.Round(availability * 45);

        // Skills the task names.
        var matched = person.Skills.Where(s => Mentions(taskText, s.Name)).OrderByDescending(s => s.Level).ToList();
        score += Math.Min(40, matched.Sum(s => (int)s.Level * 8));
        if (matched.Count > 0)
        {
            reasons.Add(string.Join(", ", matched.Take(3).Select(s => $"{s.Name} ({SkillLabels.Label(s.Level)})")));
        }

        // Labor category.
        if (laborCategoryId is null)
        {
            score += 5;
        }
        else if (person.LaborCategoryId == laborCategoryId)
        {
            score += 15;
            reasons.Add($"their own labor category, {person.LaborCategory}");
        }
        else if (person.Qualifies)
        {
            score += 10;
            reasons.Add("qualified for the labor category");
        }
        else
        {
            concerns.Add("not qualified for the labor category yet");
        }

        var why = reasons.Count == 0 ? "Available." : char.ToUpperInvariant(reasons[0][0]) + string.Join("; ", reasons)[1..] + ".";
        return new StaffingSuggestionDto(person.EmployeeId, person.Name, Math.Clamp(score, 0, 100), why, concerns, free, false);
    }

    /// <summary>Whether the task text names the skill as a whole word or phrase (so "C#" and "SQL" match, "Java" doesn't match "JavaScript").</summary>
    public static bool Mentions(string text, string skill)
    {
        if (string.IsNullOrWhiteSpace(skill) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(skill.Trim())}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static List<StaffingSuggestionDto> Rank(IEnumerable<StaffingSuggestionDto> scored) =>
        scored.OrderByDescending(s => s.Score).ThenByDescending(s => s.FreeHours).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static string Hours(decimal hours) => hours.ToString("#,##0.#", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Suggests who to put on a task: the rules score everyone free in the window, then, when the assistant is on, it picks and
/// explains the best few from the top six. Never assigns anyone; the permission check is in the identity decorator.
/// </summary>
public sealed class StaffingSuggestionService(
    IWorkbenchDbContextFactory dbFactory,
    ProjectStaffingService staffing,
    ResourcePlanningService planning,
    AssistantRunner runner,
    TimeProvider clock) : IStaffingSuggestionService
{
    private const int Shown = 3;
    private const int ForAssistant = 6;

    public async Task<StaffingSuggestionsDto> SuggestAsync(SuggestStaffRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        string taskText;
        DateOnly from;
        DateOnly to;
        HashSet<Guid> alreadyOnTask;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var project = await db.Set<Project>().AsNoTracking().Where(p => p.Id == request.ProjectId).Select(p => new { p.Name, p.Start, p.Finish }).FirstOrDefaultAsync(cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Project", request.ProjectId);
            var element = request.WbsElementId is { } wbs
                ? await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == wbs && w.ProjectId == request.ProjectId, cancellationToken)
                : null;
            taskText = element is null ? project.Name : $"{element.Name} {await ItemTextAsync(db, element, cancellationToken)}";
            (DateOnly? taskStart, DateOnly? taskFinish) = element is null ? (project.Start, project.Finish) : await TaskDatesAsync(db, element, project.Start, project.Finish, cancellationToken);
            from = request.From ?? Max(taskStart, today) ?? today;
            to = request.To ?? taskFinish ?? from.AddDays(27);
            if (to < from)
            {
                to = from.AddDays(27);
            }

            if (to.DayNumber - from.DayNumber > 2 * 366)
            {
                to = from.AddDays(2 * 366);
            }

            alreadyOnTask = element is null ? []
                : (await db.Set<StaffingAssignment>().AsNoTracking().Where(a => a.WbsElementId == element.Id).Select(a => a.EmployeeId).ToListAsync(cancellationToken)).ToHashSet();
        }

        var free = (await planning.FindAvailablePeopleAsync(new FindPeopleQuery(from, to, null, 0.01m), cancellationToken)).ToDictionary(p => p.EmployeeId);
        var candidates = await staffing.ListCandidatesAsync(request.ProjectId, request.LaborCategoryId, cancellationToken);
        var scored = new List<StaffingSuggestionDto>();
        foreach (var c in candidates.Where(c => free.ContainsKey(c.EmployeeId) && !alreadyOnTask.Contains(c.EmployeeId)))
        {
            var f = free[c.EmployeeId];
            scored.Add(StaffingSuggestionRules.Score(
                new SuggestionCandidate(c.EmployeeId, c.Name, f.AvailableHours, f.CapacityHours, c.LaborCategoryId, c.LaborCategory, c.Qualifies, c.Skills ?? []),
                taskText, request.Hours, request.LaborCategoryId));
        }

        var ranked = StaffingSuggestionRules.Rank(scored);
        var refined = ranked.Count > 1 ? await RefineAsync(request, taskText, from, to, ranked.Take(ForAssistant).ToList(), candidates, cancellationToken) : null;
        var top = refined ?? ranked.Take(Shown).ToList();
        return new StaffingSuggestionsDto(top, from, to, request.Hours, refined is not null);
    }

    private async Task<List<StaffingSuggestionDto>?> RefineAsync(
        SuggestStaffRequest request, string taskText, DateOnly from, DateOnly to,
        List<StaffingSuggestionDto> top, IReadOnlyList<StaffingCandidateDto> candidates, CancellationToken ct)
    {
        var facts = new
        {
            task = Clip(taskText, 400),
            hours = request.Hours,
            from,
            to,
            candidates = top.Select(s =>
            {
                var c = candidates.First(x => x.EmployeeId == s.EmployeeId);
                return new
                {
                    id = s.EmployeeId,
                    name = s.Name,
                    rulesScore = s.Score,
                    freeHours = s.FreeHours,
                    laborCategory = c.LaborCategory,
                    skills = (c.Skills ?? []).Take(8).Select(k => $"{k.Name} ({SkillLabels.Label(k.Level)})"),
                    draftReason = s.Why,
                    draftConcerns = s.Concerns,
                };
            }).ToList(),
        };
        var completion = await runner.CompleteTextAsync(
            AssistantInteractionKind.StaffingSuggestion,
            "Pick who to put on a task",
            AssistantPrompts.StaffingSuggestionSystem,
            $"<workbench-data source=\"task and candidates\" untrusted=\"true\">\n{JsonSerializer.Serialize(facts, AssistantPrompts.DataJson)}\n</workbench-data>\n"
                + $"<draft-json>\n{JsonSerializer.Serialize(new { picks = top.Take(Shown).Select(s => new { id = s.EmployeeId, reason = s.Why }) }, AssistantPrompts.DataJson)}\n</draft-json>",
            Schema(),
            "resourcing",
            "the task and the candidates' free hours and skills",
            top.Select(s => $"employees/{s.EmployeeId}").ToList(),
            ct);
        return completion is null ? null : TryParse(completion.Text, top);
    }

    public static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["picks"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["reason"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("id", "reason"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("picks"),
        ["additionalProperties"] = false,
    };

    /// <summary>The model's order and reasons for the people it was given; ids it invented or repeated are dropped. Null when nothing usable.</summary>
    public static List<StaffingSuggestionDto>? TryParse(string json, IReadOnlyList<StaffingSuggestionDto> given)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        var byId = given.ToDictionary(s => s.EmployeeId);
        var picked = new List<StaffingSuggestionDto>();
        foreach (var item in (node as JsonObject)?["picks"] as JsonArray ?? [])
        {
            if (item is JsonObject o && o["id"]?.ToString() is { } idText && Guid.TryParse(idText, out var id)
                && byId.TryGetValue(id, out var rules) && picked.All(p => p.EmployeeId != id)
                && o["reason"]?.ToString() is { Length: > 0 } reason)
            {
                picked.Add(rules with { Why = Clip(reason.Trim(), 240)!, ByAssistant = true });
            }
        }

        return picked.Count == 0 ? null : picked.Take(Shown).ToList();
    }

    private static async Task<string> ItemTextAsync(IWorkbenchDbContext db, WbsElement element, CancellationToken ct)
    {
        if (element.ItemId is not { } itemId)
        {
            return "";
        }

        return await db.Items.AsNoTracking().Where(i => i.Id == itemId).Select(i => i.Title).FirstOrDefaultAsync(ct) ?? "";
    }

    private static async Task<(DateOnly? Start, DateOnly? Finish)> TaskDatesAsync(IWorkbenchDbContext db, WbsElement element, DateOnly? projectStart, DateOnly? projectFinish, CancellationToken ct)
    {
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == element.ProjectId)
            .Select(w => new { w.Id, w.ParentId, w.ItemId }).ToDictionaryAsync(w => w.Id, ct);
        for (Guid? current = element.Id; current is { } c && wbs.TryGetValue(c, out var w); current = w.ParentId)
        {
            if (w.ItemId is { } itemId
                && await db.Items.AsNoTracking().Where(i => i.Id == itemId).Select(i => new { i.Start, i.Finish }).FirstOrDefaultAsync(ct) is { } item
                && (item.Start is not null || item.Finish is not null))
            {
                return (item.Start, item.Finish);
            }
        }

        return (projectStart, projectFinish);
    }

    private static DateOnly? Max(DateOnly? a, DateOnly b) => a is { } x && x > b ? x : b;

    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..(max - 1)] + "…";
}
