using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Billing;
using Workbench.Application.Evm;
using Workbench.Application.IndirectRates;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Evm;
using Workbench.Domain.Evm;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Scheduling;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for earned value on project 1001, entered through the real services as pm@: a "PMB schedule (IBR)"
/// schedule baseline that spreads the four work packages over the year, a PMB from the locked award baseline (control
/// accounts at level 2, $15,000 management reserve), one work package per technique family (level of effort, % complete,
/// 50/50, weighted milestones), the previous period closed, a variance analysis, one approved and one pending baseline change.
/// Runs once, after the financials, indirect rates and resourcing seeds.
/// </summary>
internal static class EvmSeed
{
    private const string Pm = "pm@example.com";
    private const string Finance = "dev@workbench.local";

    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        var project = await db.Set<Project>().FirstOrDefaultAsync(p => p.Code == "1001", cancellationToken);
        if (project?.SheetId is not { } sheetId
            || await db.Set<EvmBaseline>().AnyAsync(b => b.ProjectId == project.Id, cancellationToken)
            || !await db.Set<BudgetVersion>().AnyAsync(v => v.ProjectId == project.Id && v.Kind == BudgetKind.Baseline && v.IsLocked, cancellationToken))
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var periods = (await db.Set<FiscalPeriod>().ToListAsync(cancellationToken)).OrderBy(p => p.Start).ToList();
        var current = periods.LastOrDefault(p => p.Start <= today);
        var previous = current is null ? null : periods.LastOrDefault(p => p.End < current.Start);
        var wbs = await db.Set<WbsElement>().Where(w => w.ProjectId == project.Id).ToDictionaryAsync(w => w.Code, cancellationToken);
        string[] codes = ["1001.01.01", "1001.01.02", "1001.01.03", "1001.01.04"];
        if (previous is null || codes.Any(c => wbs.GetValueOrDefault(c)?.ItemId is null))
        {
            return;
        }

        // The schedule the PMB is timed by: level-of-effort planning all year, requirements over three months, the baseline schedule
        // from the second to the fourth, execution all year (it starts with the site survey).
        var start = previous.Start;
        DateOnly End(int months) => start.AddMonths(months).AddDays(-1);
        var schedule = new Baseline { SheetId = sheetId, Name = "PMB schedule (IBR)", CapturedAtUtc = DateTime.UtcNow.AddDays(-40) };
        (string Code, DateOnly From, DateOnly To)[] plan =
        [
            ("1001.01.01", start, End(12)),
            ("1001.01.02", start, End(3)),
            ("1001.01.03", start.AddMonths(1), End(4)),
            ("1001.01.04", start, End(12)),
        ];
        schedule.Entries.AddRange(plan.Select(p => new BaselineEntry { ItemId = wbs[p.Code].ItemId!.Value, Start = p.From, Finish = p.To }));
        db.Add(schedule);
        await db.SaveChangesAsync(cancellationToken);

        var factory = services.GetRequiredService<IWorkbenchDbContextFactory>();
        var user = new SeedUser { Email = Pm };
        var clock = TimeProvider.System;
        var audit = new AuditRecorder(user, clock);
        var evm = new EvmService(factory, audit, new BillingCostSource([new BurdenedCostService(factory)]), clock);
        var changes = new EvmChangeRequestService(factory, audit, clock);

        var pmb = await evm.CreateBaselineAsync(project.Id, new CreateEvmBaselineRequest(
            ScheduleBaselineId: schedule.Id, ControlAccountLevel: 2, ManagementReserve: 15_000m, DefaultTechnique: EvTechnique.PercentComplete), cancellationToken);
        Guid Wp(string code) => pmb.WorkPackages.Single(w => w.WbsCode == code).Id;
        await evm.UpdateWorkPackageAsync(Wp("1001.01.01"), new UpdateWorkPackageRequest(EvTechnique.LevelOfEffort), cancellationToken);
        await evm.UpdateWorkPackageAsync(Wp("1001.01.03"), new UpdateWorkPackageRequest(EvTechnique.FiftyFifty), cancellationToken);
        pmb = await evm.UpdateWorkPackageAsync(Wp("1001.01.04"), new UpdateWorkPackageRequest(EvTechnique.Milestones, Milestones:
        [
            new SaveEvmMilestoneRequest("Site survey complete", 10, PlannedDate: End(1)),
            new SaveEvmMilestoneRequest("Design approved", 20, PlannedDate: End(4)),
            new SaveEvmMilestoneRequest("Build complete", 35, PlannedDate: End(8)),
            new SaveEvmMilestoneRequest("Test complete", 20, PlannedDate: End(11)),
            new SaveEvmMilestoneRequest("Deployed", 15, PlannedDate: End(12)),
        ]), cancellationToken);
        await evm.SetThresholdsAsync(project.Id, new SetEvmThresholdsRequest(10, 2_500), cancellationToken);

        // Last period's status (the site survey done a week late), then this period's progress.
        var survey = pmb.WorkPackages.Single(w => w.WbsCode == "1001.01.04").Milestones.Single(m => m.Name == "Site survey complete");
        var surveyed = previous.End.AddDays(7) < today ? previous.End.AddDays(7) : today;
        await evm.SetProgressAsync(Wp("1001.01.02"), new SetWorkPackageProgressRequest(20), cancellationToken);
        await evm.ClosePeriodAsync(project.Id, previous.Id, cancellationToken);
        await evm.SetMilestoneCompletionAsync(survey.Id, new SetMilestoneCompletionRequest(surveyed), cancellationToken);
        await evm.SetProgressAsync(Wp("1001.01.02"), new SetWorkPackageProgressRequest(45), cancellationToken);

        var variances = await evm.ListVariancesAsync(project.Id, current!.Id, cancellationToken);
        var flagged = variances.FirstOrDefault(v => v.NeedsVar && v.Code == "1001.01.03") ?? variances.FirstOrDefault(v => v.NeedsVar);
        if (flagged is not null)
        {
            await evm.SaveVarianceAsync(project.Id, current.Id, flagged.ControlAccountWbsId, new SaveVarianceRequest(
                "Engineering started building the schedule before requirements were baselined, so hours are being charged before the work package can claim its 50% start credit.",
                "No impact on the critical path yet; the cost variance will reverse when the schedule work package starts formally next week.",
                "Hold the schedule kickoff review this period and stop charging the work package until requirements reach 50%.",
                flagged.Bac + 2_000m), cancellationToken);
        }

        user.Email = Pm;
        var approved = await changes.CreateAsync(project.Id, new CreateChangeRequestRequest(
            "Add data migration scope to Execution",
            "Customer direction (letter 2026-014) adds migration of the legacy case archive; funded from management reserve pending a contract modification.",
            [new SaveChangeLineRequest(wbs["1001.01.04"].Id, EvmBudgetSourceKind.ManagementReserve, 5_000m)]), cancellationToken);
        user.Email = Finance;
        await changes.ApproveAsync(approved.Id, new DecideChangeRequestRequest("Approved at the monthly CCB."), cancellationToken);
        user.Email = Pm;
        await changes.CreateAsync(project.Id, new CreateChangeRequestRequest(
            "Re-plan requirements finish",
            "Stakeholder interviews slipped two weeks; move the requirements finish without changing its budget.",
            [new SaveChangeLineRequest(wbs["1001.01.02"].Id, EvmBudgetSourceKind.ManagementReserve, 0m, NewFinish: End(3).AddDays(14))]), cancellationToken);
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email { get; set; } = "";
    }
}
