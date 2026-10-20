using Microsoft.EntityFrameworkCore;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Purchasing;

public sealed class SupplierPortalTests
{
    [Fact]
    public async Task A_vendor_contact_sees_only_their_own_vendors_records()
    {
        var (kit, portal, _, _) = await SourcingKit.CreateAsync();
        await using var _kit = kit;
        var acmeContact = await kit.ContactAsync(kit.Acme, "ana@acme.example", "Ana");
        var primaContact = await kit.ContactAsync(kit.Prima, "pat@prima.example", "Pat");

        // ACME is invited to one RFQ; a draft RFQ to ACME isn't shown yet. PRIMA has an issued order; ACME has one too.
        var acmeRfq = await kit.OpenRfqAsync([kit.HandLine(2)], kit.Acme);
        kit.As(Finance);
        var draft = await kit.Rfqs().CreateAsync(new SaveRfqRequest("Draft", SourcingKit.Today.AddDays(5), [kit.HandLine(1)], [kit.Acme.Id]));
        var acmePo = await kit.IssuedOrderAsync(kit.ProjectLine(4, 2_600m)); // ACME
        var primaPo = await kit.Orders().IssueAsync((await kit.Orders().CreateAsync(new SavePurchaseOrderRequest(kit.Prima.Id, PurchasingKit.Day,
            [new SavePurchaseOrderLineRequest("Licenses", 10, 50m, kit.Fx.Wbs1)]))).Order.Id);
        var primaBill = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "PR-1", PurchasingKit.Day, [PurchasingKit.ProjectLine(kit.Fx.Wbs1, 75m)]));

        // ACME's view.
        portal.ContactId = acmeContact.Id;
        var p = kit.Portal();
        Assert.Equal(kit.Acme.Id, (await p.GetSessionAsync()).VendorId);
        Assert.Equal([acmeRfq.Rfq.Number], (await p.ListRfqsAsync()).Select(r => r.Number));
        Assert.Equal([acmePo.Order.Number], (await p.ListOrdersAsync()).Select(o => o.Number));
        Assert.Empty(await p.ListInvoicesAsync());
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => p.GetRfqAsync(draft.Rfq.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => p.GetOrderAsync(primaPo.Order.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => p.AcknowledgeOrderAsync(primaPo.Order.Id, new AcknowledgeOrderRequest(AcknowledgementState.Accepted)));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => p.SubmitShipNoticeAsync(primaPo.Order.Id,
            new ShipNoticeRequest(SourcingKit.Today, [new ShipNoticeLineRequest(primaPo.Lines[0].Id, 1)])));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => p.SubmitInvoiceAsync(
            new SupplierInvoiceRequest(primaPo.Order.Id, "X-1", SourcingKit.Today, 50m, [new SupplierInvoiceLineRequest(primaPo.Lines[0].Id, 1)]), "x.pdf", SourcingKit.Pdf()));

        // A PRIMA line id smuggled onto ACME's own order is refused too.
        await Assert.ThrowsAsync<ArgumentException>(() => p.SubmitShipNoticeAsync(acmePo.Order.Id,
            new ShipNoticeRequest(SourcingKit.Today, [new ShipNoticeLineRequest(primaPo.Lines[0].Id, 1)])));

        // PRIMA's view: its order and paid bill, nothing of ACME's.
        portal.ContactId = primaContact.Id;
        p = kit.Portal();
        Assert.Empty(await p.ListRfqsAsync());
        Assert.Equal([primaPo.Order.Number], (await p.ListOrdersAsync()).Select(o => o.Number));
        Assert.Equal([primaBill.Bill.InvoiceNumber], (await p.ListInvoicesAsync()).Select(i => i.InvoiceNumber));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => p.GetRfqAsync(acmeRfq.Rfq.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => p.SubmitQuoteAsync(acmeRfq.Rfq.Id,
            new SupplierQuoteRequest(5, [new SaveQuoteLineRequest(acmeRfq.Lines[0].Id, 1m)])));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => p.GetOrderAsync(acmePo.Order.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => p.GetOrderAsync(Guid.NewGuid()));

        // Nobody signed in to the portal, or an internal user's session: refused outright.
        portal.ContactId = null;
        kit.As(Admin);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Portal().ListOrdersAsync());

        // Ending a contact's access takes effect on the next call.
        portal.ContactId = acmeContact.Id;
        kit.As(Finance);
        await kit.PortalAdmin().DeactivateContactAsync(acmeContact.Id);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Portal().ListRfqsAsync());

        // Every one of those calls is in the activity log, refusals included.
        var activity = await kit.PortalAdmin().ListActivityAsync(take: 500);
        Assert.Contains(activity, e => e.Actor == "ana@acme.example" && e.Action == "orders.listed" && e.Outcome == "Success");
        Assert.Contains(activity, e => e.Actor == "ana@acme.example" && e.Action == "order.denied" && e.Outcome == "Denied");
        Assert.Contains(activity, e => e.Actor == "pat@prima.example" && e.Action == "rfq.denied" && e.Outcome == "Denied");
        Assert.Contains(activity, e => e.Action == "portal.denied" && e.Actor == "(not signed in)");
        Assert.Contains(activity, e => e.Action == "access.ended" && e.Actor == "ana@acme.example");
        Assert.Contains(activity, e => e.Action == "shipnotice.submitted" && e.Outcome == "Failed");
    }

    [Fact]
    public async Task Vendors_quote_acknowledge_ship_and_invoice_through_the_portal()
    {
        var (kit, portal, _, _) = await SourcingKit.CreateAsync();
        await using var _kit = kit;
        var contact = await kit.ContactAsync(kit.Acme, "ana@acme.example", "Ana Lopez");
        var rfq = await kit.OpenRfqAsync([kit.HandLine(5, "Switches")], kit.Acme, kit.Prima);
        var po = await kit.IssuedOrderAsync(kit.ProjectLine(4, 2_600m));
        portal.ContactId = contact.Id;
        var p = kit.Portal();

        // Quote on the RFQ, with a price break; the buyers see it as a portal quote.
        var mine = await p.SubmitQuoteAsync(rfq.Rfq.Id, new SupplierQuoteRequest(12,
            [new SaveQuoteLineRequest(rfq.Lines[0].Id, 900m, [new PriceBreakDto(5, 850m)])], SourcingKit.Today.AddDays(30), "AQ-9"));
        Assert.True(mine.Rfq.Quoted);
        kit.As(Finance);
        var quote = Assert.Single((await kit.Rfqs().GetAsync(rfq.Rfq.Id)).Quotes);
        Assert.Equal((QuoteSource.Portal, "ana@acme.example", 850m), (quote.Source, quote.SubmittedBy, quote.Lines[0].EffectiveUnitPrice));

        // Acknowledge with a promised date, then a quantity change (which the buyer reviews).
        var acked = await p.AcknowledgeOrderAsync(po.Order.Id, new AcknowledgeOrderRequest(AcknowledgementState.Accepted,
            [new AcknowledgeLineRequest(po.Lines[0].Id, SourcingKit.Today.AddDays(9))]));
        Assert.Equal(AcknowledgementState.Accepted, acked.Order.Acknowledgement);
        Assert.False(acked.Order.NeedsAcknowledgement);
        Assert.Equal(SourcingKit.Today.AddDays(9), acked.Lines[0].PromisedDate);
        await Assert.ThrowsAsync<ArgumentException>(() => p.AcknowledgeOrderAsync(po.Order.Id, new AcknowledgeOrderRequest(AcknowledgementState.Rejected)));
        await p.AcknowledgeOrderAsync(po.Order.Id, new AcknowledgeOrderRequest(AcknowledgementState.Accepted,
            [new AcknowledgeLineRequest(po.Lines[0].Id, null, Quantity: 3)], "Only three in stock."));
        var toReview = Assert.Single(await kit.PortalAdmin().ListAcknowledgementsAsync(needsReviewOnly: true));
        Assert.Equal((AcknowledgementState.AcceptedWithChanges, 3m), (toReview.Status, toReview.Lines[0].Quantity!.Value));
        Assert.StartsWith("Ana Lopez <ana@acme.example>", toReview.AcknowledgedBy);
        await kit.PortalAdmin().MarkAcknowledgementReviewedAsync(toReview.Id);
        Assert.Empty(await kit.PortalAdmin().ListAcknowledgementsAsync(needsReviewOnly: true));

        // Ship two; can't ship more than is open; receiving the notice is an ordinary receipt.
        var shipped = await p.SubmitShipNoticeAsync(po.Order.Id, new ShipNoticeRequest(SourcingKit.Today, [new ShipNoticeLineRequest(po.Lines[0].Id, 2)],
            SourcingKit.Today.AddDays(3), "UPS", "1Z999", "SHP-1"));
        Assert.Equal(2, shipped.Lines[0].QuantityShipped);
        await Assert.ThrowsAsync<ArgumentException>(() => p.SubmitShipNoticeAsync(po.Order.Id, new ShipNoticeRequest(SourcingKit.Today, [new ShipNoticeLineRequest(po.Lines[0].Id, 3)])));
        var notice = Assert.Single(await kit.PortalAdmin().ListShipNoticesAsync(inTransitOnly: true));
        var received = await kit.PortalAdmin().ReceiveShipNoticeAsync(notice.Id, PurchasingKit.Day);
        Assert.Equal(ShipNoticeState.Received, received.Status);
        Assert.NotNull(received.ReceiptNumber);
        Assert.Equal(2, (await kit.Orders().GetAsync(po.Order.Id)).Lines[0].QuantityReceived);

        // Invoice two with a PDF: a draft AP bill billing the PO line, with the PDF as its copy.
        await Assert.ThrowsAsync<ArgumentException>(() => p.SubmitInvoiceAsync(new SupplierInvoiceRequest(po.Order.Id, "INV-1", PurchasingKit.Day, 5_200m,
            [new SupplierInvoiceLineRequest(po.Lines[0].Id, 2)]), "invoice.txt", new MemoryStream("hello"u8.ToArray())));
        var invoice = await p.SubmitInvoiceAsync(new SupplierInvoiceRequest(po.Order.Id, "INV-1", PurchasingKit.Day, 5_200m,
            [new SupplierInvoiceLineRequest(po.Lines[0].Id, 2)]), "invoice.pdf", SourcingKit.Pdf());
        Assert.Equal(("Received", 5_200m), (invoice.Status, invoice.Amount));
        await Assert.ThrowsAsync<ArgumentException>(() => p.SubmitInvoiceAsync(new SupplierInvoiceRequest(po.Order.Id, "INV-1", PurchasingKit.Day, 5_200m,
            [new SupplierInvoiceLineRequest(po.Lines[0].Id, 1)]), "invoice.pdf", SourcingKit.Pdf()));
        await Assert.ThrowsAsync<ArgumentException>(() => p.SubmitInvoiceAsync(new SupplierInvoiceRequest(po.Order.Id, "INV-2", PurchasingKit.Day, 7_800m,
            [new SupplierInvoiceLineRequest(po.Lines[0].Id, 3)]), "invoice.pdf", SourcingKit.Pdf())); // 2 + 3 > 4 ordered

        kit.As(Finance);
        var bill = await kit.Bills.GetAsync(invoice.BillId);
        Assert.Equal((BillState.Draft, po.Lines[0].Id, 2m), (bill.Bill.Status, bill.Lines[0].PurchaseOrderLineId!.Value, bill.Lines[0].Quantity!.Value));
        Assert.Contains("supplier portal", bill.Bill.EnteredBy);
        await using (var db = await kit.Factory.CreateAsync())
        {
            var stored = await db.Set<ApBill>().AsNoTracking().FirstAsync(b => b.Id == invoice.BillId);
            Assert.NotNull(stored.AttachmentFileId);
        }

        // AP submits (the match passes: 2 received, 2 billed), approves and pays: the vendor sees each step.
        await kit.Bills.SubmitAsync(invoice.BillId, new SubmitBillRequest());
        portal.ContactId = contact.Id;
        Assert.Equal("In review", (await kit.Portal().ListInvoicesAsync()).Single().Status);
        kit.As(Owner);
        await kit.Bills.ApproveAsync(invoice.BillId);
        await kit.PayAsync(PurchasingKit.Day, "5001", (invoice.BillId, 5_200m));
        var paid = (await kit.Portal().ListInvoicesAsync()).Single();
        Assert.Equal(("Paid", 0m, "5001", PurchasingKit.Day), (paid.Status, paid.Balance, paid.PaymentReference, paid.LastPaidOn!.Value));
    }

    [Fact]
    public async Task Sign_in_links_work_once_and_passwords_lock_after_five_wrong_tries()
    {
        var (kit, _, mail, _) = await SourcingKit.CreateAsync();
        await using var _kit = kit;
        kit.As(Finance);
        var contact = await kit.PortalAdmin().AddContactAsync(new SaveSupplierContactRequest(kit.Acme.Id, "ana@acme.example", "Ana"));
        var signIn = kit.Get<SupplierSignIn>();

        var token = TokenFrom(Assert.Single(mail.Sent, m => m.To == "ana@acme.example").TextBody);
        var first = await signIn.RedeemLinkAsync(token, "203.0.113.7");
        Assert.Equal((contact.Id, kit.Acme.Id), (first!.ContactId, first.VendorId));
        Assert.Null(await signIn.RedeemLinkAsync(token, "203.0.113.7"));
        Assert.Null(await signIn.RedeemLinkAsync("not-a-token", null));

        // Unknown emails get no email and no hint.
        await signIn.RequestLinkAsync("nobody@acme.example", "https://wb.example", null);
        Assert.DoesNotContain(mail.Sent, m => m.To == "nobody@acme.example");
        await signIn.RequestLinkAsync("ANA@acme.example", "https://wb.example", null);
        Assert.Contains("https://wb.example/supplier/signin/link?token=", mail.Sent.Last().TextBody);

        // A password: too short is refused; after it's set, five wrong tries lock it even for the right one.
        await Assert.ThrowsAsync<ArgumentException>(() => signIn.SetPasswordAsync(contact.Id, null, "short", null));
        var set = await signIn.SetPasswordAsync(contact.Id, null, "correct horse battery", null);
        Assert.True(set.SessionVersion > first.SessionVersion);
        Assert.False(await signIn.IsSessionValidAsync(contact.Id, kit.Acme.Id, first.SessionVersion)); // other sessions end
        Assert.NotNull(await signIn.PasswordSignInAsync("ana@acme.example", "correct horse battery", null));
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await signIn.PasswordSignInAsync("ana@acme.example", "wrong password!!", null));
        }

        Assert.Null(await signIn.PasswordSignInAsync("ana@acme.example", "correct horse battery", null));

        // Deactivation ends the session and stops new sign-ins.
        await kit.PortalAdmin().DeactivateContactAsync(contact.Id);
        Assert.False(await signIn.IsSessionValidAsync(contact.Id, kit.Acme.Id, set.SessionVersion));
        await signIn.RequestLinkAsync("ana@acme.example", null, null);
        Assert.Equal(2, mail.Sent.Count(m => m.To == "ana@acme.example"));

        await using var db = await kit.Factory.CreateAsync();
        Assert.All(await db.Set<SupplierSignInToken>().AsNoTracking().ToListAsync(), t => Assert.NotEqual(token, t.TokenHash));
    }

    private static string TokenFrom(string text) =>
        Uri.UnescapeDataString(System.Text.RegularExpressions.Regex.Match(text, "token=([A-Za-z0-9_%-]+)").Groups[1].Value);
}
