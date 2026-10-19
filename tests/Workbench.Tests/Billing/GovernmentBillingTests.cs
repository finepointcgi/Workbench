using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Billing;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;

namespace Workbench.Tests.Billing;

public sealed class Sf1443CalculatorTests
{
    private static Sf1443Inputs Inputs(
        ProgressPaymentBasis basis = ProgressPaymentBasis.IncurredCosts, decimal price = 1_000_000m, decimal paid = 300_000m, decimal unpaid = 50_000m,
        decimal incurred = 350_000m, decimal etc = 500_000m) =>
        new("Army", "Test Co", false, "W1", price, 0.80m, 0.80m, new DateOnly(2026, 10, 1), 3, new DateOnly(2026, 11, 5), new DateOnly(2026, 10, 31), basis,
            paid, unpaid, incurred, etc, SubcontractorPaid: 20_000m, SubcontractorLiquidated: 5_000m, SubcontractorApproved: 10_000m,
            PreviousRequested: 100_000m, CostsOfDelivered: 100_000m, PriceOfDelivered: 150_000m, UnliquidatedAdvances: 0m, LiquidatedToDate: 40_000m);

    [Fact]
    public void Every_line_follows_the_form()
    {
        var f = Sf1443Calculator.Compute(Inputs());

        Assert.Equal(350_000m, f.Line11EligibleCosts);
        Assert.Equal(280_000m, f.Line13);
        Assert.False(f.LossRatioApplied);
        Assert.Equal((15_000m, 25_000m, 305_000m), (f.Line14cSubUnliquidated, f.Line14eSubEligible, f.Line15));
        Assert.Equal((800_000m, 305_000m, 205_000m), (f.Line16, f.Line17, f.Line19MaximumEligible));
        Assert.Equal((250_000m, 200_000m, 25_000m, 225_000m), (f.Line20bCostsUndelivered, f.Line20c, f.Line20dSubEligible, f.Line20eLimitation));
        Assert.Equal((850_000m, 680_000m, 680_000m), (f.Line21bPriceUndelivered, f.Line21c, f.Line21eLimitation));
        Assert.Equal((225_000m, 60_000m, 165_000m), (f.Line22MaximumUnliquidated, f.Line24Unliquidated, f.Line25MaximumPermissible));
        Assert.Equal(165_000m, f.Line26Amount); // the lesser of 19 (205,000) and 25 (165,000)
    }

