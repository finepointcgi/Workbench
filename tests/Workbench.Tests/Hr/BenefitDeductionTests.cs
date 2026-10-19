using Microsoft.EntityFrameworkCore;
using Workbench.Application.Hr;
using Workbench.Application.Payroll;
using Workbench.Contracts;
using Workbench.Domain.Hr;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Hr;

/// <summary>Benefit deductions per pay period through the payroll provider adapter, and the employer cost split to fringe accounts.</summary>
public sealed class BenefitDeductionTests
{
    [Fact]
    public void Paylocity_formats_flat_and_percent_deductions()
    {
        var csv = PaylocityFormats.WriteDeductions(new PayrollDeductionExport("W1234", "BW", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 23),
        [
            new("2002", "Alex Rivera", "MEDPPO", 46.15m, null, 230.77m, true),
            new("1001", "Pat Morgan", "401K", null, 6m, null, true),
        ]));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Company Code,Employee Id,Employee Name,Check Date,Det,Det Code,Amount,Rate,Calc Code,ER Amount,Pre Tax", lines[0]);
        Assert.Equal("W1234,1001,Pat Morgan,10/23/2026,D,401K,,6,%,,Y", lines[1]);
        Assert.Equal("W1234,2002,Alex Rivera,10/23/2026,D,MEDPPO,46.15,,$,230.77,Y", lines[2]);
        Assert.Throws<NotSupportedException>(() => new AdpPayrollProvider().ExportDeductions(new PayrollDeductionExport("C", "G", default, default, default, [])));
    }

    [Fact]
    public async Task Elections_in_force_on_the_period_end_are_exported_per_pay_period()
    {
        await using var kit = await HrKit.CreateAsync();
        var plans = await kit.PlansAsync();
        var period = kit.Payroll.Period;

        // Elections in force on the period end (written directly: the windows that made them are long closed).
        await using (var db = await kit.Factory.CreateAsync())
        {
            var start = new DateOnly(period.End.Year, 1, 1);
            var ppoTier = await db.Set<BenefitTier>().FirstAsync(t => t.PlanId == plans.Ppo.Id && t.Coverage == Domain.Hr.CoverageLevel.EmployeeOnly);
            Election(db, kit.Fx.AnalystId, plans.Ppo.Id, BenefitPlanType.Medical, start, ppoTier.Id);
            Election(db, kit.Fx.AnalystId, plans.K401.Id, BenefitPlanType.Retirement401k, start, amount: 6m);
            Election(db, kit.Fx.PmId, plans.Hsa.Id, BenefitPlanType.Hsa, start, amount: 2_600m);
            Election(db, kit.Fx.ViewerId, plans.Ppo.Id, BenefitPlanType.Medical, start, ppoTier.Id, waived: true);
            Election(db, kit.Fx.PmId, plans.Dental.Id, BenefitPlanType.Dental, period.End.AddDays(1)); // starts after the period
            Election(db, kit.Fx.OutsiderId, plans.K401.Id, BenefitPlanType.Retirement401k, start, amount: 3m); // not in the pay group
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Deductions.PreviewAsync(kit.Payroll.Group.Id, period.Id));
        var preview = await kit.As(Finance).Deductions.PreviewAsync(kit.Payroll.Group.Id, period.Id);
        Assert.Equal(["401K", "HSA", "MED-PPO"], preview.Lines.Select(l => l.PlanCode).OrderBy(c => c, StringComparer.Ordinal));
        var ppo = preview.Lines.Single(l => l.PlanCode == "MED-PPO");
        Assert.Equal((46.15m, 230.77m), (ppo.EmployeeAmount, ppo.EmployerAmount)); // 100 and 500 a month, biweekly
        Assert.Equal(6m, preview.Lines.Single(l => l.PlanCode == "401K").EmployeePercent);
        Assert.Equal((100m, 23.08m), (preview.Lines.Single(l => l.PlanCode == "HSA").EmployeeAmount, preview.Lines.Single(l => l.PlanCode == "HSA").EmployerAmount));
        Assert.Contains(await kit.Deductions.ListPeriodsAsync(), p => p.PayPeriodId == period.Id);

        var before = await kit.SecurityEventsAsync(HrSecurityEvents.DeductionsExported);
        var file = await kit.As(HrKit.Hr).Deductions.ExportAsync(kit.Payroll.Group.Id, period.Id);
        Assert.StartsWith("paylocity-deductions-bw-", file.FileName);
        Assert.Contains("W1234,2002,Alex Rivera,", file.Content);
        Assert.Contains(",D,MEDPPO,46.15,,$,230.77,Y", file.Content);
        Assert.Contains(",D,401K,,6,%,,Y", file.Content);
        Assert.Equal(before + 1, await kit.SecurityEventsAsync(HrSecurityEvents.DeductionsExported));
        Assert.NotNull((await kit.Deductions.PreviewAsync(kit.Payroll.Group.Id, period.Id)).LastExportedAtUtc);

        // Someone without a provider employee id is listed with the issue and left out of the file.
        await using (var db = await kit.Factory.CreateAsync())
        {
            (await db.Set<Domain.Payroll.EmployeePayProfile>().FirstAsync(p => p.EmployeeId == kit.Fx.AnalystId)).ProviderEmployeeId = null;
            await db.SaveChangesAsync();
        }

        var flagged = await kit.Deductions.PreviewAsync(kit.Payroll.Group.Id, period.Id);
        Assert.All(flagged.Lines.Where(l => l.EmployeeId == kit.Fx.AnalystId), l => Assert.NotNull(l.Issue));
        Assert.DoesNotContain("Alex Rivera", (await kit.Deductions.ExportAsync(kit.Payroll.Group.Id, period.Id)).Content);
    }

    [Fact]
    public async Task The_employer_benefit_cost_is_split_to_each_plans_gl_account()
    {
        await using var kit = await HrKit.CreateAsync();
        var fringe = await kit.AccountAsync("6030");
        var plans = await kit.PlansAsync(fringe);
        var taxes = await kit.AccountAsync("6020");
        await kit.As(Finance).Benefits.SetPlanAccountAsync(plans.Dental.Id, taxes);
        var on = new DateOnly(HrKit.Today.Year, 6, 30);
        await using var db = await kit.Factory.CreateAsync();
        var start = new DateOnly(on.Year, 1, 1);
        var ppo = await db.Set<BenefitTier>().FirstAsync(t => t.PlanId == plans.Ppo.Id && t.Coverage == Domain.Hr.CoverageLevel.EmployeeOnly);
        var dental = await db.Set<BenefitTier>().FirstAsync(t => t.PlanId == plans.Dental.Id && t.Coverage == Domain.Hr.CoverageLevel.EmployeeOnly);
        Election(db, kit.Fx.AnalystId, plans.Ppo.Id, BenefitPlanType.Medical, start, ppo.Id); // employer 500/month on 6030
        Election(db, kit.Fx.AnalystId, plans.Dental.Id, BenefitPlanType.Dental, start, dental.Id); // employer 20/month on 6020
        await db.SaveChangesAsync();

        // Alex's 260 of employer benefits on the register splits 500:20; Val (no elections) stays on the payroll default.
        var split = await BenefitFringe.SplitAsync(db, [(kit.Fx.AnalystId, 260m), (kit.Fx.ViewerId, 40m)], on, CancellationToken.None);
        Assert.Equal(250m, split.Single(s => s.AccountId == fringe).Amount);
        Assert.Equal(10m, split.Single(s => s.AccountId == taxes).Amount);
        Assert.Equal(40m, split.Single(s => s.AccountId is null).Amount);
        Assert.Equal(300m, split.Sum(s => s.Amount));
    }

    private static void Election(Application.IWorkbenchDbContext db, Guid employeeId, Guid planId, BenefitPlanType type, DateOnly start, Guid? tierId = null,
        decimal? amount = null, bool waived = false) =>
        db.Set<BenefitElection>().Add(new BenefitElection
        {
            EmployeeId = employeeId, PlanId = planId, PlanType = type, TierId = tierId, Coverage = tierId is null ? null : Domain.Hr.CoverageLevel.EmployeeOnly,
            ContributionAmount = amount, IsWaived = waived, CoverageStart = start, Source = Domain.Hr.ElectionSource.Administrative, ElectedBy = "test", ElectedAtUtc = DateTime.UtcNow,
        });
}
