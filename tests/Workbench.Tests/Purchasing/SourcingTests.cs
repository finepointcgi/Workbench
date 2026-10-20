using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Purchasing;

public sealed class SourcingTests
{
    [Fact]
    public async Task An_rfq_from_a_requisition_with_three_quotes_is_awarded_into_a_purchase_order_with_the_competition_record()
    {
        var (kit, _, mail, _) = await SourcingKit.CreateAsync();
        await using var _kit = kit;
        var req = await kit.ApproveAllAsync(await kit.SubmitRequisitionAsync(kit.ProjectLine(4, 2_600m)));
        kit.As(Finance);
        var sourceable = await kit.Rfqs().ListSourceableRequisitionLinesAsync();
        Assert.Single(sourceable);

        var contact = await kit.ContactAsync(kit.Acme, "orders@acme.example");
        var rfq = await kit.Rfqs().CreateAsync(new SaveRfqRequest("Field laptops and docks", SourcingKit.Today.AddDays(7),
            [new SaveRfqLineRequest("", 0, RequisitionLineId: sourceable[0].Line.Id), kit.HandLine(4, "Docking stations", 200m)],
            [kit.Acme.Id, kit.Prima.Id, kit.Bolt.Id], "Net 30"));
        Assert.Equal(RfqState.Draft, rfq.Rfq.Status);
        Assert.Equal(("Rugged laptops", 4m, 2_600m, req.Requisition.Number), (rfq.Lines[0].Description, rfq.Lines[0].Quantity, rfq.Lines[0].EstimatedUnitCost!.Value, rfq.Lines[0].RequisitionNumber));

        // A requisition line on an open RFQ isn't offered for another one.
        Assert.Empty(await kit.Rfqs().ListSourceableRequisitionLinesAsync());

        rfq = await kit.Rfqs().IssueAsync(rfq.Rfq.Id);
        Assert.Equal(RfqState.Open, rfq.Rfq.Status);
        Assert.Contains(mail.Sent, m => m.To == "orders@acme.example" && m.Subject.Contains(rfq.Rfq.Number));
        Assert.Equal(1, rfq.Vendors.First(v => v.VendorId == kit.Acme.Id).PortalContacts);
        _ = contact;

        // PRIMA has a price break that applies at 4 units; BOLT is cheapest on docks but slow.
        await kit.Rfqs().RecordQuoteAsync(rfq.Rfq.Id, new SaveQuoteRequest(kit.Prima.Id, SourcingKit.Today, 5,
            [new SaveQuoteLineRequest(rfq.Lines[0].Id, 2_550m, [new PriceBreakDto(4, 2_450m)]), new SaveQuoteLineRequest(rfq.Lines[1].Id, 190m)], SourcingKit.Today.AddDays(30), "Q-1"));
        await kit.QuoteAsync(rfq, kit.Acme, 7, 2_500m, 185m);
        rfq = await kit.QuoteAsync(rfq, kit.Bolt, 45, 2_480m, 180m);
        Assert.Equal(3, rfq.Quotes.Count);
        Assert.Equal(3, rfq.Rfq.QuotesReceived);
        Assert.Equal(2_450m, rfq.Quotes.First(q => q.VendorId == kit.Prima.Id).Lines[0].EffectiveUnitPrice);

        var cmp = await kit.Rfqs().GetComparisonAsync(rfq.Rfq.Id);
        var laptops = cmp.Lines[0];
        Assert.Equal(3, laptops.ResponsiveQuotes);
        Assert.True(laptops.Cells.Single(c => c.VendorId == kit.Prima.Id).IsLowestPrice);
        Assert.True(laptops.Cells.Single(c => c.VendorId == kit.Prima.Id).IsRecommended);
        var docks = cmp.Lines[1];
        Assert.True(docks.Cells.Single(c => c.VendorId == kit.Bolt.Id).IsLowestPrice);
        Assert.True(docks.Cells.Single(c => c.VendorId == kit.Prima.Id).IsRecommended); // 5 days beats BOLT's 45 for $10 a unit
        Assert.True(docks.Cells.Single(c => c.VendorId == kit.Bolt.Id).Score < docks.Cells.Single(c => c.VendorId == kit.Acme.Id).Score);

        // Award line 1 to PRIMA, line 2 to ACME (not the lowest price: a rationale is required).
        var picks = new List<AwardLineRequest>
        {
            new(laptops.RfqLineId, laptops.Cells.Single(c => c.VendorId == kit.Prima.Id).QuoteLineId!.Value),
            new(docks.RfqLineId, docks.Cells.Single(c => c.VendorId == kit.Acme.Id).QuoteLineId!.Value),
        };
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Rfqs().AwardAsync(rfq.Rfq.Id, new AwardRfqRequest(picks, "")));
        var rationale = await Assert.ThrowsAsync<ArgumentException>(() => kit.Rfqs().AwardAsync(rfq.Rfq.Id, new AwardRfqRequest(picks, "Three quotes; adequate competition.")));
        Assert.Contains("lowest price", rationale.Message);

