using Workbench.Application.Scheduling;
using Workbench.Contracts.Scheduling;

namespace Workbench.Tests.Scheduling;

/// <summary>Each DCMA check on crafted plans, passing and failing at its threshold, and the CPLI and BEI arithmetic.</summary>
public sealed class ScheduleHealthTests
{
    // Monday 2026-10-05.
    private static readonly DateOnly Mon = new(2026, 10, 5);
    private static readonly WorkCalendar Cal = WorkCalendar.Weekdays;

    /// <summary>A plan under construction. The status date defaults to before the plan, so nothing is due or late.</summary>
    private sealed class Plan
    {
        public List<HealthTask> Tasks { get; } = [];
        public List<ScheduleLink> Links { get; } = [];
        public Dictionary<Guid, (DateOnly? Start, DateOnly? Finish)>? Baseline { get; set; }
        public DateOnly Status { get; set; } = Mon.AddDays(-7);

        public HealthTask Add(string title, DateOnly start, int days, decimal? percent = null, bool resourced = true, PlanConstraintDto? constraint = null)
        {
            start = Cal.OnOrAfter(start);
            var task = new HealthTask(Guid.NewGuid(), Tasks.Count + 1, (Tasks.Count + 1).ToString(), title, start, Cal.Finish(start, days),
                days == 0, percent, resourced, constraint);
            Tasks.Add(task);
            return task;
        }

        /// <summary><paramref name="count"/> tasks of <paramref name="days"/> days, each starting the day after the last (FS links).</summary>
        public List<HealthTask> Chain(int count, int days = 1, DateOnly? start = null)
        {
            var list = new List<HealthTask>();
            var next = start ?? Mon;
            for (var i = 0; i < count; i++)
            {
                var task = Add($"Task {Tasks.Count + 1}", next, days);
                if (list.Count > 0)
                {
                    Link(list[^1], task);
                }

                list.Add(task);
                next = Cal.AddWorkingDays(task.Finish, 1);
            }

            return list;
        }

        public void Link(HealthTask from, HealthTask to, DependencyKind type = DependencyKind.FinishToStart, int lag = 0) =>
            Links.Add(new ScheduleLink(from.Id, to.Id, type, lag));

        public void Replace(HealthTask task, Func<HealthTask, HealthTask> change) => Tasks[Tasks.IndexOf(task)] = change(task);

        public IReadOnlyList<HealthCheckDto> Evaluate() => ScheduleHealth.Evaluate(new HealthPlan(Tasks, Links, Cal, Status, Baseline));

        public HealthCheckDto Check(string key) => Evaluate().Single(c => c.Key == key);
    }

    [Fact]
    public void A_linked_chain_passes_every_check_it_can_measure_and_numbers_them_like_dcma()
    {
        var plan = new Plan();
        plan.Chain(10, days: 3);

        var checks = plan.Evaluate();

        Assert.Equal(Enumerable.Range(1, 14), checks.Select(c => c.Number));
        Assert.Equal(["logic", "leads", "lags", "relationships", "hard-constraints", "high-float", "negative-float", "high-duration",
            "invalid-dates", "resources", "missed-tasks", "critical-path-test", "cpli", "bei"], checks.Select(c => c.Key));
        Assert.All(checks.Where(c => c.Key is not ("missed-tasks" or "bei")), c => Assert.Equal(HealthCheckStatus.Pass, c.Status));
        Assert.All(checks, c => Assert.False(string.IsNullOrWhiteSpace(c.Explanation)));

        // Without a baseline, missed tasks and BEI aren't measured.
        Assert.Equal(HealthCheckStatus.NotApplicable, plan.Check("missed-tasks").Status);
        Assert.Equal(HealthCheckStatus.NotApplicable, plan.Check("bei").Status);
    }

