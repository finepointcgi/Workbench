using System.Globalization;
using Workbench.Contracts.Scheduling;

namespace Workbench.Application.Scheduling;

/// <summary>
/// A detail task as the health check sees it. <paramref name="Percent"/> is % complete (null: no progress recorded);
/// <paramref name="HasResources"/> is true when people or hours are assigned to it.
/// </summary>
public sealed record HealthTask(
    Guid Id,
    int TaskNumber,
    string OutlineNumber,
    string Title,
    DateOnly Start,
    DateOnly Finish,
    bool IsMilestone = false,
    decimal? Percent = null,
    bool HasResources = true,
    PlanConstraintDto? Constraint = null)
{
    public bool IsComplete => Percent >= 100;

    public bool IsStarted => Percent > 0;
}

/// <summary>
/// A plan to assess: its dated detail tasks, the links between them, the working calendar, the status date and (optionally)
/// a baseline's dates by task.
/// </summary>
public sealed record HealthPlan(
    IReadOnlyList<HealthTask> Tasks,
    IReadOnlyList<ScheduleLink> Links,
    WorkCalendar Calendar,
    DateOnly StatusDate,
    IReadOnlyDictionary<Guid, (DateOnly? Start, DateOnly? Finish)>? Baseline = null);

/// <summary>
/// The DCMA 14-point assessment, as pure rules over a <see cref="HealthPlan"/>. Thresholds are DCMA's: 5% for logic, lags,
/// hard constraints, high float, high duration and missed tasks; none for leads, negative float, invalid dates and
/// unresourced tasks; 90% Finish-to-Start links; CPLI and BEI of 0.95 or more. Float and durations are working days on the
/// plan's calendar; "high" is more than 44 working days (two months).
/// </summary>
public static class ScheduleHealth
{
    public const int HighDays = 44;

    /// <summary>How many working days the critical path test delays a critical task by.</summary>
    public const int TestDelayDays = 100;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly PlanConstraintKind[] HardConstraints =
        [PlanConstraintKind.MustStartOn, PlanConstraintKind.MustFinishOn, PlanConstraintKind.StartNoLaterThan, PlanConstraintKind.FinishNoLaterThan];

