using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Application.Hr;
using Workbench.Contracts;
using Workbench.Contracts.Hr;
using Workbench.Contracts.Identity;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.Payroll;
using Workbench.Ui.Navigation;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Hr;

/// <summary>HR reports (who may run which), the home page to-dos and the People menu.</summary>
public sealed class HrReportTests
{
    [Fact]
    public async Task Eeo1_and_vets4212_count_by_job_category_and_are_hr_only()
    {
        await using var kit = await HrKit.CreateAsync();
        kit.As(HrKit.Hr);
        var position = await kit.Positions.CreateAsync(new SavePositionRequest("SE", "Engineer", null, null, null, EeoJobCategory.Professionals, true, EmploymentTypeKind.FullTime,
            null, null, 3, PositionStatus.Approved));
        foreach (var id in new[] { kit.Fx.AnalystId, kit.Fx.ViewerId })
        {
            await kit.Personnel.SaveJobAsync(id, new SaveJobInfoRequest("Engineer", position.Id, null, kit.Fx.PmId, EmploymentTypeKind.FullTime, EmploymentStatus.Active, true, null, null));
        }

        await kit.Personnel.SaveEeoAsync(kit.Fx.AnalystId, new SaveEeoRequest(EeoGender.Male, EeoRace.HispanicOrLatino, DisabilityStatus.No, false, false, false, false, false, null));
        await kit.Personnel.SaveEeoAsync(kit.Fx.ViewerId, new SaveEeoRequest(EeoGender.Female, EeoRace.White, DisabilityStatus.No, true, true, false, false, false, null));
        await kit.Personnel.SaveEeoAsync(kit.Fx.PmId, new SaveEeoRequest(EeoGender.Female, EeoRace.White, DisabilityStatus.No, false, false, false, false, false, null,
            EeoJobCategory.FirstMidOfficials));

        var eeo1 = await kit.Reports.GetEeo1Async(HrKit.Today);
        Assert.Equal(4, eeo1.Employees);
        Assert.Equal(1, eeo1.MissingJobCategory); // outsider@
        Assert.Equal(1, eeo1.NotDisclosed);
        Assert.Contains(eeo1.Cells, c => c is { JobCategory: EeoJobCategory.Professionals, Race: EeoRace.HispanicOrLatino, Gender: EeoGender.Male, Count: 1 });
        Assert.Contains(eeo1.Cells, c => c is { JobCategory: EeoJobCategory.FirstMidOfficials, Race: EeoRace.White, Gender: EeoGender.Female, Count: 1 });

        var vets = await kit.Reports.GetVets4212Async(HrKit.Today);
        var professionals = vets.Rows.Single(r => r.JobCategory == EeoJobCategory.Professionals);
        Assert.Equal((1, 2), (professionals.ProtectedVeteranEmployees, professionals.TotalEmployees));
        Assert.Equal(4, vets.MaxEmployees);

        var before = await kit.SecurityEventsAsync(HrSecurityEvents.Eeo1Exported);
        var csv = await kit.Reports.ExportEeo1Async(HrKit.Today);
        Assert.Contains("Professionals,Hispanic or Latino,Male,1", csv.Content);
        Assert.Equal(before + 1, await kit.SecurityEventsAsync(HrSecurityEvents.Eeo1Exported));
        Assert.Contains("Maximum employees", (await kit.Reports.ExportVets4212Async(HrKit.Today)).Content);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Finance).Reports.GetEeo1Async(HrKit.Today));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Finance).Reports.ExportVets4212Async(HrKit.Today));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Reports.GetVets4212Async(HrKit.Today));
        Assert.Equal(4, (await kit.As(Admin).Reports.GetEeo1Async(HrKit.Today)).Employees);
    }

    [Fact]
    public async Task Headcount_turnover_and_the_benefits_summary_are_for_hr_and_finance()
    {
        await using var kit = await HrKit.CreateAsync();
        var plans = await kit.PlansAsync(await kit.AccountAsync("6030"));
        await kit.OpenWindowAsync();
        await kit.As(Editor).Benefits.ElectAsync(kit.Fx.AnalystId, new ElectBenefitRequest(plans.Ppo.Id, CoverageLevel.EmployeeOnly, [], null, ElectionSource.OpenEnrollment));
        await kit.As(HrKit.Hr).Onboarding.StartOffboardingAsync(new StartOffboardingRequest(kit.Fx.OutsiderId, HrKit.Today.AddDays(-1), TerminationReason.Involuntary, null, null));

        var headcount = await kit.As(Finance).Reports.GetHeadcountAsync(HrKit.Today);
        Assert.Equal(3, headcount.Total);
        var turnover = await kit.Reports.GetTurnoverAsync(HrKit.Today.AddMonths(-1), HrKit.Today);
        Assert.Equal((1, 1), (turnover.Separations, turnover.Involuntary));
        Assert.Equal((4, 3), (turnover.StartHeadcount, turnover.EndHeadcount));
        Assert.Equal(Math.Round(1 / 3.5m, 4), turnover.TurnoverRate);

        var next = new DateOnly(HrKit.Today.Year + 1, 1, 15);
        var summary = await kit.Reports.GetBenefitSummaryAsync(next);
        var ppo = Assert.Single(summary.Rows);
        Assert.Equal(("MED-PPO", 1, 100m, 500m), (ppo.PlanCode, ppo.Enrolled, ppo.EmployeeMonthly, ppo.EmployerMonthly));
        Assert.StartsWith("6030", ppo.ExpenseAccount);
        Assert.Contains("MED-PPO", (await kit.Reports.ExportBenefitSummaryAsync(next)).Content);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Reports.GetHeadcountAsync(HrKit.Today));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Editor).Reports.GetBenefitSummaryAsync(HrKit.Today));
    }

    [Fact]
    public async Task The_home_page_lists_expiring_clearances_open_enrollment_and_life_events_to_verify()
    {
        await using var kit = await HrKit.CreateAsync();
        await kit.PlansAsync();
        await kit.OpenWindowAsync();
        await kit.Personnel.SaveCredentialAsync(kit.Fx.AnalystId, null, new SaveCredentialRequest(CredentialKind.SecurityClearance, "Secret", null, null, ClearanceLevel.Secret,
            null, HrKit.Today.AddDays(30), null));
        await kit.As(Viewer).Benefits.ReportLifeEventAsync(kit.Fx.ViewerId, new ReportLifeEventRequest(LifeEventKind.Marriage, HrKit.Today.AddDays(-2), null));

        var alex = await kit.As(Editor).Get<IMyTodoService>().GetAsync();
        Assert.Contains(alex.Items, i => i.Title == $"Your Secret clearance expires {HrKit.Today.AddDays(30):MMM d}");
        Assert.Contains(alex.Items, i => i.Key.StartsWith("hr-oe:") && i.Link == "hr/benefits/me");
        Assert.DoesNotContain(alex.Unavailable, u => u.Contains("Benefits") || u.Contains("Clearances"));

        var pat = await kit.As(Owner).Get<IMyTodoService>().GetAsync(); // Alex's manager
        Assert.Contains(pat.Items, i => i.Title.StartsWith("Alex Rivera's Secret clearance"));

        var hr = await kit.As(HrKit.Hr).Get<IMyTodoService>().GetAsync();
        Assert.Contains(hr.Items, i => i.Key == "hr-expiring");
        Assert.Contains(hr.Items, i => i.Key == "hr-le-verify");

        // Once Alex has chosen or waived medical, dental and vision (no vision plan here), the open enrollment item goes.
        var plans = await kit.Benefits.ListPlansAsync();
        kit.As(Editor);
        await kit.Benefits.ElectAsync(kit.Fx.AnalystId, new ElectBenefitRequest(plans.Single(p => p.Code == "MED-PPO").Id, null, null, null, ElectionSource.OpenEnrollment, Waive: true));
        await kit.Benefits.ElectAsync(kit.Fx.AnalystId, new ElectBenefitRequest(plans.Single(p => p.Code == "DEN").Id, CoverageLevel.EmployeeOnly, [], null, ElectionSource.OpenEnrollment));
        Assert.DoesNotContain((await kit.Get<IMyTodoService>().GetAsync()).Items, i => i.Key.StartsWith("hr-oe:"));
    }

    [Fact]
    public void The_people_menu_follows_the_role()
    {
        static List<string> Links(OrgRoles roles) =>
            NavCatalog.MenuEntries(new NavAccess(new CurrentUserInfo("x@example.com", "X", roles, Guid.NewGuid()))).Where(e => e.Section == NavCatalog.People || e.Href == "hr/reports")
                .Select(e => e.Href).ToList();

        Assert.Equal(["hr/me", "hr/benefits/me", "hr", "hr/org-chart"], Links(OrgRoles.Employee));
        Assert.Equal(["hr/me", "hr/benefits/me", "hr", "hr/org-chart", "hr/positions", "hr/onboarding", "hr/requests", "hr/compensation", "hr/benefits", "hr/reports"], Links(OrgRoles.Hr));
        Assert.Equal(Links(OrgRoles.Hr), Links(OrgRoles.Admin));
        Assert.Equal(["hr/me", "hr/benefits/me", "hr", "hr/org-chart", "hr/positions", "hr/compensation", "hr/benefits", "hr/reports"], Links(OrgRoles.Finance));
        Assert.Equal("Employee", NavCatalog.Match("hr/employees/123", new NavAccess(new CurrentUserInfo("x@example.com", "X", OrgRoles.Employee, null)))!.Value.Entry.Detail);
    }

    [Fact]
    public async Task The_people_directory_renders_for_an_employee()
    {
        await using var kit = await HrKit.CreateAsync();
        kit.As(Editor);
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(kit.Personnel);
        ctx.Services.AddSingleton(kit.Get<IHrAccessService>());
        var page = ctx.Render<Workbench.Ui.Hr.PeoplePage>();
        page.WaitForAssertion(() => Assert.Contains("Alex Rivera", page.Markup));
        Assert.DoesNotContain("Include former employees", page.Markup);
        Accessibility.A11yAssert.Check(page);
    }
}