    [Fact]
    public void Logic_allows_5_percent_of_tasks_without_a_predecessor_or_successor()
    {
        var plan = new Plan();
        var chain = plan.Chain(19);

        // A side task: has a predecessor but nothing after it, and finishes before the plan does.
        var dangling = plan.Add("Side task", chain[4].Finish.AddDays(1), 1);
        plan.Link(chain[4], dangling);

        var logic = plan.Check("logic");
        Assert.Equal(HealthCheckStatus.Pass, logic.Status); // 1 of 20 = 5%
        Assert.Equal(1, logic.Count);
        Assert.Equal(20, logic.Total);
        Assert.Equal(5m, logic.Percent);
        Assert.Equal("No successor", Assert.Single(logic.Tasks).Detail);
        Assert.Equal(dangling.Id, logic.Tasks[0].ItemId);

        // The first task has no predecessor and the last no successor: that's the plan's start and finish, not flagged.
        Assert.DoesNotContain(logic.Tasks, t => t.ItemId == chain[0].Id || t.ItemId == chain[^1].Id);

        plan.Add("Orphan", Mon.AddDays(7), 1);
        logic = plan.Check("logic");
        Assert.Equal(HealthCheckStatus.Fail, logic.Status); // 2 of 21 = 9.5%
        Assert.Equal(9.5m, logic.Percent);
        Assert.Contains(logic.Tasks, t => t.Title == "Orphan" && t.Detail == "No predecessor or successor");
    }

    [Fact]
    public void Logic_ignores_finished_tasks()
    {
        var plan = new Plan();
        plan.Chain(5);
        plan.Add("Done orphan", Mon, 1, percent: 100);
        plan.Status = Mon.AddDays(1);

        Assert.DoesNotContain(plan.Check("logic").Tasks, t => t.Title == "Done orphan");
    }

    [Fact]
    public void Any_lead_fails()
    {
        var plan = new Plan();
        var chain = plan.Chain(3);
        Assert.Equal(HealthCheckStatus.Pass, plan.Check("leads").Status);

        plan.Links[1] = plan.Links[1] with { LagDays = -1 };
        var leads = plan.Check("leads");
        Assert.Equal(HealthCheckStatus.Fail, leads.Status);
        Assert.Equal(1, leads.Count);
        Assert.Equal(2, leads.Total);
        Assert.Equal(chain[2].Id, leads.Tasks[0].ItemId);
        Assert.Equal("Lead of 1 working day on the link from task 2", leads.Tasks[0].Detail);
    }

    [Fact]
    public void Lags_are_allowed_on_5_percent_of_links()
    {
        var plan = new Plan();
        plan.Chain(21); // 20 links
        plan.Links[3] = plan.Links[3] with { LagDays = 2 };

        var lags = plan.Check("lags");
        Assert.Equal(HealthCheckStatus.Pass, lags.Status);
        Assert.Equal((1, 20, 5m), (lags.Count, lags.Total, lags.Percent));

        plan.Links[7] = plan.Links[7] with { LagDays = 1 };
        lags = plan.Check("lags");
        Assert.Equal(HealthCheckStatus.Fail, lags.Status);
        Assert.Equal(10m, lags.Percent);
    }

    [Fact]
    public void Relationship_types_need_90_percent_finish_to_start()
    {
        var plan = new Plan();
        plan.Chain(11); // 10 links
        Assert.Equal("All 10 links are Finish-to-Start.", plan.Check("relationships").Result);

        plan.Links[0] = plan.Links[0] with { Type = DependencyKind.StartToStart };
        Assert.Equal(HealthCheckStatus.Pass, plan.Check("relationships").Status); // 90% FS

        plan.Links[1] = plan.Links[1] with { Type = DependencyKind.FinishToFinish };
        var types = plan.Check("relationships");
        Assert.Equal(HealthCheckStatus.Fail, types.Status); // 80% FS
        Assert.Equal(2, types.Count);
        Assert.Contains(types.Tasks, t => t.Detail.StartsWith("Finish-to-Finish", StringComparison.Ordinal));
    }

