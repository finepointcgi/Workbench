using System.Text;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Materials;
using Workbench.Domain.Accounting;
using Workbench.Domain.Identity;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;
using static Workbench.Tests.Materials.MaterialsKit;

namespace Workbench.Tests.Materials;

/// <summary>
/// <see cref="MaterialsKit"/> plus CLINs on contract C1 (project 1001): 0001 firm fixed price and 0002 cost plus fixed fee,
/// C1's billing settings (CAGE, admin and paying offices), and bd@ (Business development).
/// </summary>
internal sealed class SalesKit : IAsyncDisposable
{
    public const string Bd = "bd@example.com";

    private SalesKit(MaterialsKit materials) => M = materials;

    public MaterialsKit M { get; }
    public Guid FfpClin { get; private set; }
    public Guid CostClin { get; private set; }
    public ISalesOrderService Orders => M.Kit.Get<ISalesOrderService>();
    public IShippingService Shipping => M.Kit.Get<IShippingService>();
    public Guid Project1 => M.Kit.Fx.Project1;
    public Guid Wbs1 => M.Kit.Fx.Wbs1;
    public Guid Contract1 => M.Kit.Fx.Contract1;

    public static async Task<SalesKit> CreateAsync()
    {
        var kit = new SalesKit(await MaterialsKit.CreateAsync());
        await using (var db = await kit.M.Kit.Factory.CreateAsync())
        {
            var c1 = kit.Contract1;
            var ffp = new Clin { ContractId = c1, Number = "0001", Description = "Sensor units", Pricing = ClinPricing.FirmFixedPrice, PeriodStart = Day.AddYears(-1), PeriodEnd = Day.AddYears(1), CeilingValue = 50_000m, FundedValue = 50_000m };
            var cost = new Clin { ContractId = c1, Number = "0002", Description = "Spares (cost)", Pricing = ClinPricing.CostPlusFixedFee, FeeRate = 0.07m, PeriodStart = Day.AddYears(-1), PeriodEnd = Day.AddYears(1), CeilingValue = 100_000m, FundedValue = 100_000m, Order = 1 };
            db.Set<Clin>().AddRange(ffp, cost);
            db.Set<AppUser>().Add(new AppUser { Email = Bd, DisplayName = "Blake", IsBusinessDevelopment = true });
            await db.SaveChangesAsync();
            (kit.FfpClin, kit.CostClin) = (ffp.Id, cost.Id);
            kit.CustomerId = await db.Set<Contract>().Where(c => c.Id == c1).Select(c => c.CustomerId).SingleAsync();
        }

        kit.M.As(Finance);
        await kit.M.Kit.Get<IInvoiceService>().SaveSettingsAsync(kit.Contract1, new SaveContractBillingSettingsRequest("C1", 0, PayingOffice: "DFAS Columbus, HQ0338",
            AdminOffice: "S1103A DCMA Boston", CageCode: "1abc5"));
        kit.M.As(MaterialsUser);
        return kit;
    }

    public SalesKit As(string email)
    {
        M.As(email);
        return this;
    }

    /// <summary>A released order on C1 / 1001 (cost-type shipments to 1001.01) with WAWF codes.</summary>
    public async Task<SalesOrderDto> OrderAsync(params SaveSalesOrderLineRequest[] lines)
    {
        var order = await Orders.CreateAsync(Request(lines));
        return await Orders.ReleaseAsync(order.Summary.Id);
    }

    public SaveSalesOrderRequest Request(IReadOnlyList<SaveSalesOrderLineRequest> lines, Guid? wbs = null, bool noWbs = false) =>
        new(CustomerId, Day, lines, Contract1, Project1, noWbs ? null : wbs ?? Wbs1, "W56KGU26F0007", "DLA Distribution", "New Cumberland PA", "sw3124", null,
            "W56KGU", "S1103A", "S1103A", GovernmentInspectionPoint.Origin, GovernmentInspectionPoint.Origin);

    public Guid CustomerId { get; private set; }