        var awarded = await kit.Rfqs().AwardAsync(rfq.Rfq.Id, new AwardRfqRequest(picks, "Three quotes; adequate competition.",
            Rationale: "ACME's docks match the laptops already in the field; BOLT's 45 days misses the field test.", IssueOrders: true));
        Assert.Equal(RfqState.Awarded, awarded.Rfq.Status);
        var award = Assert.Single(awarded.Awards);
        Assert.Equal((3, 3, false), (award.VendorsInvited, award.QuotesReceived, award.SoleSource));
        Assert.Equal(2, award.PurchaseOrderNumbers.Count);
        Assert.Equal(4 * 2_450m + 4 * 185m, award.AwardedTotal);
        Assert.Equal([true, false], award.Lines.Select(l => l.WasLowestPrice));

        // One purchase order per vendor, at the quoted prices, issued; the requisition is ordered.
        var primaPo = await kit.Orders().GetAsync(awarded.Lines[0].PurchaseOrderId!.Value);
        Assert.Equal((kit.Prima.Id, PurchaseOrderState.Issued, 9_800m), (primaPo.Order.VendorId, primaPo.Order.Status, primaPo.Order.Total));
        Assert.Equal(req.Requisition.Number, primaPo.Lines[0].RequisitionNumber);
        Assert.Contains(rfq.Rfq.Number, primaPo.Notes);
        var acmePo = await kit.Orders().GetAsync(awarded.Lines[1].PurchaseOrderId!.Value);
        Assert.Equal((kit.Acme.Id, 740m, "Docking stations"), (acmePo.Order.VendorId, acmePo.Order.Total, acmePo.Lines[0].Description));
        Assert.Equal(RequisitionState.Ordered, (await kit.Requisitions().GetAsync(req.Requisition.Id)).Requisition.Status);
        Assert.Empty(await kit.Orders().ListOrderableRequisitionLinesAsync());