    [Fact]
    public void Hard_constraints_are_allowed_on_5_percent_of_tasks_and_soft_ones_dont_count()
    {
        var plan = new Plan();
        var chain = plan.Chain(20);
        plan.Replace(chain[2], t => t with { Constraint = new PlanConstraintDto(PlanConstraintKind.MustStartOn, t.Start) });
        plan.Replace(chain[3], t => t with { Constraint = new PlanConstraintDto(PlanConstraintKind.StartNoEarlierThan, t.Start) });
        plan.Replace(chain[4], t => t with { Constraint = new PlanConstraintDto(PlanConstraintKind.FinishNoEarlierThan, t.Finish) });

        var hard = plan.Check("hard-constraints");
        Assert.Equal(HealthCheckStatus.Pass, hard.Status);
        Assert.Equal(1, hard.Count);
        Assert.StartsWith("Must Start On", hard.Tasks[0].Detail, StringComparison.Ordinal);

        plan.Replace(plan.Tasks[10], t => t with { Constraint = new PlanConstraintDto(PlanConstraintKind.FinishNoLaterThan, t.Finish.AddDays(30)) });
        hard = plan.Check("hard-constraints");
        Assert.Equal(HealthCheckStatus.Fail, hard.Status);
        Assert.Equal(10m, hard.Percent);
    }

    /// <summary>Start → long (50 d) → End and Start → short → End: the short task has 50 − its length days of float.</summary>
    private static (Plan Plan, HealthTask Short) Diamond(int shortDays)
    {
        var plan = new Plan();
        var start = plan.Add("Start", Mon, 0);
        var longTask = plan.Add("Long", Mon, 50);
        var shortTask = plan.Add("Short", Mon, shortDays);
        var end = plan.Add("End", Cal.AddWorkingDays(longTask.Finish, 1), 0);
        plan.Link(start, longTask);
        plan.Link(start, shortTask);
        plan.Link(longTask, end);
        plan.Link(shortTask, end);
        return (plan, shortTask);
    }

    [Fact]
    public void High_float_flags_more_than_44_working_days()
    {
        var (plan, shortTask) = Diamond(shortDays: 6); // 44 days of float: allowed
        Assert.Empty(plan.Check("high-float").Tasks);

        (plan, shortTask) = Diamond(shortDays: 5); // 45 days
        var high = plan.Check("high-float");
        Assert.Equal(HealthCheckStatus.Fail, high.Status);
        Assert.Equal(shortTask.Id, Assert.Single(high.Tasks).ItemId);
        Assert.Equal("45 working days of float", high.Tasks[0].Detail);
    }

    [Fact]
    public void Negative_float_fails_when_a_deadline_cant_be_met()
    {
        var plan = new Plan();
        var chain = plan.Chain(5, days: 2);
        Assert.Equal(HealthCheckStatus.Pass, plan.Check("negative-float").Status);

        // The last task must finish 3 working days before the chain can get there.
        plan.Replace(chain[^1], t => t with { Constraint = new PlanConstraintDto(PlanConstraintKind.FinishNoLaterThan, Cal.AddWorkingDays(t.Finish, -3)) });
        var negative = plan.Check("negative-float");
        Assert.Equal(HealthCheckStatus.Fail, negative.Status);
        Assert.Equal(5, negative.Count); // the whole chain drives the deadline
        Assert.All(negative.Tasks, t => Assert.Equal("-3 working days of float", t.Detail));
    }

    [Fact]
    public void High_duration_flags_tasks_over_44_working_days()
    {
        var plan = new Plan();
        var chain = plan.Chain(20, days: 2);
        plan.Replace(chain[5], t => t with { Finish = Cal.Finish(t.Start, 44) });
        Assert.Empty(plan.Check("high-duration").Tasks);

        plan.Replace(plan.Tasks[5], t => t with { Finish = Cal.Finish(t.Start, 45) });
        var high = plan.Check("high-duration");
        Assert.Equal(HealthCheckStatus.Pass, high.Status); // 1 of 20 = 5%
        Assert.Equal("45 working days long", Assert.Single(high.Tasks).Detail);

        plan.Replace(plan.Tasks[6], t => t with { Finish = Cal.Finish(t.Start, 60) });
        Assert.Equal(HealthCheckStatus.Fail, plan.Check("high-duration").Status);
    }

