using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Materials;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Materials;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for kitting, backflushing, landed cost, physical inventory and capacity scheduling, on top of
/// <see cref="MaterialsSeed"/> and through the real services: the passive component kit backflushes from A-01 on a PCB
/// build completed halfway; freight on the first receipt is allocated by value and posted; the government property cage is
/// frozen for a physical inventory with one tag counted; three sensor head builds next week are scheduled within capacity,
/// then pinned by the planner onto the same Monday so the assembly bench is overloaded that week. Runs once.
/// </summary>
internal static class ShopFloorSeed
{
    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<BackflushSetting>().AnyAsync(cancellationToken) || await db.Set<CapacityScheduleRun>().AnyAsync(cancellationToken)
            || await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Number == "PCB-400", cancellationToken) is not { } pcb)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var open = await db.Set<FiscalPeriod>().AsNoTracking().AnyAsync(p => p.Start <= today && p.End >= today && p.Status == PeriodStatus.Open, cancellationToken);
        var parts = await db.Set<Part>().AsNoTracking().ToDictionaryAsync(p => p.Number, cancellationToken);
        var main = await db.Set<Warehouse>().AsNoTracking().FirstAsync(w => w.Code == "MAIN", cancellationToken);
        var bins = await db.Set<Bin>().AsNoTracking().Where(b => b.WarehouseId == main.Id).ToDictionaryAsync(b => b.Code, b => b.Id, cancellationToken);
        var mia = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Email == MaterialsSeed.MiaEmail, cancellationToken);
        var kit = new Kit(services, new SeedUser { Email = MaterialsSeed.MiaEmail });

        // Backflush: the passive component kit is issued from A-01 when PCB builds complete; nobody kits it by hand.
        await kit.Backflush.SaveSettingAsync(new SaveBackflushSettingRequest(parts["RES-KIT"].Id, true, BinId: bins["A-01"]), cancellationToken);
        await kit.Parts.SaveBinAsync(main.Id, null, new SaveBinRequest("KIT-01", "Kit staging (held for kits)"), cancellationToken);

        // Work center calendars: assembly works a normal week with a plant shutdown next Friday; test runs two stations.
        var centers = await db.Set<WorkCenter>().AsNoTracking().ToDictionaryAsync(c => c.Code, cancellationToken);
        var toMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        var nextMonday = today.AddDays(toMonday == 0 ? 7 : toMonday);
        if (centers.TryGetValue("ASM", out var asm))
        {
            await kit.Capacity.SaveCalendarAsync(asm.Id, new SaveWorkCenterCalendarRequest("YYYYYNN", 8, 1), cancellationToken);
            await kit.Capacity.SaveExceptionAsync(asm.Id, new SaveCapacityExceptionRequest(nextMonday.AddDays(4), 0, "Plant shutdown"), cancellationToken);
        }

        if (centers.TryGetValue("TEST", out var test))
        {
            await kit.Capacity.SaveCalendarAsync(test.Id, new SaveWorkCenterCalendarRequest("YYYYYNN", 8, 2), cancellationToken);
        }

        // Three sensor head builds next week (18.5 h of assembly each).
        var heads = new List<WorkOrderDto>();
        for (var i = 0; i < 3; i++)
        {
            var wo = await kit.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(parts["SNS-210"].Id, 12, nextMonday, nextMonday.AddDays(9 + i * 2),
                Notes: i == 0 ? "Spares lot for the depot" : "Production lot"), cancellationToken);
            heads.Add(await kit.Manufacturing.ReleaseAsync(wo.Summary.Id, cancellationToken));
        }

        if (open)
        {
            // A PCB build with the passive kit backflushed when the first half is completed.
            var build = await kit.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(pcb.Id, 4, today.AddDays(-2), today.AddDays(3), Notes: "Backflush example"), cancellationToken);
            build = await kit.Manufacturing.ReleaseAsync(build.Summary.Id, cancellationToken);
            foreach (var c in build.Components.Where(c => c.PartNumber != parts["RES-KIT"].Display))
            {
                await kit.Manufacturing.IssueComponentsAsync(build.Summary.Id, new IssueComponentsRequest(today.AddDays(-2), [new IssueComponentLine(c.Id, c.QuantityRequired)]), cancellationToken);
            }

            await kit.Manufacturing.ReportLaborAsync(build.Summary.Id, new ReportLaborRequest(build.Operations[0].Id, today.AddDays(-1), 1.5m, 2, mia.Id), cancellationToken);
            await kit.Manufacturing.CompleteAsync(build.Summary.Id, new CompleteWorkOrderRequest(2, today.AddDays(-1), Lot: "PCB-2610-02"), cancellationToken);

            // Freight on the first receipt, spread by value and posted (Finance's step).
            var receiptLines = await kit.LandedCost.ListReceiptLinesAsync(today.AddDays(-60), null, cancellationToken);
            var first = receiptLines.OrderBy(l => l.ReceivedOn).ThenBy(l => l.ReceiptNumber, StringComparer.Ordinal).FirstOrDefault()?.ReceiptNumber;
            var lines = receiptLines.Where(l => l.ReceiptNumber == first).ToList();
            if (lines.Count > 0)
            {
                var charge = await kit.LandedCost.CreateAsync(new SaveLandedChargeRequest(LandedChargeType.Freight, "Inbound freight, Digi-Parts shipment 1", today.AddDays(-11),
                    LandedCostBasis.Value, lines.Select(l => new LandedChargeTargetRequest(l.ReceiptTransactionId)).ToList(), Amount: 186.40m), cancellationToken);
                kit.User.Email = "pm@example.com";
                await kit.LandedCost.PostAsync(charge.Summary.Id, cancellationToken);
                kit.User.Email = MaterialsSeed.MiaEmail;
            }

            // A count in progress: the government property cage is frozen, one tag counted.
            var pi = await kit.Counts.CreateAsync(new CreatePhysicalInventoryRequest(main.Id, today, [bins["CAGE"]], 0, 0, true, "Annual government property inventory (FAR 52.245-1)"),
                cancellationToken);
            if (pi.Tags.Count > 0)
            {
                await kit.Counts.EnterCountAsync(pi.Summary.Id, new EnterCountRequest(pi.Tags[0].TagNumber, pi.Tags[0].FrozenQuantity), cancellationToken);
            }
        }

        // Schedule within capacity, then the planner pins all three builds' assembly onto next Monday: that week is overloaded.
        var schedule = await kit.Capacity.RunAsync(new RunScheduleRequest(ScheduleMode.Forward, true, today), cancellationToken);
        foreach (var wo in heads)
        {
            if (schedule.Operations.FirstOrDefault(o => o.WorkOrderId == wo.Summary.Id && o.Sequence == 10) is { } op)
            {
                schedule = await kit.Capacity.MoveAsync(op.Id, new MoveOperationRequest(nextMonday), cancellationToken);
            }
        }
    }

    /// <summary>The shop floor services built by hand (no permission decorators), running as <see cref="User"/>.</summary>
    private sealed class Kit
    {
        public Kit(IServiceProvider services, SeedUser user)
        {
            User = user;
            var factory = (IWorkbenchDbContextFactory)services.GetService(typeof(IWorkbenchDbContextFactory))!;
            var clock = TimeProvider.System;
            var audit = new AuditRecorder(user, clock);
            var ledger = new MaterialsLedger(new GeneralLedger(audit, clock), Options.Create(new MaterialsOptions()));
            Parts = new PartService(factory, audit, clock);
            Manufacturing = new ManufacturingService(factory, ledger, audit, clock);
            Backflush = new BackflushService(factory, ledger, audit, clock);
            LandedCost = new LandedCostService(factory, ledger, audit, Options.Create(new LandedCostOptions()), clock);
            Counts = new PhysicalInventoryService(factory, ledger, audit, clock);
            Capacity = new CapacitySchedulingService(factory, audit, clock);
        }

        public SeedUser User { get; }
        public PartService Parts { get; }
        public ManufacturingService Manufacturing { get; }
        public BackflushService Backflush { get; }
        public LandedCostService LandedCost { get; }
        public PhysicalInventoryService Counts { get; }
        public CapacitySchedulingService Capacity { get; }
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email { get; set; } = "";
    }
}
