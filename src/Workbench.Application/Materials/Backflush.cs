using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Materials;

namespace Workbench.Application.Materials;

/// <summary>
/// Issue-on-completion. Called inside a work order completion or labor report (same unit of work and journal entry as the
/// event): every backflushed component is issued up to its share of the units done so far, from its configured location,
/// oldest stock first. A component consumed at an operation follows that operation's completed units; the others follow
/// the work order's completed units. What can't be issued is recorded as an open shortage, never skipped silently.
/// </summary>
internal static class Backflush
{
    public sealed record Result(List<InventoryTransaction> Issued, List<BackflushShortage> Shortages);

    /// <summary>
    /// Backflushes <paramref name="wo"/> as if <paramref name="completedQuantity"/> units were finished (the work order's
    /// completed quantity after the event). Operations' completed quantities are read from the tracked work order.
    /// </summary>
    public static async Task<Result> ApplyAsync(StockEngine engine, WorkOrder wo, decimal completedQuantity, DateOnly date, CancellationToken ct)
    {
        var result = new Result([], []);
        if (wo.Components.Count == 0 || wo.Quantity <= 0)
        {
            return result;
        }

        var db = engine.Db;
        var partIds = wo.Components.Select(c => c.PartId).Distinct().ToList();
        var settings = await db.Set<BackflushSetting>().AsNoTracking()
            .Where(s => partIds.Contains(s.PartId) && (s.BomId == null || s.BomId == wo.BomId)).ToListAsync(ct);
        if (settings.Count == 0)
        {
            return result;
        }

        var shortages = await db.Set<BackflushShortage>().Where(s => s.WorkOrderId == wo.Id && s.ResolvedAtUtc == null).ToListAsync(ct);
        var ops = wo.Operations.GroupBy(o => o.Sequence).ToDictionary(g => g.Key, g => g.First());
        var debit = wo.IsForProject ? engine.Accounts?.DirectMaterial : engine.Accounts?.Wip;
        foreach (var c in wo.Components.OrderBy(c => c.Sequence))
        {
            var setting = settings.FirstOrDefault(s => s.PartId == c.PartId && s.BomId == wo.BomId && wo.BomId != null) ?? settings.FirstOrDefault(s => s.PartId == c.PartId && s.BomId == null);
            if (setting is not { IsBackflushed: true })
            {
                continue;
            }

            var basis = completedQuantity;
            var trigger = BackflushTrigger.Completion;
            if (c.OperationSequence is { } seq && ops.TryGetValue(seq, out var op) && op.QuantityCompleted > basis)
            {
                basis = op.QuantityCompleted;
                trigger = BackflushTrigger.Operation;
            }

            var part = await engine.LoadPartAsync(c.PartId, ct);
            var target = basis >= wo.Quantity ? c.QuantityRequired : Math.Round(c.QuantityRequired * basis / wo.Quantity, 4, MidpointRounding.AwayFromZero);
            if (part.Tracking == TrackingMode.Serial)
            {
                target = Math.Min(Math.Ceiling(target), Math.Ceiling(c.QuantityRequired));
            }

            var need = target - c.QuantityIssued;
            var open = shortages.FirstOrDefault(s => s.WorkOrderComponentId == c.Id);
            if (need <= 0)
            {
                Resolve(open, engine);
                continue;
            }

            var candidates = engine.PickOrder(part.Id, wo.ProjectId)
                .Where(b => setting.BinId is { } bin ? b.BinId == bin : setting.WarehouseId is not { } wh || b.WarehouseId == wh)
                .ToList();
            var left = need;
            foreach (var b in candidates)
            {
                var take = Math.Min(left, b.Quantity);
                var t = engine.Issue(part, b, take, InventoryTransactionType.IssueToWorkOrder, date, debit, $"backflushed to {wo.Number}", wo.ProjectId, wo.WbsElementId);
                t.WorkOrderId = wo.Id;
                t.Reason = trigger == BackflushTrigger.Operation ? $"Backflushed at operation {c.OperationSequence}" : "Backflushed at completion";
                wo.MaterialCost += -t.Amount;
                result.Issued.Add(t);
                left -= take;
                if (left == 0)
                {
                    break;
                }
            }

            c.QuantityIssued += need - left;
            if (left == 0)
            {
                Resolve(open, engine);
                continue;
            }

            var message = Describe(engine, setting, part, need - left, left);
            if (open is null)
            {
                open = new BackflushShortage
                {
                    WorkOrderId = wo.Id,
                    WorkOrderComponentId = c.Id,
                    PartId = c.PartId,
                    CreatedAtUtc = engine.UtcNow,
                };
                db.Set<BackflushShortage>().Add(open);
                shortages.Add(open);
            }

            open.QuantityShort = left;
            open.Trigger = trigger;
            open.Date = date;
            open.Message = message.Length > 500 ? message[..500] : message;
            open.UpdatedAtUtc = engine.UtcNow;
            result.Shortages.Add(open);
        }

        if (result.Issued.Count > 0 && wo.Status == WorkOrderStatus.Released)
        {
            wo.Status = WorkOrderStatus.InProgress;
        }

        return result;
    }

    private static void Resolve(BackflushShortage? open, StockEngine engine)
    {
        if (open is not null)
        {
            open.QuantityShort = 0;
            open.ResolvedAtUtc = engine.UtcNow;
            open.UpdatedAtUtc = engine.UtcNow;
        }
    }

    private static string Describe(StockEngine engine, BackflushSetting setting, Part part, decimal issued, decimal left)
    {
        var where = setting.BinId is not null ? "the backflush bin" : setting.WarehouseId is not null ? "the backflush warehouse" : "stock";
        if (setting.BinId is { } bin && engine.FrozenBins.TryGetValue(bin, out var pi))
        {
            return $"Short {left:0.####} {part.UnitOfMeasure} of {part.Display}: the backflush bin is frozen for physical inventory {pi}.";
        }

        return issued > 0
            ? $"Short {left:0.####} {part.UnitOfMeasure} of {part.Display}: only {issued:0.####} was in {where}."
            : $"Short {left:0.####} {part.UnitOfMeasure} of {part.Display}: none in {where}.";
    }
}