    public static IReadOnlyList<HealthCheckDto> Evaluate(HealthPlan plan)
    {
        var tasks = plan.Tasks;
        var byId = tasks.ToDictionary(t => t.Id);
        var links = plan.Links.Where(l => l.PredecessorId != l.SuccessorId && byId.ContainsKey(l.PredecessorId) && byId.ContainsKey(l.SuccessorId)).ToList();
        var open = tasks.Where(t => !t.IsComplete).ToList();
        var analysis = Analyze(plan, tasks, links);
        var slack = analysis.Items.ToDictionary(i => i.ItemId);

        // Links into remaining work: the ones that still drive the schedule.
        var openLinks = links.Where(l => !byId[l.SuccessorId].IsComplete).ToList();
        HealthTaskDto Row(HealthTask t, string detail) => new(t.Id, t.TaskNumber, t.OutlineNumber, t.Title, detail);
        HealthTaskDto LinkRow(ScheduleLink l, string detail) => Row(byId[l.SuccessorId], $"{detail} on the link from task {byId[l.PredecessorId].TaskNumber.ToString(Inv)}");

        return
        [
            Logic(tasks, open, links, Row),
            Ratio(2, "leads", "Leads", "A lead (negative lag) lets a task start before its predecessor is done; it hides the real logic.",
                "None", 0, openLinks.Count, openLinks.Where(l => l.LagDays < 0).Select(l => LinkRow(l, $"Lead of {Days(-l.LagDays)}")).ToList(), "links"),
            Ratio(3, "lags", "Lags", "A lag is waiting time nobody owns; a task for the wait can be tracked instead.",
                "5% of links or less", 5, openLinks.Count, openLinks.Where(l => l.LagDays > 0).Select(l => LinkRow(l, $"Lag of {Days(l.LagDays)}")).ToList(), "links"),
            Ratio(4, "relationships", "Relationship types", "Finish-to-Start links are the easiest to follow, so most links should be FS.",
                "90% or more Finish-to-Start", 10, openLinks.Count,
                openLinks.Where(l => l.Type != DependencyKind.FinishToStart).Select(l => LinkRow(l, KindName(l.Type))).ToList(), "links",
                flaggedAre: "aren't Finish-to-Start", allClear: "All {0} links are Finish-to-Start."),
            Ratio(5, "hard-constraints", "Hard constraints", "Must Start/Finish On and No Later Than dates stop the logic from moving tasks, which hides slips.",
                "5% of tasks or less", 5, open.Count,
                open.Where(t => t.Constraint is { } c && HardConstraints.Contains(c.Kind))
                    .Select(t => Row(t, $"{t.Constraint!.Label} {t.Constraint.Date.ToString("MMM d, yyyy", Inv)}")).ToList()),
            Ratio(6, "high-float", "High float", $"More than {HighDays} working days (about two months) of float usually means a missing link.",
                "5% of tasks or less", 5, open.Count,
                open.Where(t => slack.TryGetValue(t.Id, out var s) && s.SlackDays > HighDays).Select(t => Row(t, $"{Days(slack[t.Id].SlackDays)} of float")).ToList()),
            Ratio(7, "negative-float", "Negative float", "Negative float means a deadline can't be met as the plan stands.",
                "None", 0, open.Count,
                open.Where(t => slack.TryGetValue(t.Id, out var s) && s.SlackDays < 0).Select(t => Row(t, $"{Days(slack[t.Id].SlackDays)} of float")).ToList()),
            Ratio(8, "high-duration", "High duration", $"Tasks longer than {HighDays} working days are hard to status; split them into smaller pieces.",
                "5% of tasks or less", 5, open.Count(t => !t.IsMilestone),
                open.Where(t => !t.IsMilestone && Duration(plan, t) > HighDays).Select(t => Row(t, $"{Days(Duration(plan, t))} long")).ToList()),
            InvalidDates(plan, tasks, Row),
            Ratio(10, "resources", "Resources", "Every task with work should have people or hours assigned, so the plan can be staffed.",
                "None without resources", 0, open.Count(t => !t.IsMilestone),
                open.Where(t => !t.IsMilestone && !t.HasResources).Select(t => Row(t, "No one assigned and no hours")).ToList()),
            MissedTasks(plan, tasks, Row),
            CriticalPathTest(plan, tasks, links, analysis, Row),
            Cpli(plan, open, analysis, Row),
            Bei(plan, tasks, Row),
        ];
    }

    /// <summary>The critical path over the plan's detail tasks (planned dates and constraints).</summary>
    public static ScheduleAnalysis Analyze(HealthPlan plan, IReadOnlyCollection<HealthTask> tasks, IReadOnlyCollection<ScheduleLink> links) =>
        CriticalPathCalculator.Analyze(
            tasks.Select(t => new ScheduleTask(t.Id, t.Start, t.Finish, t.IsMilestone)).ToList(),
            links,
            plan.Calendar,
            tasks.Where(t => t.Constraint is not null).ToDictionary(t => t.Id, t => t.Constraint!));

    private static int Duration(HealthPlan plan, HealthTask t) => plan.Calendar.Duration(t.Start, t.Finish, t.IsMilestone);

    /// <summary>
    /// 1. Missing logic. The plan's first task may have no predecessor and its last no successor (the start and finish of the
    /// project); every other unfinished task needs both.
    /// </summary>
    private static HealthCheckDto Logic(
        IReadOnlyList<HealthTask> tasks, IReadOnlyList<HealthTask> open, IReadOnlyList<ScheduleLink> links, Func<HealthTask, string, HealthTaskDto> row)
    {
        var hasPredecessor = links.Select(l => l.SuccessorId).ToHashSet();
        var hasSuccessor = links.Select(l => l.PredecessorId).ToHashSet();
        var first = tasks.OrderBy(t => t.Start).ThenBy(t => t.TaskNumber).FirstOrDefault()?.Id;
        var last = tasks.OrderByDescending(t => t.Finish).ThenByDescending(t => t.TaskNumber).FirstOrDefault()?.Id;
        var flagged = new List<HealthTaskDto>();
        foreach (var t in open)
        {
            var noPredecessor = !hasPredecessor.Contains(t.Id) && t.Id != first;
            var noSuccessor = !hasSuccessor.Contains(t.Id) && t.Id != last;
            if (noPredecessor || noSuccessor)
            {
                flagged.Add(row(t, noPredecessor && noSuccessor ? "No predecessor or successor" : noPredecessor ? "No predecessor" : "No successor"));
            }
        }

        return Ratio(1, "logic", "Logic", "Every task needs a task before it and after it, so a slip flows through the plan.",
            "5% of tasks or less", 5, open.Count, flagged);
    }

