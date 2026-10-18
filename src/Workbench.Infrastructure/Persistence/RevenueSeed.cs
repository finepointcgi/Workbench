using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Billing;
using Workbench.Application.IndirectRates;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Revenue;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Revenue;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Revenue;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for revenue recognition: project 1001 recognizes cost plus fee (burdened by the seeded indirect
/// rates), with an approved estimate at completion, and the current period's revenue run posted. The run catches up the
/// burden and fee that the seeded direct-cost invoice didn't bill. Runs once, after the billing and indirect-rate seeds.
/// </summary>
internal static class RevenueSeed
{
    private const string Actor = "pm@example.com";

    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<RevenueSetup>().AnyAsync(cancellationToken) || !await db.Set<Account>().AnyAsync(cancellationToken))
        {
            return;
        }

        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == "1001" && p.ContractId != null, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Start <= today && p.End >= today && p.Status == PeriodStatus.Open, cancellationToken);
        if (project is null || period is null)
        {
            return;
        }

        var factory = (IWorkbenchDbContextFactory)services.GetService(typeof(IWorkbenchDbContextFactory))!;
        var clock = TimeProvider.System;
        var audit = new AuditRecorder(new SeedUser(), clock);
        var setup = new RevenueSetupService(factory, audit, clock);
        var runs = new RevenueRunService(factory, new RevenueEngine(new BillingCostSource([new BurdenedCostService(factory)])), new GeneralLedger(audit, clock), audit);

        await setup.SaveSetupAsync(new SaveRevenueSetupRequest(project.Id, RevenueMethodKind.CostPlusFee,
            Notes: "CPFF: allowable burdened cost plus each CLIN's fee (seeded)."), cancellationToken);
        var estimate = await setup.SubmitEstimateAsync(new SubmitEstimateRequest(project.Id, period.Start, 1_150_000m, 1_230_500m,
            Notes: "Baseline EAC: remaining labor at current staffing, travel per plan (seeded)."), cancellationToken);
        await setup.ApproveEstimateAsync(estimate.Id, new DecideEstimateRequest("Reviewed with the PM"), cancellationToken);
        await runs.PostAsync(period.Id, new PostRevenueRunRequest([project.Id]), cancellationToken);
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email => Actor;
    }
}
