using Workbench.Contracts;
using Workbench.Contracts.Crm;

namespace Workbench.Tests.Crm;

/// <summary>Capture plans, the color-team schedule, the Pwin assessment and the BD highlights (clock: Wed 2026-10-21).</summary>
public sealed class CapturePlanTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0, 0, 5)]
    [InlineData(5, 5, 5, 5, 5, 5, 100, 90)]
    [InlineData(3, 3, 3, 3, 3, 3, 60, 60)]
    [InlineData(5, 4, 2, 3, 1, 0, 60, 60)] // 25 + 16 + 8 + 9 + 2 + 0
    public void The_weighted_score_suggests_a_pwin_between_5_and_90(int customer, int solution, int past, int price, int competition, int teaming, int score, int pwin)
    {
        var factors = new PwinFactors(customer, solution, past, price, competition, teaming);

        Assert.Equal(score, factors.Score);
        Assert.Equal(pwin, factors.SuggestedPwin);
    }

    [Fact]
    public async Task The_standard_schedule_counts_back_from_the_due_date_on_weekdays_and_only_once()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.NewAsync();
        await SetDueAsync(kit, o, new DateOnly(2026, 12, 15));
        var plans = kit.Get<ICapturePlanService>();

        var schedule = await plans.AddStandardScheduleAsync(o.Id);

        Assert.Equal(
            [
                (MilestoneKind.BlackHat, new DateOnly(2026, 10, 30)), // Sat 10-31 → Fri
                (MilestoneKind.BlueTeam, new DateOnly(2026, 11, 10)),
                (MilestoneKind.Kickoff, new DateOnly(2026, 11, 17)),
                (MilestoneKind.PinkTeam, new DateOnly(2026, 11, 24)),
                (MilestoneKind.RedTeam, new DateOnly(2026, 12, 4)), // Sat 12-05 → Fri
                (MilestoneKind.GoldTeam, new DateOnly(2026, 12, 11)),
                (MilestoneKind.WhiteGlove, new DateOnly(2026, 12, 14)), // Sun 12-13 → Mon
                (MilestoneKind.Submission, new DateOnly(2026, 12, 15)),
            ],
            schedule.Select(m => (m.Kind, m.DueOn)).ToList());
        Assert.All(schedule, m => Assert.Equal(CrmKit.Bd, m.OwnerEmail));
        Assert.Equal(8, (await plans.AddStandardScheduleAsync(o.Id)).Count);
    }

    [Fact]
    public async Task Reviews_whose_dates_have_passed_are_left_out()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.NewAsync();
        await SetDueAsync(kit, o, new DateOnly(2026, 11, 20));

        var schedule = await kit.Get<ICapturePlanService>().AddStandardScheduleAsync(o.Id);

        Assert.DoesNotContain(schedule, m => m.Kind is MilestoneKind.BlackHat or MilestoneKind.BlueTeam);
        Assert.Contains(schedule, m => m.Kind == MilestoneKind.Submission && m.DueOn == new DateOnly(2026, 11, 20));
    }

    [Fact]
    public async Task The_schedule_needs_a_due_date()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.NewAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => kit.Get<ICapturePlanService>().AddStandardScheduleAsync(o.Id));
        Assert.Contains("proposal due date", ex.Message);
    }

    [Fact]
    public async Task Applying_an_assessment_sets_the_opportunity_pwin()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.NewAsync(pwin: 10);
        var plans = kit.Get<ICapturePlanService>();

        var saved = await plans.SaveAssessmentAsync(o.Id, new SavePwinAssessmentRequest(new PwinFactors(4, 4, 3, 3, 2, 2), "Strong with the PM"));

        Assert.Equal(saved.SuggestedPwin, (await kit.Opportunities.GetAsync(o.Id)).Pwin);
        Assert.Equal("Strong with the PM", (await plans.GetAsync(o.Id)).Assessment!.Notes);

        await plans.SaveAssessmentAsync(o.Id, new SavePwinAssessmentRequest(new PwinFactors(1, 1, 1, 1, 1, 1), ApplyToOpportunity: false));
        Assert.Equal(saved.SuggestedPwin, (await kit.Opportunities.GetAsync(o.Id)).Pwin);
        await Assert.ThrowsAsync<ArgumentException>(() => plans.SaveAssessmentAsync(o.Id, new SavePwinAssessmentRequest(new PwinFactors(6, 0, 0, 0, 0, 0))));
    }

    [Fact]
    public async Task Managers_edit_the_plan_and_the_rest_of_bd_reads_it()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.NewAsync();
        var plans = kit.Get<ICapturePlanService>();
        var theme = await plans.AddItemAsync(o.Id, new SavePlanItemRequest(CapturePlanItemKind.WinTheme, "Zero-downtime cutover, proven at PEO EIS"));
        await plans.AddItemAsync(o.Id, new SavePlanItemRequest(CapturePlanItemKind.Threat, "Incumbent bundles licenses"));

        kit.As(CrmKit.Bd2);
        var plan = await plans.GetAsync(o.Id);
        Assert.False(plan.CanEdit);
        Assert.Equal(["Zero-downtime cutover, proven at PEO EIS", "Incumbent bundles licenses"], plan.Items.Select(i => i.Text));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => plans.AddItemAsync(o.Id, new SavePlanItemRequest(CapturePlanItemKind.Strength, "x")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => plans.DeleteItemAsync(theme.Id));

        kit.As(CrmKit.Bd);
        await plans.DeleteItemAsync(theme.Id);
        Assert.Single((await plans.GetAsync(o.Id)).Items);
    }

    [Fact]
    public async Task Agency_history_counts_wins_and_losses_across_the_department()
    {
        await using var kit = await CrmKit.CreateAsync();
        var won = await kit.NewAsync("Won one");
        var lost = await kit.NewAsync("Lost one");
        var current = await kit.NewAsync("Current");
        await kit.AdvanceAsync(won.Id, OpportunityStage.Submitted);
        await kit.AdvanceAsync(lost.Id, OpportunityStage.Submitted);
        await kit.Opportunities.RecordOutcomeAsync(won.Id, new RecordOutcomeRequest(OpportunityStage.Won, OutcomeReason.Technical));
        await kit.Opportunities.RecordOutcomeAsync(lost.Id, new RecordOutcomeRequest(OpportunityStage.Lost, OutcomeReason.Price));

        var history = (await kit.Get<ICapturePlanService>().GetAsync(current.Id)).AgencyHistory!;

        Assert.Equal(("Army", 1, 1, 50m), (history.Scope, history.Won, history.Lost, history.WinRate!.Value));
    }

    [Fact]
    public async Task Highlights_flag_missed_dates_and_list_whats_coming_up()
    {
        await using var kit = await CrmKit.CreateAsync();
        var late = await kit.NewAsync("Late proposal", value: 2_000_000m);
        await SetDueAsync(kit, late, new DateOnly(2026, 10, 14));
        var soon = await kit.NewAsync("Due soon", value: 3_000_000m);
        await SetDueAsync(kit, soon, new DateOnly(2026, 11, 4));
        await kit.Get<ICapturePlanService>().AddMilestoneAsync(soon.Id, new SaveMilestoneRequest(MilestoneKind.PinkTeam, "Pink team", new DateOnly(2026, 10, 28)));

        var h = await kit.Pipeline.GetHighlightsAsync();

        Assert.Equal(2, h.OpenCount);
        Assert.Equal(5_000_000m, h.OpenValue);
        Assert.Contains(h.Attention, a => a.OpportunityId == late.Id && a.Severity == AttentionSeverity.Critical && a.Reason.Contains("was due Oct 14"));
        Assert.Equal(["Pink team", "Proposal due"], h.Upcoming.Where(u => u.OpportunityId == soon.Id).Select(u => u.What));
        Assert.DoesNotContain(h.Upcoming, u => u.OpportunityId == late.Id);
        Assert.Equal(2, h.Funnel.Single(f => f.Stage == OpportunityStage.Identified).Count);
    }

    private static async Task SetDueAsync(CrmKit kit, OpportunityDto o, DateOnly due)
    {
        kit.As(CrmKit.Bd);
        await kit.Opportunities.UpdateAsync(o.Id, new SaveOpportunityRequest(o.Name, AgencyAccountId: o.AgencyAccountId, EstimatedValue: o.EstimatedValue,
            PeriodOfPerformanceMonths: o.PeriodOfPerformanceMonths, ProposalDueOn: due, CaptureManagerEmail: o.CaptureManagerEmail, Pwin: o.Pwin, Pgo: o.Pgo));
    }
}