        // Nothing more to award, and the award is audited.
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Rfqs().AwardAsync(rfq.Rfq.Id, new AwardRfqRequest(picks, "Again")));
        await using var db = await kit.Factory.CreateAsync();
        Assert.True(await db.Set<AuditEvent>().AnyAsync(e => e.EntityId == rfq.Rfq.Id && e.Action == "Awarded" && e.Actor == Finance));
    }

    [Fact]
    public async Task A_line_with_only_one_quote_needs_a_sole_source_justification()
    {
        var (kit, _, _, _) = await SourcingKit.CreateAsync();
        await using var _kit = kit;
        var rfq = await kit.OpenRfqAsync([kit.HandLine(10, "Calibration service"), kit.HandLine(10, "Probe cables")], kit.Acme, kit.Prima);
        rfq = await kit.QuoteAsync(rfq, kit.Acme, 14, 300m, 12m);
        await kit.QuoteAsync(rfq, kit.Prima, 14, null, 11m); // PRIMA doesn't bid on the calibration
        var cmp = await kit.Rfqs().GetComparisonAsync(rfq.Rfq.Id);
        Assert.Equal(1, cmp.Lines[0].ResponsiveQuotes);
        var pick = new AwardLineRequest(cmp.Lines[0].RfqLineId, cmp.Lines[0].Cells.Single(c => c.VendorId == kit.Acme.Id).QuoteLineId!.Value);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => kit.Rfqs().AwardAsync(rfq.Rfq.Id, new AwardRfqRequest([pick], "Matches last year's price.")));
        Assert.Contains("sole-source justification", refused.Message);

        // A no-bid line can't be awarded at all.
        var noBid = cmp.Lines[0].Cells.Single(c => c.VendorId == kit.Prima.Id).QuoteLineId!.Value;
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Rfqs().AwardAsync(rfq.Rfq.Id,
            new AwardRfqRequest([new AwardLineRequest(pick.RfqLineId, noBid)], "x", "y")));

        var awarded = await kit.Rfqs().AwardAsync(rfq.Rfq.Id, new AwardRfqRequest([pick], "Matches last year's price within 2%.",
            "ACME is the only accredited lab for these instruments within 200 miles."));
        var award = Assert.Single(awarded.Awards);
        Assert.True(award.SoleSource);
        Assert.Equal(1, award.QuotesReceived);
        Assert.Equal("ACME is the only accredited lab for these instruments within 200 miles.", award.SoleSourceJustification);
        Assert.Equal(PurchaseOrderState.Draft, (await kit.Orders().GetAsync(awarded.Lines[0].PurchaseOrderId!.Value)).Order.Status);
    }

    [Fact]
    public async Task Expired_quotes_are_not_awarded_and_quotes_are_closed_once_awarded()
    {
        var (kit, _, _, _) = await SourcingKit.CreateAsync();
        await using var _kit = kit;
        var rfq = await kit.OpenRfqAsync([kit.HandLine(2), kit.HandLine(3, "Patch cables")], kit.Acme, kit.Prima);
        rfq = await kit.Rfqs().RecordQuoteAsync(rfq.Rfq.Id, new SaveQuoteRequest(kit.Acme.Id, SourcingKit.Today.AddDays(-40), 5,
            rfq.Lines.Select(l => new SaveQuoteLineRequest(l.Id, 100m)).ToList(), SourcingKit.Today.AddDays(-10)));
        rfq = await kit.QuoteAsync(rfq, kit.Prima, 5, 120m, 20m);
        var cmp = await kit.Rfqs().GetComparisonAsync(rfq.Rfq.Id);
        var acmeCell = cmp.Lines[0].Cells.Single(c => c.VendorId == kit.Acme.Id);
        Assert.True(acmeCell.IsExpired);
        Assert.Null(acmeCell.Score);
        Assert.True(cmp.Lines[0].Cells.Single(c => c.VendorId == kit.Prima.Id).IsRecommended);

        var expired = await Assert.ThrowsAsync<ArgumentException>(() => kit.Rfqs().AwardAsync(rfq.Rfq.Id,
            new AwardRfqRequest([new AwardLineRequest(cmp.Lines[0].RfqLineId, acmeCell.QuoteLineId!.Value)], "Cheapest", "x")));
        Assert.Contains("expired", expired.Message);

        // Partial award: line 1 to PRIMA; line 2 stays open, and PRIMA's quote can no longer change.
        var partly = await kit.Rfqs().AwardAsync(rfq.Rfq.Id, new AwardRfqRequest(
            [new AwardLineRequest(cmp.Lines[0].RfqLineId, cmp.Lines[0].Cells.Single(c => c.VendorId == kit.Prima.Id).QuoteLineId!.Value)],
            "Only valid quote; checked against the estimate.", "ACME's quote expired before award."));
        Assert.Equal(RfqState.PartiallyAwarded, partly.Rfq.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.QuoteAsync(partly, kit.Prima, 5, 110m, 18m));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Rfqs().CancelAsync(rfq.Rfq.Id, new RfqReasonRequest("Changed my mind")));
    }

    [Fact]
    public async Task Rfqs_and_the_portal_admin_are_for_finance_and_admins()
    {
        var (kit, _, _, _) = await SourcingKit.CreateAsync();
        await using var _kit = kit;
        var rfq = await kit.OpenRfqAsync([kit.HandLine(1)], kit.Acme);

        foreach (var who in new[] { Editor, Owner, Viewer })
        {
            kit.As(who);
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Rfqs().ListAsync(new RfqQuery()));
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Rfqs().GetAsync(rfq.Rfq.Id));
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.PortalAdmin().ListContactsAsync());
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Edi().ListLogAsync(new EdiLogQuery()));
        }

        kit.As(Admin);
        Assert.Single(await kit.Rfqs().ListAsync(new RfqQuery()));
    }

    [Fact]
    public async Task Vendor_contacts_are_kept_apart_from_internal_users()
    {
        var (kit, _, _, _) = await SourcingKit.CreateAsync();
        await using var _kit = kit;
        kit.As(Finance);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.PortalAdmin().AddContactAsync(new SaveSupplierContactRequest(kit.Acme.Id, Viewer, "Val")));
        await kit.ContactAsync(kit.Acme, "Sales@Acme.example");
        await Assert.ThrowsAsync<ArgumentException>(() => kit.PortalAdmin().AddContactAsync(new SaveSupplierContactRequest(kit.Prima.Id, "sales@acme.example", "Dup")));
        Assert.Equal("sales@acme.example", Assert.Single(await kit.PortalAdmin().ListContactsAsync()).Email);
    }
}
