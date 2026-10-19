using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Application.Ceilings;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Ceilings;

/// <summary>Scheduled threshold alerts, requisition checks and fee caps by fiscal year.</summary>
public sealed class CeilingFollowUpTests
{
    // ---- Scheduled alerts ----

    [Fact]
    public async Task The_scheduled_check_alerts_on_cost_that_reached_a_ceiling_without_an_enforced_charge_once_per_threshold()
    {
        await using var kit = await CeilingKit.CreateAsync();
        await kit.WbsHoursAsync(20m, CeilingMode.Warn); // 8 h on record: 40%, nothing to alert yet
        Assert.Empty(await kit.NotifiedAsync("ceiling.threshold"));

        // An import (or an approval) adds 10 h straight to the books, past the enforcement points.
        await AddImportedHoursAsync(kit, 10m);
        Assert.Empty(await kit.NotifiedAsync("ceiling.threshold"));

        kit.As(Viewer);
        var job = Job(kit);
        Assert.Equal(1, await job.RunOnceAsync(CancellationToken.None)); // 18/20 = 90%: the 80% threshold
        var sent = await kit.NotifiedAsync("ceiling.threshold");
        Assert.Contains(Owner, sent);
        Assert.Contains(Admin, sent);

        Assert.Equal(0, await job.RunOnceAsync(CancellationToken.None)); // already sent
        Assert.Equal(sent.Count, (await kit.NotifiedAsync("ceiling.threshold")).Count);

        await AddImportedHoursAsync(kit, 2m); // 20/20: 95% and 100% together, one alert per recipient
        Assert.Equal(1, await job.RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, (await kit.NotifiedAsync("ceiling.threshold")).Count(e => e == Owner));
    }

    [Fact]
    public async Task The_scheduled_check_skips_deactivated_ceilings_and_runs_as_workbench_system()
    {
        await using var kit = await CeilingKit.CreateAsync();
        var ceiling = await kit.WbsHoursAsync(20m, CeilingMode.Warn);
        kit.As(Finance);
        await kit.Ceilings.DeactivateAsync(ceiling.Id);
        await AddImportedHoursAsync(kit, 12m);

        kit.As(Viewer);
        Assert.Equal(0, await Job(kit).RunOnceAsync(CancellationToken.None));
        Assert.Empty(await kit.NotifiedAsync("ceiling.threshold"));
    }

    [Fact]
    public async Task The_web_host_runs_the_scheduled_check()
    {
        await using var factory = new Workbench.Tests.Identity.WorkbenchWebFactory("Development");
        Assert.Contains(factory.Services.GetServices<IHostedService>(), s => s is CeilingAlertBackgroundService);
    }

    // ---- Requisitions ----

    [Fact]
    public async Task A_requisition_past_a_blocking_cost_ceiling_only_warns_by_default_and_the_po_stays_the_check()
    {
        await using var kit = await CeilingKit.CreateAsync();
        await kit.ProjectCostAsync(1_000m); // $700 on record

        var submitted = await SubmitRequisitionAsync(kit, 500m);
        Assert.Equal(RequisitionState.PendingApproval, submitted.Requisition.Status);
        Assert.Contains(submitted.CeilingWarnings!, w => w.Contains("this requisition would bring cost to $1,200.00") && w.Contains("past the ceiling of $1,000.00"));

        kit.As(Owner);
        var shown = await Requisitions(kit).GetAsync(submitted.Requisition.Id);
        Assert.NotEmpty(shown.CeilingWarnings!);
        var approved = await Requisitions(kit).ApproveAsync(submitted.Requisition.Id, new RequisitionDecisionRequest());
        Assert.NotEmpty(approved.CeilingWarnings!);

        // The order made from it is refused when issued.
        kit.As(Finance);
        var po = await kit.Orders.CreateAsync(kit.Order(500m));
        await Assert.ThrowsAsync<CeilingExceededException>(() => kit.Orders.IssueAsync(po.Order.Id));
    }

