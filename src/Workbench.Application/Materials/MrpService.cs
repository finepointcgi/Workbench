using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Materials;

/// <summary>
/// Runs MRP over the database (demand: open and planned work orders, project material requirements, forecasts and safety
/// stock; supply: costed on-hand stock, open part purchase orders and requisitions, open stock work orders) and firms
/// planned orders into requisitions (the ordinary requisition flow, approvals included) or planned work orders.
/// </summary>
public sealed class MrpService(
    IWorkbenchDbContextFactory dbFactory, MaterialsLedger ledger, IRequisitionService requisitions, AuditRecorder audit, TimeProvider clock) : IMrpService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<MrpRunDto> RunAsync(RunMrpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.HorizonDays is < 1 or > 730)
        {
            throw new ArgumentException("The planning horizon is 1 to 730 days.");
        }

        var asOf = request.AsOf ?? DateOnly.FromDateTime(UtcNow);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var input = await LoadAsync(db, asOf, cancellationToken);
        var result = MrpEngine.Plan(input.Parts, input.Components, input.Demands, input.Supplies, asOf, request.HorizonDays);

        foreach (var old in await db.Set<PlannedOrder>().Where(p => p.Status == PlannedOrderStatus.Planned).ToListAsync(cancellationToken))
        {
            old.Status = PlannedOrderStatus.Superseded;
        }

        var run = new MrpRun
        {
            AsOf = asOf,
            HorizonDays = request.HorizonDays,
            PlannedOrderCount = result.Plans.Count,
            LateCount = result.Plans.Count(p => p.IsLate),
            Messages = Truncate(string.Join("\n", input.Messages.Concat(result.Messages)), 8000),
            RunBy = audit.Actor,
            RunAtUtc = UtcNow,
        };
        db.Set<MrpRun>().Add(run);
        foreach (var p in result.Plans)
        {
            db.Set<PlannedOrder>().Add(new PlannedOrder
            {
                RunId = run.Id,
                PartId = p.PartId,
                Kind = p.Kind,
                Quantity = p.Quantity,
                NeedDate = p.NeedDate,
                ReleaseDate = p.ReleaseDate,
                IsLate = p.IsLate,
                Pegging = p.Pegging,
                ProjectId = p.ProjectId,
                Status = PlannedOrderStatus.Planned,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return await RunDtoAsync(db, run, cancellationToken);
    }

    private sealed record Input(List<MrpPart> Parts, List<MrpComponent> Components, List<MrpDemand> Demands, List<MrpSupply> Supplies, List<string> Messages);

    private static async Task<Input> LoadAsync(IWorkbenchDbContext db, DateOnly asOf, CancellationToken ct)
    {
        var messages = new List<string>();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => p.IsActive).ToListAsync(ct);
        var onHand = await db.Set<InventoryBalance>().AsNoTracking().Where(b => b.Ownership != InventoryOwnership.GovernmentFurnished)
            .GroupBy(b => b.PartId).Select(g => new { g.Key, Qty = g.Sum(b => b.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Qty, ct);
        var boms = await db.Set<BillOfMaterial>().AsNoTracking().Include(b => b.Components).Where(b => b.IsActive).ToListAsync(ct);
        var bomParts = boms.Select(b => b.PartId).ToHashSet();
        var mrpParts = parts.Select(p => new MrpPart(p.Id, p.Display, p.Source, p.LeadTimeDays, p.SafetyStock, p.MinimumOrderQuantity, p.OrderMultiple,
            onHand.GetValueOrDefault(p.Id), bomParts.Contains(p.Id))).ToList();
        var components = boms.SelectMany(b => b.Components.Select(c => new MrpComponent(b.PartId, c.ComponentPartId, c.QuantityPer, c.ScrapPercent, c.EffectiveFrom, c.EffectiveTo))).ToList();
        var demands = new List<MrpDemand>();
        var supplies = new List<MrpSupply>();

        var orders = await db.Set<WorkOrder>().AsNoTracking().Include(w => w.Components)
            .Where(w => w.Status == WorkOrderStatus.Planned || w.Status == WorkOrderStatus.Released || w.Status == WorkOrderStatus.InProgress).ToListAsync(ct);
        var byParent = components.ToLookup(c => c.ParentId);
        foreach (var wo in orders)
        {
            if (wo.Status == WorkOrderStatus.Planned)
            {
                // Not released yet: its components come from the BOM in force on the start date.
                foreach (var c in byParent[wo.PartId].Where(c => c.IsEffectiveOn(wo.StartDate)))
                {
                    demands.Add(new MrpDemand(c.ComponentId, wo.StartDate, c.RequiredFor(wo.QuantityOpen), $"{wo.Number} (planned)", wo.ProjectId));
                }
            }
            else
            {
                foreach (var c in wo.Components.Where(c => c.QuantityOpen > 0))
                {
                    demands.Add(new MrpDemand(c.PartId, wo.StartDate, c.QuantityOpen, wo.Number, wo.ProjectId));
                }
            }

            if (!wo.IsForProject && wo.QuantityOpen > 0)
            {
                supplies.Add(new MrpSupply(wo.PartId, wo.DueDate, wo.QuantityOpen, wo.Number));
            }
        }

        var requirements = await db.Set<ProjectMaterialRequirement>().AsNoTracking().Where(r => !r.IsClosed).ToListAsync(ct);
        var projectCodes = await db.Set<Project>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        foreach (var r in requirements.Where(r => r.OpenQuantity > 0))
        {
            demands.Add(new MrpDemand(r.PartId, r.NeededBy, r.OpenQuantity, $"{projectCodes.GetValueOrDefault(r.ProjectId)} requirement", r.ProjectId));
        }

        foreach (var f in await db.Set<PartForecast>().AsNoTracking().Where(f => f.Date >= asOf).ToListAsync(ct))
        {
            demands.Add(new MrpDemand(f.PartId, f.Date, f.Quantity, "Forecast"));
        }

        demands.AddRange(await SalesOrderDemand.LoadAsync(db, ct)); // Released sales orders' open lines.

        foreach (var s in await MrpData.OpenPurchaseSupplyAsync(db, ct))
        {
            supplies.Add(new MrpSupply(s.PartId, s.Date, s.Quantity, s.Document));
        }

        var gfm = await db.Set<InventoryBalance>().AsNoTracking().AnyAsync(b => b.Ownership == InventoryOwnership.GovernmentFurnished, ct);
        if (gfm)
        {
            messages.Add("Government-furnished material isn't netted (it's used only on its own project, and isn't replenished).");
        }

        return new Input(mrpParts, components, demands, supplies, messages);
    }

    private static string? Truncate(string s, int max) => s.Length == 0 ? null : s.Length > max ? s[..max] : s;

    public async Task<MrpRunDto?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await db.Set<MrpRun>().AsNoTracking().OrderByDescending(r => r.RunAtUtc).FirstOrDefaultAsync(cancellationToken);
        return run is null ? null : await RunDtoAsync(db, run, cancellationToken);
    }

    private static async Task<MrpRunDto> RunDtoAsync(IWorkbenchDbContext db, MrpRun run, CancellationToken ct)
    {
        var orders = await db.Set<PlannedOrder>().AsNoTracking().Where(p => p.RunId == run.Id).ToListAsync(ct);
        var partIds = orders.Select(o => o.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var vendorIds = parts.Values.Where(p => p.PreferredVendorId != null).Select(p => p.PreferredVendorId!.Value).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        var projectIds = orders.Where(o => o.ProjectId != null).Select(o => o.ProjectId!.Value).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        var reqIds = orders.Where(o => o.RequisitionId != null).Select(o => o.RequisitionId!.Value).ToList();
        var reqs = await db.Set<PurchaseRequisition>().AsNoTracking().Where(r => reqIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Number, ct);
        var woIds = orders.Where(o => o.WorkOrderId != null).Select(o => o.WorkOrderId!.Value).ToList();
        var wos = await db.Set<WorkOrder>().AsNoTracking().Where(w => woIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Number, ct);
        var dtos = orders.OrderBy(o => o.ReleaseDate).ThenBy(o => parts[o.PartId].Number, StringComparer.Ordinal)
            .Select(o =>
            {
                var p = parts[o.PartId];
                return new PlannedOrderDto(o.Id, p.Id, p.Display, p.Description, o.Kind.ToContract(), o.Quantity, o.NeedDate, o.ReleaseDate, o.IsLate, o.Pegging,
                    o.ProjectId is { } pid ? projects.GetValueOrDefault(pid) : null, o.Status.ToContract(),
                    p.PreferredVendorId is { } v ? vendors.GetValueOrDefault(v) : null, p.StandardCost > 0 ? p.StandardCost : p.AverageCost,
                    o.RequisitionId is { } r ? reqs.GetValueOrDefault(r) : o.WorkOrderId is { } w ? wos.GetValueOrDefault(w) : null);
            }).ToList();
        var messages = (run.Messages ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return new MrpRunDto(run.Id, run.AsOf, run.HorizonDays, run.RunAtUtc, run.RunBy, run.LateCount, messages, dtos);
    }

    public async Task<FirmResultDto> FirmAsync(FirmPlannedOrdersRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = (request.PlannedOrderIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException("Choose the planned orders to firm.");
        }

        List<PlannedOrder> orders;
        Dictionary<Guid, Part> parts;
        Guid clearing;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            orders = await db.Set<PlannedOrder>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
            if (orders.Count != ids.Count)
            {
                throw new WorkbenchNotFoundException("A planned order wasn't found; run MRP again.");
            }

            if (orders.Any(o => o.Status != PlannedOrderStatus.Planned))
            {
                throw new InvalidOperationException("A planned order is already firmed or superseded; run MRP again.");
            }

            var partIds = orders.Select(o => o.PartId).Distinct().ToList();
            parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
            clearing = (await ledger.AccountsAsync(db, cancellationToken))?.ReceivedNotInvoiced
                ?? throw new ArgumentException("Set up the chart of accounts before firming planned buys (they charge the inventory clearing account).");
        }

        var requisitionNumbers = new List<string>();
        var buys = orders.Where(o => o.Kind == PlannedOrderKind.Buy).ToList();
        foreach (var group in buys.GroupBy(o => parts[o.PartId].PreferredVendorId))
        {
            var list = group.OrderBy(o => o.NeedDate).ThenBy(o => parts[o.PartId].Number, StringComparer.Ordinal).ToList();
            var lines = list.Select(o =>
            {
                var p = parts[o.PartId];
                var cost = p.StandardCost > 0 ? p.StandardCost : p.AverageCost > 0 ? p.AverageCost : 0.01m;
                return new SaveRequisitionLineRequest($"{p.Display} {p.Description}".Trim(), o.Quantity, cost, AccountId: clearing, OrgUnitId: request.OrgUnitId,
                    NeededBy: o.NeedDate, SuggestedVendorId: p.PreferredVendorId);
            }).ToList();
            var created = await requisitions.CreateAsync(new SaveRequisitionRequest($"MRP planned buys ({list.Count} line{(list.Count == 1 ? "" : "s")}) for inventory", lines, request.OrgUnitId), cancellationToken);
            await using (var db = await dbFactory.CreateAsync(cancellationToken))
            {
                var reqLines = created.Lines.OrderBy(l => l.LineNumber).ToList();
                for (var i = 0; i < list.Count; i++)
                {
                    db.Set<MaterialPurchaseLink>().Add(new MaterialPurchaseLink
                    {
                        PartId = list[i].PartId,
                        RequisitionLineId = reqLines[i].Id,
                        PlannedOrderId = list[i].Id,
                        Ownership = InventoryOwnership.General,
                        CreatedBy = audit.Actor,
                        CreatedAtUtc = UtcNow,
                    });
                    var planned = await db.Set<PlannedOrder>().FirstAsync(p => p.Id == list[i].Id, cancellationToken);
                    planned.Status = PlannedOrderStatus.Firmed;
                    planned.RequisitionId = created.Requisition.Id;
                    planned.FirmedBy = audit.Actor;
                    planned.FirmedAtUtc = UtcNow;
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            requisitionNumbers.Add(request.SubmitRequisitions ? (await requisitions.SubmitAsync(created.Requisition.Id, cancellationToken)).Requisition.Number : created.Requisition.Number);
        }

        var workOrders = new List<string>();
        var makes = orders.Where(o => o.Kind == PlannedOrderKind.Make).ToList();
        if (makes.Count > 0)
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var today = DateOnly.FromDateTime(UtcNow);
            foreach (var o in makes.OrderBy(o => o.ReleaseDate))
            {
                var start = o.ReleaseDate < today ? today : o.ReleaseDate;
                var wo = await ManufacturingService.CreateCoreAsync(db,
                    new SaveWorkOrderRequest(o.PartId, o.Quantity, start, o.NeedDate < start ? start : o.NeedDate, Notes: Truncate($"Firmed from MRP: {o.Pegging}".Trim(), 2000)),
                    o.Id, audit.Actor, UtcNow, cancellationToken);
                var planned = await db.Set<PlannedOrder>().FirstAsync(p => p.Id == o.Id, cancellationToken);
                planned.Status = PlannedOrderStatus.Firmed;
                planned.WorkOrderId = wo.Id;
                planned.FirmedBy = audit.Actor;
                planned.FirmedAtUtc = UtcNow;
                audit.Record(db, MaterialRules.WorkOrderEntity, wo.Id, "Created", after: new { wo.Number, wo.Quantity, FromPlannedOrder = o.Id });
                workOrders.Add(wo.Number);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return new FirmResultDto(requisitionNumbers, workOrders);
    }
}