    /// <summary>Picks everything (or the given quantity of one line), confirms the picks and ships it on <see cref="Day"/>.</summary>
    public async Task<ShipmentDto> ShipAsync(SalesOrderDto order, IReadOnlyList<PickLineRequest>? lines = null, bool final = false)
    {
        var pick = await Shipping.CreatePickListAsync(order.Summary.Id, new CreatePickListRequest(lines));
        foreach (var l in pick.Lines)
        {
            pick = await Shipping.ConfirmPickAsync(l.Id, new ConfirmPickRequest(l.Quantity));
        }

        var shipment = await Shipping.CreateShipmentAsync(new CreateShipmentRequest([pick.Summary.Id]));
        shipment = await Shipping.SaveShipmentAsync(shipment.Summary.Id, new SaveShipmentRequest(Day, "UPS", "BOL-1", "1Z999", final,
            [new SavePackageRequest(1, 12.5m, 10, 10, 8, "1Z999-1")], shipment.Lines.Select(l => new PackLineRequest(l.Id, 1)).ToList()));
        return await Shipping.ConfirmShipmentAsync(shipment.Summary.Id, new ConfirmShipmentRequest(Day));
    }

    public async Task<List<JournalLine>> LinesAsync(string account)
    {
        var id = await AccountIdAsync(account);
        return (await M.EntriesAsync()).SelectMany(e => e.Lines).Where(l => l.AccountId == id).ToList();
    }

    public async Task<Guid> AccountIdAsync(string number)
    {
        await using var db = await M.Kit.Factory.CreateAsync();
        return await db.Set<Account>().Where(a => a.Number == number).Select(a => a.Id).SingleAsync();
    }

    public ValueTask DisposeAsync() => M.DisposeAsync();
}