    [Fact]
    public void Invalid_dates_flag_progress_in_the_future_and_plans_left_in_the_past()
    {
        var plan = new Plan { Status = Mon.AddDays(14) }; // Monday 10-19
        var done = plan.Add("Done on time", Mon, 3, percent: 100);
        var underway = plan.Add("Underway", Mon.AddDays(10), 10, percent: 40);
        var doneFuture = plan.Add("Done in the future", Mon.AddDays(15), 2, percent: 100);
        var startedFuture = plan.Add("Started in the future", Mon.AddDays(21), 2, percent: 10);
        var notStarted = plan.Add("Should have started", Mon.AddDays(7), 10, percent: 0);
        var notFinished = plan.Add("Should have finished", Mon, 5, percent: 50);
        var later = plan.Add("Later", Mon.AddDays(21), 5);

        var invalid = plan.Check("invalid-dates");

        Assert.Equal(HealthCheckStatus.Fail, invalid.Status);
        Assert.Equal(7, invalid.Total);
        var flagged = invalid.Tasks.ToDictionary(t => t.ItemId, t => t.Detail);
        Assert.Equal(4, flagged.Count);
        Assert.Equal("Marked complete, but finishes after the status date", flagged[doneFuture.Id]);
        Assert.Equal("Shown as started, but starts after the status date", flagged[startedFuture.Id]);
        Assert.Equal("Not started, but its start date has passed", flagged[notStarted.Id]);
        Assert.Equal("Not complete, but its finish date has passed", flagged[notFinished.Id]);
        Assert.DoesNotContain(done.Id, flagged.Keys);
        Assert.DoesNotContain(underway.Id, flagged.Keys);
        Assert.DoesNotContain(later.Id, flagged.Keys);

        var clean = new Plan { Status = Mon.AddDays(14) };
        clean.Add("Done", Mon, 3, percent: 100);
        clean.Add("Later", Mon.AddDays(21), 5);
        Assert.Equal(HealthCheckStatus.Pass, clean.Check("invalid-dates").Status);
    }

    [Fact]
    public void Resources_flag_unfinished_work_without_people_or_hours_but_not_milestones()
    {
        var plan = new Plan();
        var chain = plan.Chain(4, days: 2);
        plan.Add("Review (milestone)", Mon.AddDays(14), 0, resourced: false);
        Assert.Equal(HealthCheckStatus.Pass, plan.Check("resources").Status);
        Assert.Equal(4, plan.Check("resources").Total);

        plan.Replace(chain[1], t => t with { HasResources = false });
        var resources = plan.Check("resources");
        Assert.Equal(HealthCheckStatus.Fail, resources.Status);
        Assert.Equal(chain[1].Id, Assert.Single(resources.Tasks).ItemId);
    }

    /// <summary>20 one-day tasks, all baselined to finish before the status date; the first <paramref name="done"/> are complete on time.</summary>
    private static Plan DuePlan(int done)
    {
        var plan = new Plan();
        var chain = plan.Chain(20);
        plan.Status = Cal.AddWorkingDays(chain[^1].Finish, 5);
        plan.Baseline = chain.ToDictionary(t => t.Id, t => ((DateOnly?)t.Start, (DateOnly?)t.Finish));
        for (var i = 0; i < chain.Count; i++)
        {
            var percent = i < done ? 100m : 50m;
            plan.Replace(chain[i], t => t with { Percent = percent });
        }

        return plan;
    }

    [Fact]
    public void Missed_tasks_allow_5_percent_of_tasks_due_by_the_status_date()
    {
        var plan = DuePlan(done: 19);
        var missed = plan.Check("missed-tasks");
        Assert.Equal(HealthCheckStatus.Pass, missed.Status);
        Assert.Equal((1, 20, 5m), (missed.Count, missed.Total, missed.Percent));
        Assert.StartsWith("Not complete", missed.Tasks[0].Detail, StringComparison.Ordinal);

        // Finishing late counts as missed too.
        plan.Replace(plan.Tasks[0], t => t with { Finish = Cal.AddWorkingDays(t.Finish, 2), Start = t.Start });
        missed = plan.Check("missed-tasks");
        Assert.Equal(HealthCheckStatus.Fail, missed.Status);
        Assert.Contains(missed.Tasks, t => t.Detail.StartsWith("Finished 2 working days after", StringComparison.Ordinal));
    }

