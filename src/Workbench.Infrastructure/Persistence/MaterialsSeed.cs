using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Identity;
using Workbench.Application.Materials;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Identity;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for materials and manufacturing, entered through the real services: Mia Torres
/// (mia.materials@example.com, the Materials role), warehouse MAIN, work centers, hardware project 3001 "Rugged sensor kit"
/// on the 1001 contract, a three-level BOM (kit → sensor head → PCB → purchased parts), a purchase order received into
/// stock (lots, a purchase price variance, one line still open), government-furnished IR detectors for 3001, a closed PCB
/// build, a sensor-head build in progress and the kit's project work order partly kitted. Runs once, after the other seeds.
/// </summary>
internal static class MaterialsSeed
{
    public const string MiaEmail = "mia.materials@example.com";

    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<Part>().AnyAsync(cancellationToken)
            || await db.Set<Employee>().FirstOrDefaultAsync(e => e.Email == "pm@example.com", cancellationToken) is not { } pat
            || await db.Set<Project>().FirstOrDefaultAsync(p => p.Code == "1001", cancellationToken) is not { } p1001)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var open = await db.Set<FiscalPeriod>().AsNoTracking()
            .AnyAsync(p => p.Start <= today && p.End >= today && p.Status == PeriodStatus.Open, cancellationToken);

        // People and the hardware project.
        var category = await db.Set<LaborCategory>().FirstOrDefaultAsync(c => c.Code == "SE2", cancellationToken);
        var technician = new LaborCategory { Code = "MFG2", Name = "Manufacturing Technician II" };
        db.Add(technician);
        if (!await db.Set<AppUser>().AnyAsync(u => u.Email == MiaEmail, cancellationToken))
        {
            await RoleStore.AddUserAsync(db, new AppUser { Email = MiaEmail, DisplayName = "Mia Torres", CreatedAtUtc = DateTime.UtcNow }, OrgRoles.Materials, cancellationToken);
        }

        var mia = new Employee
        {
            EmployeeNumber = "E1008", Email = MiaEmail, DisplayName = "Mia Torres", OrgUnitId = pat.OrgUnitId, SupervisorId = pat.Id,
            DefaultLaborCategoryId = technician.Id, HireDate = pat.HireDate,
        };
        db.Add(mia);
        db.Add(new EmployeeCostRate { EmployeeId = mia.Id, EffectiveFrom = mia.HireDate, HourlyCost = 58m });
        var project = new Project { Code = "3001", Name = "Rugged Sensor Kit (hardware)", ContractId = p1001.ContractId, ProjectManagerId = pat.Id, Start = today.AddDays(-30), Finish = today.AddDays(180) };
        db.Add(project);
        var production = new WbsElement { ProjectId = project.Id, Code = "3001.01", Name = "Kit production", Order = 0 };
        var spares = new WbsElement { ProjectId = project.Id, Code = "3001.02", Name = "Spares and repair", Order = 1 };
        db.AddRange(production, spares);
        db.AddRange(new ChargeAssignment { WbsElementId = production.Id, EmployeeId = mia.Id }, new ChargeAssignment { WbsElementId = spares.Id, EmployeeId = mia.Id });
        var digi = new Vendor { Code = "DIGI", Name = "Digi-Parts Electronics" };
        db.Add(digi);
        await db.SaveChangesAsync(cancellationToken);

        var user = new SeedUser { Email = MiaEmail };
        var kit = new Kit(services, user);

        // Locations and work centers.
        var main = await kit.Parts.SaveWarehouseAsync(null, new SaveWarehouseRequest("MAIN", "Main stockroom", "1200 Wilson Blvd, Arlington VA"), cancellationToken);
        foreach (var (code, description) in new[] { ("A-01", "Electronics shelving"), ("A-02", "Mechanical parts"), ("FG-01", "Finished assemblies"), ("CAGE", "Government property cage") })
        {
            main = await kit.Parts.SaveBinAsync(main.Id, null, new SaveBinRequest(code, description), cancellationToken);
        }

        Guid Bin(string code) => main.Bins.Single(b => b.Code == code).Id;
        var asm = await kit.Manufacturing.SaveWorkCenterAsync(null, new SaveWorkCenterRequest("ASM", "Electronics assembly", technician.Id), cancellationToken);
        var test = await kit.Manufacturing.SaveWorkCenterAsync(null, new SaveWorkCenterRequest("TEST", "Test and inspection", category?.Id ?? technician.Id), cancellationToken);

        // Parts: three levels under the kit.
        async Task<PartDto> PartAsync(string number, string description, PartSourceKind source, PartCostMethod method, decimal standard, PartTracking tracking,
            int lead, string bin, Guid? vendor = null, decimal safety = 0) =>
            await kit.Parts.CreateAsync(new SavePartRequest(number, source == PartSourceKind.Make ? "A" : null, description, "EA", source, tracking, method, standard,
                vendor, lead, safety, DefaultBinId: Bin(bin)), cancellationToken);

        var mcu = await PartAsync("MCU-32", "32-bit microcontroller, industrial temp", PartSourceKind.Buy, PartCostMethod.Standard, 6.40m, PartTracking.Lot, 21, "A-01", digi.Id, safety: 10);
        var bare = await PartAsync("PCB-BARE-400", "Bare sensor board, 6 layer", PartSourceKind.Buy, PartCostMethod.Standard, 14m, PartTracking.None, 15, "A-01", digi.Id);
        var passives = await PartAsync("RES-KIT", "Passive component kit (resistors, capacitors)", PartSourceKind.Buy, PartCostMethod.Average, 3.10m, PartTracking.None, 10, "A-01", digi.Id);
        var connector = await PartAsync("CONN-12", "12-pin circular connector, MIL-DTL-38999", PartSourceKind.Buy, PartCostMethod.Average, 2.25m, PartTracking.None, 30, "A-01", digi.Id);
        var housing = await PartAsync("HSG-220", "Machined aluminum sensor housing", PartSourceKind.Buy, PartCostMethod.Average, 95m, PartTracking.None, 25, "A-02", digi.Id);
        var lens = await PartAsync("LENS-050", "IR lens module", PartSourceKind.Buy, PartCostMethod.Standard, 210m, PartTracking.Lot, 35, "A-02", digi.Id);
        var cable = await PartAsync("CAB-300", "Field cable harness, 5 m", PartSourceKind.Buy, PartCostMethod.Average, 85m, PartTracking.None, 14, "A-02", digi.Id, safety: 2);
        var transit = await PartAsync("CASE-120", "Transit case with foam insert", PartSourceKind.Buy, PartCostMethod.Average, 310m, PartTracking.None, 20, "A-02", digi.Id);
        var detector = await PartAsync("DET-900", "IR detector (government-furnished)", PartSourceKind.Buy, PartCostMethod.Average, 0m, PartTracking.Serial, 0, "CAGE");
        var pcb = await PartAsync("PCB-400", "Sensor PCB assembly", PartSourceKind.Make, PartCostMethod.Standard, 46m, PartTracking.Lot, 7, "FG-01");
        var head = await PartAsync("SNS-210", "Sensor head assembly", PartSourceKind.Make, PartCostMethod.Standard, 420m, PartTracking.Serial, 10, "FG-01");
        var sensorKit = await PartAsync("RSK-100", "Rugged sensor kit", PartSourceKind.Make, PartCostMethod.Average, 1_400m, PartTracking.Serial, 5, "FG-01");

        await kit.Manufacturing.SaveBomAsync(null, new SaveBomRequest(pcb.Summary.Id, "A",
        [
            new SaveBomComponentRequest(bare.Summary.Id, 1), new SaveBomComponentRequest(mcu.Summary.Id, 1, ReferenceDesignators: "U1"),
            new SaveBomComponentRequest(passives.Summary.Id, 1, ScrapPercent: 2), new SaveBomComponentRequest(connector.Summary.Id, 2, ReferenceDesignators: "J1, J2"),
        ]), cancellationToken);
        await kit.Manufacturing.SaveBomAsync(null, new SaveBomRequest(head.Summary.Id, "A",
            [new SaveBomComponentRequest(pcb.Summary.Id, 1), new SaveBomComponentRequest(housing.Summary.Id, 1), new SaveBomComponentRequest(lens.Summary.Id, 1)]), cancellationToken);
        await kit.Manufacturing.SaveBomAsync(null, new SaveBomRequest(sensorKit.Summary.Id, "A",
        [
            new SaveBomComponentRequest(head.Summary.Id, 2), new SaveBomComponentRequest(detector.Summary.Id, 2, ReferenceDesignators: "GFM: installed at final integration"),
            new SaveBomComponentRequest(cable.Summary.Id, 2), new SaveBomComponentRequest(transit.Summary.Id, 1),
        ]), cancellationToken);
        await kit.Manufacturing.SaveRoutingAsync(null, new SaveRoutingRequest(pcb.Summary.Id, "A", [new SaveRoutingOperationRequest(10, "SMT assembly and reflow", asm.Id, 0.5m, 0.4m, technician.Id)]), cancellationToken);
        await kit.Manufacturing.SaveRoutingAsync(null, new SaveRoutingRequest(head.Summary.Id, "A",
            [new SaveRoutingOperationRequest(10, "Assemble sensor head", asm.Id, 0.5m, 1.5m, technician.Id), new SaveRoutingOperationRequest(20, "Thermal and optical test", test.Id, 0.25m, 0.5m)]), cancellationToken);
        await kit.Manufacturing.SaveRoutingAsync(null, new SaveRoutingRequest(sensorKit.Summary.Id, "A",
            [new SaveRoutingOperationRequest(10, "Final integration", asm.Id, 0, 2m, technician.Id), new SaveRoutingOperationRequest(20, "Acceptance test and pack", test.Id, 0, 1m)]), cancellationToken);
        await kit.Parts.AddRequirementAsync(new SaveMaterialRequirementRequest(spares.Id, head.Summary.Id, 2, today.AddDays(45), "Depot spares, CDRL A004"), cancellationToken);
        await kit.Parts.AddForecastAsync(new SaveForecastRequest(cable.Summary.Id, today.AddDays(60), 6, "Field replacement cables"), cancellationToken);

        if (!open || pat.OrgUnitId is null)
        {
            return; // Master data only: stock movements need an open fiscal period.
        }

        var org = pat.OrgUnitId.Value;

        // Buy and receive (lots, a price variance on the microcontrollers; the transit cases are still on order).
        user.Email = "pm@example.com";
        var lines = new (PartDto Part, decimal Quantity, decimal Price)[]
        {
            (mcu, 40, 6.85m), (bare, 30, 14m), (passives, 30, 3.10m), (connector, 60, 2.25m), (housing, 8, 95m), (lens, 8, 205m), (cable, 6, 85m), (transit, 2, 310m),
        };
        var clearing = await kit.ClearingAccountAsync(cancellationToken);
        var po = await kit.Orders.CreateAsync(new SavePurchaseOrderRequest(digi.Id, today.AddDays(-20),
            lines.Select(l => new SavePurchaseOrderLineRequest($"{l.Part.Summary.Number} {l.Part.Summary.Description}", l.Quantity, l.Price,
                AccountId: clearing, OrgUnitId: org, NeededBy: today.AddDays(-5))).ToList(),
            Terms: "Net 30, FOB destination", ShipTo: "Workbench HQ receiving dock", Receivers: [MiaEmail]), cancellationToken);
        po = await kit.Orders.IssueAsync(po.Order.Id, cancellationToken);
        user.Email = MiaEmail;
        for (var i = 0; i < lines.Length; i++)
        {
            await kit.Inventory.LinkPurchaseOrderLineAsync(new LinkPurchaseLineRequest(po.Lines[i].Id, lines[i].Part.Summary.Id), cancellationToken);
        }

        var received = today.AddDays(-12);
        await kit.Inventory.ReceivePurchaseOrderAsync(new ReceiveIntoInventoryRequest(po.Order.Id, received,
            lines.Take(7).Select((l, i) => new ReceiveIntoInventoryLine(po.Lines[i].Id, l.Quantity,
                Lot: l.Part.Summary.Number == "MCU-32" ? "MCU-2609A" : l.Part.Summary.Number == "LENS-050" ? "LNS-0926" : null)).ToList(), "Shipment 1 of 2"), cancellationToken);

        // Government-furnished IR detectors for 3001 (never costed).
        await kit.Inventory.ReceiveGovernmentFurnishedAsync(new GovernmentReceiptRequest(detector.Summary.Id, project.Id, 4, today.AddDays(-10), main.Id, Bin("CAGE"),
            "DD1149-W91CRB-26-0117", Serials: ["DET-0001", "DET-0002", "DET-0003", "DET-0004"]), cancellationToken);

        // PCB build to stock: kitted, labor reported, completed and closed.
        var pcbWo = await kit.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(pcb.Summary.Id, 8, today.AddDays(-9), today.AddDays(-6)), cancellationToken);
        pcbWo = await kit.Manufacturing.ReleaseAsync(pcbWo.Summary.Id, cancellationToken);
        pcbWo = await kit.Manufacturing.IssueComponentsAsync(pcbWo.Summary.Id, new IssueComponentsRequest(today.AddDays(-9)), cancellationToken);
        await kit.Manufacturing.ReportLaborAsync(pcbWo.Summary.Id, new ReportLaborRequest(pcbWo.Operations[0].Id, today.AddDays(-8), 3.75m, 8, mia.Id), cancellationToken);
        await kit.Manufacturing.CompleteAsync(pcbWo.Summary.Id, new CompleteWorkOrderRequest(8, today.AddDays(-7), Lot: "PCB-2610-01"), cancellationToken);
        await kit.Manufacturing.CloseAsync(pcbWo.Summary.Id, new CloseWorkOrderRequest(today.AddDays(-7)), cancellationToken);

        // Sensor head build to stock: in progress, two of four done.
        var headWo = await kit.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(head.Summary.Id, 4, today.AddDays(-6), today.AddDays(4),
            Notes: "First article build for 3001"), cancellationToken);
        headWo = await kit.Manufacturing.ReleaseAsync(headWo.Summary.Id, cancellationToken);
        headWo = await kit.Manufacturing.IssueComponentsAsync(headWo.Summary.Id, new IssueComponentsRequest(today.AddDays(-6)), cancellationToken);
        await kit.Manufacturing.ReportLaborAsync(headWo.Summary.Id, new ReportLaborRequest(headWo.Operations[0].Id, today.AddDays(-5), 4m, 2, mia.Id), cancellationToken);
        await kit.Manufacturing.ReportLaborAsync(headWo.Summary.Id, new ReportLaborRequest(headWo.Operations[1].Id, today.AddDays(-4), 1.5m, 2, mia.Id), cancellationToken);
        await kit.Manufacturing.CompleteAsync(headWo.Summary.Id, new CompleteWorkOrderRequest(2, today.AddDays(-4), Serials: ["SH-0001", "SH-0002"]), cancellationToken);

        // The kit's project work order: sensor heads, GFM detectors and cables kitted; the transit case is still on order.
        var kitWo = await kit.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(sensorKit.Summary.Id, 1, today.AddDays(-3), today.AddDays(7), WbsElementId: production.Id,
            Notes: "Unit 1 for first article inspection"), cancellationToken);
        kitWo = await kit.Manufacturing.ReleaseAsync(kitWo.Summary.Id, cancellationToken);
        await kit.Manufacturing.IssueComponentsAsync(kitWo.Summary.Id, new IssueComponentsRequest(today.AddDays(-3),
            kitWo.Components.Where(c => c.PartId != transit.Summary.Id).Select(c => new IssueComponentLine(c.Id, c.QuantityRequired)).ToList()), cancellationToken);
        await kit.Manufacturing.ReportLaborAsync(kitWo.Summary.Id, new ReportLaborRequest(kitWo.Operations[0].Id, today.AddDays(-2), 1.5m, 0, mia.Id), cancellationToken);

        // MRP has something to say: the open requirement, the forecast and the short transit case.
        await kit.Mrp.RunAsync(new RunMrpRequest(120, today), cancellationToken);
    }

    /// <summary>The materials and purchasing services built by hand (no permission decorators), running as <see cref="SeedUser"/>.</summary>
    private sealed class Kit
    {
        private readonly IWorkbenchDbContextFactory _factory;
        private readonly MaterialsLedger _ledger;

        public Kit(IServiceProvider services, SeedUser user)
        {
            _factory = (IWorkbenchDbContextFactory)services.GetService(typeof(IWorkbenchDbContextFactory))!;
            var clock = TimeProvider.System;
            var audit = new AuditRecorder(user, clock);
            var gl = new GeneralLedger(audit, clock);
            var access = new AccessControl(_factory, user);
            var notifications = new NoNotifications();
            var context = new InventoryReceiptContext();
            _ledger = new MaterialsLedger(gl, Options.Create(new MaterialsOptions()));
            Orders = new InventoryReceivingPurchaseOrderService(new PurchaseOrderService(_factory, audit, access, notifications, clock), _factory, _ledger, context);
            var requisitions = new RequisitionService(_factory, audit, access, new RequisitionAccess(_factory, access), notifications, Options.Create(new PurchasingOptions()), clock);
            Parts = new PartService(_factory, audit, clock);
            Inventory = new InventoryService(_factory, _ledger, audit, Orders, context);
            Manufacturing = new ManufacturingService(_factory, _ledger, audit, clock);
            Mrp = new MrpService(_factory, _ledger, requisitions, audit, clock);
        }

        public IPurchaseOrderService Orders { get; }
        public PartService Parts { get; }
        public InventoryService Inventory { get; }
        public ManufacturingService Manufacturing { get; }
        public MrpService Mrp { get; }

        private Guid? _clearing;

        public async Task<Guid> ClearingAccountAsync(CancellationToken ct)
        {
            if (_clearing is null)
            {
                await using var db = await _factory.CreateAsync(ct);
                _clearing = await _ledger.ReceivedNotInvoicedAccountAsync(db, ct) ?? throw new InvalidOperationException("No chart of accounts.");
            }

            return _clearing.Value;
        }
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email { get; set; } = "";
    }

    private sealed class NoNotifications : INotificationPublisher
    {
        public Task<bool> PublishAsync(string recipientEmail, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> PublishAsync(IEnumerable<string?> recipientEmails, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
