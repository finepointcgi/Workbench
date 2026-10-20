using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Quality;
using static Workbench.Tests.Identity.SeededHost;
using static Workbench.Tests.Materials.MaterialsKit;
using static Workbench.Tests.Quality.QualityKit;

namespace Workbench.Tests.Quality;

/// <summary>Engineering change orders: approval routing, the effectivity switch, open work orders, revision history and as-designed vs as-built.</summary>
public sealed class EngineeringChangeTests
{
    private static readonly DateOnly Later = new(2099, 1, 1);

    private sealed record Product(PartSummaryDto Board, PartSummaryDto OldChip, PartSummaryDto NewChip, BomDto RevA, BomDto RevB);

    /// <summary>A serial-tracked board on BOM A (old chip), and an inactive BOM B (new chip).</summary>
    private static async Task<Product> ProductAsync(QualityKit kit)
    {
        var oldChip = await kit.M.PartAsync("CHIP-OLD", tracking: PartTracking.Lot);
        var newChip = await kit.M.PartAsync("CHIP-NEW", tracking: PartTracking.Lot);
        var board = await kit.M.PartAsync("BOARD", PartSourceKind.Make, PartCostMethod.Standard, 30m, PartTracking.Serial);
        var a = await kit.M.Manufacturing.SaveBomAsync(null, new SaveBomRequest(board.Id, "A", [new SaveBomComponentRequest(oldChip.Id, 1)]));
        var b = await kit.M.Manufacturing.CopyBomAsync(a.Summary.Id, "B");
        b = await kit.M.Manufacturing.SaveBomAsync(b.Summary.Id, new SaveBomRequest(board.Id, "B", [new SaveBomComponentRequest(newChip.Id, 1)]));
        await kit.M.StockAsync(oldChip.Id, 5, 10m, lot: "OLD-1");
        await kit.M.StockAsync(newChip.Id, 5, 11m, lot: "NEW-1");
        return new Product(board, oldChip, newChip, a, b);
    }

    private static SaveEcoRequest Change(Product p, Effectivity kind = Effectivity.Date, DateOnly? date = null, string? serial = null, decimal cost = 500m) =>
        new("Replace obsolete chip", EcoReason.Obsolescence, "CHIP-OLD is end of life; use CHIP-NEW.", [new SaveEcoItemRequest(p.Board.Id, p.RevB.Summary.Id, Note: "U1")],
            kind, date, serial, EcoStockDisposition.UseUp, EcoWorkOrderDisposition.FinishOnOldRevision, cost);

    [Fact]
    public async Task An_approved_change_switches_the_bom_at_its_effective_date_and_flags_open_work_orders()
    {
        await using var kit = await QualityKit.CreateAsync();
        var p = await ProductAsync(kit);
        var open = await kit.M.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(p.Board.Id, 1, Day, Day.AddDays(5)));
        await kit.M.Manufacturing.ReleaseAsync(open.Summary.Id);

        var eco = await kit.Changes.SaveAsync(null, Change(p, date: Later));
        Assert.Equal((EcoState.Draft, "A", "B"), (eco.Summary.Status, eco.Items[0].FromBomRevision, eco.Items[0].ToBomRevision));
        eco = await kit.Changes.SubmitAsync(eco.Summary.Id);
        Assert.Equal(EcoState.PendingApproval, eco.Summary.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Changes.SaveAsync(eco.Summary.Id, Change(p, date: Later))); // Only drafts change.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Changes.ApproveAsync(eco.Summary.Id, new DecideEcoRequest()));

        // Approval routing: the other Materials member, from the inbox.
        kit.As(QualityUser);
        Assert.Contains(await kit.M.Kit.Get<IApprovalInboxService>().ListMineAsync(), i => i.DocumentType == ApprovalDocumentTypes.EngineeringChange && i.DocumentId == eco.Summary.Id);
        Assert.True((await kit.Changes.GetAsync(eco.Summary.Id)).CanDecide);
        eco = await kit.Changes.ApproveAsync(eco.Summary.Id, new DecideEcoRequest("Go"));
        Assert.Equal(EcoState.Approved, eco.Summary.Status); // Effective date not reached.
        Assert.True((await kit.M.Manufacturing.GetBomAsync(p.RevA.Summary.Id)).Summary.IsActive);

