using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Quality;
using Workbench.Domain.Accounting;
using static Workbench.Tests.Identity.SeededHost;
using static Workbench.Tests.Materials.MaterialsKit;
using static Workbench.Tests.Quality.QualityKit;

namespace Workbench.Tests.Quality;

/// <summary>Receiving inspection hold, accept/reject, NCRs and the material review board's dispositions.</summary>
public sealed class InspectionTests
{
    [Fact]
    public async Task Received_material_waits_in_inspection_hold_out_of_issue_and_mrp_until_it_passes()
    {
        await using var kit = await QualityKit.CreateAsync();
        var part = await kit.M.PartAsync("HSG", safety: 5);
        await kit.ReceivingPlanAsync(part.Id);
        var (_, inspection) = await kit.ReceiveForInspectionAsync(part.Id, 10, 12m);

        Assert.Equal((10m, 10, InspectionType.Receiving), (inspection.Summary.Quantity, inspection.Summary.SampleSize, inspection.Summary.Kind));
        Assert.Equal("Acme Federal", inspection.Summary.Vendor);
        Assert.Equal(10m, await kit.QuantityAsync(part.Id, StockStatus.InspectionHold));
        Assert.Equal(0m, await kit.QuantityAsync(part.Id, StockStatus.Available));
        Assert.Equal(120m, await kit.M.BalanceAsync("1410")); // Held stock is still on the books.

        // Not issuable, and MRP doesn't count it: the safety stock still needs a buy.
        var short1 = await Assert.ThrowsAsync<ArgumentException>(() => kit.M.Inventory.IssueToProjectAsync(new IssueToProjectRequest(part.Id, 1, Day, kit.M.Kit.Fx.Wbs1)));
        Assert.Contains("0 available", short1.Message);
        var held = (await kit.M.Inventory.ListBalancesAsync(new BalanceQuery(part.Id))).Single();
        var direct = await Assert.ThrowsAsync<ArgumentException>(() => kit.M.Inventory.IssueToProjectAsync(new IssueToProjectRequest(part.Id, 1, Day, kit.M.Kit.Fx.Wbs1, held.Id)));
        Assert.Contains("inspection hold", direct.Message);
        var run = await kit.M.Mrp.RunAsync(new RunMrpRequest(60, Day));
        Assert.Contains(run.PlannedOrders, o => o.PartId == part.Id && o.Quantity == 5);

        // Accepting needs the certificate of conformance.
        var noCert = await Assert.ThrowsAsync<ArgumentException>(() => kit.Inspections.CompleteAsync(inspection.Summary.Id, new CompleteInspectionRequest(Day, Results(inspection))));
        Assert.Contains("certificate of conformance", noCert.Message);
        await kit.AttachCertAsync(inspection.Summary.Id);
        var done = await kit.Inspections.CompleteAsync(inspection.Summary.Id, new CompleteInspectionRequest(Day, Results(inspection)));
        Assert.Equal((InspectionState.Completed, 10m, 0m, MaterialsUser), (done.Summary.Status, done.Summary.QuantityAccepted, done.Summary.QuantityRejected, done.Summary.Inspector));
        Assert.All(done.Results, r => Assert.True(r.Passed));
        Assert.Null(done.Summary.NonconformanceId);
        Assert.Equal(10m, await kit.QuantityAsync(part.Id, StockStatus.Available));
        Assert.Equal(120m, await kit.M.BalanceAsync("1410"));

        run = await kit.M.Mrp.RunAsync(new RunMrpRequest(60, Day));
        Assert.DoesNotContain(run.PlannedOrders, o => o.PartId == part.Id);
        Assert.Single(await kit.M.Inventory.IssueToProjectAsync(new IssueToProjectRequest(part.Id, 10, Day, kit.M.Kit.Fx.Wbs1)));
    }

