using Workbench.Contracts;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Purchasing;

public sealed class RequisitionTests
{
    [Fact]
    public async Task A_project_line_under_the_threshold_needs_only_the_project_manager()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var req = await kit.SubmitRequisitionAsync(kit.ProjectLine(2, 1_500m));
        Assert.Equal(RequisitionState.PendingApproval, req.Requisition.Status);
        Assert.Equal(RequisitionApprovalStage.ProjectManager, req.Requisition.Stage);
        Assert.Equal(Owner, req.Lines[0].RoutedTo);
        Assert.False(req.NeedsFinance);
        Assert.False(req.CanApprove);

        // The requester can't approve their own; someone it isn't routed to can't either.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Requisitions().ApproveAsync(req.Requisition.Id, new RequisitionDecisionRequest()));
        kit.As(Finance);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Requisitions().ApproveAsync(req.Requisition.Id, new RequisitionDecisionRequest()));

        kit.As(Owner);
        Assert.Single(await kit.Requisitions().ListApprovalQueueAsync());
        var approved = await kit.Requisitions().ApproveAsync(req.Requisition.Id, new RequisitionDecisionRequest("OK for the field test"));
        Assert.Equal(RequisitionState.Approved, approved.Requisition.Status);
        Assert.Null(approved.Requisition.Stage);
        var decision = Assert.Single(approved.History);
        Assert.Equal((RequisitionApprovalStage.ProjectManager, "Approved", Owner, "1001"), (decision.Stage, decision.Decision, decision.DecidedBy, decision.ProjectCode));
        Assert.Empty(await kit.Requisitions().ListApprovalQueueAsync());
    }

    [Fact]
    public async Task Over_the_threshold_finance_approves_after_the_project_manager()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var req = await kit.SubmitRequisitionAsync(kit.ProjectLine(4, 2_600m));
        Assert.Equal(10_400m, req.Requisition.Total);
        Assert.True(req.NeedsFinance);
        Assert.Equal(RequisitionApprovalStage.ProjectManager, req.Requisition.Stage);

        // Finance can't jump the queue.
        kit.As(Finance);
        Assert.Empty(await kit.Requisitions().ListApprovalQueueAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Requisitions().ApproveAsync(req.Requisition.Id, new RequisitionDecisionRequest()));

        kit.As(Owner);
        var afterPm = await kit.Requisitions().ApproveAsync(req.Requisition.Id, new RequisitionDecisionRequest());
        Assert.Equal(RequisitionState.PendingApproval, afterPm.Requisition.Status);
        Assert.Equal(RequisitionApprovalStage.Finance, afterPm.Requisition.Stage);

        // The PM (not in Finance) can't do the Finance stage.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Requisitions().ApproveAsync(req.Requisition.Id, new RequisitionDecisionRequest()));

        kit.As(Finance);
        Assert.Single(await kit.Requisitions().ListApprovalQueueAsync());
        var approved = await kit.Requisitions().ApproveAsync(req.Requisition.Id, new RequisitionDecisionRequest());
        Assert.Equal(RequisitionState.Approved, approved.Requisition.Status);
        Assert.Equal([RequisitionApprovalStage.ProjectManager, RequisitionApprovalStage.Finance], approved.History.Select(h => h.Stage));
    }

    [Fact]
    public async Task The_threshold_is_configurable_and_exactly_at_it_needs_no_finance()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var atDefault = await kit.SubmitRequisitionAsync(kit.ProjectLine(1, 10_000m));
        Assert.False(atDefault.NeedsFinance);

        kit.Options().RequisitionFinanceThreshold = 500m;
        var overLower = await kit.SubmitRequisitionAsync(kit.ProjectLine(1, 600m));
        Assert.True(overLower.NeedsFinance);
    }

    [Fact]
    public async Task Account_lines_go_to_finance_and_nobody_approves_their_own()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var req = await kit.SubmitRequisitionAsync(kit.AccountLine(1, 900m));
        Assert.Equal(RequisitionApprovalStage.Finance, req.Requisition.Stage);
        Assert.Equal("Finance", req.Lines[0].RoutedTo);
        Assert.Equal(kit.OrgId, req.Lines[0].OrgUnitId);

        // finance@ requisitions for themselves: someone else in Finance has to approve.
        kit.As(Finance);
        var own = await kit.Requisitions().SubmitAsync((await kit.Requisitions().CreateAsync(new SaveRequisitionRequest("Monitors", [kit.AccountLine(2, 300m)], kit.OrgId))).Requisition.Id);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Requisitions().ApproveAsync(own.Requisition.Id, new RequisitionDecisionRequest()));
        Assert.DoesNotContain(await kit.Requisitions().ListApprovalQueueAsync(), r => r.Id == own.Requisition.Id);

        kit.As(Admin);
        Assert.Equal(RequisitionState.Approved, (await kit.Requisitions().ApproveAsync(own.Requisition.Id, new RequisitionDecisionRequest())).Requisition.Status);
    }

    [Fact]
    public async Task A_project_manager_requisitioning_on_their_own_project_goes_to_finance()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        kit.As(Owner);
        var req = await kit.Requisitions().CreateAsync(new SaveRequisitionRequest("Spares", [kit.ProjectLine(1, 100m)]));
        var submitted = await kit.Requisitions().SubmitAsync(req.Requisition.Id);
        Assert.Equal(RequisitionApprovalStage.Finance, submitted.Requisition.Stage);
        Assert.True(submitted.NeedsFinance);
    }

    [Fact]
    public async Task A_rejected_requisition_can_be_corrected_and_resubmitted_and_everything_is_audited()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var req = await kit.SubmitRequisitionAsync(kit.ProjectLine(3, 1_000m));
        kit.As(Owner);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Requisitions().RejectAsync(req.Requisition.Id, new RequisitionDecisionRequest()));
        var rejected = await kit.Requisitions().RejectAsync(req.Requisition.Id, new RequisitionDecisionRequest("Two is enough"));
        Assert.Equal(RequisitionState.Rejected, rejected.Requisition.Status);
        Assert.Equal("Two is enough", rejected.RejectionReason);

        kit.As(Editor);
        await kit.Requisitions().UpdateAsync(req.Requisition.Id, new SaveRequisitionRequest("Needed for the field test", [kit.ProjectLine(2, 1_000m)]));
        var resubmitted = await kit.Requisitions().SubmitAsync(req.Requisition.Id);
        Assert.Equal(RequisitionState.PendingApproval, resubmitted.Requisition.Status);
        Assert.Equal(2_000m, resubmitted.Requisition.Total);

        kit.As(Admin);
        var audit = await kit.Get<Workbench.Contracts.ProjectAccounting.IAuditLogService>().ListAsync("PurchaseRequisition", req.Requisition.Id);
        Assert.Equal(["Created", "Submitted", "Rejected", "Updated", "Submitted"], audit.OrderBy(a => a.OccurredAtUtc).Select(a => a.Action));
    }

    [Fact]
    public async Task Employees_requisition_only_for_their_projects_and_org_and_see_only_their_own()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        kit.As(Editor);

        // Project 2002: analyst@ isn't assigned to it.
        var other = new SaveRequisitionLineRequest("Cables", 1, 50m, WbsElementId: await Wbs2Async(kit), OdcType: OdcCostType.Materials);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Requisitions().CreateAsync(new SaveRequisitionRequest("x", [other])));

        // Another organization's account line.
        Guid otherOrg;
        await using (var db = await kit.Factory.CreateAsync())
        {
            var org = new Workbench.Domain.ProjectAccounting.OrgUnit { Code = "FIN", Name = "Finance" };
            db.Set<Workbench.Domain.ProjectAccounting.OrgUnit>().Add(org);
            await db.SaveChangesAsync();
            otherOrg = org.Id;
        }

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            kit.Requisitions().CreateAsync(new SaveRequisitionRequest("x", [kit.AccountLine(1, 10m) with { OrgUnitId = otherOrg }])));

        var options = await kit.Requisitions().GetOptionsAsync();
        Assert.Equal(["1001.01"], options.ChargeCodes.Select(c => c.WbsCode));
        Assert.Equal([kit.OrgId], options.Orgs.Select(o => o.OrgUnitId));

        var mine = await kit.SubmitRequisitionAsync(kit.ProjectLine(1, 10m));
        kit.As(Viewer);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Requisitions().GetAsync(mine.Requisition.Id));
        Assert.Empty(await kit.Requisitions().ListAsync(new RequisitionQuery()));

        // The routed PM and Finance can see it.
        kit.As(Owner);
        Assert.Equal(mine.Requisition.Id, (await kit.Requisitions().GetAsync(mine.Requisition.Id)).Requisition.Id);
        kit.As(Finance);
        Assert.Single(await kit.Requisitions().ListAsync(new RequisitionQuery()));
    }

    private static async Task<Guid> Wbs2Async(PurchasingKit kit)
    {
        await using var db = await kit.Factory.CreateAsync();
        return db.Set<Workbench.Domain.ProjectAccounting.WbsElement>().First(w => w.Code == "2002.01").Id;
    }
}
