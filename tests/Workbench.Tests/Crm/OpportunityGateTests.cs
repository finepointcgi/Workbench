using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Crm;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Crm;

/// <summary>Stages and go/no-go gates, with approval through the workflow engine (document type "opportunity-gate").</summary>
public sealed class OpportunityGateTests
{
    [Fact]
    public async Task Default_gates_guard_qualified_capture_and_proposal()
    {
        await using var kit = await CrmKit.CreateAsync();
        var gates = await kit.As(CrmKit.Bd).Gates.ListGatesAsync();

        Assert.Equal([OpportunityStage.Qualified, OpportunityStage.Capture, OpportunityStage.Proposal], gates.Select(g => g.ToStage));
        Assert.False(gates[0].RequiresApproval);
        Assert.True(gates[1].RequiresApproval);
        Assert.All(gates, g => Assert.NotEmpty(g.Criteria));
    }

    [Fact]
    public async Task Moving_into_a_gated_stage_needs_the_gate_but_ungated_and_backward_moves_do_not()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.NewAsync();

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => kit.Opportunities.ChangeStageAsync(o.Id, new ChangeStageRequest(OpportunityStage.Qualified)));
        Assert.Contains("Qualification", refused.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Opportunities.ChangeStageAsync(o.Id, new ChangeStageRequest(OpportunityStage.Submitted)));

        // The qualification gate needs no approval: a Go applies at once.
        var review = await kit.SubmitGateAsync(o.Id, OpportunityStage.Qualified);
        Assert.Equal(GateReviewStatus.Approved, review.Status);
        Assert.Equal(80m, review.ScorePercent);
        Assert.Equal(OpportunityStage.Qualified, (await kit.Opportunities.GetAsync(o.Id)).Stage);

        // Back to Identified and forward again: the gate was passed already.
        await kit.Opportunities.ChangeStageAsync(o.Id, new ChangeStageRequest(OpportunityStage.Identified));
        Assert.Equal(OpportunityStage.Qualified, (await kit.Opportunities.ChangeStageAsync(o.Id, new ChangeStageRequest(OpportunityStage.Qualified))).Stage);

        var history = await kit.Opportunities.GetHistoryAsync(o.Id);
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public async Task A_gate_needing_approval_waits_for_another_bd_member_and_then_moves_the_opportunity()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.AdvanceAsync((await kit.NewAsync()).Id, OpportunityStage.Qualified);

        var review = await kit.SubmitGateAsync(o.Id, OpportunityStage.Capture, score: 2);
        Assert.Equal(GateReviewStatus.Pending, review.Status);
        Assert.True(review.BelowPassing);
        Assert.Equal(OpportunityStage.Qualified, (await kit.Opportunities.GetAsync(o.Id)).Stage);
        Assert.True((await kit.Pipeline.GetBoardAsync()).Lanes.Single(l => l.Stage == OpportunityStage.Qualified).Cards.Single().HasPendingGate);

        // One pending decision at a time; nobody approves their own; people outside the route can't.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.SubmitGateAsync(o.Id, OpportunityStage.Capture));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(CrmKit.Bd).Gates.ApproveAsync(review.Id, new DecideGateRequest(null)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Finance).Gates.ApproveAsync(review.Id, new DecideGateRequest(null)));
        Assert.Empty(await kit.As(Finance).Gates.ListWaitingOnMeAsync());

        // bd2@ (Business development) sees it waiting, in the gate list and the approvals inbox.
        var waiting = Assert.Single(await kit.As(CrmKit.Bd2).Gates.ListWaitingOnMeAsync());
        Assert.True(waiting.CanDecide);
        var inbox = await kit.Get<IApprovalInboxService>().ListMineAsync();
        Assert.Contains(inbox, i => i.DocumentType == ApprovalDocumentTypes.OpportunityGate);

        await kit.Gates.ApproveAsync(review.Id, new DecideGateRequest("Go"));
        var moved = await kit.As(CrmKit.Bd).Opportunities.GetAsync(o.Id);
        Assert.Equal(OpportunityStage.Capture, moved.Stage);
        Assert.Equal(GateReviewStatus.Approved, (await kit.Gates.ListReviewsAsync(o.Id)).First().Status);
    }

    [Fact]
    public async Task The_approvals_inbox_decides_gates_through_the_gate_service()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.AdvanceAsync((await kit.NewAsync()).Id, OpportunityStage.Qualified);
        var review = await kit.SubmitGateAsync(o.Id, OpportunityStage.Capture);

        kit.As(Admin); // Administrators may act on a Business development step.
        await kit.Get<IApprovalInboxService>().DecideAsync(ApprovalDocumentTypes.OpportunityGate, review.Id, new ApprovalDecisionRequest(ApprovalDecisionKind.Approve, "ok"));

        Assert.Equal(OpportunityStage.Capture, (await kit.As(CrmKit.Bd).Opportunities.GetAsync(o.Id)).Stage);
    }

    [Fact]
    public async Task A_rejected_gate_leaves_the_stage_and_an_approved_no_go_is_a_no_bid()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.AdvanceAsync((await kit.NewAsync()).Id, OpportunityStage.Qualified);

        var go = await kit.SubmitGateAsync(o.Id, OpportunityStage.Capture);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.As(CrmKit.Bd2).Gates.RejectAsync(go.Id, new DecideGateRequest(" ")));
        var rejected = await kit.Gates.RejectAsync(go.Id, new DecideGateRequest("Not funded yet"));
        Assert.Equal(GateReviewStatus.Rejected, rejected.Status);
        Assert.Equal(OpportunityStage.Qualified, (await kit.As(CrmKit.Bd).Opportunities.GetAsync(o.Id)).Stage);

        var noGo = await kit.SubmitGateAsync(o.Id, OpportunityStage.Capture, recommendation: GateRecommendation.NoGo, score: 1);
        await kit.As(CrmKit.Bd2).Gates.ApproveAsync(noGo.Id, new DecideGateRequest(null));
        var after = await kit.As(CrmKit.Bd).Opportunities.GetAsync(o.Id);
        Assert.Equal(OpportunityStage.NoBid, after.Stage);
        Assert.Equal(OutcomeReason.NotAligned, after.OutcomeReason);
    }

    [Fact]
    public async Task The_submitter_withdraws_a_pending_gate()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.AdvanceAsync((await kit.NewAsync()).Id, OpportunityStage.Qualified);
        var review = await kit.SubmitGateAsync(o.Id, OpportunityStage.Capture);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(CrmKit.Bd2).Gates.CancelAsync(review.Id));
        Assert.Equal(GateReviewStatus.Cancelled, (await kit.As(CrmKit.Bd).Gates.CancelAsync(review.Id)).Status);
        Assert.Empty(await kit.As(CrmKit.Bd2).Gates.ListWaitingOnMeAsync());
    }

    [Fact]
    public async Task Gates_are_configurable_by_business_development()
    {
        await using var kit = await CrmKit.CreateAsync();
        var submitted = new SaveGateDefinitionRequest("Proposal submittal", OpportunityStage.Submitted, 80, false,
            [new SaveGateCriterion("Red team comments resolved", 3), new SaveGateCriterion("Pricing approved", 2)]);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Finance).Gates.CreateGateAsync(submitted));
        var gate = await kit.As(CrmKit.Bd).Gates.CreateGateAsync(submitted);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Gates.CreateGateAsync(submitted with { Name = "Duplicate" }));

        var o = await kit.AdvanceAsync((await kit.NewAsync()).Id, OpportunityStage.Proposal);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Opportunities.ChangeStageAsync(o.Id, new ChangeStageRequest(OpportunityStage.Submitted)));

        await kit.Gates.UpdateGateAsync(gate.Id, submitted with { IsActive = false });
        Assert.Equal(OpportunityStage.Submitted, (await kit.Opportunities.ChangeStageAsync(o.Id, new ChangeStageRequest(OpportunityStage.Submitted))).Stage);
    }

    [Fact]
    public async Task Reaching_capture_creates_an_indirect_bp_charge_code_under_bid_and_proposal()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.NewAsync("Cyber operations");
        Assert.Null(o.BpWbsElementId);

        o = await kit.AdvanceAsync(o.Id, OpportunityStage.Capture);
        Assert.Equal($"IND.BP.{o.Number[4..]}", o.BpChargeCode);
        var code = await kit.WbsAsync(o.BpWbsElementId!.Value);
        Assert.Equal(ChargeKind.Indirect, code.Kind);
        Assert.Equal(kit.BpParentId, code.ParentId);
        Assert.Equal(kit.IndirectProjectId, code.ProjectId);
        Assert.False(code.RequiresAssignment);

        // IND.BP was itself a charge code: general B&P keeps a home beside the opportunity codes.
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.True(await db.Set<WbsElement>().AnyAsync(w => w.Code == "IND.BP.GEN" && w.ParentId == kit.BpParentId && w.IsChargeable));
            Assert.Contains(await db.Set<Workbench.Domain.ProjectAccounting.AuditEvent>().Select(a => a.Reason).ToListAsync(), r => r != null && r.Contains(o.Number));
        }

        // Employees can charge it (and general B&P), not the parent; it's indirect, not a direct project.
        kit.As(Owner);
        var codes = await kit.Get<IProjectService>().ListChargeCodesAsync(kit.Fx.AnalystId, CrmKit.Today);
        Assert.Contains(codes, c => c.Code == o.BpChargeCode);
        Assert.Contains(codes, c => c.Code == "IND.BP.GEN");
        Assert.DoesNotContain(codes, c => c.Code == "IND.BP");

        // Moving back and forward again doesn't make another.
        await kit.As(CrmKit.Bd).Opportunities.ChangeStageAsync(o.Id, new ChangeStageRequest(OpportunityStage.Qualified));
        Assert.Equal(o.BpWbsElementId, (await kit.Opportunities.ChangeStageAsync(o.Id, new ChangeStageRequest(OpportunityStage.Capture))).BpWbsElementId);
    }

    [Fact]
    public async Task The_bp_code_closes_when_the_pursuit_ends_and_reopens_with_it()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.AdvanceAsync((await kit.NewAsync()).Id, OpportunityStage.Submitted);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Opportunities.RecordOutcomeAsync(o.Id, new RecordOutcomeRequest(OpportunityStage.Capture, OutcomeReason.Price)));
        o = await kit.Opportunities.RecordOutcomeAsync(o.Id, new RecordOutcomeRequest(OpportunityStage.Lost, OutcomeReason.Price, "12% high", AwardedTo: "Booz Allen"));
        Assert.Equal(OpportunityStage.Lost, o.Stage);
        Assert.False((await kit.WbsAsync(o.BpWbsElementId!.Value)).IsOpen);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Opportunities.ChangeStageAsync(o.Id, new ChangeStageRequest(OpportunityStage.Proposal)));

        o = await kit.Opportunities.ReopenAsync(o.Id, new ChangeStageRequest(OpportunityStage.Proposal));
        Assert.Equal(OpportunityStage.Proposal, o.Stage);
        Assert.Null(o.OutcomeReason);
        Assert.True((await kit.WbsAsync(o.BpWbsElementId!.Value)).IsOpen);
    }

    [Fact]
    public async Task Wins_and_losses_need_a_submitted_proposal_but_a_no_bid_does_not()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.NewAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Opportunities.RecordOutcomeAsync(o.Id, new RecordOutcomeRequest(OpportunityStage.Won, OutcomeReason.Technical)));
        var noBid = await kit.Opportunities.RecordOutcomeAsync(o.Id, new RecordOutcomeRequest(OpportunityStage.NoBid, OutcomeReason.NoCapacity));
        Assert.Equal(OpportunityStage.NoBid, noBid.Stage);
        Assert.Equal(CrmKit.Today, noBid.DecidedOn);
    }
}
