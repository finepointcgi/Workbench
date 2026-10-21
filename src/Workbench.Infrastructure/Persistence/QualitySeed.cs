using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.Materials;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Application.Quality;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Quality;
using Workbench.Domain.Identity;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Domain.Quality;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for quality and engineering change, on top of <see cref="MaterialsSeed"/>, entered through
/// the real services: Morgan Lee (morgan.quality@example.com, E1080, the Materials role) inspects; the machined housing
/// HSG-220 needs receiving inspection (AQL sample, certificate of conformance); a second shipment of six failed (bore
/// oversize), went to nonconforming stock on an NCR, and was returned to Digi-Parts after Mia approved the disposition
/// (debit memo open, supplier corrective action open); the sensor head SNS-210 has a final inspection plan and an approved,
/// effective ECO adding a thermal pad (BOM rev B), which flagged its open work order for review. Runs once.
/// </summary>
internal static class QualitySeed
{
    public const string MorganEmail = "morgan.quality@example.com";

    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<InspectionPlan>().AnyAsync(cancellationToken)
            || await db.Set<Part>().FirstOrDefaultAsync(p => p.Number == "HSG-220", cancellationToken) is not { } housing
            || await db.Set<Part>().FirstOrDefaultAsync(p => p.Number == "SNS-210", cancellationToken) is not { } head
            || await db.Set<Employee>().FirstOrDefaultAsync(e => e.Email == "pm@example.com", cancellationToken) is not { } pat
            || await db.Set<Vendor>().FirstOrDefaultAsync(v => v.Code == "DIGI", cancellationToken) is not { } digi)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var open = await db.Set<FiscalPeriod>().AsNoTracking().AnyAsync(p => p.Start <= today && p.End >= today && p.Status == PeriodStatus.Open, cancellationToken);

        if (!await db.Set<AppUser>().AnyAsync(u => u.Email == MorganEmail, cancellationToken))
        {
            await RoleStore.AddUserAsync(db, new AppUser { Email = MorganEmail, DisplayName = "Morgan Lee", CreatedAtUtc = DateTime.UtcNow }, OrgRoles.Materials, cancellationToken);
        }

        var morgan = new Employee
        {
            EmployeeNumber = "E1080", Email = MorganEmail, DisplayName = "Morgan Lee", OrgUnitId = pat.OrgUnitId, SupervisorId = pat.Id,
            DefaultLaborCategoryId = (await db.Set<LaborCategory>().FirstOrDefaultAsync(c => c.Code == "MFG2", cancellationToken))?.Id, HireDate = pat.HireDate,
        };
        db.Add(morgan);
        db.Add(new EmployeeCostRate { EmployeeId = morgan.Id, EffectiveFrom = morgan.HireDate, HourlyCost = 61m });
        await db.SaveChangesAsync(cancellationToken);

        var user = new SeedUser { Email = MaterialsSeed.MiaEmail };
        var kit = new Kit(services, user);

        // Plans: receiving inspection of the machined housing, final inspection of the sensor head.
        user.Email = MorganEmail;
        var plan = await kit.Inspections.SavePlanAsync(null, new SaveInspectionPlanRequest("HSG-220-RCV", "Machined housing receiving inspection", housing.Id, InspectionType.Receiving,
        [
            new SaveCharacteristicRequest("Bore diameter", CharacteristicType.Measured, 22.00m, 21.95m, 22.05m, "mm", IsCritical: true),
            new SaveCharacteristicRequest("Overall length", CharacteristicType.Measured, 120.0m, 119.8m, 120.2m, "mm"),
            new SaveCharacteristicRequest("M4 threads gauge go / no-go"),
            new SaveCharacteristicRequest("Anodize finish free of scratches and pits"),
        ], Sampling.Aql, AcceptNumber: 0, Aql: 1.0m, RequiredDocuments: ["Certificate of conformance"],
            Instructions: "Measure the bore with the 22 mm bore gauge at both ends. Check the cert of conformance names the 6061-T6 heat lot."), cancellationToken);
        await kit.Inspections.SavePlanAsync(null, new SaveInspectionPlanRequest("SNS-210-FI", "Sensor head final test", head.Id, InspectionType.Final,
        [
            new SaveCharacteristicRequest("Supply current at 24 V", CharacteristicType.Measured, 0.35m, null, 0.45m, "A"),
            new SaveCharacteristicRequest("Optical alignment within the fixture window", IsCritical: true),
            new SaveCharacteristicRequest("Label and serial plate present"),
        ], Instructions: "Run the thermal and optical test script; attach the printout."), cancellationToken);

        // The engineering change: a thermal pad on the sensor head (BOM rev B), approved and effective today.
        user.Email = MaterialsSeed.MiaEmail;
        var main = (await kit.Parts.ListWarehousesAsync(cancellationToken)).FirstOrDefault(w => w.Code == "MAIN");
        var pad = await kit.Parts.CreateAsync(new SavePartRequest("TPAD-10", null, "Thermal interface pad, 10 × 10 mm", "EA", PartSourceKind.Buy, PartTracking.None,
            PartCostMethod.Average, 1.20m, digi.Id, 14, DefaultWarehouseId: main?.Id, DefaultBinId: main?.Bins.FirstOrDefault(b => b.Code == "A-01")?.Id), cancellationToken);
        var revA = (await kit.Manufacturing.ListBomsAsync(head.Id, cancellationToken)).FirstOrDefault(b => b.IsActive);
        if (revA is not null)
        {
            var a = await kit.Manufacturing.GetBomAsync(revA.Id, cancellationToken);
            var b = await kit.Manufacturing.CopyBomAsync(a.Summary.Id, "B", cancellationToken);
            b = await kit.Manufacturing.SaveBomAsync(b.Summary.Id, new SaveBomRequest(head.Id, "B",
                a.Components.Select(c => new SaveBomComponentRequest(c.ComponentPartId, c.QuantityPer, c.ScrapPercent, c.EffectiveFrom, c.EffectiveTo, c.ReferenceDesignators, c.OperationSequence))
                    .Append(new SaveBomComponentRequest(pad.Summary.Id, 1, ReferenceDesignators: "Between the PCB and the housing")).ToList(),
                "Rev B: thermal pad (ECO)"), cancellationToken);
            var eco = await kit.Changes.SaveAsync(null, new SaveEcoRequest("Add thermal pad to the sensor head", EcoReason.DesignImprovement,
                "First article thermal test ran 9 °C over limit at 50 °C ambient. A 10 × 10 mm thermal pad between the PCB and the housing brings it 6 °C under.",
                [new SaveEcoItemRequest(head.Id, b.Summary.Id, Note: "Rev A → B")], Effectivity.Date, today, null, EcoStockDisposition.UseUp,
                EcoWorkOrderDisposition.UpdateToNewRevision, 240m), cancellationToken);
            await kit.Changes.SubmitAsync(eco.Summary.Id, cancellationToken);
            user.Email = MorganEmail;
            await kit.Changes.ApproveAsync(eco.Summary.Id, new DecideEcoRequest("Thermal margin verified on the bench"), cancellationToken);
        }

        if (!open || pat.OrgUnitId is null)
        {
            return; // The failed receipt needs an open fiscal period.
        }

        // A second shipment of housings, received into inspection hold.
        user.Email = "pm@example.com";
        var clearing = await kit.ClearingAccountAsync(cancellationToken);
        var po = await kit.Orders.CreateAsync(new SavePurchaseOrderRequest(digi.Id, today.AddDays(-8),
            [new SavePurchaseOrderLineRequest("HSG-220 Machined aluminum sensor housing", 6, 95m, AccountId: clearing, OrgUnitId: pat.OrgUnitId, NeededBy: today.AddDays(-2))],
            Terms: "Net 30, FOB destination", ShipTo: "Workbench HQ receiving dock", Receivers: [MaterialsSeed.MiaEmail]), cancellationToken);
        po = await kit.Orders.IssueAsync(po.Order.Id, cancellationToken);
        user.Email = MaterialsSeed.MiaEmail;
        await kit.Inventory.LinkPurchaseOrderLineAsync(new LinkPurchaseLineRequest(po.Lines[0].Id, housing.Id), cancellationToken);
        await kit.Inventory.ReceivePurchaseOrderAsync(new ReceiveIntoInventoryRequest(po.Order.Id, today.AddDays(-3), [new ReceiveIntoInventoryLine(po.Lines[0].Id, 6)], "Shipment 2"),
            cancellationToken);

        // Morgan inspects the sample of two: one bore is oversize (critical), so the lot is rejected.
        user.Email = MorganEmail;
        var inspection = (await kit.Inspections.ListAsync(new InspectionQuery(PartId: housing.Id), cancellationToken)).First(i => i.Status == InspectionState.Pending);
        var results = plan.Characteristics.Select(c => c.Name switch
        {
            "Bore diameter" => new RecordResultRequest(c.Id, 22.11m, Note: "Unit 2 measured 22.11 mm (both ends)"),
            "Overall length" => new RecordResultRequest(c.Id, 120.05m),
            _ => new RecordResultRequest(c.Id, Passed: true),
        }).ToList();
        var done = await kit.Inspections.CompleteAsync(inspection.Id, new CompleteInspectionRequest(today.AddDays(-2), results, QuantityAccepted: 0, DefectsFound: 1,
            DefectCodes: ["DIM", "DOC"], NonconformanceDescription: "Sample unit 2 bore 22.11 mm (limit 22.05). Certificate of conformance missing the heat lot. Lot of 6 rejected.",
            Notes: "Rejected per AQL 1.0, accept 0."), cancellationToken);
        var ncrId = done.Summary.NonconformanceId!.Value;

        // The material review board: return to vendor, proposed by Morgan, approved by Mia.
        await kit.Ncrs.ProposeDispositionAsync(ncrId, new ProposeDispositionRequest(NcrDisposition.ReturnToVendor, "Out of tolerance on a critical feature; vendor to replace.",
            ReturnAuthorization: "DPE-RMA-4471", Date: today.AddDays(-1)), cancellationToken);
        user.Email = MaterialsSeed.MiaEmail;
        await kit.Ncrs.ApproveAsync(ncrId, new DecideNcrRequest("Agreed: return for replacement."), cancellationToken);

        // A supplier corrective action for Digi-Parts.
        user.Email = MorganEmail;
        await kit.Cars.SaveAsync(null, new SaveCarRequest("HSG-220 bore oversize from Digi-Parts", "Shipment 2 of HSG-220: bore 22.11 mm against a 22.05 mm limit, and the cert of conformance had no heat lot.",
            MorganEmail, today.AddDays(28), ncrId, RootCauseCategory: RootCause.Supplier,
            RootCause: "Vendor reports a worn boring bar on their second-shift machine (to be confirmed by their 8D)."), cancellationToken);
    }

    /// <summary>The quality, materials and purchasing services built by hand (no permission decorators), running as <see cref="SeedUser"/>.</summary>
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
            var approvals = new ApprovalEngine(clock);
            var options = Options.Create(new QualityOptions());
            _ledger = new MaterialsLedger(gl, Options.Create(new MaterialsOptions()));
            Orders = new InventoryReceivingPurchaseOrderService(new PurchaseOrderService(_factory, audit, access, notifications, clock), _factory, _ledger, context);
            Parts = new PartService(_factory, audit, clock);
            Inventory = new InventoryService(_factory, _ledger, audit, Orders, context);
            Manufacturing = new ManufacturingService(_factory, _ledger, audit, clock);
            Inspections = new InspectionService(_factory, _ledger, audit, clock);
            Ncrs = new NonconformanceService(_factory, _ledger, audit, notifications, options, clock, approvals);
            Cars = new CorrectiveActionService(_factory, audit, notifications, clock);
            Changes = new EngineeringChangeService(_factory, audit, notifications, options, clock, approvals);
        }

        public IPurchaseOrderService Orders { get; }
        public PartService Parts { get; }
        public InventoryService Inventory { get; }
        public ManufacturingService Manufacturing { get; }
        public InspectionService Inspections { get; }
        public NonconformanceService Ncrs { get; }
        public CorrectiveActionService Cars { get; }
        public EngineeringChangeService Changes { get; }

        public async Task<Guid> ClearingAccountAsync(CancellationToken ct)
        {
            await using var db = await _factory.CreateAsync(ct);
            return await _ledger.ReceivedNotInvoicedAccountAsync(db, ct) ?? throw new InvalidOperationException("No chart of accounts.");
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
