using Workbench.Application.Purchasing.Edi;
using Workbench.Contracts.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Purchasing;

public sealed class EdiTests
{
    [Fact]
    public void The_x12_reader_and_writer_round_trip_the_sample_files()
    {
        foreach (var name in new[] { "850-purchase-order.x12", "855-acknowledgement.x12", "856-ship-notice.x12", "810-invoice.x12" })
        {
            var parsed = X12.Parse(SourcingKit.Sample(name));
            var written = X12.Write(parsed);
            var again = X12.Parse(written);
            Assert.Equal(parsed.ControlNumber, again.ControlNumber);
            Assert.Equal(parsed.Sets.Single().Segments.Select(s => string.Join('*', s.Elements)), again.Sets.Single().Segments.Select(s => string.Join('*', s.Elements)));
            Assert.Equal(106, written.IndexOf('\n'));
        }

        var ack = X12Documents.Read855(X12.Parse(SourcingKit.Sample("855-acknowledgement.x12")).Sets.Single());
        Assert.Equal(("PO-000001", AcknowledgementState.Accepted, new DateOnly(2026, 11, 10)), (ack.PurchaseOrderNumber, ack.Status, ack.Lines[0].PromisedDate!.Value));
        var asn = X12Documents.Read856(X12.Parse(SourcingKit.Sample("856-ship-notice.x12")).Sets.Single());
        Assert.Equal(("ACME-SHP-7781", "UPS Ground", "1Z999AA10123456784", (1, 2m)), (asn.ShipmentId, asn.Carrier, asn.TrackingNumber, asn.Lines.Single()));
        var inv = X12Documents.Read810(X12.Parse(SourcingKit.Sample("810-invoice.x12")).Sets.Single());
        Assert.Equal(("ACME-INV-5521", 5_200m, (1, 2m, (decimal?)2_600m)), (inv.InvoiceNumber, inv.Total!.Value, inv.Lines.Single()));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("GS*PO*X*Y~", "ISA")]
    public void Malformed_files_are_rejected_with_a_reason(string text, string expected)
    {
        var ex = Assert.Throws<X12FormatException>(() => X12.Parse(text));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Envelope_counts_and_control_numbers_are_checked()
    {
        var good = SourcingKit.Sample("810-invoice.x12");
        Assert.Contains("SE01", Assert.Throws<X12FormatException>(() => X12.Parse(good.Replace("SE*6*0001", "SE*9*0001"))).Message);
        Assert.Contains("IEA02", Assert.Throws<X12FormatException>(() => X12.Parse(good.Replace("IEA*1*000000103", "IEA*1*000000999"))).Message);
    }

    [Fact]
    public async Task Issuing_an_order_sends_an_850_and_the_vendors_855_856_and_810_flow_into_workbench()
    {
        var (kit, _, _, root) = await SourcingKit.CreateAsync();
        await using var _kit = kit;
        kit.As(Finance);
        var partner = await kit.Edi().SavePartnerAsync(null, new SaveEdiPartnerRequest(kit.Acme.Id, "acmefed", "acme-fed"));
        Assert.Equal("ACMEFED", partner.InterchangeId);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Edi().SavePartnerAsync(null, new SaveEdiPartnerRequest(kit.Prima.Id, "PRIMA", "../escape")));

        // PO-000001 to ACME: issuing it drops an 850 in the partner's outbound folder.
        var po = await kit.IssuedOrderAsync(kit.ProjectLine(4, 2_600m));
        Assert.Equal("PO-000001", po.Order.Number);
        var outbound = Directory.GetFiles(Path.Combine(root, "default", "acme-fed", "outbound"));
        var sent = X12.Parse(File.ReadAllText(Assert.Single(outbound)));
        Assert.Equal(("WORKBENCH", "ACMEFED", "000000001"), (sent.SenderId, sent.ReceiverId, sent.ControlNumber));
        var set = sent.Sets.Single();
        Assert.Equal(("850", "PO-000001", "00"), (set.Id, set.First("BEG")![3], set.First("BEG")![1]));
        Assert.Equal(("1", "4", "2600"), (set.First("PO1")![1], set.First("PO1")![2], set.First("PO1")![4]));
        Assert.Equal("10400.00", set.First("AMT")![2]);
        var log = await kit.Edi().ListLogAsync(new EdiLogQuery(EdiFlow.Outbound));
        Assert.Equal((EdiOutcome.Processed, "PO-000001"), (Assert.Single(log).Status, log[0].DocumentNumber));

        // The vendor's three files arrive in the inbound folder and are picked up.
        var inbound = Directory.CreateDirectory(Path.Combine(root, "default", "acme-fed", "inbound")).FullName;
        foreach (var name in new[] { "855-acknowledgement.x12", "856-ship-notice.x12", "810-invoice.x12" })
        {
            File.WriteAllText(Path.Combine(inbound, name), SourcingKit.Sample(name));
        }

        var polled = await kit.Edi().PollAsync();
        Assert.Equal((3, 3, 0, 0), (polled.Files, polled.Processed, polled.Failed, polled.Duplicates));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(inbound, "processed")).Length);
        Assert.Empty(Directory.GetFiles(inbound));

        // The same paths as the portal: an acknowledgement, a ship notice, a draft bill.
        var ack = Assert.Single(await kit.PortalAdmin().ListAcknowledgementsAsync(po.Order.Id));
        Assert.Equal((AcknowledgementState.Accepted, SupplierChannel.Edi, "EDI (ACME)", new DateOnly(2026, 11, 10)), (ack.Status, ack.Channel, ack.AcknowledgedBy, ack.Lines[0].PromisedDate!.Value));
        var asn = Assert.Single(await kit.PortalAdmin().ListShipNoticesAsync(po.Order.Id));
        Assert.Equal(("ACME-SHP-7781", 2m, ShipNoticeState.InTransit), (asn.ShipmentReference, asn.Lines.Single().Quantity, asn.Status));
        var inbound810 = (await kit.Edi().ListLogAsync(new EdiLogQuery(EdiFlow.Inbound, TransactionSet: "810"))).Single();
        var bill = await kit.Bills.GetAsync(inbound810.DocumentId!.Value);
        Assert.Equal((BillState.Draft, "ACME-INV-5521", 5_200m), (bill.Bill.Status, bill.Bill.InvoiceNumber, bill.Bill.Amount));

        // The same file again is a duplicate, not applied twice.
        var again = await kit.Edi().ImportAsync("810-invoice.x12", SourcingKit.Sample("810-invoice.x12"));
        Assert.Equal(EdiOutcome.Duplicate, Assert.Single(again).Status);
        Assert.Single(await kit.Bills.ListAsync(new BillQuery(VendorId: kit.Acme.Id)));
    }

    [Fact]
    public async Task Documents_that_cant_be_applied_are_logged_with_the_reason_and_can_be_retried()
    {
        var (kit, _, _, _) = await SourcingKit.CreateAsync();
        await using var _kit = kit;
        kit.As(Finance);

        // No partner for ACMEFED yet; then a broken file.
        var unknown = Assert.Single(await kit.Edi().ImportAsync("855.x12", SourcingKit.Sample("855-acknowledgement.x12")));
        Assert.Equal(EdiOutcome.Failed, unknown.Status);
        Assert.Contains("No active EDI partner", unknown.Error);
        var broken = Assert.Single(await kit.Edi().ImportAsync("junk.x12", "hello"));
        Assert.Equal(("?", EdiOutcome.Failed), (broken.TransactionSet, broken.Status));

        // A partner, but PO-000001 doesn't exist yet: failed, then retried once it does.
        await kit.Edi().SavePartnerAsync(null, new SaveEdiPartnerRequest(kit.Acme.Id, "ACMEFED", "acme", SendPurchaseOrders: false));
        var early = Assert.Single(await kit.Edi().ImportAsync("856.x12", SourcingKit.Sample("856-ship-notice.x12")));
        Assert.Equal(EdiOutcome.Failed, early.Status);
        Assert.Contains("PO-000001", early.Error);
        var detail = await kit.Edi().GetMessageAsync(early.Id);
        Assert.StartsWith("ISA*", detail.Content);

        await kit.IssuedOrderAsync(kit.ProjectLine(4, 2_600m));
        Assert.Empty(await kit.Edi().ListLogAsync(new EdiLogQuery(EdiFlow.Outbound))); // this partner takes 850s by hand only
        var retried = Assert.Single(await kit.Edi().RetryAsync(early.Id));
        Assert.Equal((EdiOutcome.Processed, "856"), (retried.Status, retried.TransactionSet));
        Assert.StartsWith("ASN-", retried.DocumentNumber);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Edi().RetryAsync(retried.Id));

        // Over-shipping is refused by the same rule as the portal (2 shipped, 4 ordered: 3 more is too many).
        var tooMany = SourcingKit.Sample("856-ship-notice.x12").Replace("ACME-SHP-7781", "ACME-SHP-7782").Replace("SN1**2*EA", "SN1**3*EA")
            .Replace("000000102", "000000104").Replace("*102*", "*104*").Replace("GE*1*102", "GE*1*104");
        var refused = Assert.Single(await kit.Edi().ImportAsync("856b.x12", tooMany));
        Assert.Equal(EdiOutcome.Failed, refused.Status);
        Assert.Contains("more than", refused.Error);
    }
}