    [Fact]
    public async Task A_partial_reject_opens_an_ncr_and_holds_the_rejected_quantity_as_nonconforming()
    {
        await using var kit = await QualityKit.CreateAsync();
        var part = await kit.M.PartAsync("LENS", tracking: PartTracking.Lot);
        await kit.ReceivingPlanAsync(part.Id);
        var (_, inspection) = await kit.ReceiveForInspectionAsync(part.Id, 10, 12m, "L-7");

        // A rejection needs defect codes and a description; an out-of-limits measurement is recorded as failed.
        await kit.AttachCertAsync(inspection.Summary.Id);
        var noCodes = await Assert.ThrowsAsync<ArgumentException>(() =>
            kit.Inspections.CompleteAsync(inspection.Summary.Id, new CompleteInspectionRequest(Day, Results(inspection, 10.3m), QuantityAccepted: 8)));
        Assert.Contains("defect code", noCodes.Message);
        var done = await kit.Inspections.CompleteAsync(inspection.Summary.Id, new CompleteInspectionRequest(Day, Results(inspection, 10.3m), QuantityAccepted: 8,
            DefectsFound: 2, DefectCodes: ["dim"], NonconformanceDescription: "Two lenses 0.3 mm long"));
        Assert.False(done.Results.Single(r => r.Name == "Length").Passed);
        Assert.Equal((8m, 2m), (done.Summary.QuantityAccepted, done.Summary.QuantityRejected));
        Assert.Equal(8m, await kit.QuantityAsync(part.Id, StockStatus.Available));
        Assert.Equal(2m, await kit.QuantityAsync(part.Id, StockStatus.Nonconforming));

        var ncr = await kit.Ncrs.GetAsync(done.Summary.NonconformanceId!.Value);
        Assert.Equal((NcrState.Open, NcrSource.ReceivingInspection, 2m, "L-7"), (ncr.Summary.Status, ncr.Summary.Source, ncr.Summary.Quantity, ncr.Summary.Lot));
        Assert.Equal(["DIM"], ncr.Summary.DefectCodes);
        Assert.Equal("Acme Federal", ncr.Summary.Vendor);
        Assert.Equal("L-7", Assert.Single(ncr.Items).Lot);

        // Nonconforming stock can't be issued either.
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => kit.M.Inventory.IssueToProjectAsync(new IssueToProjectRequest(part.Id, 9, Day, kit.M.Kit.Fx.Wbs1)));
        Assert.Contains("8 available", refused.Message);

