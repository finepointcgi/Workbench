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
/// Development sample data for sales orders and shipping, entered through the real services as Mia Torres (Materials):
/// SO-000001 from the Army on contract W56KGU-26-C-0001 for hardware project 3001 (spares on cost-type CLIN 0002, charged to
/// 3001.02 when they ship): a UID-marked sensor head still being built, sensor boards and field cables. Half the boards and
/// cables are picked, packed in two boxes and shipped (DD250 SHP0001), and the WAWF receiving report is recorded as
/// submitted. Runs once, after <see cref="MaterialsSeed"/>; needs its stock and an open fiscal period.
/// </summary>
internal static class SalesShippingSeed
{
    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (await db.Set<SalesOrder>().AnyAsync(cancellationToken)
            || await db.Set<Project>().FirstOrDefaultAsync(p => p.Code == "3001", cancellationToken) is not { } project
            || project.ContractId is not { } contractId
            || await db.Set<Contract>().FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken) is not { } contract
            || await db.Set<Clin>().FirstOrDefaultAsync(c => c.ContractId == contractId && c.Number == "0002", cancellationToken) is not { } odcClin
            || await db.Set<WbsElement>().FirstOrDefaultAsync(w => w.ProjectId == project.Id && w.Code == "3001.02", cancellationToken) is not { } spares
            || !await db.Set<FiscalPeriod>().AnyAsync(p => p.Start <= today && p.End >= today && p.Status == PeriodStatus.Open, cancellationToken))
        {
            return;
        }

        var parts = await db.Set<Part>().AsNoTracking().Where(p => p.Number == "SNS-210" || p.Number == "PCB-400" || p.Number == "CAB-300").ToDictionaryAsync(p => p.Number, cancellationToken);
        if (parts.Count < 3)
        {
            return;
        }

        var factory = (IWorkbenchDbContextFactory)services.GetService(typeof(IWorkbenchDbContextFactory))!;
        var user = new SeedUser { Email = MaterialsSeed.MiaEmail };
        var clock = TimeProvider.System;
        var audit = new AuditRecorder(user, clock);
        var ledger = new MaterialsLedger(new GeneralLedger(audit, clock), Options.Create(new MaterialsOptions()));
        var orders = new SalesOrderService(factory, audit, clock);
        var shipping = new ShippingService(factory, ledger, audit, Options.Create(new SalesShippingOptions()), new ManualWawfSubmitter(), clock);

        var order = await orders.CreateAsync(new SaveSalesOrderRequest(
            contract.CustomerId, today.AddDays(-6),
            [
                new SaveSalesOrderLineRequest(parts["SNS-210"].Id, 2, 1_250m, today.AddDays(21), "Sensor head assembly, depot spare", odcClin.Id, "0002AA",
                    StockNumber: "6650-01-725-0418", UidRequired: true),
                new SaveSalesOrderLineRequest(parts["PCB-400"].Id, 4, 180m, today, "Sensor PCB assembly, depot spare", odcClin.Id, "0002AB"),
                new SaveSalesOrderLineRequest(parts["CAB-300"].Id, 4, 140m, today, "Field cable harness, 5 m", odcClin.Id, "0002AC", StockNumber: "6150-01-688-2210"),
            ],
            contract.Id, project.Id, spares.Id, "W56KGU26F0014",
            "DLA Distribution Susquehanna", "2001 Mission Dr, Bldg 81\nNew Cumberland PA 17070", "SW3124", "W81PGQ",
            "W56KGU", "S2404A", "S2404A", GovernmentInspectionPoint.Origin, GovernmentInspectionPoint.Origin,
            "Depot spares for the rugged sensor kit fielding (CDRL A004)."), cancellationToken);
        order = await orders.ReleaseAsync(order.Summary.Id, cancellationToken);

        // Half the boards and cables now; the sensor heads ship when the build in progress finishes.
        var lines = order.Lines.ToDictionary(l => l.PartNumber.Split(' ')[0]);
        var pick = await shipping.CreatePickListAsync(order.Summary.Id, new CreatePickListRequest(
            [new PickLineRequest(lines["PCB-400"].Id, 2), new PickLineRequest(lines["CAB-300"].Id, 2)]), cancellationToken);
        foreach (var line in pick.Lines)
        {
            pick = await shipping.ConfirmPickAsync(line.Id, new ConfirmPickRequest(line.Quantity), cancellationToken);
        }

        var shipment = await shipping.CreateShipmentAsync(new CreateShipmentRequest([pick.Summary.Id]), cancellationToken);
        shipment = await shipping.SaveShipmentAsync(shipment.Summary.Id, new SaveShipmentRequest(today, "UPS Ground", "1Z9W46A80391846802", "1Z9W46A80391846802", false,
            [new SavePackageRequest(1, 6.5m, 14, 10, 6, "1Z9W46A80391846802"), new SavePackageRequest(2, 9.0m, 18, 12, 8, "1Z9W46A80391846813")],
            shipment.Lines.Select(l => new PackLineRequest(l.Id, l.PartNumber.StartsWith("CAB", StringComparison.Ordinal) ? 2 : 1)).ToList(),
            "Boxes marked with the DD250 shipment number and MIL-STD-129 labels."), cancellationToken);
        shipment = await shipping.ConfirmShipmentAsync(shipment.Summary.Id, new ConfirmShipmentRequest(today), cancellationToken);
        if ((await shipping.ValidateWawfAsync(shipment.Summary.Id, WawfDocument.ReceivingReport, cancellationToken)).IsValid)
        {
            await shipping.SubmitWawfAsync(shipment.Summary.Id, WawfDocument.ReceivingReport, cancellationToken);
        }
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email { get; set; } = "";
    }
}
