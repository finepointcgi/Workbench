using Microsoft.EntityFrameworkCore;
using Workbench.Application.Materials;
using Workbench.Domain.Materials;
using Workbench.Domain.Quality;

namespace Workbench.Application.Quality;

/// <summary>
/// Where quality plugs into receiving and manufacturing (called from the materials services in their unit of work):
/// parts with an active receiving or final inspection plan go into inspection hold with a pending inspection;
/// in-process plans open an inspection when a work order is released and must be done before it completes; an approved
/// engineering change dated on or before a work order's start decides which BOM revision it's released with.
/// </summary>
internal static class QualityHooks
{
    public static async Task<InspectionPlan?> ActivePlanAsync(IWorkbenchDbContext db, Guid partId, InspectionKind kind, CancellationToken ct) =>
        await db.Set<InspectionPlan>().AsNoTracking().Include(p => p.Characteristics)
            .Where(p => p.PartId == partId && p.Kind == kind && p.IsActive).OrderBy(p => p.Code).FirstOrDefaultAsync(ct);

    public static InventoryStockStatus StatusFor(InspectionPlan? plan) => plan is null ? InventoryStockStatus.Available : InventoryStockStatus.InspectionHold;

    /// <summary>Opens a pending inspection for stock just put into inspection hold (one item per location, lot and serial).</summary>
    public static async Task<Inspection> OpenInspectionAsync(
        IWorkbenchDbContext db, InspectionPlan plan, Part part, IReadOnlyList<InventoryTransaction> received, DateOnly readyOn, string actor, DateTime utcNow,
        Action<Inspection> source, CancellationToken ct)
    {
        var quantity = received.Sum(t => t.Quantity);
        var inspection = new Inspection
        {
            Number = (await Customization.DocumentNumbers.NextInUnitOfWorkAsync(db, Customization.DocumentNumberTypes.Inspection, null, DateOnly.FromDateTime(DateTime.UtcNow), ct)).Number,
            PlanId = plan.Id,
            PartId = part.Id,
            Kind = plan.Kind,
            Quantity = quantity,
            SampleSize = QualityRules.SampleSize(plan.Sampling.ToContract(), plan.SampleSize, quantity),
            ReadyOn = readyOn,
            CreatedBy = actor,
            CreatedAtUtc = utcNow,
        };
        source(inspection);
        foreach (var g in received.GroupBy(t => (t.WarehouseId, t.BinId, t.Lot, t.Serial, t.Ownership, t.OwnerProjectId)))
        {
            inspection.Items.Add(new InspectionItem
            {
                InspectionId = inspection.Id, WarehouseId = g.Key.WarehouseId, BinId = g.Key.BinId, Lot = g.Key.Lot, Serial = g.Key.Serial,
                Ownership = g.Key.Ownership, OwnerProjectId = g.Key.OwnerProjectId, Quantity = g.Sum(t => t.Quantity),
            });
        }

        db.Set<Inspection>().Add(inspection);
        return inspection;
    }

    /// <summary>At release: a pending in-process inspection for each active in-process plan whose operation is on the order.</summary>
    public static async Task OpenInProcessInspectionsAsync(IWorkbenchDbContext db, WorkOrder wo, string actor, DateTime utcNow, CancellationToken ct)
    {
        var plans = await db.Set<InspectionPlan>().AsNoTracking()
            .Where(p => p.PartId == wo.PartId && p.Kind == InspectionKind.InProcess && p.IsActive).OrderBy(p => p.OperationSequence).ToListAsync(ct);
        foreach (var plan in plans)
        {
            var sequence = plan.OperationSequence ?? wo.Operations.Select(o => (int?)o.Sequence).Max();
            if (sequence is null || (plan.OperationSequence is not null && wo.Operations.All(o => o.Sequence != plan.OperationSequence)))
            {
                continue;
            }

            db.Set<Inspection>().Add(new Inspection
            {
                Number = (await Customization.DocumentNumbers.NextInUnitOfWorkAsync(db, Customization.DocumentNumberTypes.Inspection, null, DateOnly.FromDateTime(DateTime.UtcNow), ct)).Number,
                PlanId = plan.Id,
                PartId = wo.PartId,
                Kind = InspectionKind.InProcess,
                Quantity = wo.Quantity,
                SampleSize = QualityRules.SampleSize(plan.Sampling.ToContract(), plan.SampleSize, wo.Quantity),
                WorkOrderId = wo.Id,
                OperationSequence = sequence,
                ReadyOn = wo.StartDate,
                CreatedBy = actor,
                CreatedAtUtc = utcNow,
            });
        }
    }

    /// <summary>A work order can't complete while an in-process inspection on it is pending.</summary>
    public static async Task EnsureInProcessInspectedAsync(IWorkbenchDbContext db, WorkOrder wo, CancellationToken ct)
    {
        var pending = await db.Set<Inspection>().AsNoTracking()
            .Where(i => i.WorkOrderId == wo.Id && i.Kind == InspectionKind.InProcess && i.Status == InspectionStatus.Pending)
            .OrderBy(i => i.OperationSequence).FirstOrDefaultAsync(ct);
        if (pending is not null)
        {
            throw new InvalidOperationException($"Inspect operation {pending.OperationSequence} ({pending.Number}) before completing {wo.Number}.");
        }
    }

    /// <summary>The BOM an approved, date-effective engineering change puts in force on or before <paramref name="date"/> (null: the active BOM).</summary>
    public static async Task<Guid?> BomEffectiveOnAsync(IWorkbenchDbContext db, Guid partId, DateOnly date, CancellationToken ct)
    {
        var candidates = await db.Set<EngineeringChangeItem>().AsNoTracking().Where(i => i.PartId == partId && i.ToBomId != null)
            .Join(db.Set<EngineeringChange>().AsNoTracking().Where(c => c.Status == EngineeringChangeStatus.Approved && c.EffectivityKind == EffectivityKind.Date),
                i => i.ChangeId, c => c.Id, (i, c) => new { i.ToBomId, c.EffectiveDate, c.Number })
            .ToListAsync(ct);
        return candidates.Where(c => c.EffectiveDate is { } d && d <= date).OrderByDescending(c => c.EffectiveDate).ThenByDescending(c => c.Number, StringComparer.Ordinal)
            .Select(c => c.ToBomId).FirstOrDefault();
    }
}