    [Fact]
    public void Paid_cost_basis_leaves_out_unpaid_costs_and_a_loss_applies_the_loss_ratio()
    {
        var f = Sf1443Calculator.Compute(Inputs(ProgressPaymentBasis.PaidCosts, price: 100_000m, paid: 50_000m, unpaid: 10_000m, incurred: 60_000m, etc: 65_000m));

        Assert.Equal(50_000m, f.Line11EligibleCosts);
        Assert.True(f.LossRatioApplied);
        Assert.Equal(32_000m, f.Line13); // 50,000 × 80% × 100,000 ÷ 125,000
        Assert.Contains(f.Notes, n => n.Contains("Loss ratio", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("not yet paid", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_is_requested_once_earlier_requests_reach_the_limit()
    {
        var f = Sf1443Calculator.Compute(Inputs() with { PreviousRequested = 400_000m });

        Assert.True(f.Line19MaximumEligible < 0);
        Assert.Equal(0m, f.Line26Amount);
        Assert.Contains(f.Notes, n => n.StartsWith("Nothing can be requested", StringComparison.Ordinal));
    }
}

public sealed class ProgressPaymentTests
{
    [Fact]
    public async Task A_request_is_computed_from_the_ledger_and_bills_line_26_as_financing()
    {
        var kit = await GovernmentBillingKit.CreateAsync();
        await kit.UseProgressPaymentsAsync();
        kit.SetCosts();
        await kit.AddVendorBillAsync(10_000m, paid: 4_000m);

        var form = await kit.ProgressPayments.PreviewAsync(new ProgressPaymentPreviewRequest(kit.ContractId, GovernmentBillingKit.Through));

        Assert.Equal((100_000m, 94_000m, 6_000m), (form.Line12aIncurredToDate, form.PaidCosts, form.UnpaidCosts));
        Assert.Equal(94_000m, form.Line11EligibleCosts); // paid costs only
        Assert.Equal((75_200m, 160_000m, 75_200m, 75_200m), (form.Line13, form.Line16, form.Line19MaximumEligible, form.Line25MaximumPermissible));
        Assert.Equal(75_200m, form.Line26Amount);

        var request = await kit.ProgressPayments.CreateAsync(new ProgressPaymentPreviewRequest(kit.ContractId, GovernmentBillingKit.Through, new DateOnly(2026, 11, 5)));
        Assert.Equal((1, 75_200m, InvoiceState.Draft), (request.Number, request.Amount, request.InvoiceStatus));
        var invoice = await kit.Invoices.GetAsync(request.InvoiceId!.Value);
        var line = Assert.Single(invoice.Lines);
        Assert.Equal((InvoiceKind.ProgressPayment, InvoiceLineType.ProgressPayment, (Guid?)null, 75_200m), (invoice.Summary.Type, line.Kind, line.ClinId, line.Amount));
        Assert.Empty(invoice.Funding); // financing doesn't count against CLIN funding

        await kit.Invoices.ApproveAsync(invoice.Summary.Id, new ApproveInvoiceRequest());
        Assert.Equal(-75_200m, await kit.BalanceAsync(BillingLedger.ContractFinancing));
        Assert.Equal(75_200m, await kit.BalanceAsync(BillingLedger.ArBilled));
        Assert.Equal(0m, await kit.BalanceAsync("4040"));
        Assert.Equal(75_200m, (await kit.Government.GetFinancingAsync(kit.ContractId)).Unliquidated);
    }

    [Fact]
    public async Task Liquidation_reduces_delivery_invoices_and_feeds_the_next_request()
    {
        var kit = await GovernmentBillingKit.CreateAsync();
        await kit.UseProgressPaymentsAsync();
        kit.SetCosts();
        await kit.AddVendorBillAsync(10_000m, paid: 4_000m);
        var first = await kit.ProgressPayments.CreateAsync(new ProgressPaymentPreviewRequest(kit.ContractId, GovernmentBillingKit.Through, new DateOnly(2026, 11, 5)));
        await kit.Invoices.ApproveAsync(first.InvoiceId!.Value, new ApproveInvoiceRequest());

        var delivery = await kit.DeliverAsync(50_000m);

        var liquidation = Assert.Single(delivery.Lines, l => l.Kind == InvoiceLineType.Liquidation);
        Assert.Equal((-40_000m, (Guid?)null), (liquidation.Amount, liquidation.ClinId)); // 80% of 50,000
        Assert.Equal((10_000m, 10_000m), (delivery.Summary.Total, delivery.Summary.Balance));
        Assert.Equal(50_000m, Assert.Single(delivery.Funding).ThisInvoice); // the CLIN is billed the full delivery
        Assert.Equal(50_000m, -await kit.BalanceAsync("4040"));
        Assert.Equal(-35_200m, await kit.BalanceAsync(BillingLedger.ContractFinancing));

        var position = await kit.Government.GetFinancingAsync(kit.ContractId);
        Assert.Equal((75_200m, 40_000m, 35_200m, 50_000m), (position.FinancingBilled, position.Liquidated, position.Unliquidated, position.DeliveriesBilled));

        // The next request counts the earlier request (18), the liquidation (23) and the delivery (21a, and 20a from it).
        kit.SetCosts(labor: 160_000m);
        var second = await kit.ProgressPayments.PreviewAsync(new ProgressPaymentPreviewRequest(kit.ContractId, GovernmentBillingKit.Through));
        Assert.Equal((75_200m, 40_000m, 50_000m, 50_000m), (second.Line18PreviousRequested, second.Line23Liquidated, second.Line21aPriceDelivered, second.Line20aCostsDelivered));
        Assert.Equal((80_000m, 115_200m, 35_200m, 80_000m), (second.Line19MaximumEligible, second.Line22MaximumUnliquidated, second.Line24Unliquidated, second.Line25MaximumPermissible));
        Assert.Equal(80_000m, second.Line26Amount);

        // A progress payment that deliveries have liquidated can't be voided.
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Invoices.VoidAsync(first.InvoiceId!.Value, new VoidInvoiceRequest("Oops")));
    }

    [Fact]
    public async Task Liquidation_never_takes_more_than_is_outstanding()
    {
        var kit = await GovernmentBillingKit.CreateAsync();
        await kit.UseProgressPaymentsAsync();
        kit.SetCosts(labor: 0m); // 40,000 of cost → 32,000 requested
        var request = await kit.ProgressPayments.CreateAsync(new ProgressPaymentPreviewRequest(kit.ContractId, GovernmentBillingKit.Through, new DateOnly(2026, 11, 5)));
        Assert.Equal(32_000m, request.Amount);
        await kit.Invoices.ApproveAsync(request.InvoiceId!.Value, new ApproveInvoiceRequest());

        var delivery = await kit.DeliverAsync(100_000m);

        Assert.Equal(-32_000m, Assert.Single(delivery.Lines, l => l.Kind == InvoiceLineType.Liquidation).Amount); // not 80,000
        Assert.Equal(68_000m, delivery.Summary.Total);
        Assert.Contains(delivery.Warnings, w => w.Contains("still outstanding", StringComparison.Ordinal));
        Assert.Equal(0m, (await kit.Government.GetFinancingAsync(kit.ContractId)).Unliquidated);
    }

    [Fact]
    public async Task Without_progress_payments_a_request_is_refused_and_deliveries_are_not_liquidated()
    {
        var kit = await GovernmentBillingKit.CreateAsync();
        kit.SetCosts();

        await Assert.ThrowsAsync<ArgumentException>(() => kit.ProgressPayments.PreviewAsync(new ProgressPaymentPreviewRequest(kit.ContractId, GovernmentBillingKit.Through)));
        var delivery = await kit.DeliverAsync(10_000m);
        Assert.DoesNotContain(delivery.Lines, l => l.Kind == InvoiceLineType.Liquidation);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Invoices.GenerateDraftAsync(kit.Billing.Request(InvoiceKind.ProgressPayment)));
    }

    [Fact]
    public async Task Deleting_the_draft_deletes_the_request()
    {
        var kit = await GovernmentBillingKit.CreateAsync();
        await kit.UseProgressPaymentsAsync();
        kit.SetCosts();
        var request = await kit.ProgressPayments.CreateAsync(new ProgressPaymentPreviewRequest(kit.ContractId, GovernmentBillingKit.Through, new DateOnly(2026, 11, 5)));

        await kit.Invoices.DeleteDraftAsync(request.InvoiceId!.Value);

        Assert.Empty(await kit.ProgressPayments.ListAsync(kit.ContractId));
    }
}

public sealed class PerformanceBasedPaymentTests
{
    [Fact]
    public async Task Events_are_capped_completed_with_evidence_billed_and_liquidated()
    {
        var kit = await GovernmentBillingKit.CreateAsync();
        await kit.UsePerformanceBasedAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Events.CreateAsync(kit.ContractId, new SavePerformanceEventRequest("Everything", 190_000m, new DateOnly(2026, 10, 15))));
        var design = await kit.Events.CreateAsync(kit.ContractId, new SavePerformanceEventRequest("Critical design review passed", 60_000m, new DateOnly(2026, 10, 15), "CDR minutes signed by the COR"));
        await kit.Events.CreateAsync(kit.ContractId, new SavePerformanceEventRequest("Qualification test passed", 40_000m, new DateOnly(2027, 3, 1)));

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Events.CompleteAsync(design.Id, new CompletePerformanceEventRequest(new DateOnly(2026, 10, 20), " ")));
        await kit.Events.CompleteAsync(design.Id, new CompletePerformanceEventRequest(new DateOnly(2026, 10, 20), "CDR minutes, COR sign-off 2026-10-20", "DOC-118"));

        var draft = await kit.Invoices.GenerateDraftAsync(kit.Billing.Request(InvoiceKind.PerformanceBased));
        var line = Assert.Single(draft.Lines);
        Assert.Equal((InvoiceLineType.PerformanceBasedPayment, 60_000m, (Guid?)null), (line.Kind, line.Amount, line.ClinId));
        var pbp = await kit.Invoices.ApproveAsync(draft.Summary.Id, new ApproveInvoiceRequest());
        Assert.Equal(pbp.Summary.Id, (await kit.Events.ListAsync(kit.ContractId)).First().InvoiceId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Events.ReopenAsync(design.Id));
        Assert.Equal(-60_000m, await kit.BalanceAsync(BillingLedger.ContractFinancing));

        // Liquidation rate: events total ÷ contract price = 100,000 ÷ 200,000 = 50%.
        var delivery = await kit.DeliverAsync(50_000m);
        Assert.Equal(-25_000m, Assert.Single(delivery.Lines, l => l.Kind == InvoiceLineType.Liquidation).Amount);
        Assert.Contains("performance-based", Assert.Single(delivery.Lines, l => l.Kind == InvoiceLineType.Liquidation).Description, StringComparison.Ordinal);
        Assert.Equal(25_000m, delivery.Summary.Total);
        var position = await kit.Government.GetFinancingAsync(kit.ContractId);
        Assert.Equal((FinancingKind.PerformanceBased, 0.5m, 35_000m), (position.Financing, position.LiquidationRate, position.Unliquidated));

        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Invoices.VoidAsync(pbp.Summary.Id, new VoidInvoiceRequest("Oops")));
    }

    [Fact]
    public async Task Deleting_a_draft_releases_its_events()
    {
        var kit = await GovernmentBillingKit.CreateAsync();
        await kit.UsePerformanceBasedAsync(liquidationRate: 0.40m);
        var e = await kit.Events.CreateAsync(kit.ContractId, new SavePerformanceEventRequest("Kickoff", 10_000m, new DateOnly(2026, 10, 2)));
        await kit.Events.CompleteAsync(e.Id, new CompletePerformanceEventRequest(new DateOnly(2026, 10, 3), "Kickoff held"));
        var draft = await kit.Invoices.GenerateDraftAsync(kit.Billing.Request(InvoiceKind.PerformanceBased));

        await kit.Invoices.DeleteDraftAsync(draft.Summary.Id);

        Assert.Null(Assert.Single(await kit.Events.ListAsync(kit.ContractId)).InvoiceId);
        Assert.Contains(InvoiceKind.PerformanceBased, (await kit.Invoices.ListBillingContractsAsync()).Single(c => c.Id == kit.ContractId).SuggestedTypes);
    }

    [Fact]
    public async Task Events_need_performance_based_financing()
    {
        var kit = await GovernmentBillingKit.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Events.CreateAsync(kit.ContractId, new SavePerformanceEventRequest("Kickoff", 10_000m, new DateOnly(2026, 10, 2))));
    }
}