    /// <summary>9. Progress that can't be true on the status date: work done in the future, or planned work left in the past.</summary>
    private static HealthCheckDto InvalidDates(HealthPlan plan, IReadOnlyList<HealthTask> tasks, Func<HealthTask, string, HealthTaskDto> row)
    {
        var flagged = new List<HealthTaskDto>();
        foreach (var t in tasks)
        {
            var reason = t switch
            {
                { IsComplete: true } when t.Finish > plan.StatusDate => "Marked complete, but finishes after the status date",
                { IsStarted: true } when t.Start > plan.StatusDate => "Shown as started, but starts after the status date",
                { IsComplete: false } when t.Finish < plan.StatusDate => "Not complete, but its finish date has passed",
                { IsStarted: false } when t.Start < plan.StatusDate => "Not started, but its start date has passed",
                _ => null,
            };
            if (reason is not null)
            {
                flagged.Add(row(t, reason));
            }
        }

        return Ratio(9, "invalid-dates", "Invalid dates", "Work can't be done in the future, and planned work can't be left in the past; update the status.",
            "None", 0, tasks.Count, flagged);
    }

    /// <summary>11. Tasks the baseline had finishing before the status date that finished late or haven't finished.</summary>
    private static HealthCheckDto MissedTasks(HealthPlan plan, IReadOnlyList<HealthTask> tasks, Func<HealthTask, string, HealthTaskDto> row)
    {
        const string name = "Missed tasks", explanation = "Tasks the baseline said would be done by now that finished late or not at all.", threshold = "5% of tasks or less";
        if (plan.Baseline is null)
        {
            return NotApplicable(11, "missed-tasks", name, explanation, threshold, "Save a baseline on the Gantt to measure this.");
        }

        var due = Due(plan, tasks);
        if (due.Count == 0)
        {
            return NotApplicable(11, "missed-tasks", name, explanation, threshold, "No baselined task was due to finish before the status date.");
        }

        var flagged = due
            .Where(d => !d.Task.IsComplete || d.Task.Finish > d.BaselineFinish)
            .Select(d => row(d.Task, d.Task.IsComplete
                ? $"Finished {Days(plan.Calendar.Between(d.BaselineFinish, d.Task.Finish))} after the baseline finish {d.BaselineFinish.ToString("MMM d", Inv)}"
                : $"Not complete; the baseline finish was {d.BaselineFinish.ToString("MMM d, yyyy", Inv)}"))
            .ToList();
        return Ratio(11, "missed-tasks", name, explanation, threshold, 5, due.Count, flagged);
    }