        // Sampling: a critical failure or too many defects in the sample can't be accepted.
        var sampled = await kit.M.PartAsync("BOLT");
        await kit.ReceivingPlanAsync(sampled.Id, "RCV-BOLT", Sampling.Aql, acceptNumber: 1);
        var (_, bolts) = await kit.ReceiveForInspectionAsync(sampled.Id, 100, 1m);
        Assert.Equal(20, bolts.Summary.SampleSize); // Z1.4 level II, lot of 91–150.
        await kit.AttachCertAsync(bolts.Summary.Id);
        var over = await Assert.ThrowsAsync<ArgumentException>(() => kit.Inspections.CompleteAsync(bolts.Summary.Id, new CompleteInspectionRequest(Day, Results(bolts), DefectsFound: 2)));
        Assert.Contains("accept number", over.Message);
    }

    [Fact]
    public async Task Return_to_vendor_goes_through_approval_then_credits_inventory_at_the_po_price_and_writes_a_debit_memo()
    {
        await using var kit = await QualityKit.CreateAsync();
        var part = await kit.M.PartAsync("CHIP", method: PartCostMethod.Standard, standard: 10m);
        await kit.ReceivingPlanAsync(part.Id);
        var (order, inspection) = await kit.ReceiveForInspectionAsync(part.Id, 10, 12m);
        Assert.Equal(-120m, await kit.M.BalanceAsync("1405"));
        Assert.Equal(20m, await kit.M.BalanceAsync("6150"));
        var done = await kit.Inspections.CompleteAsync(inspection.Summary.Id, new CompleteInspectionRequest(Day, Results(inspection, visual: false), QuantityAccepted: 0,
            DefectCodes: ["COS", "DOC"], NonconformanceDescription: "Scratched, no certificate"));
        var ncrId = done.Summary.NonconformanceId!.Value;
        Assert.Equal(10m, await kit.QuantityAsync(part.Id, StockStatus.Nonconforming));

        var proposed = await kit.Ncrs.ProposeDispositionAsync(ncrId, new ProposeDispositionRequest(NcrDisposition.ReturnToVendor, "Return for credit", ReturnAuthorization: "rma-55", Date: Day));
        Assert.Equal((NcrState.PendingApproval, 100m), (proposed.Summary.Status, proposed.Summary.ValueAtCost));
        Assert.False(proposed.CanDecide); // Never your own disposition.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Ncrs.ApproveAsync(ncrId, new DecideNcrRequest()));
        Assert.Equal(10m, await kit.QuantityAsync(part.Id, StockStatus.Nonconforming)); // Nothing moves before approval.

        // Routed to the other member of the Materials role, through the approvals inbox.
        kit.As(QualityUser);
        var inbox = await kit.M.Kit.Get<IApprovalInboxService>().ListMineAsync();
        Assert.Contains(inbox, i => i.DocumentType == ApprovalDocumentTypes.NcrDisposition && i.DocumentId == ncrId);
        await kit.M.Kit.Get<IApprovalInboxService>().DecideAsync(ApprovalDocumentTypes.NcrDisposition, ncrId, new ApprovalDecisionRequest(ApprovalDecisionKind.Approve, "OK to return"));

        var closed = await kit.Ncrs.GetAsync(ncrId);
        Assert.Equal((NcrState.Closed, NcrDisposition.ReturnToVendor, QualityUser), (closed.Summary.Status, closed.Summary.Disposition, closed.DecidedBy));
        Assert.Equal(0m, await kit.QuantityAsync(part.Id, StockStatus.Nonconforming));
        var memo = closed.DebitMemo!;
        Assert.Equal((10m, 12m, 120m, "RMA-55", DebitMemoState.Open), (memo.Quantity, memo.UnitPrice, memo.Amount, memo.ReturnAuthorization?.ToUpperInvariant(), memo.Status));

        // Inventory relieved at standard, the clearing account debited at the PO price, the receipt's variance reversed.
        Assert.Equal(0m, await kit.M.BalanceAsync("1410"));
        Assert.Equal(0m, await kit.M.BalanceAsync("1405"));
        Assert.Equal(0m, await kit.M.BalanceAsync("6150"));
        var entry = (await kit.M.EntriesAsync(JournalSource.Inventory)).Single(e => e.Number == closed.JournalEntryNumber);
        Assert.Equal(entry.TotalDebit, entry.TotalCredit);
        var returned = (await kit.M.Inventory.ListTransactionsAsync(new TransactionQuery(part.Id, Type: InventoryMovement.VendorReturn))).Single();
        Assert.Equal((-10m, -100m), (returned.Quantity, returned.Amount));

        // Accounts payable takes the memo; the supplier quality view counts the reject.
        kit.As(Finance);
        var applied = await kit.Ncrs.ApplyDebitMemoAsync(memo.Id, new ApplyDebitMemoRequest("Short-paid on V-000009"));
        Assert.Equal(DebitMemoState.Applied, applied.Status);
        var supplier = (await kit.Cars.SupplierQualityAsync(Day.AddDays(-30), Day)).Rows.Single();
        Assert.Equal(("ACME", 10m, 10m, 100m, 1, 120m), (supplier.VendorCode, supplier.QuantityInspected, supplier.QuantityRejected, supplier.RejectRatePercent, supplier.Ncrs, supplier.ReturnedValue));
    }

    [Fact]
    public async Task Scrap_charges_a_project_and_use_as_is_and_rework_bring_stock_back()
    {
        await using var kit = await QualityKit.CreateAsync();
        var part = await kit.M.PartAsync("CABLE");
        await kit.M.StockAsync(part.Id, 10, 5m);
        var stock = (await kit.M.Inventory.ListBalancesAsync(new BalanceQuery(part.Id))).Single();

        // Scrap from stock to a project WBS: direct material with project segments.
        var scrapNcr = await kit.Ncrs.CreateAsync(new CreateNcrRequest(stock.Id, 2, ["WKM"], "Crimps pulled out"));
        Assert.Equal((NcrSource.Stock, 2m), (scrapNcr.Summary.Source, await kit.QuantityAsync(part.Id, StockStatus.Nonconforming)));
        await kit.Ncrs.ProposeDispositionAsync(scrapNcr.Summary.Id, new ProposeDispositionRequest(NcrDisposition.Scrap, ScrapWbsElementId: kit.M.Kit.Fx.Wbs1, Date: Day));
        kit.As(QualityUser);
        var scrapped = await kit.Ncrs.ApproveAsync(scrapNcr.Summary.Id, new DecideNcrRequest());
        Assert.Equal(NcrState.Closed, scrapped.Summary.Status);
        var entry = (await kit.M.EntriesAsync(JournalSource.Inventory)).Single(e => e.Number == scrapped.JournalEntryNumber);
        var debit = entry.Lines.Single(l => l.Debit > 0);
        Assert.Equal((kit.M.Account("5120"), 10m, kit.M.Kit.Fx.Project1), (debit.AccountId, debit.Debit, debit.ProjectId!.Value));
        Assert.Equal(40m, await kit.M.BalanceAsync("1410"));

        // Use as is: back to available, no posting.
        kit.As(MaterialsUser);
        stock = (await kit.M.Inventory.ListBalancesAsync(new BalanceQuery(part.Id))).Single(b => b.Status == StockStatus.Available);
        var useNcr = await kit.Ncrs.CreateAsync(new CreateNcrRequest(stock.Id, 3, ["COS"], "Scuffed jacket"));
        await kit.Ncrs.ProposeDispositionAsync(useNcr.Summary.Id, new ProposeDispositionRequest(NcrDisposition.UseAsIs, "Cosmetic only", Date: Day));
        kit.As(Admin);
        var used = await kit.Ncrs.ApproveAsync(useNcr.Summary.Id, new DecideNcrRequest("Engineering agrees"));
        Assert.Null(used.JournalEntryNumber);
        Assert.Equal((8m, 0m), (await kit.QuantityAsync(part.Id, StockStatus.Available), await kit.QuantityAsync(part.Id, StockStatus.Nonconforming)));

        // Rework: a rework work order kitted with the material; completing it puts it back in stock.
        kit.As(MaterialsUser);
        stock = (await kit.M.Inventory.ListBalancesAsync(new BalanceQuery(part.Id))).Single(b => b.Status == StockStatus.Available);
        var reworkNcr = await kit.Ncrs.CreateAsync(new CreateNcrRequest(stock.Id, 2, ["WKM"], "Wrong pinout"));
        await kit.Ncrs.ProposeDispositionAsync(reworkNcr.Summary.Id, new ProposeDispositionRequest(NcrDisposition.Rework, "Re-terminate J1", Date: Day));
        kit.As(QualityUser);
        var reworked = await kit.Ncrs.ApproveAsync(reworkNcr.Summary.Id, new DecideNcrRequest());
        kit.As(MaterialsUser);
        var wo = await kit.M.Manufacturing.GetWorkOrderAsync(reworked.ReworkWorkOrderId!.Value);
        Assert.Equal((WorkOrderState.InProgress, 2m, 10m), (wo.Summary.Status, wo.Summary.Quantity, wo.Cost.MaterialCost));
        Assert.Equal(10m, await kit.M.BalanceAsync("1450"));
        Assert.Equal(6m, await kit.QuantityAsync(part.Id, StockStatus.Available));
        await kit.M.Manufacturing.CompleteAsync(wo.Summary.Id, new CompleteWorkOrderRequest(2, Day));
        Assert.Equal(8m, await kit.QuantityAsync(part.Id, StockStatus.Available));
        Assert.Equal(0m, await kit.M.BalanceAsync("1450"));
    }

    [Fact]
    public async Task Dispositions_over_the_threshold_also_need_finance_and_a_sent_back_one_reopens_the_ncr()
    {
        await using var kit = await QualityKit.CreateAsync();
        var part = await kit.M.PartAsync("RADAR");
        await kit.M.StockAsync(part.Id, 2, 4_000m);
        var stock = (await kit.M.Inventory.ListBalancesAsync(new BalanceQuery(part.Id))).Single();
        var ncr = await kit.Ncrs.CreateAsync(new CreateNcrRequest(stock.Id, 2, ["FUN"], "Fails self test"));
        await kit.Ncrs.ProposeDispositionAsync(ncr.Summary.Id, new ProposeDispositionRequest(NcrDisposition.Scrap, Date: Day));

        kit.As(QualityUser);
        var rejected = await Assert.ThrowsAsync<ArgumentException>(() => kit.Ncrs.RejectAsync(ncr.Summary.Id, new DecideNcrRequest()));
        Assert.Contains("reason", rejected.Message);
        var step = await kit.Ncrs.ApproveAsync(ncr.Summary.Id, new DecideNcrRequest());
        Assert.Equal(NcrState.PendingApproval, step.Summary.Status); // 8,000 at cost is over the 5,000 threshold: Finance next.
        Assert.Equal(2m, await kit.QuantityAsync(part.Id, StockStatus.Nonconforming));

        kit.As(Finance);
        var back = await kit.Ncrs.RejectAsync(ncr.Summary.Id, new DecideNcrRequest("Try repair first"));
        Assert.Equal((NcrState.Open, "Try repair first"), (back.Summary.Status, back.DecisionComment));

        // Viewers can't see quality records; Finance reads but can't inspect.
        kit.As(Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Ncrs.ListAsync(new NcrQuery()));
        kit.As(Finance);
        Assert.Single(await kit.Ncrs.ListAsync(new NcrQuery()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Ncrs.CreateAsync(new CreateNcrRequest(stock.Id, 1, ["FUN"], "x")));
    }

    [Fact]
    public async Task Final_inspection_holds_completions_and_in_process_inspection_blocks_completion()
    {
        await using var kit = await QualityKit.CreateAsync();
        var chip = await kit.M.PartAsync("CHIP");
        var board = await kit.M.PartAsync("BOARD", PartSourceKind.Make, PartCostMethod.Standard, 30m);
        await kit.M.Manufacturing.SaveBomAsync(null, new SaveBomRequest(board.Id, "A", [new SaveBomComponentRequest(chip.Id, 1)]));
        var center = await kit.M.Manufacturing.SaveWorkCenterAsync(null, new SaveWorkCenterRequest("tst", "Test"));
        await kit.M.Manufacturing.SaveRoutingAsync(null, new SaveRoutingRequest(board.Id, "A",
            [new SaveRoutingOperationRequest(10, "Assemble", center.Id, 0, 1m), new SaveRoutingOperationRequest(20, "Test", center.Id, 0, 0.5m)]));
        await kit.M.StockAsync(chip.Id, 2, 25m);
        await kit.Inspections.SavePlanAsync(null, new SaveInspectionPlanRequest("BOARD-IP", "Solder inspection", board.Id, InspectionType.InProcess,
            [new SaveCharacteristicRequest("Solder joints per IPC-A-610 class 3")], OperationSequence: 10));
        await kit.Inspections.SavePlanAsync(null, new SaveInspectionPlanRequest("BOARD-FI", "Final test", board.Id, InspectionType.Final,
            [new SaveCharacteristicRequest("Current draw", CharacteristicType.Measured, UpperLimit: 0.5m, Unit: "A")]));

        var wo = await kit.M.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(board.Id, 2, Day, Day.AddDays(3)));
        wo = await kit.M.Manufacturing.ReleaseAsync(wo.Summary.Id);
        await kit.M.Manufacturing.IssueComponentsAsync(wo.Summary.Id, new IssueComponentsRequest(Day));
        var inProcess = Assert.Single(await kit.Inspections.ListAsync(new InspectionQuery(Kind: InspectionType.InProcess)));
        Assert.Equal((10, wo.Summary.Number + " op 10"), (inProcess.Kind == InspectionType.InProcess ? 10 : 0, inProcess.Source));
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.M.Manufacturing.CompleteAsync(wo.Summary.Id, new CompleteWorkOrderRequest(2, Day)));
        Assert.Contains("Inspect operation 10", blocked.Message);

        var ip = await kit.Inspections.GetAsync(inProcess.Id);
        await kit.Inspections.CompleteAsync(ip.Summary.Id, new CompleteInspectionRequest(Day, [new RecordResultRequest(ip.Characteristics[0].Id, Passed: true)]));
        await kit.M.Manufacturing.CompleteAsync(wo.Summary.Id, new CompleteWorkOrderRequest(2, Day));
        Assert.Equal(2m, await kit.QuantityAsync(board.Id, StockStatus.InspectionHold));

        var final = await kit.Inspections.GetAsync(Assert.Single(await kit.Inspections.ListAsync(new InspectionQuery(Kind: InspectionType.Final))).Id);
        Assert.Equal(wo.Summary.Id, final.WorkOrderId);
        await kit.Inspections.CompleteAsync(final.Summary.Id, new CompleteInspectionRequest(Day, [new RecordResultRequest(final.Characteristics[0].Id, 0.42m)]));
        Assert.Equal(2m, await kit.QuantityAsync(board.Id, StockStatus.Available));
    }
}