public sealed class GovernmentVoucherTests
{
    [Fact]
    public void Lines_that_do_not_fit_go_on_sf_1035_continuation_sheets()
    {
        var lines = Enumerable.Range(1, 31).Select(i => new VoucherLineDto("0001", $"Line {i}", null, null, null, i, null)).ToList();

        var pages = Sf1034Paginator.Paginate(lines);

        Assert.Equal([8, 22, 1], pages.Select(p => p.Lines.Count));
        Assert.All(pages, p => Assert.Equal(3, p.PageCount));
        Assert.False(pages[0].IsContinuation);
        Assert.True(pages[1].IsContinuation);
        Assert.Equal(lines.Take(30).Sum(l => l.Amount), pages[2].BroughtForward);
        Assert.Equal(lines.Sum(l => l.Amount), pages[2].CarriedForward);
        Assert.Single(Sf1034Paginator.Paginate([]));
    }

    [Fact]
    public async Task The_voucher_carries_the_contract_format_hours_and_cumulative_amounts()
    {
        var kit = await GovernmentBillingKit.CreateAsync();
        await kit.UseProgressPaymentsAsync(); // also picks the SF 1034 format
        var tm = await kit.Billing.ApprovedAsync(InvoiceKind.TimeAndMaterials);

        var voucher = await kit.Government.GetVoucherAsync(tm.Summary.Id);

        Assert.Equal((VoucherFormat.Sf1034, true, "Partial"), (voucher.ContractFormat, voucher.IsCostType, voucher.PaymentKind));
        var labor = voucher.Pages[0].Lines.First(l => l.Unit == "HR");
        Assert.Equal(16m, labor.Quantity);
        Assert.Equal(labor.Amount, labor.CumulativeAmount);
        Assert.Equal(tm.Summary.Total, voucher.Pages[^1].CarriedForward);
        Assert.Equal("1ABC2", voucher.CageCode);
    }
}