    [Fact]
    public void Bei_divides_finished_tasks_by_the_tasks_due()
    {
        var bei = DuePlan(done: 19).Check("bei");
        Assert.Equal(HealthCheckStatus.Pass, bei.Status);
        Assert.Equal(0.95m, bei.Value);
        Assert.Equal("BEI 0.95: 19 finished of 20 due by the status date.", bei.Result);

        bei = DuePlan(done: 18).Check("bei");
        Assert.Equal(HealthCheckStatus.Fail, bei.Status);
        Assert.Equal(0.90m, bei.Value);
        Assert.Equal(2, bei.Tasks.Count);

        // A task finished ahead of its baseline counts as finished, so BEI can go above 1.
        var ahead = DuePlan(done: 20);
        var future = ahead.Add("Done early", ahead.Status.AddDays(-3), 1, percent: 100);
        ahead.Baseline![future.Id] = (ahead.Status.AddDays(7), ahead.Status.AddDays(7));
        Assert.Equal(1.05m, ahead.Check("bei").Value);
    }

    [Fact]
    public void Critical_path_test_passes_when_a_delay_reaches_the_finish_and_fails_when_a_pinned_date_holds_it()
    {
        var plan = new Plan();
        var chain = plan.Chain(6, days: 2);
        var test = plan.Check("critical-path-test");
        Assert.Equal(HealthCheckStatus.Pass, test.Status);
        Assert.Equal(chain[0].Id, Assert.Single(test.Tasks).ItemId);

        // The last task is pinned with Must Start On: delaying the chain doesn't move it, so the path is broken.
        plan.Replace(chain[^1], t => t with { Constraint = new PlanConstraintDto(PlanConstraintKind.MustStartOn, t.Start) });
        plan.Add("After the pin", Cal.AddWorkingDays(chain[^1].Finish, 1), 3);
        plan.Link(plan.Tasks[5], plan.Tasks[6]);
        test = plan.Check("critical-path-test");
        Assert.Equal(HealthCheckStatus.Fail, test.Status);
        Assert.Contains("moved the finish only", test.Result, StringComparison.Ordinal);
    }

    // US federal holidays the seed company calendar has, inside a 100-working-day window from late October 2026.
    private static readonly WorkCalendar Holidays = new([
        new(2026, 10, 12), new(2026, 11, 11), new(2026, 11, 26), new(2026, 12, 25), new(2027, 1, 1), new(2027, 1, 18), new(2027, 2, 15), new(2027, 5, 31)]);

    private static HealthTask Task(int number, string title, DateOnly start, DateOnly finish, bool milestone = false, PlanConstraintDto? constraint = null) =>
        new(Guid.NewGuid(), number, number.ToString(), title, start, finish, milestone, Constraint: constraint);

    private static HealthCheckDto CriticalPathTest(WorkCalendar calendar, IReadOnlyList<HealthTask> tasks, params (int From, int To)[] links) =>
        ScheduleHealth.Evaluate(new HealthPlan(tasks, links.Select(l => new ScheduleLink(tasks[l.From].Id, tasks[l.To].Id, DependencyKind.FinishToStart, 0)).ToList(),
            calendar, new DateOnly(2026, 10, 23))).Single(c => c.Key == "critical-path-test");

