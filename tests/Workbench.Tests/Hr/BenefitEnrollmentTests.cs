using Microsoft.EntityFrameworkCore;
using Workbench.Application.Hr;
using Workbench.Contracts;
using Workbench.Contracts.Hr;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;
using D = Workbench.Domain.Hr;

namespace Workbench.Tests.Hr;

/// <summary>The enrollment rules: when elections may change, coverage against dependents, HSA/FSA, limits and costs.</summary>
public sealed class BenefitEnrollmentTests
{
    private static D.BenefitDependent Dependent(D.DependentRelationship relationship, DateOnly born) =>
        new() { FirstName = relationship.ToString(), LastName = "Doe", Relationship = relationship, BirthDate = born };

    [Fact]
    public void Coverage_levels_must_match_the_dependents_covered()
    {
        var start = new DateOnly(2027, 1, 1);
        var spouse = Dependent(D.DependentRelationship.Spouse, new DateOnly(1990, 1, 1));
        var partner = Dependent(D.DependentRelationship.DomesticPartner, new DateOnly(1990, 1, 1));
        var child = Dependent(D.DependentRelationship.Child, new DateOnly(2015, 1, 1));
        var adult = Dependent(D.DependentRelationship.Child, new DateOnly(2001, 1, 1)); // 26 on 2027-01-01

        BenefitRules.CheckCoverage(D.CoverageLevel.EmployeeOnly, [], start);
        BenefitRules.CheckCoverage(D.CoverageLevel.EmployeeSpouse, [partner], start);
        BenefitRules.CheckCoverage(D.CoverageLevel.EmployeeChildren, [child], start);
        BenefitRules.CheckCoverage(D.CoverageLevel.Family, [spouse, child], start);
        Assert.Throws<ArgumentException>(() => BenefitRules.CheckCoverage(D.CoverageLevel.EmployeeOnly, [child], start));
        Assert.Throws<ArgumentException>(() => BenefitRules.CheckCoverage(D.CoverageLevel.EmployeeSpouse, [spouse, child], start));
        Assert.Throws<ArgumentException>(() => BenefitRules.CheckCoverage(D.CoverageLevel.EmployeeChildren, [spouse, child], start));
        Assert.Throws<ArgumentException>(() => BenefitRules.CheckCoverage(D.CoverageLevel.Family, [child], start));
        Assert.Throws<ArgumentException>(() => BenefitRules.CheckCoverage(D.CoverageLevel.Family, [spouse, partner, child], start));
        Assert.Contains("26", Assert.Throws<ArgumentException>(() => BenefitRules.CheckCoverage(D.CoverageLevel.EmployeeChildren, [adult], start)).Message);
        BenefitRules.CheckCoverage(D.CoverageLevel.EmployeeChildren, [adult], start.AddDays(-1));
    }

    [Fact]
    public void Costs_are_monthly_and_deducted_per_pay_period()
    {
        Assert.Equal(46.15m, BenefitRules.PerPeriod(100m, Domain.Payroll.PayFrequency.Biweekly)); // 100 × 12 ÷ 26
        Assert.Equal(50m, BenefitRules.PerPeriod(100m, Domain.Payroll.PayFrequency.SemiMonthly));
        Assert.Equal(new DateOnly(2026, 11, 1), BenefitRules.NewHireCoverageStart(new DateOnly(2026, 10, 19)));
        Assert.Equal(new DateOnly(2026, 11, 1), BenefitRules.NewHireCoverageStart(new DateOnly(2026, 11, 1)));
        Assert.Equal(new DateOnly(2026, 10, 7), BenefitRules.LifeEventCoverageStart(D.LifeEventKind.BirthOrAdoption, new DateOnly(2026, 10, 7)));
        Assert.Equal(new DateOnly(2026, 11, 1), BenefitRules.LifeEventCoverageStart(D.LifeEventKind.Marriage, new DateOnly(2026, 10, 7)));

        var k401 = new D.BenefitPlan { Code = "401K", Name = "401K", DeductionCode = "401K", Type = D.BenefitPlanType.Retirement401k, EmployerMatchRate = 1m, EmployerMatchCapPercent = 0.04m };
        var six = new D.BenefitElection { ContributionAmount = 6m, ElectedBy = "x" };
        Assert.Equal((600m, 400m), BenefitRules.MonthlyCost(k401, null, six, 120_000m)); // 6% of 10,000/month; match capped at 4%
        var hsa = new D.BenefitPlan { Code = "HSA", Name = "HSA", DeductionCode = "HSA", Type = D.BenefitPlanType.Hsa, EmployerMonthlyContribution = 50m };
        Assert.Equal((250m, 50m), BenefitRules.MonthlyCost(hsa, null, new D.BenefitElection { ContributionAmount = 3_000m, ElectedBy = "x" }, null));
    }