public sealed class PieeExportTests
{
    [Fact]
    public async Task Missing_fields_are_listed_and_block_the_export()
    {
        var kit = await GovernmentBillingKit.CreateAsync();
        var delivery = await kit.DeliverAsync(10_000m);

        var status = await kit.Government.GetPieeAsync(delivery.Summary.Id);

        Assert.False(status.IsValid);
        Assert.Equal(PieeDocumentKind.TwoInOne, status.DocumentType);
        Assert.Equal(["Contract number", "Issue-by DoDAAC", "Admin-by DoDAAC", "Pay office DoDAAC"], status.Issues.Select(i => i.Field));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Government.ExportPieeAsync(delivery.Summary.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Government.SetPieeStatusAsync(delivery.Summary.Id, new SetPieeStatusRequest(PieeState.Submitted, "X")));
    }

    [Fact]
    public async Task A_delivery_with_liquidation_exports_header_routing_and_clin_lines()
    {
        var kit = await GovernmentBillingKit.CreateAsync();
        await kit.UseProgressPaymentsAsync();
        await kit.MakePieeReadyAsync();
        kit.SetCosts();
        var request = await kit.ProgressPayments.CreateAsync(new ProgressPaymentPreviewRequest(kit.ContractId, GovernmentBillingKit.Through, new DateOnly(2026, 11, 5)));
        await kit.Invoices.ApproveAsync(request.InvoiceId!.Value, new ApproveInvoiceRequest());
        var delivery = await kit.DeliverAsync(50_000m);

        var file = await kit.Government.ExportPieeAsync(delivery.Summary.Id);

        Assert.Equal($"piee-{delivery.Summary.Number}.xml", file.FileName);
        var invoice = XDocument.Parse(file.Content).Root!.Element("Invoice")!;
        var header = invoice.Element("Header")!;
        Assert.Equal("2-in-1", header.Element("DocumentType")!.Value);
        Assert.Equal("W56HZV26C0042", header.Element("ContractNumber")!.Value);
        Assert.Equal("1ABC2", header.Element("Vendor")!.Element("CageCode")!.Value);
        Assert.Equal(("W56HZV", "S0512A", "HQ0337"), (header.Element("Routing")!.Element("IssueByDoDAAC")!.Value,
            header.Element("Routing")!.Element("AdminByDoDAAC")!.Value, header.Element("Routing")!.Element("PayOfficeDoDAAC")!.Value));
        Assert.Equal(("50000.00", "40000.00", "10000.00"), (header.Element("Amounts")!.Element("Gross")!.Value,
            header.Element("Amounts")!.Element("Liquidation")!.Value, header.Element("Amounts")!.Element("NetAmount")!.Value));
        var item = Assert.Single(invoice.Element("LineItems")!.Elements("LineItem"));
        Assert.Equal(("0004", "50000.00", "LO"), (item.Element("ItemNumber")!.Value, item.Element("Amount")!.Value, item.Element("UnitOfMeasure")!.Value));

        // The progress payment goes as a Progress Payment document with the SF 1443 lines.
        var pp = XDocument.Parse((await kit.Government.ExportPieeAsync(request.InvoiceId!.Value)).Content).Root!.Element("Invoice")!;
        Assert.Equal("Progress Payment", pp.Element("Header")!.Element("DocumentType")!.Value);
        Assert.Null(pp.Element("LineItems"));
        var sf1443 = pp.Element("ProgressPaymentRequest")!;
        Assert.Equal("80000.00", sf1443.Elements("Line").Single(l => l.Attribute("item")!.Value == "26").Value);
        Assert.Equal("0.8", sf1443.Element("ProgressPaymentRate")!.Value);
    }