    /// <summary>
    /// 12. Delays the most critical unfinished task by <see cref="TestDelayDays"/> working days: the plan's finish must move by
    /// the same amount. When it doesn't, the critical path is broken (a missing link, or a hard date holding it).
    /// </summary>
    private static HealthCheckDto CriticalPathTest(
        HealthPlan plan, IReadOnlyList<HealthTask> tasks, IReadOnlyList<ScheduleLink> links, ScheduleAnalysis analysis, Func<HealthTask, string, HealthTaskDto> row)
    {
        const string name = "Critical path test", explanation = "Delaying a critical task should delay the finish by the same amount; if not, the logic is broken.",
            threshold = "The finish moves by the delay";
        // The driving path to the finish: deadlines (No Later Than) only add negative float, so they're left out when choosing.
        var driving = Analyze(plan, tasks.Select(t => t.Constraint?.Kind is PlanConstraintKind.StartNoLaterThan or PlanConstraintKind.FinishNoLaterThan
            ? t with { Constraint = null } : t).ToList(), links);
        var slack = driving.Items.ToDictionary(i => i.ItemId);
        var candidate = tasks.Where(t => !t.IsComplete && slack.ContainsKey(t.Id))
            .OrderBy(t => slack[t.Id].SlackDays).ThenBy(t => t.IsMilestone).ThenBy(t => slack[t.Id].EarlyStart).ThenBy(t => t.TaskNumber)
            .FirstOrDefault();
        if (candidate is null || analysis.ProjectFinish is not { } finish)
        {
            return NotApplicable(12, "critical-path-test", name, explanation, threshold, "There's no unfinished work to test.");
        }

        var delayed = tasks.Select(t => t.Id != candidate.Id ? t : t with
        {
            Finish = plan.Calendar.AddWorkingDays(plan.Calendar.OnOrAfter(t.Finish), TestDelayDays),
            IsMilestone = false,
        }).ToList();

        // The task's own date constraint would hold it, so the test lifts it; anything else holding the finish is a fault.
        var moved = Analyze(plan, delayed.Select(t => t.Id == candidate.Id ? t with { Constraint = null } : t).ToList(), links).ProjectFinish is { } after
            ? plan.Calendar.Between(finish, after)
            : 0;
        var passed = moved == TestDelayDays;
        var detail = $"Delayed {Days(TestDelayDays)}: the finish moved {Days(moved)}";
        return new HealthCheckDto(12, "critical-path-test", name, explanation, threshold,
            passed ? HealthCheckStatus.Pass : HealthCheckStatus.Fail, passed ? 0 : 1, 1, null, null,
            passed ? $"Delaying task {candidate.TaskNumber.ToString(Inv)} by {Days(TestDelayDays)} moved the finish by the same amount."
                : $"Delaying task {candidate.TaskNumber.ToString(Inv)} by {Days(TestDelayDays)} moved the finish only {Days(moved)}.",
            [row(candidate, detail)]);
    }

    /// <summary>
    /// 13. Critical path length index: (remaining critical path length + its total float) ÷ the length. The length runs from
    /// the status date (or the plan's start, if later) to the planned finish; the float is the lowest total float of the
    /// unfinished tasks, negative when a deadline is missed.
    /// </summary>
    private static HealthCheckDto Cpli(HealthPlan plan, IReadOnlyList<HealthTask> open, ScheduleAnalysis analysis, Func<HealthTask, string, HealthTaskDto> row)
    {
        const string name = "Critical path length index (CPLI)",
            explanation = "Whether the remaining critical path can still make the finish: 1.0 is on time, under 1.0 behind.", threshold = "0.95 or more";
        var slack = analysis.Items.ToDictionary(i => i.ItemId);
        var remaining = open.Where(t => slack.ContainsKey(t.Id)).ToList();
        if (remaining.Count == 0 || analysis.ProjectStart is not { } start || analysis.ProjectFinish is not { } finish)
        {
            return NotApplicable(13, "cpli", name, explanation, threshold, "There's no unfinished work.");
        }

        var length = CriticalPathLength(plan.Calendar, plan.StatusDate, start, finish);
        if (length <= 0)
        {
            return NotApplicable(13, "cpli", name, explanation, threshold, "The planned finish has passed.");
        }

        var totalFloat = remaining.Min(t => slack[t.Id].SlackDays);
        var cpli = Index(length + totalFloat, length);
        var passed = cpli >= 0.95m;
        var flagged = totalFloat < 0
            ? remaining.Where(t => slack[t.Id].SlackDays == totalFloat).Select(t => row(t, $"{Days(totalFloat)} of float")).ToList()
            : [];
        return new HealthCheckDto(13, "cpli", name, explanation, threshold, passed ? HealthCheckStatus.Pass : HealthCheckStatus.Fail,
            flagged.Count, remaining.Count, null, cpli,
            $"CPLI {cpli.ToString("0.00", Inv)}: {Days(length)} of critical path left with {Days(totalFloat)} of float.", flagged);
    }

    /// <summary>Working days from the status date (or the plan's start, if later) through the planned finish.</summary>
    public static int CriticalPathLength(WorkCalendar calendar, DateOnly statusDate, DateOnly start, DateOnly finish)
    {
        var from = statusDate > start ? statusDate : start;
        return calendar.Index(finish.AddDays(1)) - calendar.Index(from);
    }