    [Fact]
    public async Task A_blocking_ceiling_set_to_check_at_requisition_refuses_submitting_and_approving_past_it()
    {
        await using var kit = await CeilingKit.CreateAsync();
        var ceiling = await kit.CeilingAsync(new SaveCeilingRequest(CeilingScope.Project, ProjectId: kit.Fx.Project1, CostCeiling: 1_000m, Mode: CeilingMode.Block, CheckAtRequisition: true));
        Assert.True(ceiling.CheckAtRequisition);

        await Assert.ThrowsAsync<CeilingExceededException>(() => SubmitRequisitionAsync(kit, 500m));

        var small = await SubmitRequisitionAsync(kit, 200m); // 900 ≤ 1,000
        Assert.Null(small.CeilingWarnings);

        // Lowered while it waits: the approval is refused.
        kit.As(Finance);
        await kit.Ceilings.UpdateAsync(ceiling.Id, new SaveCeilingRequest(CeilingScope.Project, CostCeiling: 800m, Mode: CeilingMode.Block, CheckAtRequisition: true));
        kit.As(Owner);
        await Assert.ThrowsAsync<CeilingExceededException>(() => Requisitions(kit).ApproveAsync(small.Requisition.Id, new RequisitionDecisionRequest()));

        // A warning ceiling never blocks, even when it checks at requisition.
        kit.As(Finance);
        await kit.Ceilings.UpdateAsync(ceiling.Id, new SaveCeilingRequest(CeilingScope.Project, CostCeiling: 800m, Mode: CeilingMode.Warn, CheckAtRequisition: true));
        kit.As(Owner);
        var approved = await Requisitions(kit).ApproveAsync(small.Requisition.Id, new RequisitionDecisionRequest());
        Assert.Contains(approved.CeilingWarnings!, w => w.Contains("past the ceiling of $800.00"));
    }

    [Fact]
    public async Task A_requisition_under_its_ceilings_has_no_warnings()
    {
        await using var kit = await CeilingKit.CreateAsync();
        await kit.ProjectCostAsync(5_000m);
        var req = await SubmitRequisitionAsync(kit, 500m);
        Assert.Null(req.CeilingWarnings);
    }

    // ---- Fee caps by fiscal year (validation; billing is in BillingCeilingTests) ----

    [Fact]
    public async Task Fee_caps_take_a_fiscal_year()
    {
        await using var kit = await CeilingKit.CreateAsync();
        kit.As(Finance);
        var rate = await kit.Ceilings.CreateRateCeilingAsync(kit.Fx.Contract1, new SaveRateCeilingRequest(RateCapKind.FeeRate, MaxRate: 0.05m, FiscalYear: 2027));
        Assert.Equal((2027, "Fee rate capped at 5.00% (FY2027)"), (rate.FiscalYear, rate.Label));
        var amount = await kit.Ceilings.CreateRateCeilingAsync(kit.Fx.Contract1, new SaveRateCeilingRequest(RateCapKind.FeeAmount, MaxAmount: 10_000m, FiscalYear: 2026));
        Assert.Equal(2026, amount.FiscalYear);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Ceilings.CreateRateCeilingAsync(kit.Fx.Contract1, new SaveRateCeilingRequest(RateCapKind.FeeRate, MaxRate: 0.05m, FiscalYear: 1999)));
    }

    private static CeilingAlertBackgroundService Job(CeilingKit kit) => new(
        kit.Seeded.Host.Get<IServiceScopeFactory>(), kit.Seeded.Host.Get<ITenantJobRunner>(), TimeProvider.System, NullLogger<CeilingAlertBackgroundService>.Instance);

    private static IRequisitionService Requisitions(CeilingKit kit) => kit.Seeded.Host.Get<IRequisitionService>();

    /// <summary>analyst@ requisitions <paramref name="amount"/> of materials on 1001.01 and submits it.</summary>
    private static async Task<RequisitionDto> SubmitRequisitionAsync(CeilingKit kit, decimal amount)
    {
        kit.As(Editor);
        var req = await Requisitions(kit).CreateAsync(new SaveRequisitionRequest("Field kit",
            [new SaveRequisitionLineRequest("Sensors", 1, amount, kit.Fx.Wbs1, OdcCostType.Materials)]));
        return await Requisitions(kit).SubmitAsync(req.Requisition.Id);
    }

    /// <summary>Hours written to the books directly, as an import or a posting would.</summary>
    private static async Task AddImportedHoursAsync(CeilingKit kit, decimal hours)
    {
        await using var db = await kit.Factory.CreateAsync();
        var sheet = await db.Set<Timesheet>().FirstAsync(t => t.EmployeeId == kit.Fx.AnalystId);
        db.Set<TimeEntry>().Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = kit.Fx.Wbs1, LaborCategoryId = kit.CategoryId, WorkDate = sheet.PeriodStart, Hours = hours });
        await db.SaveChangesAsync();
    }
}