    [Fact]
    public async Task Cost_vouchers_and_combos_pick_their_document_and_combos_need_a_ship_to()
    {
        var kit = await GovernmentBillingKit.CreateAsync();
        await kit.MakePieeReadyAsync(PieeDocumentKind.Combo);
        kit.SetCosts();
        var cost = await kit.Billing.ApprovedAsync(InvoiceKind.CostPlus, overrideReason: "Funding mod in signature");
        Assert.Equal(PieeDocumentKind.CostVoucher, (await kit.Government.GetPieeAsync(cost.Summary.Id)).DocumentType);
        Assert.Equal("Cost Voucher", XDocument.Parse((await kit.Government.ExportPieeAsync(cost.Summary.Id)).Content).Root!.Descendants("DocumentType").Single().Value);

        var delivery = await kit.DeliverAsync(10_000m);
        Assert.True((await kit.Government.GetPieeAsync(delivery.Summary.Id)).IsValid);
        var s = await kit.Government.GetSettingsAsync(kit.ContractId);
        await kit.Government.SaveSettingsAsync(kit.ContractId, new SaveGovernmentBillingSettingsRequest(s.Format, s.Financing, s.IsSmallBusiness, s.ProgressPaymentRate,
            s.LiquidationRate, s.CostBasis, s.ContractPrice, s.IssueByDodaac, s.AdminByDodaac, s.PayOfficeDodaac, null, PieeDocumentKind.Combo));
        var combo = await kit.Government.GetPieeAsync(delivery.Summary.Id);
        Assert.Equal(PieeDocumentKind.Combo, combo.DocumentType);
        Assert.Equal("Ship-to DoDAAC", Assert.Single(combo.Issues).Field);
    }