    /// <summary>14. Baseline execution index: baselined tasks finished ÷ baselined tasks due to finish before the status date.</summary>
    private static HealthCheckDto Bei(HealthPlan plan, IReadOnlyList<HealthTask> tasks, Func<HealthTask, string, HealthTaskDto> row)
    {
        const string name = "Baseline execution index (BEI)",
            explanation = "Tasks finished compared with the tasks the baseline said would be finished by now.", threshold = "0.95 or more";
        if (plan.Baseline is null)
        {
            return NotApplicable(14, "bei", name, explanation, threshold, "Save a baseline on the Gantt to measure this.");
        }

        var due = Due(plan, tasks);
        if (due.Count == 0)
        {
            return NotApplicable(14, "bei", name, explanation, threshold, "No baselined task was due to finish before the status date.");
        }

        // Every finished task in the baseline counts, including ones finished early.
        var finished = tasks.Count(t => t.IsComplete && plan.Baseline.TryGetValue(t.Id, out var b) && b.Finish is not null);
        var bei = Index(finished, due.Count);
        var passed = bei >= 0.95m;
        var late = due.Where(d => !d.Task.IsComplete).Select(d => row(d.Task, $"Due {d.BaselineFinish.ToString("MMM d, yyyy", Inv)} in the baseline; not complete")).ToList();
        return new HealthCheckDto(14, "bei", name, explanation, threshold, passed ? HealthCheckStatus.Pass : HealthCheckStatus.Fail,
            late.Count, due.Count, null, bei,
            $"BEI {bei.ToString("0.00", Inv)}: {finished.ToString(Inv)} finished of {due.Count.ToString(Inv)} due by the status date.", late);
    }

    /// <summary>Baselined tasks whose baseline finish is before the status date.</summary>
    private static List<(HealthTask Task, DateOnly BaselineFinish)> Due(HealthPlan plan, IReadOnlyList<HealthTask> tasks) =>
        tasks.Select(t => (Task: t, Finish: plan.Baseline!.TryGetValue(t.Id, out var b) ? b.Finish : null))
            .Where(x => x.Finish is { } f && f < plan.StatusDate)
            .Select(x => (x.Task, x.Finish!.Value))
            .ToList();

    /// <summary>Two decimals, rounded half away from zero.</summary>
    public static decimal Index(int numerator, int denominator) =>
        denominator == 0 ? 0 : Math.Round((decimal)numerator / denominator, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// A "no more than <paramref name="maxPercent"/>% flagged" check (0: none at all). Nothing to measure (an empty plan, no
    /// links) is not applicable.
    /// </summary>
    private static HealthCheckDto Ratio(
        int number, string key, string name, string explanation, string threshold, decimal maxPercent, int total, IReadOnlyList<HealthTaskDto> flagged,
        string unit = "tasks", string flaggedAre = "are flagged", string? allClear = null)
    {
        if (total == 0)
        {
            return NotApplicable(number, key, name, explanation, threshold, $"There are no unfinished {unit} to measure.");
        }

        var percent = Math.Round(100m * flagged.Count / total, 1, MidpointRounding.AwayFromZero);
        var exact = 100m * flagged.Count / total;
        var passed = maxPercent == 0 ? flagged.Count == 0 : exact <= maxPercent;
        var result = flagged.Count == 0
            ? allClear is not null ? string.Format(Inv, allClear, total) : $"None of {total.ToString(Inv)} {unit} {flaggedAre}."
            : $"{flagged.Count.ToString(Inv)} of {total.ToString(Inv)} {unit} ({percent.ToString("0.#", Inv)}%) {flaggedAre}.";
        return new HealthCheckDto(number, key, name, explanation, threshold, passed ? HealthCheckStatus.Pass : HealthCheckStatus.Fail,
            flagged.Count, total, percent, null, result, flagged);
    }

    private static HealthCheckDto NotApplicable(int number, string key, string name, string explanation, string threshold, string why) =>
        new(number, key, name, explanation, threshold, HealthCheckStatus.NotApplicable, 0, 0, null, null, why, []);

    private static string Days(int days) => Math.Abs(days) == 1 ? $"{days.ToString(Inv)} working day" : $"{days.ToString(Inv)} working days";

    private static string KindName(DependencyKind kind) => kind switch
    {
        DependencyKind.StartToStart => "Start-to-Start",
        DependencyKind.FinishToFinish => "Finish-to-Finish",
        DependencyKind.StartToFinish => "Start-to-Finish",
        _ => "Finish-to-Start",
    };
}