    [Fact]
    public async Task Elections_change_only_in_a_window_and_replace_the_same_plan_type()
    {
        await using var kit = await HrKit.CreateAsync();
        var plans = await kit.PlansAsync();
        var me = kit.Fx.AnalystId;

        // No open enrollment, long past the hire date, no life event: refused. 401(k) changes any time.
        kit.As(Editor);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Benefits.ElectAsync(me, new ElectBenefitRequest(plans.Ppo.Id, CoverageLevel.EmployeeOnly, [], null, ElectionSource.OpenEnrollment)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Benefits.ElectAsync(me, new ElectBenefitRequest(plans.Ppo.Id, CoverageLevel.EmployeeOnly, [], null, ElectionSource.NewHire)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Benefits.ElectAsync(me, new ElectBenefitRequest(plans.Ppo.Id, CoverageLevel.EmployeeOnly, [], null, ElectionSource.AnyTime)));
        var k401 = await kit.Benefits.ElectAsync(me, new ElectBenefitRequest(plans.K401.Id, null, null, 6m, ElectionSource.AnyTime));
        Assert.Equal(BenefitRules.FirstOfNextMonth(HrKit.Today), k401.CoverageStart);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Benefits.ElectAsync(me, new ElectBenefitRequest(plans.K401.Id, null, null, 80m, ElectionSource.AnyTime))); // over 75%
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Benefits.ElectAsync(me, new ElectBenefitRequest(plans.K401.Id, null, null, 5m, ElectionSource.Administrative)));

        // Open enrollment: coverage from next January 1.
        var window = await kit.OpenWindowAsync();
        kit.As(Editor);
        var spouse = await kit.Benefits.SaveDependentAsync(me, null, new SaveDependentRequest("Jordan", "Rivera", DependentRelationship.Spouse, new DateOnly(1990, 4, 2)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Benefits.ElectAsync(me, new ElectBenefitRequest(plans.Ppo.Id, CoverageLevel.Family, [spouse.Id], null, ElectionSource.OpenEnrollment)));
        var ppo = await kit.Benefits.ElectAsync(me, new ElectBenefitRequest(plans.Ppo.Id, CoverageLevel.EmployeeSpouse, [spouse.Id], null, ElectionSource.OpenEnrollment));
        Assert.Equal(window.CoverageStartsOn, ppo.CoverageStart);
        Assert.Equal((200m, 1_000m), (ppo.EmployeeMonthlyCost, ppo.EmployerMonthlyCost));

        // Changing my mind in the same window replaces it; choosing the HDHP replaces the PPO (one medical plan).
        var hdhp = await kit.Benefits.ElectAsync(me, new ElectBenefitRequest(plans.Hdhp.Id, CoverageLevel.EmployeeOnly, [], null, ElectionSource.OpenEnrollment));
        var mine = await kit.Benefits.GetMineAsync();
        Assert.Equal([hdhp.Id], mine.Elections.Where(e => e.PlanType == BenefitPlanType.Medical).Select(e => e.Id));
        Assert.Contains(BenefitPlanType.Medical, mine.ElectedThisWindow);
        Assert.Contains(mine.Opportunities, o => o.Source == ElectionSource.OpenEnrollment && o.WindowId == window.Id);

        // Others can't see or change my benefits; HR can.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Benefits.GetForEmployeeAsync(me));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Viewer).Benefits.ElectAsync(me, new ElectBenefitRequest(plans.Dental.Id, CoverageLevel.EmployeeOnly, [], null, ElectionSource.OpenEnrollment)));
        Assert.Equal(2, (await kit.As(HrKit.Hr).Benefits.GetForEmployeeAsync(me)).Elections.Count);
    }