    [Fact]
    public void Critical_path_test_passes_when_the_plan_ends_on_a_weekend_with_a_milestone_and_the_delay_spans_holidays()
    {
        // Project 1001's plan: the chain's last task and an unlinked milestone both end on Sunday Nov 8. The milestone's date
        // snaps to Monday Nov 9 while the task's last working day is Friday Nov 6, though both end the network at the same
        // point. Measuring between those dates made a 100-day delay look like 99.
        var tasks = new[]
        {
            Task(2, "Kickoff meeting", new(2026, 10, 19), new(2026, 10, 19)),
            Task(3, "Requirements review", new(2026, 10, 20), new(2026, 10, 29)),
            Task(4, "Baseline schedule", new(2026, 10, 30), new(2026, 11, 8)),
            Task(5, "Planning complete (baseline approved)", new(2026, 11, 8), new(2026, 11, 8), milestone: true),
        };

        var test = CriticalPathTest(Holidays, tasks, (0, 1), (1, 2));

        Assert.Equal(HealthCheckStatus.Pass, test.Status);
        Assert.Equal("Delayed 100 working days: the finish moved 100 working days", test.Tasks[0].Detail);
    }

    [Fact]
    public void Critical_path_test_passes_on_a_chain_whose_delay_spans_holidays_and_ends_on_a_weekend()
    {
        var tasks = new[]
        {
            Task(1, "Design", new(2026, 11, 9), new(2026, 11, 20)),     // spans Veterans Day
            Task(2, "Build", new(2026, 11, 23), new(2026, 12, 5)),      // spans Thanksgiving, ends on a Saturday
            Task(3, "Test", new(2026, 12, 7), new(2026, 12, 27)),       // spans Christmas, ends on a Sunday
        };

        var test = CriticalPathTest(Holidays, tasks, (0, 1), (1, 2));

        Assert.Equal(HealthCheckStatus.Pass, test.Status);
        Assert.Equal(tasks[0].Id, test.Tasks[0].ItemId);
    }

    [Fact]
    public void Critical_path_test_keeps_the_delayed_tasks_own_start_constraint()
    {
        // The first task can't start until Wednesday (Must Start On), two days after its planned start. Lifting that
        // constraint used to pull it back two days, so the delay looked two days short.
        var tasks = new[]
        {
            Task(1, "Site access", new(2026, 11, 2), new(2026, 11, 6), constraint: new(PlanConstraintKind.MustStartOn, new(2026, 11, 4))),
            Task(2, "Install", new(2026, 11, 9), new(2026, 11, 13)),
        };

        var test = CriticalPathTest(Holidays, tasks, (0, 1));

        Assert.Equal(HealthCheckStatus.Pass, test.Status);
    }

    [Theory]
    [InlineData(PlanConstraintKind.MustStartOn)]
    [InlineData(PlanConstraintKind.MustFinishOn)]
    public void Critical_path_test_still_fails_when_a_pinned_successor_absorbs_the_slip(PlanConstraintKind pin)
    {
        var last = new DateOnly(2026, 12, 7);
        var tasks = new[]
        {
            Task(1, "Design", new(2026, 11, 9), new(2026, 11, 20)),
            Task(2, "Build", new(2026, 11, 23), new(2026, 12, 4)),
            Task(3, "Ship", last, last, constraint: new(pin, last)),
        };

        var test = CriticalPathTest(Holidays, tasks, (0, 1), (1, 2));

        Assert.Equal(HealthCheckStatus.Fail, test.Status);
        Assert.Contains("moved the finish only", test.Result, StringComparison.Ordinal);
    }

    [Fact]
    public void Critical_path_test_can_delay_a_milestone()
    {
        var plan = new Plan { Status = Mon.AddDays(14) };
        var chain = plan.Chain(2, days: 3);
        foreach (var task in chain)
        {
            plan.Replace(task, t => t with { Percent = 100 });
        }

        var end = plan.Add("Delivered", Cal.AddWorkingDays(chain[^1].Finish, 1), 0);
        plan.Link(plan.Tasks[1], end);

        var test = plan.Check("critical-path-test");
        Assert.Equal(end.Id, test.Tasks[0].ItemId);
        Assert.Equal(HealthCheckStatus.Pass, test.Status);
    }