    [Fact]
    public async Task Status_is_tracked_by_hand_through_submitted_rejected_and_accepted()
    {
        var kit = await GovernmentBillingKit.CreateAsync();
        await kit.MakePieeReadyAsync();
        var delivery = await kit.DeliverAsync(10_000m);
        var id = delivery.Summary.Id;

        var submit = await kit.Government.SubmitPieeAsync(id);
        Assert.False(submit.Transmitted);
        Assert.Equal(PieeState.NotSubmitted, submit.Status.Status);
        Assert.Equal("Manual upload", submit.Status.SubmitterName);
        Assert.StartsWith("<?xml", submit.File.Content, StringComparison.Ordinal);

        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Government.SetPieeStatusAsync(id, new SetPieeStatusRequest(PieeState.Accepted)));
        var submitted = await kit.Government.SetPieeStatusAsync(id, new SetPieeStatusRequest(PieeState.Submitted, "PIEE-0001"));
        Assert.Equal((PieeState.Submitted, "PIEE-0001"), (submitted.Status, submitted.Reference));
        Assert.Equal(InvoiceState.Sent, (await kit.Invoices.GetAsync(id)).Summary.Status);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Government.SetPieeStatusAsync(id, new SetPieeStatusRequest(PieeState.Rejected)));
        var rejected = await kit.Government.SetPieeStatusAsync(id, new SetPieeStatusRequest(PieeState.Rejected, Reason: "Wrong pay DoDAAC"));
        Assert.Equal((PieeState.Rejected, "Wrong pay DoDAAC"), (rejected.Status, rejected.RejectionReason));

        await kit.Government.SetPieeStatusAsync(id, new SetPieeStatusRequest(PieeState.Submitted, "PIEE-0002"));
        var accepted = await kit.Government.SetPieeStatusAsync(id, new SetPieeStatusRequest(PieeState.Accepted));
        Assert.Equal((PieeState.Accepted, "PIEE-0002", (string?)null), (accepted.Status, accepted.Reference, accepted.RejectionReason));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Government.SetPieeStatusAsync(id, new SetPieeStatusRequest(PieeState.NotSubmitted)));

        await using var db = await kit.Billing.Factory.CreateAsync();
        Assert.Equal(4, await db.Set<Domain.ProjectAccounting.AuditEvent>().CountAsync(a => a.EntityId == id && a.Action == "PieeStatus"));
    }

    [Fact]
    public async Task Drafts_cannot_be_exported()
    {
        var kit = await GovernmentBillingKit.CreateAsync();
        await kit.MakePieeReadyAsync();
        var draft = await kit.Invoices.GenerateDraftAsync(kit.Billing.Request(InvoiceKind.TimeAndMaterials));

        var status = await kit.Government.GetPieeAsync(draft.Summary.Id);

        Assert.Contains(status.Issues, i => i.Field == "Status");
    }
}