    [Fact]
    public async Task An_hsa_needs_a_high_deductible_plan_and_excludes_an_fsa()
    {
        await using var kit = await HrKit.CreateAsync();
        var plans = await kit.PlansAsync();
        await kit.OpenWindowAsync();
        var me = kit.Fx.AnalystId;
        kit.As(Editor);
        ElectBenefitRequest Hsa(decimal amount) => new(plans.Hsa.Id, null, null, amount, ElectionSource.OpenEnrollment);

        Assert.Contains("high-deductible", (await Assert.ThrowsAsync<ArgumentException>(() => kit.Benefits.ElectAsync(me, Hsa(1_000m)))).Message);
        await kit.Benefits.ElectAsync(me, new ElectBenefitRequest(plans.Hdhp.Id, CoverageLevel.EmployeeOnly, [], null, ElectionSource.OpenEnrollment));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Benefits.ElectAsync(me, Hsa(5_000m))); // over the limit
        var hsa = await kit.Benefits.ElectAsync(me, Hsa(3_000m));
        Assert.Equal((250m, 50m), (hsa.EmployeeMonthlyCost, hsa.EmployerMonthlyCost));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Benefits.ElectAsync(me, new ElectBenefitRequest(plans.Fsa.Id, null, null, 1_000m, ElectionSource.OpenEnrollment)));

        // Waiving the HSA frees the FSA.
        await kit.Benefits.ElectAsync(me, new ElectBenefitRequest(plans.Hsa.Id, null, null, null, ElectionSource.OpenEnrollment, Waive: true));
        await kit.Benefits.ElectAsync(me, new ElectBenefitRequest(plans.Fsa.Id, null, null, 1_000m, ElectionSource.OpenEnrollment));
    }

    [Fact]
    public async Task A_verified_life_event_opens_thirty_days_and_new_hires_have_thirty_days()
    {
        await using var kit = await HrKit.CreateAsync();
        var plans = await kit.PlansAsync();
        var me = kit.Fx.AnalystId;
        kit.As(Editor);
        var child = await kit.Benefits.SaveDependentAsync(me, null, new SaveDependentRequest("Sam", "Rivera", DependentRelationship.Child, HrKit.Today.AddDays(-5)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Benefits.ReportLifeEventAsync(me, new ReportLifeEventRequest(LifeEventKind.BirthOrAdoption, HrKit.Today.AddDays(-45), null)));
        var lifeEvent = await kit.Benefits.ReportLifeEventAsync(me, new ReportLifeEventRequest(LifeEventKind.BirthOrAdoption, HrKit.Today.AddDays(-5), "Sam was born"));
        Assert.Equal(LifeEventStatus.Pending, lifeEvent.Status);
        var request = new ElectBenefitRequest(plans.Ppo.Id, CoverageLevel.EmployeeChildren, [child.Id], null, ElectionSource.LifeEvent, LifeEventId: lifeEvent.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Benefits.ElectAsync(me, request)); // not verified yet

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Benefits.DecideLifeEventAsync(lifeEvent.Id, new DecideLifeEventRequest(true, null)));
        Assert.Single(await kit.As(HrKit.Hr).Benefits.ListPendingLifeEventsAsync());
        await kit.Benefits.DecideLifeEventAsync(lifeEvent.Id, new DecideLifeEventRequest(true, null));
        var election = await kit.As(Editor).Benefits.ElectAsync(me, request);
        Assert.Equal(HrKit.Today.AddDays(-5), election.CoverageStart); // a birth is covered from the day

        // A new hire (hired a week ago) gets the 1st of next month; part-time employees can't take full-time-only plans.
        await kit.Payroll.UpdateEmployeeAsync(kit.Fx.ViewerId, e => e.HireDate = HrKit.Today.AddDays(-7));
        var newHire = await kit.As(Viewer).Benefits.ElectAsync(kit.Fx.ViewerId, new ElectBenefitRequest(plans.Dental.Id, CoverageLevel.EmployeeOnly, [], null, ElectionSource.NewHire));
        Assert.Equal(BenefitRules.NewHireCoverageStart(HrKit.Today.AddDays(-7)), newHire.CoverageStart);
        await kit.As(HrKit.Hr).Personnel.SaveJobAsync(kit.Fx.ViewerId, new SaveJobInfoRequest(null, null, null, kit.Fx.PmId, Contracts.Payroll.EmploymentTypeKind.PartTime,
            EmploymentStatus.Active, false, null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.As(Viewer).Benefits.ElectAsync(kit.Fx.ViewerId, new ElectBenefitRequest(plans.Life.Id, CoverageLevel.EmployeeOnly, [], null, ElectionSource.NewHire)));
    }

    [Fact]
    public async Task Plans_are_hr_setup_and_map_to_gl_accounts()
    {
        await using var kit = await HrKit.CreateAsync();
        var fringe = await kit.AccountAsync("6030");
        var plans = await kit.PlansAsync(fringe);
        Assert.True(plans.Ppo.ExpenseAccountIsFringe);
        Assert.Equal("6030", plans.Ppo.ExpenseAccountNumber);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Finance).Benefits.SavePlanAsync(null, new SaveBenefitPlanRequest("X", "X", BenefitPlanType.Vision,
            null, HrKit.Today, HrKit.Today, "X", true, null, false, true, null, null, null, null, null, true, [new SaveBenefitTier(CoverageLevel.EmployeeOnly, 1, 1)])));

        // Finance may move a plan's cost to another account, but only an active expense account.
        var other = await kit.AccountAsync("6020");
        Assert.Equal("6020", (await kit.As(Finance).Benefits.SetPlanAccountAsync(plans.Dental.Id, other)).ExpenseAccountNumber);
        var cash = await kit.AccountAsync("1010");
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Benefits.SetPlanAccountAsync(plans.Dental.Id, cash));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Viewer).Benefits.SetPlanAccountAsync(plans.Dental.Id, other));
        Assert.Contains((await kit.As(HrKit.Hr).Benefits.ListExpenseAccountsAsync()), a => a.Number == "6030" && a.IsFringe);

        // Plan validation: tiers for tiered plans only, a contribution plan has none.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Benefits.SavePlanAsync(null, new SaveBenefitPlanRequest("VIS", "Vision", BenefitPlanType.Vision, null,
            HrKit.Today, HrKit.Today.AddYears(1), "VIS", true, null, false, true, null, null, null, null, null, true, [])));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Benefits.SavePlanAsync(null, new SaveBenefitPlanRequest("FSA2", "FSA", BenefitPlanType.Fsa, null,
            HrKit.Today, HrKit.Today.AddYears(1), "FSA2", true, null, false, true, 100, null, null, null, null, true, [new SaveBenefitTier(CoverageLevel.EmployeeOnly, 1, 1)])));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Benefits.SaveWindowAsync(null, new SaveOpenEnrollmentWindowRequest("Late", HrKit.Today, HrKit.Today.AddDays(-1), HrKit.Today)));

        await using var db = await kit.Factory.CreateAsync();
        Assert.Equal(7, await db.Set<D.BenefitPlan>().CountAsync());
        Assert.True(await db.Set<Employee>().AnyAsync());
    }
}