    [Fact]
    public void Critical_path_test_tests_the_driving_path_not_a_branch_with_a_deadline()
    {
        var plan = new Plan();
        var chain = plan.Chain(5, days: 4);
        var branch = plan.Add("Branch", Mon, 2, constraint: new PlanConstraintDto(PlanConstraintKind.FinishNoLaterThan, Mon));
        plan.Link(branch, chain[^1]);

        var test = plan.Check("critical-path-test");
        Assert.Equal(HealthCheckStatus.Pass, test.Status);
        Assert.NotEqual(branch.Id, test.Tasks[0].ItemId);
    }

    [Fact]
    public void Cpli_is_the_critical_path_length_plus_float_over_the_length()
    {
        // 20 working days of chain from the status date; on time is 1.00.
        var plan = new Plan { Status = Mon };
        var chain = plan.Chain(10, days: 2);
        var cpli = plan.Check("cpli");
        Assert.Equal(HealthCheckStatus.Pass, cpli.Status);
        Assert.Equal(1.00m, cpli.Value);
        Assert.Equal(20, ScheduleHealth.CriticalPathLength(Cal, Mon, chain[0].Start, chain[^1].Finish));

        // A deadline one day early: (20 − 1) / 20 = 0.95, the threshold.
        plan.Replace(chain[^1], t => t with { Constraint = new PlanConstraintDto(PlanConstraintKind.FinishNoLaterThan, Cal.AddWorkingDays(t.Finish, -1)) });
        cpli = plan.Check("cpli");
        Assert.Equal(0.95m, cpli.Value);
        Assert.Equal(HealthCheckStatus.Pass, cpli.Status);

        // Two days early: 18 / 20 = 0.90.
        plan.Replace(plan.Tasks[^1], t => t with { Constraint = new PlanConstraintDto(PlanConstraintKind.FinishNoLaterThan, Cal.AddWorkingDays(chain[^1].Finish, -2)) });
        cpli = plan.Check("cpli");
        Assert.Equal(0.90m, cpli.Value);
        Assert.Equal(HealthCheckStatus.Fail, cpli.Status);
        Assert.NotEmpty(cpli.Tasks);

        // Five days into the plan the remaining length is 15: (15 − 2) / 15 = 0.87.
        plan.Status = Cal.AddWorkingDays(Mon, 5);
        for (var i = 0; i < 2; i++)
        {
            plan.Replace(plan.Tasks[i], t => t with { Percent = 100 });
        }

        plan.Replace(plan.Tasks[2], t => t with { Percent = 50 });
        Assert.Equal(0.87m, plan.Check("cpli").Value);
    }

    [Fact]
    public void Index_rounds_to_two_places()
    {
        Assert.Equal(0.67m, ScheduleHealth.Index(2, 3));
        Assert.Equal(1.00m, ScheduleHealth.Index(5, 5));
        Assert.Equal(0m, ScheduleHealth.Index(1, 0));
    }

    [Fact]
    public void Free_float_is_the_slip_before_a_successor_moves()
    {
        //  A (2d) ──► C (1d)          A has no free float: C starts right after it.
        //  B (1d) ──┘                  B finishes a day before C needs it: 1 day of free float, 1 of total.
        var a = new ScheduleTask(Guid.NewGuid(), Mon, Mon.AddDays(1));
        var b = new ScheduleTask(Guid.NewGuid(), Mon, Mon);
        var c = new ScheduleTask(Guid.NewGuid(), Mon.AddDays(2), Mon.AddDays(2));
        var d = new ScheduleTask(Guid.NewGuid(), Mon, Mon); // unlinked: free float to the plan finish
        var analysis = CriticalPathCalculator.Analyze([a, b, c, d],
            [new(a.Id, c.Id, DependencyKind.FinishToStart, 0), new(b.Id, c.Id, DependencyKind.FinishToStart, 0)]);
        var slack = analysis.Items.ToDictionary(i => i.ItemId);

        Assert.Equal(0, slack[a.Id].FreeFloatDays);
        Assert.Equal(1, slack[b.Id].FreeFloatDays);
        Assert.Equal(1, slack[b.Id].SlackDays);
        Assert.Equal(0, slack[c.Id].FreeFloatDays);
        Assert.Equal(2, slack[d.Id].FreeFloatDays);
    }
}