public sealed class SalesShippingTests
{
    [Fact]
    public async Task Shipping_issues_stock_at_average_cost_to_cost_of_goods_sold_and_ships_in_parts()
    {
        await using var kit = await SalesKit.CreateAsync();
        var box = await kit.M.PartAsync("BOX");
        await kit.M.StockAsync(box.Id, 10, 20m);
        var order = await kit.OrderAsync(new SaveSalesOrderLineRequest(box.Id, 6, 50m, Day, ClinId: kit.FfpClin));
        Assert.Equal(SalesOrderState.Released, order.Summary.Status);
        Assert.False(order.Lines[0].ChargesWbs);

        var first = await kit.ShipAsync(order, [new PickLineRequest(order.Lines[0].Id, 4)]);
        Assert.Equal(ShipmentState.Confirmed, first.Summary.Status);
        Assert.Equal("SHP0001", first.Summary.ShipmentNumber);
        var line = Assert.Single(first.Lines);
        Assert.Equal((4m, 20m, 80m, false), (line.Quantity, line.UnitCost, line.CostAmount, line.ChargedToWbs));
        Assert.Equal(200m, first.Summary.Value);

        order = await kit.Orders.GetAsync(order.Summary.Id);
        Assert.Equal(SalesOrderState.PartlyShipped, order.Summary.Status);
        Assert.Equal((4m, 2m, 200m), (order.Lines[0].QuantityShipped, order.Lines[0].QuantityOpen, order.Summary.ShippedValue));

        // Cost of goods sold (5160) with the project's WBS segments, inventory relieved at the moving average.
        var cogs = Assert.Single(await kit.LinesAsync("5160"));
        Assert.Equal((80m, kit.Project1, kit.Wbs1), (cogs.Debit, cogs.ProjectId, cogs.WbsElementId));
        Assert.Equal(-80m, (await kit.LinesAsync("1410")).Where(l => l.Credit > 0).Sum(l => -l.Credit));
        var transactions = await kit.M.Inventory.ListTransactionsAsync(new TransactionQuery(PartId: box.Id, Type: InventoryMovement.SalesShipment));
        var issue = Assert.Single(transactions);
        Assert.Equal((-4m, -80m, first.Summary.Number), (issue.Quantity, issue.Amount, issue.Reference));
        Assert.Equal(120m, (await kit.M.Parts.GetAsync(box.Id)).Summary.Value);

        // The rest: the order is shipped, and the second DD250 number follows on the contract.
        var second = await kit.ShipAsync(order, final: true);
        Assert.Equal("SHP0002Z", second.Summary.ShipmentNumber);
        order = await kit.Orders.GetAsync(order.Summary.Id);
        Assert.Equal(SalesOrderState.Shipped, order.Summary.Status);
        Assert.Equal(4m, (await kit.M.Parts.GetAsync(box.Id)).Summary.OnHand);
        Assert.Equal(120m, (await kit.LinesAsync("5160")).Sum(l => l.Debit));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Shipping.CreatePickListAsync(order.Summary.Id, new CreatePickListRequest()));
    }

    [Fact]
    public async Task Cost_type_lines_are_issued_to_the_orders_wbs_element_as_direct_material()
    {
        await using var kit = await SalesKit.CreateAsync();
        var spare = await kit.M.PartAsync("SPARE");
        await kit.M.StockAsync(spare.Id, 5, 30m);
        var line = new SaveSalesOrderLineRequest(spare.Id, 2, 45m, Day, ClinId: kit.CostClin);

        var noWbs = await kit.Orders.CreateAsync(kit.Request([line], noWbs: true));
        Assert.True(noWbs.Lines[0].ChargesWbs);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Orders.ReleaseAsync(noWbs.Summary.Id));

        var order = await kit.OrderAsync(line);
        var shipment = await kit.ShipAsync(order);
        Assert.True(shipment.Lines.Single().ChargedToWbs);
        var issue = Assert.Single(await kit.M.Inventory.ListTransactionsAsync(new TransactionQuery(PartId: spare.Id, Type: InventoryMovement.IssueToProject)));
        Assert.Equal(("1001", "1001.01", -60m), (issue.ProjectCode, issue.WbsCode, issue.Amount));
        var direct = Assert.Single(await kit.LinesAsync("5120"));
        Assert.Equal((60m, kit.Wbs1), (direct.Debit, direct.WbsElementId));
        Assert.Empty(await kit.LinesAsync("5160"));
    }

    [Fact]
    public async Task Holds_stop_release_picking_and_shipping_and_a_credit_hold_is_finances_call()
    {
        await using var kit = await SalesKit.CreateAsync();
        var box = await kit.M.PartAsync("BOX");
        await kit.M.StockAsync(box.Id, 5, 10m);
        var draft = await kit.Orders.CreateAsync(kit.Request([new SaveSalesOrderLineRequest(box.Id, 2, 25m, Day)]));

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Orders.SetHoldAsync(draft.Summary.Id, new SetHoldRequest(true, false)));
        await kit.Orders.SetHoldAsync(draft.Summary.Id, new SetHoldRequest(true, false, "Waiting on the customer's drawing change"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Orders.ReleaseAsync(draft.Summary.Id));
        await kit.Orders.SetHoldAsync(draft.Summary.Id, new SetHoldRequest(false, false));
        var order = await kit.Orders.ReleaseAsync(draft.Summary.Id);

        // Materials can't put an order on credit hold; Finance can, and then nothing ships.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Orders.SetHoldAsync(order.Summary.Id, new SetHoldRequest(false, true, "Past due")));
        var pick = await kit.Shipping.CreatePickListAsync(order.Summary.Id, new CreatePickListRequest());
        await kit.Shipping.ConfirmPickAsync(pick.Lines[0].Id, new ConfirmPickRequest(2));
        var shipment = await kit.Shipping.CreateShipmentAsync(new CreateShipmentRequest([pick.Summary.Id]));
        kit.As(Finance);
        order = await kit.Orders.SetHoldAsync(order.Summary.Id, new SetHoldRequest(false, true, "Invoices 90 days past due"));
        Assert.True(order.Summary.OnCreditHold);
        kit.As(MaterialsUser);
        var held = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Shipping.ConfirmShipmentAsync(shipment.Summary.Id, new ConfirmShipmentRequest(Day)));
        Assert.Contains("credit hold", held.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Shipping.CreatePickListAsync(order.Summary.Id, new CreatePickListRequest()));
    }

    [Fact]
    public async Task Picking_respects_contract_inventory_government_furnished_material_and_other_pick_lists()
    {
        await using var kit = await SalesKit.CreateAsync();
        var part = await kit.M.PartAsync("KIT");
        var project2 = kit.M.Kit.Fx.Project2;
        await kit.M.Inventory.AdjustAsync(new AdjustInventoryRequest(part.Id, kit.M.Main.Id, kit.M.A1, 3, Day, "Bought for 2002", Ownership: StockOwnership.Contract, OwnerProjectId: project2, UnitCost: 10m));
        await kit.M.Inventory.AdjustAsync(new AdjustInventoryRequest(part.Id, kit.M.Main.Id, kit.M.A2, 2, Day, "Bought for 1001", Ownership: StockOwnership.Contract, OwnerProjectId: kit.Project1, UnitCost: 10m));
        await kit.M.StockAsync(part.Id, 2, 10m);
        await kit.M.Inventory.ReceiveGovernmentFurnishedAsync(new GovernmentReceiptRequest(part.Id, kit.Project1, 4, Day, kit.M.Main.Id, kit.M.A2, "DD1149-1"));
        var order = await kit.OrderAsync(new SaveSalesOrderLineRequest(part.Id, 10, 15m, Day, ClinId: kit.FfpClin));

        // Pickable for 1001: its contract stock first, then general stock, then its GFM (never 2002's contract stock).
        var stock = await kit.Shipping.ListPickableStockAsync(order.Lines[0].Id);
        Assert.Equal([StockOwnership.Contract, StockOwnership.General, StockOwnership.GovernmentFurnished], stock.Select(s => s.Ownership).ToList());
        Assert.Equal(4m, order.Lines[0].Available); // 1001's contract stock and general stock: GFM isn't counted as free.

        // Picking everything takes contract and general stock only: GFM is picked on purpose.
        var pick = await kit.Shipping.CreatePickListAsync(order.Summary.Id, new CreatePickListRequest());
        Assert.Equal(4m, pick.Lines.Sum(l => l.Quantity));
        Assert.DoesNotContain(pick.Lines, l => l.Ownership == StockOwnership.GovernmentFurnished);
        Assert.Contains(pick.Lines, l => l.Ownership == StockOwnership.Contract && l.Quantity == 2);

        // Another project's contract stock can't be chosen; what's on the open pick list isn't free to pick twice.
        var theirs = (await kit.M.Inventory.ListBalancesAsync(new BalanceQuery(part.Id, Ownership: StockOwnership.Contract, ProjectId: project2))).Single();
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Shipping.CreatePickListAsync(order.Summary.Id, new CreatePickListRequest([new PickLineRequest(order.Lines[0].Id, 1, theirs.Id)])));
        var general = stock.Single(s => s.Ownership == StockOwnership.General);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Shipping.CreatePickListAsync(order.Summary.Id, new CreatePickListRequest([new PickLineRequest(order.Lines[0].Id, 1, general.BalanceId)])));

        // The project's GFM, chosen on purpose, ships at no cost.
        var gfm = stock.Single(s => s.Ownership == StockOwnership.GovernmentFurnished);
        var gfmPick = await kit.Shipping.CreatePickListAsync(order.Summary.Id, new CreatePickListRequest([new PickLineRequest(order.Lines[0].Id, 4, gfm.BalanceId)]));
        await kit.Shipping.ConfirmPickAsync(gfmPick.Lines[0].Id, new ConfirmPickRequest(4));
        foreach (var l in pick.Lines)
        {
            await kit.Shipping.ConfirmPickAsync(l.Id, new ConfirmPickRequest(l.Quantity));
        }

        var shipment = await kit.Shipping.CreateShipmentAsync(new CreateShipmentRequest([pick.Summary.Id, gfmPick.Summary.Id]));
        await kit.Shipping.SaveShipmentAsync(shipment.Summary.Id, new SaveShipmentRequest(Day, null, null, null, false, [new SavePackageRequest(1, 3m)],
            shipment.Lines.Select(l => new PackLineRequest(l.Id, 1)).ToList()));
        shipment = await kit.Shipping.ConfirmShipmentAsync(shipment.Summary.Id, new ConfirmShipmentRequest(Day));
        Assert.Equal(0m, shipment.Lines.Where(l => l.Ownership == StockOwnership.GovernmentFurnished).Sum(l => l.CostAmount));
        Assert.Equal(40m, shipment.Lines.Sum(l => l.CostAmount));
        Assert.True((await kit.Shipping.GetDd250Async(shipment.Summary.Id)).Items.Single().GovernmentFurnished);
        Assert.Equal(3m, (await kit.M.Inventory.ListBalancesAsync(new BalanceQuery(part.Id))).Sum(b => b.Quantity)); // 2002's three
    }

    [Fact]
    public async Task Confirmed_picks_can_swap_serials_in_the_bin_and_a_cancelled_shipment_reopens_its_pick_list()
    {
        await using var kit = await SalesKit.CreateAsync();
        var unit = await kit.M.PartAsync("UNIT", tracking: PartTracking.Serial);
        foreach (var s in new[] { "SN1", "SN2", "SN3" })
        {
            await kit.M.StockAsync(unit.Id, 1, 1_000m, serial: s);
        }

        var order = await kit.OrderAsync(new SaveSalesOrderLineRequest(unit.Id, 1, 6_000m, Day, ClinId: kit.FfpClin));
        Assert.True(order.Lines[0].UidRequired); // Serial tracked and $5,000 or more.
        var pick = await kit.Shipping.CreatePickListAsync(order.Summary.Id, new CreatePickListRequest());
        Assert.Equal("SN1", pick.Lines.Single().Serial);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Shipping.ConfirmPickAsync(pick.Lines[0].Id, new ConfirmPickRequest(1, Serial: "SN9")));
        pick = await kit.Shipping.ConfirmPickAsync(pick.Lines[0].Id, new ConfirmPickRequest(1, Serial: "sn3"));
        Assert.Equal("SN3", pick.Lines[0].Serial);

        var shipment = await kit.Shipping.CreateShipmentAsync(new CreateShipmentRequest([pick.Summary.Id]));
        Assert.Equal(PickListState.Packed, (await kit.Shipping.GetPickListAsync(pick.Summary.Id)).Summary.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Shipping.CreatePickListAsync(order.Summary.Id, new CreatePickListRequest()));
        await kit.Shipping.CancelShipmentAsync(shipment.Summary.Id);
        Assert.Equal(PickListState.Open, (await kit.Shipping.GetPickListAsync(pick.Summary.Id)).Summary.Status);
        Assert.Equal(1m, (await kit.Orders.GetAsync(order.Summary.Id)).Lines[0].QuantityOnPickLists);
    }

    [Fact]
    public async Task The_dd250_has_the_contract_parties_items_serials_and_a_pdf()
    {
        await using var kit = await SalesKit.CreateAsync();
        var unit = await kit.M.PartAsync("UNIT", tracking: PartTracking.Serial);
        var box = await kit.M.PartAsync("BOX");
        foreach (var s in new[] { "SN1", "SN2" })
        {
            await kit.M.StockAsync(unit.Id, 1, 1_000m, serial: s);
        }

        await kit.M.StockAsync(box.Id, 3, 5m);
        var order = await kit.OrderAsync(
            new SaveSalesOrderLineRequest(unit.Id, 2, 7_500m, Day, ClinId: kit.FfpClin, ItemNumber: "0001AA", StockNumber: "6650-01-123-4567"),
            new SaveSalesOrderLineRequest(box.Id, 3, 12m, Day, ClinId: kit.FfpClin, ItemNumber: "0001AB"));
        var shipment = await kit.ShipAsync(order);

        var dd = await kit.Shipping.GetDd250Async(shipment.Summary.Id);
        Assert.Equal(("C1", "W56KGU26F0007", "SHP0001", (DateOnly?)Day, "BOL-1"), (dd.ContractNumber, dd.DeliveryOrder, dd.ShipmentNumber, dd.DateShipped, dd.BillOfLading));
        Assert.Equal(("1ABC5", "S1103A", "DCMA Boston", "HQ0338", "DFAS Columbus", "SW3124"),
            (dd.PrimeContractor.Code, dd.AdministeredBy.Code, dd.AdministeredBy.Name, dd.PaymentBy.Code, dd.PaymentBy.Name, dd.ShippedTo.Code));
        Assert.Equal(["0001AA", "0001AB"], dd.Items.Select(i => i.ItemNumber).ToList());
        var units = dd.Items[0];
        Assert.Equal((2m, 15_000m, true, "6650-01-123-4567"), (units.Quantity, units.Amount, units.UidMarked, units.StockNumber));
        Assert.Equal(["SN1", "SN2"], units.Serials);
        Assert.Equal(15_036m, dd.Total);
        Assert.Equal((1, 12.5m), (dd.PackageCount, dd.TotalWeight));
        Assert.Empty(dd.Warnings);

        var pdf = await kit.Shipping.GetDd250PdfAsync(shipment.Summary.Id);
        Assert.Equal(("application/pdf", "DD250-C1-SHP0001.pdf"), (pdf.ContentType, pdf.FileName));
        var text = Encoding.ASCII.GetString(pdf.Content);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("(MATERIAL INSPECTION AND RECEIVING REPORT)", text);
        Assert.Contains("(SHP0001)", text);
        Assert.Contains("Serial SN1, SN2", text);
        Assert.Contains("(15,036.00)", text);
    }

    [Fact]
    public async Task The_wawf_file_is_validated_and_carries_uid_data_and_the_combo_needs_an_approved_invoice()
    {
        await using var kit = await SalesKit.CreateAsync();
        var unit = await kit.M.PartAsync("UNIT", tracking: PartTracking.Serial);
        await kit.M.StockAsync(unit.Id, 1, 1_000m, serial: "SN-1");
        var bad = await kit.Orders.CreateAsync(kit.Request([new SaveSalesOrderLineRequest(unit.Id, 1, 6_000m, Day, ClinId: kit.FfpClin, ItemNumber: "1")]) with { IssuedByCode = null, ShipToCode = "X" });
        bad = await kit.Orders.ReleaseAsync(bad.Summary.Id);
        var pick = await kit.Shipping.CreatePickListAsync(bad.Summary.Id, new CreatePickListRequest());
        await kit.Shipping.ConfirmPickAsync(pick.Lines[0].Id, new ConfirmPickRequest(1));
        var shipment = await kit.Shipping.CreateShipmentAsync(new CreateShipmentRequest([pick.Summary.Id]));
        Assert.Contains("Confirm the shipment first.", (await kit.Shipping.ValidateWawfAsync(shipment.Summary.Id, WawfDocument.ReceivingReport)).Errors);
        shipment = await kit.Shipping.ConfirmShipmentAsync(shipment.Summary.Id, new ConfirmShipmentRequest(Day));

        var errors = (await kit.Shipping.ValidateWawfAsync(shipment.Summary.Id, WawfDocument.ReceivingReport)).Errors;
        Assert.Contains(errors, e => e.Contains("issued-by DoDAAC"));
        Assert.Contains(errors, e => e.Contains("ship-to DoDAAC"));
        Assert.Contains(errors, e => e.Contains("contract number C1 isn't a 13-character PIID"));
        Assert.Contains(errors, e => e.Contains("Item 1:"));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Shipping.ExportWawfAsync(shipment.Summary.Id, WawfDocument.ReceivingReport));

        // A real PIID and codes: the receiving report is valid; the Combo needs the approved invoice.
        await using (var db = await kit.M.Kit.Factory.CreateAsync())
        {
            (await db.Set<Contract>().SingleAsync(c => c.Id == kit.Contract1)).ContractNumber = "W56KGU-26-C-0001";
            var o = await db.Set<SalesOrder>().Include(x => x.Lines).SingleAsync(x => x.Id == bad.Summary.Id);
            (o.IssuedByCode, o.Lines[0].ItemNumber) = ("W56KGU", "0001");
            (await db.Set<Shipment>().SingleAsync(s => s.Id == shipment.Summary.Id)).ShipToCode = "SW3124";
            await db.SaveChangesAsync();
        }

        Assert.True((await kit.Shipping.ValidateWawfAsync(shipment.Summary.Id, WawfDocument.ReceivingReport)).IsValid);
        var combo = await kit.Shipping.ValidateWawfAsync(shipment.Summary.Id, WawfDocument.Combo);
        Assert.Contains("A Combo or 2-in-1 includes the invoice: create the delivery invoice first.", combo.Errors);

        var file = await kit.Shipping.ExportWawfAsync(shipment.Summary.Id, WawfDocument.ReceivingReport);
        Assert.Equal("WAWF-ReceivingReport-W56KGU26C0001-SHP0001.xml", file.FileName);
        var xml = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(file.Content));
        Assert.Equal("Receiving Report", xml.Root!.Attribute("documentType")!.Value);
        Assert.Equal("W56KGU26C0001", xml.Root.Element("Header")!.Element("ContractNumber")!.Value);
        Assert.Null(xml.Root.Element("Routing")!.Element("PayOfficeDodaac"));
        var uid = xml.Descendants("UidItem").Single();
        Assert.Equal(("D1ABC5UNITSN-1", "2", "1ABC5", "SN-1"), (uid.Element("Uii")!.Value, uid.Element("Construct")!.Value, uid.Element("EnterpriseIdentifier")!.Value, uid.Element("SerialNumber")!.Value));
        Assert.Equal(WawfReportState.Exported, (await kit.Shipping.GetShipmentAsync(shipment.Summary.Id)).Wawf!.Status);

        // The delivery invoice (Finance), approved: now the Combo is valid and carries it.
        kit.As(Finance);
        shipment = await kit.Shipping.CreateInvoiceAsync(shipment.Summary.Id);
        Assert.Contains("Approve invoice", Assert.Single((await kit.Shipping.ValidateWawfAsync(shipment.Summary.Id, WawfDocument.Combo)).Errors));
        await kit.M.Kit.Get<IInvoiceService>().ApproveAsync(shipment.Summary.InvoiceId!.Value, new ApproveInvoiceRequest());
        Assert.True((await kit.Shipping.ValidateWawfAsync(shipment.Summary.Id, WawfDocument.Combo)).IsValid);
        var comboXml = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString((await kit.Shipping.ExportWawfAsync(shipment.Summary.Id, WawfDocument.Combo)).Content));
        Assert.Equal(("HQ0338", shipment.Summary.InvoiceNumber, "6000.00"),
            (comboXml.Root!.Element("Routing")!.Element("PayOfficeDodaac")!.Value, comboXml.Root.Element("Invoice")!.Element("InvoiceNumber")!.Value, comboXml.Root.Element("Invoice")!.Element("Amount")!.Value));

        // Submitted through the (manual) submitter, then accepted.
        kit.As(MaterialsUser);
        shipment = await kit.Shipping.SubmitWawfAsync(shipment.Summary.Id, WawfDocument.Combo);
        Assert.Equal((WawfReportState.Submitted, WawfDocument.Combo), (shipment.Wawf!.Status, shipment.Wawf.DocumentType));
        Assert.Contains("Upload", shipment.Wawf.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Shipping.SubmitWawfAsync(shipment.Summary.Id, WawfDocument.Combo));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Shipping.SetWawfStatusAsync(shipment.Summary.Id, new SetWawfStatusRequest(WawfReportState.Rejected)));
        shipment = await kit.Shipping.SetWawfStatusAsync(shipment.Summary.Id, new SetWawfStatusRequest(WawfReportState.Accepted, "RR-0042"));
        Assert.Equal((WawfReportState.Accepted, "RR-0042"), (shipment.Wawf!.Status, shipment.Wawf.Reference));
    }

    [Fact]
    public async Task Released_orders_are_mrp_demand_until_they_ship()
    {
        await using var kit = await SalesKit.CreateAsync();
        var widget = await kit.M.PartAsync("WIDGET", leadTime: 10);
        await kit.M.StockAsync(widget.Id, 2, 10m);
        var draft = await kit.Orders.CreateAsync(kit.Request([new SaveSalesOrderLineRequest(widget.Id, 5, 20m, Day.AddDays(30), ClinId: kit.FfpClin)]));

        Assert.DoesNotContain((await kit.M.Mrp.RunAsync(new RunMrpRequest(120, Day))).PlannedOrders, p => p.PartId == widget.Id);

        var order = await kit.Orders.ReleaseAsync(draft.Summary.Id);
        var planned = Assert.Single((await kit.M.Mrp.RunAsync(new RunMrpRequest(120, Day))).PlannedOrders, p => p.PartId == widget.Id);
        Assert.Equal((3m, Day.AddDays(30), Day.AddDays(20), "1001"), (planned.Quantity, planned.NeedDate, planned.ReleaseDate, planned.ProjectCode));
        Assert.Contains($"{order.Summary.Number} line 1 5", planned.Pegging);

        // Shipping the two on hand leaves three open: still three short.
        await kit.ShipAsync(order);
        planned = Assert.Single((await kit.M.Mrp.RunAsync(new RunMrpRequest(120, Day))).PlannedOrders, p => p.PartId == widget.Id);
        Assert.Equal(3m, planned.Quantity);

        await kit.Orders.CloseAsync(order.Summary.Id);
        Assert.DoesNotContain((await kit.M.Mrp.RunAsync(new RunMrpRequest(120, Day))).PlannedOrders, p => p.PartId == widget.Id);
    }

    [Fact]
    public async Task A_confirmed_shipment_becomes_a_draft_delivery_invoice_through_billing()
    {
        await using var kit = await SalesKit.CreateAsync();
        var box = await kit.M.PartAsync("BOX");
        var spare = await kit.M.PartAsync("SPARE");
        await kit.M.StockAsync(box.Id, 4, 10m);
        await kit.M.StockAsync(spare.Id, 1, 10m);
        var order = await kit.OrderAsync(
            new SaveSalesOrderLineRequest(box.Id, 4, 250m, Day, ClinId: kit.FfpClin),
            new SaveSalesOrderLineRequest(spare.Id, 1, 99m, Day, ClinId: kit.CostClin));
        var shipment = await kit.ShipAsync(order);
        Assert.True(shipment.CanInvoice);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Shipping.CreateInvoiceAsync(shipment.Summary.Id));
        kit.As(Finance);
        shipment = await kit.Shipping.CreateInvoiceAsync(shipment.Summary.Id);
        Assert.False(shipment.CanInvoice);
        var invoice = await kit.M.Kit.Get<IInvoiceService>().GetAsync(shipment.Summary.InvoiceId!.Value);
        Assert.Equal((InvoiceKind.FixedPriceMilestone, InvoiceState.Draft, 1_000m, Day), (invoice.Summary.Type, invoice.Summary.Status, invoice.Summary.Total, invoice.Summary.InvoiceDate));
        var line = Assert.Single(invoice.Lines); // Only the FFP CLIN: the cost-type spare is billed with cost vouchers.
        Assert.Equal(("0001", 1_000m), (line.ClinNumber, line.Amount));
        Assert.Contains("SHP0001", line.Description);
        var milestone = Assert.Single(await kit.M.Kit.Get<IBillingMilestoneService>().ListAsync(kit.Contract1));
        Assert.Equal((Day, invoice.Summary.Id), (milestone.CompletedDate, milestone.InvoiceId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Shipping.CreateInvoiceAsync(shipment.Summary.Id));
        Assert.Equal(invoice.Summary.Number, (await kit.Shipping.GetDd250Async(shipment.Summary.Id)).InvoiceNumber);
    }

    [Fact]
    public async Task Business_development_reads_orders_finance_ships_and_others_are_refused()
    {
        await using var kit = await SalesKit.CreateAsync();
        var box = await kit.M.PartAsync("BOX");
        await kit.M.StockAsync(box.Id, 2, 10m);
        var order = await kit.OrderAsync(new SaveSalesOrderLineRequest(box.Id, 1, 25m, Day));

        kit.As(SalesKit.Bd);
        Assert.Single(await kit.Orders.ListAsync(new SalesOrderQuery()));
        await kit.Orders.GetAsync(order.Summary.Id);
        await kit.Orders.GetOptionsAsync();
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Orders.CreateAsync(kit.Request([new SaveSalesOrderLineRequest(box.Id, 1, 1m, Day)])));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Orders.CloseAsync(order.Summary.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Shipping.ListPickListsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Shipping.CreatePickListAsync(order.Summary.Id, new CreatePickListRequest()));

        kit.As(Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Orders.ListAsync(new SalesOrderQuery()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Shipping.ListShipmentsAsync(new ShipmentQuery()));

        kit.As(Finance);
        var shipment = await kit.ShipAsync(order);
        Assert.Equal(ShipmentState.Confirmed, shipment.Summary.Status);
        Assert.Single(await kit.Shipping.ListShipmentsAsync(new ShipmentQuery(ShipmentState.Confirmed)));
    }

    [Fact]
    public async Task Clin_lines_take_the_clin_number_and_price_and_drafts_alone_change_lines()
    {
        await using var kit = await SalesKit.CreateAsync();
        var unit = await kit.M.PartAsync("UNIT", tracking: PartTracking.Serial);
        var draft = await kit.Orders.CreateAsync(kit.Request([]));
        var order = await kit.Orders.AddLinesFromClinsAsync(draft.Summary.Id, new AddClinLinesRequest([new ClinLineRequest(kit.FfpClin, unit.Id, 4)]));
        var line = Assert.Single(order.Lines);
        Assert.Equal(("0001", "Sensor units", 12_500m, 50_000m, true), (line.ItemNumber, line.Description, line.UnitPrice, line.Amount, line.UidRequired));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Orders.AddLinesFromClinsAsync(draft.Summary.Id, new AddClinLinesRequest([new ClinLineRequest(kit.FfpClin, unit.Id, 1.5m)])));

        await kit.Orders.ReleaseAsync(draft.Summary.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Orders.AddLinesFromClinsAsync(draft.Summary.Id, new AddClinLinesRequest([new ClinLineRequest(kit.FfpClin, unit.Id, 1)])));
        var updated = await kit.Orders.UpdateAsync(draft.Summary.Id, kit.Request([]) with { CustomerReference = "NEW-REF" });
        Assert.Equal(("NEW-REF", 1), (updated.Summary.CustomerReference, updated.Lines.Count)); // Lines kept once released.
        await kit.Orders.CancelAsync(draft.Summary.Id, new CancelSalesOrderRequest("Customer withdrew the order"));
        Assert.Empty(await kit.Orders.ListAsync(new SalesOrderQuery(OpenOnly: true)));
    }
}