        // A work order starting on the effective date is released on the new revision already.
        var future = await kit.M.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(p.Board.Id, 1, Later, Later.AddDays(5)));
        future = await kit.M.Manufacturing.ReleaseAsync(future.Summary.Id);
        Assert.Equal(("B", "CHIP-NEW"), (future.BomRevision, Assert.Single(future.Components).PartNumber));

        // Nothing is due today; on the date it becomes effective and the open order on revision A is flagged.
        Assert.Equal(0, await kit.Changes.ApplyDueAsync(Day));
        Assert.Equal(1, await kit.Changes.ApplyDueAsync(Later));
        eco = await kit.Changes.GetAsync(eco.Summary.Id);
        Assert.Equal(EcoState.Effective, eco.Summary.Status);
        Assert.True((await kit.M.Manufacturing.GetBomAsync(p.RevB.Summary.Id)).Summary.IsActive);
        Assert.False((await kit.M.Manufacturing.GetBomAsync(p.RevA.Summary.Id)).Summary.IsActive);
        var review = Assert.Single(eco.WorkOrderReviews);
        Assert.Equal((open.Summary.Number, WorkOrderReviewState.Open), (review.WorkOrderNumber, review.Status));
        Assert.Equal(1, eco.Summary.OpenWorkOrderReviews);
        review = await kit.Changes.MarkWorkOrderReviewedAsync(review.Id, new ReviewWorkOrderRequest("Finish on rev A per disposition"));
        Assert.Equal(WorkOrderReviewState.Reviewed, review.Status);
        Assert.Empty(await kit.Changes.ListOpenWorkOrderReviewsAsync());

        var history = await kit.Changes.RevisionHistoryAsync(p.Board.Id);
        Assert.Equal("B", history.ActiveBomRevision);
        Assert.Contains(history.Events, e => e.Kind == "Change effective" && e.EngineeringChangeNumber == eco.Summary.Number && e.Revision == "B");
    }

    [Fact]
    public async Task Cost_impact_over_the_threshold_also_needs_finance_and_a_rejection_returns_it_to_draft()
    {
        await using var kit = await QualityKit.CreateAsync();
        var p = await ProductAsync(kit);
        var eco = await kit.Changes.SubmitAsync((await kit.Changes.SaveAsync(null, Change(p, date: Day, cost: 30_000m))).Summary.Id);
        kit.As(QualityUser);
        eco = await kit.Changes.ApproveAsync(eco.Summary.Id, new DecideEcoRequest());
        Assert.Equal(EcoState.PendingApproval, eco.Summary.Status);
        kit.As(Finance);
        Assert.True((await kit.Changes.GetAsync(eco.Summary.Id)).CanDecide);
        eco = await kit.Changes.RejectAsync(eco.Summary.Id, new DecideEcoRequest("Cost not justified"));
        Assert.Equal((EcoState.Draft, "Cost not justified"), (eco.Summary.Status, eco.DecisionComment));
        Assert.True((await kit.M.Manufacturing.GetBomAsync(p.RevA.Summary.Id)).Summary.IsActive);

        // Resubmitted at a lower cost: one approval, effective at once (dated today or earlier).
        kit.As(MaterialsUser);
        await kit.Changes.SaveAsync(eco.Summary.Id, Change(p, date: Day, cost: 900m));
        await kit.Changes.SubmitAsync(eco.Summary.Id);
        kit.As(QualityUser);
        eco = await kit.Changes.ApproveAsync(eco.Summary.Id, new DecideEcoRequest());
        Assert.Equal(EcoState.Effective, eco.Summary.Status);

        kit.As(Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Changes.ListAsync(new EcoQuery()));
    }

    [Fact]
    public async Task Serial_effectivity_decides_each_units_as_designed_configuration_against_its_as_built_genealogy()
    {
        await using var kit = await QualityKit.CreateAsync();
        var p = await ProductAsync(kit);

        async Task BuildAsync(string serial)
        {
            var wo = await kit.M.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(p.Board.Id, 1, Day, Day.AddDays(2)));
            wo = await kit.M.Manufacturing.ReleaseAsync(wo.Summary.Id);
            await kit.M.Manufacturing.IssueComponentsAsync(wo.Summary.Id, new IssueComponentsRequest(Day));
            await kit.M.Manufacturing.CompleteAsync(wo.Summary.Id, new CompleteWorkOrderRequest(1, Day, Serials: [serial]));
        }

        await BuildAsync("SN-001");

        // From SN-002 on, the board is designed to revision B (effective at approval).
        var eco = await kit.Changes.SubmitAsync((await kit.Changes.SaveAsync(null, Change(p, Effectivity.SerialOrLot, serial: "sn-002"))).Summary.Id);
        kit.As(Admin);
        eco = await kit.Changes.ApproveAsync(eco.Summary.Id, new DecideEcoRequest());
        Assert.Equal((EcoState.Effective, "SN-002"), (eco.Summary.Status, eco.Summary.EffectiveSerialOrLot));
        kit.As(MaterialsUser);
        await BuildAsync("SN-002");

        var first = await kit.Changes.ConfigurationAsync(p.Board.Id, "sn-001");
        Assert.Equal(("A", "A", true), (first.AsBuiltBomRevision, first.AsDesignedBomRevision, first.Matches));
        Assert.Equal(["Lot OLD-1"], Assert.Single(first.Lines).LotsAndSerials);
        var second = await kit.Changes.ConfigurationAsync(p.Board.Id, "SN-002");
        Assert.Equal(("B", "B", eco.Summary.Number, true), (second.AsBuiltBomRevision, second.AsDesignedBomRevision, second.AsDesignedBy, second.Matches));
        Assert.Equal("CHIP-NEW", Assert.Single(second.Lines).PartNumber);

        var units = await kit.Changes.ListUnitsAsync(p.Board.Id);
        Assert.Equal(["SN-001", "SN-002"], units.Select(u => u.Serial).Order(StringComparer.Ordinal));
        Assert.All(units, u => Assert.True(u.Matches));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Changes.ConfigurationAsync(p.Board.Id, "SN-999"));
    }
}
