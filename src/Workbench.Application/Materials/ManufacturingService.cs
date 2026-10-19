using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Materials;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Materials;

/// <summary>
/// Bills of materials, routings and work orders. Project work orders charge their WBS element as cost is incurred
/// (material at issue, labor through the timesheet) and deliver to the project; stock work orders collect cost in WIP
/// (material issued, shop labor applied at the employee's cost rate), relieve it to inventory at completion and post
/// what's left as the manufacturing variance at close.
/// </summary>
public sealed class ManufacturingService(IWorkbenchDbContextFactory dbFactory, MaterialsLedger ledger, AuditRecorder audit, TimeProvider clock) : IManufacturingService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    // ---- BOMs ----

    public async Task<IReadOnlyList<BomSummaryDto>> ListBomsAsync(Guid? partId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var boms = await db.Set<BillOfMaterial>().AsNoTracking().Include(b => b.Components).Where(b => partId == null || b.PartId == partId).ToListAsync(cancellationToken);
        return await BomSummariesAsync(db, boms, cancellationToken);
    }

    private static async Task<IReadOnlyList<BomSummaryDto>> BomSummariesAsync(IWorkbenchDbContext db, IReadOnlyList<BillOfMaterial> boms, CancellationToken ct)
    {
        var ids = boms.Select(b => b.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        return boms.OrderBy(b => parts[b.PartId].Number, StringComparer.Ordinal).ThenBy(b => b.Revision, StringComparer.Ordinal)
            .Select(b => new BomSummaryDto(b.Id, b.PartId, parts[b.PartId].Number, parts[b.PartId].Revision, parts[b.PartId].Description, b.Revision, b.IsActive, b.Components.Count, b.UpdatedAtUtc))
            .ToList();
    }

    public async Task<BomDto> GetBomAsync(Guid bomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await BomDetailAsync(db, bomId, cancellationToken);
    }

    private static async Task<BomDto> BomDetailAsync(IWorkbenchDbContext db, Guid bomId, CancellationToken ct)
    {
        var bom = await db.Set<BillOfMaterial>().AsNoTracking().Include(b => b.Components).FirstOrDefaultAsync(b => b.Id == bomId, ct) ?? throw WorkbenchNotFoundException.For("BOM", bomId);
        var ids = bom.Components.Select(c => c.ComponentPartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var components = bom.Components.OrderBy(c => c.Sequence).Select(c =>
        {
            var p = parts[c.ComponentPartId];
            return new BomComponentDto(c.Id, c.Sequence, p.Id, p.Number, p.Revision, p.Description, p.UnitOfMeasure, p.Source.ToContract(), c.QuantityPer, c.ScrapPercent,
                c.EffectiveFrom, c.EffectiveTo, c.ReferenceDesignators, c.OperationSequence, p.CurrentUnitCost);
        }).ToList();
        return new BomDto((await BomSummariesAsync(db, [bom], ct))[0], bom.Notes, components);
    }

    public async Task<BomDto> SaveBomAsync(Guid? bomId, SaveBomRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var part = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PartId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Part", request.PartId);
        if (part.Source != PartSource.Make)
        {
            throw new ArgumentException($"{part.Display} is a buy part; only make parts have a bill of materials.");
        }

        var revision = MaterialRules.Code(request.Revision, "BOM revision", 16);
        BillOfMaterial bom;
        if (bomId is { } id)
        {
            bom = await db.Set<BillOfMaterial>().Include(b => b.Components).FirstOrDefaultAsync(b => b.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("BOM", id);
            if (bom.PartId != part.Id)
            {
                throw new ArgumentException("A BOM can't move to another part.");
            }
        }
        else
        {
            bom = new BillOfMaterial
            {
                PartId = part.Id,
                Revision = revision,
                CreatedBy = audit.Actor,
                CreatedAtUtc = UtcNow,
                IsActive = !await db.Set<BillOfMaterial>().AnyAsync(b => b.PartId == part.Id, cancellationToken),
            };
            db.Set<BillOfMaterial>().Add(bom);
        }

        if (await db.Set<BillOfMaterial>().AnyAsync(b => b.PartId == part.Id && b.Revision == revision && b.Id != bom.Id, cancellationToken))
        {
            throw new ArgumentException($"{part.Display} already has BOM revision {revision}.");
        }

        var inputs = request.Components ?? [];
        var componentIds = inputs.Select(c => c.ComponentPartId).Distinct().ToList();
        var components = await db.Set<Part>().AsNoTracking().Where(p => componentIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var allBoms = await db.Set<BillOfMaterial>().AsNoTracking().Include(b => b.Components).Where(b => b.IsActive && b.Id != bom.Id).ToListAsync(cancellationToken);
        var children = allBoms.ToDictionary(b => b.PartId, b => b.Components.Select(c => c.ComponentPartId).ToList());
        bool Uses(Guid from, Guid target, int depth) =>
            from == target || (depth < 30 && children.TryGetValue(from, out var list) && list.Any(c => Uses(c, target, depth + 1)));

        var before = bomId is null ? null : new { bom.Revision, Components = bom.Components.Count };
        db.Set<BomComponent>().RemoveRange(bom.Components);
        bom.Components.Clear();
        var sequence = 0;
        foreach (var c in inputs)
        {
            sequence += 10;
            var prefix = $"Component {sequence / 10}";
            if (!components.TryGetValue(c.ComponentPartId, out var cp))
            {
                throw WorkbenchNotFoundException.For("Part", c.ComponentPartId);
            }

            if (Uses(cp.Id, part.Id, 0))
            {
                throw new ArgumentException($"{prefix}: {cp.Display} is {part.Display} or uses it, which would make the BOM use itself.");
            }

            if (c.QuantityPer <= 0 || decimal.Round(c.QuantityPer, 6) != c.QuantityPer)
            {
                throw new ArgumentException($"{prefix}: the quantity per must be greater than 0 (up to six decimals).");
            }

            if (c.ScrapPercent is < 0 or >= 100)
            {
                throw new ArgumentException($"{prefix}: scrap is 0 to 99.99%.");
            }

            if (c.EffectiveFrom is { } f && c.EffectiveTo is { } t && t < f)
            {
                throw new ArgumentException($"{prefix}: the effectivity ends before it starts.");
            }

            AddTo(db, bom.Components, new BomComponent
            {
                BomId = bom.Id,
                Sequence = sequence,
                ComponentPartId = cp.Id,
                QuantityPer = c.QuantityPer,
                ScrapPercent = decimal.Round(c.ScrapPercent, 4),
                EffectiveFrom = c.EffectiveFrom,
                EffectiveTo = c.EffectiveTo,
                ReferenceDesignators = PurchasingSupport.Optional(c.ReferenceDesignators, $"{prefix}: reference designators", 200),
                OperationSequence = c.OperationSequence,
            });
        }

        bom.Revision = revision;
        bom.Notes = PurchasingSupport.Optional(request.Notes, "Notes", 2000);
        bom.UpdatedAtUtc = UtcNow;
        audit.Record(db, MaterialRules.BomEntity, bom.Id, bomId is null ? "Created" : "Updated", before, new { Part = part.Display, bom.Revision, Components = bom.Components.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await BomDetailAsync(db, bom.Id, cancellationToken);
    }

    public async Task<BomDto> ActivateBomAsync(Guid bomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var bom = await db.Set<BillOfMaterial>().FirstOrDefaultAsync(b => b.Id == bomId, cancellationToken) ?? throw WorkbenchNotFoundException.For("BOM", bomId);
        foreach (var other in await db.Set<BillOfMaterial>().Where(b => b.PartId == bom.PartId && b.Id != bom.Id && b.IsActive).ToListAsync(cancellationToken))
        {
            other.IsActive = false;
        }

        bom.IsActive = true;
        bom.UpdatedAtUtc = UtcNow;
        audit.Record(db, MaterialRules.BomEntity, bom.Id, "Activated", after: new { bom.Revision });
        await db.SaveChangesAsync(cancellationToken);
        return await BomDetailAsync(db, bom.Id, cancellationToken);
    }

    public async Task<BomDto> CopyBomAsync(Guid bomId, string newRevision, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var bom = await db.Set<BillOfMaterial>().AsNoTracking().Include(b => b.Components).FirstOrDefaultAsync(b => b.Id == bomId, cancellationToken) ?? throw WorkbenchNotFoundException.For("BOM", bomId);
        var revision = MaterialRules.Code(newRevision, "BOM revision", 16);
        if (await db.Set<BillOfMaterial>().AnyAsync(b => b.PartId == bom.PartId && b.Revision == revision, cancellationToken))
        {
            throw new ArgumentException($"Revision {revision} already exists.");
        }

        var copy = new BillOfMaterial { PartId = bom.PartId, Revision = revision, Notes = bom.Notes, CreatedBy = audit.Actor, CreatedAtUtc = UtcNow, UpdatedAtUtc = UtcNow };
        foreach (var c in bom.Components.OrderBy(c => c.Sequence))
        {
            copy.Components.Add(new BomComponent
            {
                BomId = copy.Id,
                Sequence = c.Sequence,
                ComponentPartId = c.ComponentPartId,
                QuantityPer = c.QuantityPer,
                ScrapPercent = c.ScrapPercent,
                EffectiveFrom = c.EffectiveFrom,
                EffectiveTo = c.EffectiveTo,
                ReferenceDesignators = c.ReferenceDesignators,
                OperationSequence = c.OperationSequence,
            });
        }

        db.Set<BillOfMaterial>().Add(copy);
        audit.Record(db, MaterialRules.BomEntity, copy.Id, "Created", after: new { CopiedFrom = bom.Revision, copy.Revision });
        await db.SaveChangesAsync(cancellationToken);
        return await BomDetailAsync(db, copy.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<BomExplosionRowDto>> ExplodeAsync(Guid partId, decimal quantity, DateOnly asOf, CancellationToken cancellationToken = default)
    {
        MaterialRules.Quantity(quantity, "Quantity");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var parts = await db.Set<Part>().AsNoTracking().ToDictionaryAsync(p => p.Id, cancellationToken);
        if (!parts.ContainsKey(partId))
        {
            throw WorkbenchNotFoundException.For("Part", partId);
        }

        var boms = (await db.Set<BillOfMaterial>().AsNoTracking().Include(b => b.Components).Where(b => b.IsActive).ToListAsync(cancellationToken)).ToDictionary(b => b.PartId);
        var rows = new List<BomExplosionRowDto>();
        void Walk(Guid id, decimal qty, int level)
        {
            if (level > 20 || !boms.TryGetValue(id, out var bom))
            {
                return;
            }

            foreach (var c in bom.Components.Where(c => c.IsEffectiveOn(asOf)).OrderBy(c => c.Sequence))
            {
                var p = parts[c.ComponentPartId];
                var ext = c.RequiredFor(qty);
                rows.Add(new BomExplosionRowDto(level, p.Id, p.Number, p.Revision, p.Description, p.UnitOfMeasure, p.Source.ToContract(), c.QuantityPer, ext,
                    p.CurrentUnitCost, Money.Round(ext * p.CurrentUnitCost)));
                Walk(p.Id, ext, level + 1);
            }
        }

        Walk(partId, quantity, 1);
        return rows;
    }

    public async Task<IReadOnlyList<WhereUsedDto>> WhereUsedAsync(Guid partId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await MaterialQueries.WhereUsedAsync(db, partId, cancellationToken);
    }

    /// <summary>Adds a child explicitly (its v7 id would otherwise make EF treat it as an existing row on a tracked parent).</summary>
    private static void AddTo<T>(IWorkbenchDbContext db, List<T> list, T child) where T : class
    {
        db.Set<T>().Add(child);
        list.Add(child);
    }

    // ---- Work centers and routings ----

    public async Task<IReadOnlyList<WorkCenterDto>> ListWorkCentersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await WorkCentersAsync(db, cancellationToken);
    }

    private static async Task<IReadOnlyList<WorkCenterDto>> WorkCentersAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        return (await db.Set<WorkCenter>().AsNoTracking().ToListAsync(ct)).OrderBy(w => w.Code, StringComparer.Ordinal)
            .Select(w => new WorkCenterDto(w.Id, w.Code, w.Name, w.LaborCategoryId, w.LaborCategoryId is { } l ? categories.GetValueOrDefault(l) : null, w.HoursPerDay, w.IsActive)).ToList();
    }

    public async Task<WorkCenterDto> SaveWorkCenterAsync(Guid? workCenterId, SaveWorkCenterRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var w = workCenterId is { } id
            ? await db.Set<WorkCenter>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Work center", id)
            : new WorkCenter { Code = "", Name = "" };
        w.Code = MaterialRules.Code(request.Code, "Work center code", 32);
        w.Name = PurchasingSupport.Required(request.Name, "Name", 200);
        if (request.LaborCategoryId is { } lc && !await db.Set<LaborCategory>().AnyAsync(c => c.Id == lc, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Labor category", lc);
        }

        w.LaborCategoryId = request.LaborCategoryId;
        w.HoursPerDay = request.HoursPerDay is > 0 and <= 24 ? decimal.Round(request.HoursPerDay, 2) : throw new ArgumentException("Hours per day is more than 0 and at most 24.");
        w.IsActive = request.IsActive;
        if (await db.Set<WorkCenter>().AnyAsync(x => x.Code == w.Code && x.Id != w.Id, cancellationToken))
        {
            throw new ArgumentException($"There's already a work center {w.Code}.");
        }

        if (workCenterId is null)
        {
            db.Set<WorkCenter>().Add(w);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await WorkCentersAsync(db, cancellationToken)).First(x => x.Id == w.Id);
    }

    public async Task<IReadOnlyList<RoutingDto>> ListRoutingsAsync(Guid partId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var routings = await db.Set<Routing>().AsNoTracking().Include(r => r.Operations).Where(r => r.PartId == partId).ToListAsync(cancellationToken);
        return await RoutingDtosAsync(db, routings, cancellationToken);
    }

    private static async Task<IReadOnlyList<RoutingDto>> RoutingDtosAsync(IWorkbenchDbContext db, IReadOnlyList<Routing> routings, CancellationToken ct)
    {
        var centers = await db.Set<WorkCenter>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var partIds = routings.Select(r => r.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Display, ct);
        return routings.OrderByDescending(r => r.IsActive).ThenBy(r => r.Revision, StringComparer.Ordinal)
            .Select(r => new RoutingDto(r.Id, r.PartId, parts.GetValueOrDefault(r.PartId) ?? "", r.Revision, r.IsActive,
                r.Operations.OrderBy(o => o.Sequence).Select(o => new RoutingOperationDto(o.Id, o.Sequence, o.Description, o.WorkCenterId, centers.GetValueOrDefault(o.WorkCenterId) ?? "",
                    o.LaborCategoryId, o.LaborCategoryId is { } l ? categories.GetValueOrDefault(l) : null, o.SetupHours, o.RunHoursPerUnit)).ToList()))
            .ToList();
    }

    public async Task<RoutingDto> SaveRoutingAsync(Guid? routingId, SaveRoutingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var part = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PartId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Part", request.PartId);
        if (part.Source != PartSource.Make)
        {
            throw new ArgumentException($"{part.Display} is a buy part; only make parts have a routing.");
        }

        var revision = MaterialRules.Code(request.Revision, "Routing revision", 16);
        Routing routing;
        if (routingId is { } id)
        {
            routing = await db.Set<Routing>().Include(r => r.Operations).FirstOrDefaultAsync(r => r.Id == id && r.PartId == part.Id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Routing", id);
            db.Set<RoutingOperation>().RemoveRange(routing.Operations);
            routing.Operations.Clear();
        }
        else
        {
            routing = new Routing { PartId = part.Id, Revision = revision, CreatedBy = audit.Actor, CreatedAtUtc = UtcNow };
            db.Set<Routing>().Add(routing);
        }

        if (await db.Set<Routing>().AnyAsync(r => r.PartId == part.Id && r.Revision == revision && r.Id != routing.Id, cancellationToken))
        {
            throw new ArgumentException($"{part.Display} already has routing revision {revision}.");
        }

        routing.Revision = revision;
        var centers = (await db.Set<WorkCenter>().AsNoTracking().Where(c => c.IsActive).Select(c => c.Id).ToListAsync(cancellationToken)).ToHashSet();
        var ops = request.Operations ?? [];
        if (ops.Select(o => o.Sequence).Distinct().Count() != ops.Count)
        {
            throw new ArgumentException("Each operation needs its own sequence number.");
        }

        foreach (var o in ops.OrderBy(o => o.Sequence))
        {
            if (o.Sequence <= 0 || !centers.Contains(o.WorkCenterId))
            {
                throw new ArgumentException($"Operation {o.Sequence}: choose an active work center (sequence numbers are positive).");
            }

            if (o.SetupHours < 0 || o.RunHoursPerUnit < 0)
            {
                throw new ArgumentException($"Operation {o.Sequence}: hours can't be negative.");
            }

            AddTo(db, routing.Operations, new RoutingOperation
            {
                RoutingId = routing.Id,
                Sequence = o.Sequence,
                Description = PurchasingSupport.Required(o.Description, $"Operation {o.Sequence}: description", 200),
                WorkCenterId = o.WorkCenterId,
                LaborCategoryId = o.LaborCategoryId,
                SetupHours = decimal.Round(o.SetupHours, 2),
                RunHoursPerUnit = decimal.Round(o.RunHoursPerUnit, 4),
            });
        }

        if (request.Activate)
        {
            foreach (var other in await db.Set<Routing>().Where(r => r.PartId == part.Id && r.Id != routing.Id && r.IsActive).ToListAsync(cancellationToken))
            {
                other.IsActive = false;
            }

            routing.IsActive = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await RoutingDtosAsync(db, [routing], cancellationToken))[0];
    }

    // ---- Work orders ----

    public async Task<IReadOnlyList<WorkOrderSummaryDto>> ListWorkOrdersAsync(WorkOrderQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<WorkOrder>().AsNoTracking();
        if (query.Status is { } s)
        {
            var status = s.ToDomain();
            q = q.Where(w => w.Status == status);
        }

        if (query.OpenOnly)
        {
            q = q.Where(w => w.Status == WorkOrderStatus.Planned || w.Status == WorkOrderStatus.Released || w.Status == WorkOrderStatus.InProgress || w.Status == WorkOrderStatus.Completed);
        }

        if (query.PartId is { } pid)
        {
            q = q.Where(w => w.PartId == pid);
        }

        if (query.ProjectId is { } project)
        {
            q = q.Where(w => w.ProjectId == project);
        }

        var rows = await SummariesAsync(db, await q.ToListAsync(cancellationToken), cancellationToken);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var t = query.Search.Trim();
            rows = rows.Where(r => r.Number.Contains(t, StringComparison.OrdinalIgnoreCase) || r.PartNumber.Contains(t, StringComparison.OrdinalIgnoreCase)
                || r.PartDescription.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return rows;
    }

    private async Task<IReadOnlyList<WorkOrderSummaryDto>> SummariesAsync(IWorkbenchDbContext db, IReadOnlyList<WorkOrder> orders, CancellationToken ct)
    {
        var partIds = orders.Select(o => o.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var projectIds = orders.Where(o => o.ProjectId != null).Select(o => o.ProjectId!.Value).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        var wbsIds = orders.Where(o => o.WbsElementId != null).Select(o => o.WbsElementId!.Value).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var today = Today;
        return orders.OrderByDescending(o => o.Number, StringComparer.Ordinal)
            .Select(o => new WorkOrderSummaryDto(o.Id, o.Number, o.PartId, parts[o.PartId].Display, parts[o.PartId].Description, o.Quantity, o.QuantityCompleted, o.QuantityScrapped,
                o.Status.ToContract(), o.ProjectId is { } p ? projects.GetValueOrDefault(p) : null, o.WbsElementId is { } w ? wbs.GetValueOrDefault(w) : null,
                o.StartDate, o.DueDate, o.Status is WorkOrderStatus.Planned or WorkOrderStatus.Released or WorkOrderStatus.InProgress && o.DueDate < today))
            .ToList();
    }

    public async Task<WorkOrderDto> GetWorkOrderAsync(Guid workOrderId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, workOrderId, cancellationToken);
    }

    private async Task<WorkOrderDto> DetailAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var wo = await db.Set<WorkOrder>().AsNoTracking().Include(w => w.Components).Include(w => w.Operations).FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw WorkbenchNotFoundException.For("Work order", id);
        var summary = (await SummariesAsync(db, [wo], ct))[0];
        var part = await db.Set<Part>().AsNoTracking().FirstAsync(p => p.Id == wo.PartId, ct);
        var compIds = wo.Components.Select(c => c.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => compIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var balances = await db.Set<InventoryBalance>().AsNoTracking().Where(b => compIds.Contains(b.PartId) && b.Quantity > 0).ToListAsync(ct);
        decimal Available(Guid partId) => balances.Where(b => b.PartId == partId && (b.Ownership == InventoryOwnership.General || (wo.ProjectId is { } p && b.OwnerProjectId == p))).Sum(b => b.Quantity);
        var centers = await db.Set<WorkCenter>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var reports = await db.Set<WorkOrderLaborReport>().AsNoTracking().Where(r => r.WorkOrderId == id).OrderBy(r => r.WorkDate).ThenBy(r => r.CreatedAtUtc).ToListAsync(ct);
        var employeeIds = reports.Select(r => r.EmployeeId).Distinct().ToList();
        var employees = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);
        var charge = wo.WbsElementId ?? wo.LaborChargeWbsElementId;
        var timesheet = new Dictionary<(Guid, DateOnly), decimal>();
        if (charge is { } c && reports.Count > 0)
        {
            var dates = reports.Select(r => r.WorkDate).Distinct().ToList();
            var entries = await db.Set<TimeEntry>().AsNoTracking().Where(e => e.WbsElementId == c && dates.Contains(e.WorkDate))
                .Join(db.Set<Timesheet>().AsNoTracking().Where(t => employeeIds.Contains(t.EmployeeId)), e => e.TimesheetId, t => t.Id, (e, t) => new { t.EmployeeId, e.WorkDate, e.Hours })
                .ToListAsync(ct);
            foreach (var g in entries.GroupBy(e => (e.EmployeeId, e.WorkDate)))
            {
                timesheet[g.Key] = g.Sum(e => e.Hours);
            }
        }

        var ops = wo.Operations.ToDictionary(o => o.Id);
        string? chargeCode = null;
        if (charge is { } cc)
        {
            chargeCode = await db.Set<WbsElement>().AsNoTracking().Where(w => w.Id == cc)
                .Join(db.Set<Project>().AsNoTracking(), w => w.ProjectId, p => p.Id, (w, p) => p.Code + " " + w.Code).FirstOrDefaultAsync(ct);
        }

        string? location = null;
        if ((wo.BinId ?? part.DefaultBinId) is { } bid)
        {
            location = await db.Set<Bin>().AsNoTracking().Where(b => b.Id == bid)
                .Join(db.Set<Warehouse>().AsNoTracking(), b => b.WarehouseId, w => w.Id, (b, w) => w.Code + " / " + b.Code).FirstOrDefaultAsync(ct);
        }

        var bomRev = wo.BomId is { } bomId ? await db.Set<BillOfMaterial>().AsNoTracking().Where(b => b.Id == bomId).Select(b => b.Revision).FirstOrDefaultAsync(ct) : null;
        var routingRev = wo.RoutingId is { } rid ? await db.Set<Routing>().AsNoTracking().Where(r => r.Id == rid).Select(r => r.Revision).FirstOrDefaultAsync(ct) : null;
        var transactions = await db.Set<InventoryTransaction>().AsNoTracking().Where(t => t.WorkOrderId == id).OrderBy(t => t.Number).ToListAsync(ct);
        var done = wo.QuantityCompleted;
        var cost = new WorkOrderCostDto(wo.MaterialCost, wo.LaborCost, wo.ScrapCost, wo.CompletedCost, wo.VarianceCost, wo.IsForProject ? null : wo.WipBalance,
            Money.Round(wo.Quantity * part.StandardCost), done > 0 ? Math.Round((wo.MaterialCost + wo.LaborCost - wo.ScrapCost) / (done + 0m), 4) : null);
        return new WorkOrderDto(summary, wo.ProjectId, wo.WbsElementId, wo.LaborChargeWbsElementId, chargeCode, bomRev, routingRev, location, part.Tracking.ToContract(), wo.Notes, cost,
            wo.Components.OrderBy(x => x.Sequence).Select(x =>
            {
                var p = parts[x.PartId];
                return new WorkOrderComponentDto(x.Id, x.Sequence, p.Id, p.Display, p.Description, p.UnitOfMeasure, p.Tracking.ToContract(), x.QuantityPer, x.QuantityRequired,
                    x.QuantityIssued, x.QuantityOpen, Available(p.Id), x.OperationSequence);
            }).ToList(),
            wo.Operations.OrderBy(o => o.Sequence).Select(o => new WorkOrderOperationDto(o.Id, o.Sequence, o.Description, centers.GetValueOrDefault(o.WorkCenterId) ?? "",
                o.LaborCategoryId is { } l ? categories.GetValueOrDefault(l) : null, o.PlannedHours(wo.Quantity), o.HoursReported, o.QuantityCompleted)).ToList(),
            reports.Select(r => new LaborReportDto(r.Id, ops.TryGetValue(r.OperationId, out var op) ? $"{op.Sequence} {op.Description}" : "", employees.GetValueOrDefault(r.EmployeeId) ?? "",
                r.WorkDate, r.Hours, r.QuantityCompleted, r.Cost, charge is null ? null : timesheet.GetValueOrDefault((r.EmployeeId, r.WorkDate)), r.Note)).ToList(),
            await MaterialQueries.TransactionsAsync(db, transactions, ct));
    }

    public async Task<WorkOrderDto> CreateWorkOrderAsync(SaveWorkOrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wo = await CreateCoreAsync(db, request, null, audit.Actor, UtcNow, cancellationToken);
        audit.Record(db, MaterialRules.WorkOrderEntity, wo.Id, "Created", after: new { wo.Number, wo.Quantity, wo.ProjectId, wo.WbsElementId, wo.DueDate });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, wo.Id, cancellationToken);
    }

    /// <summary>Adds a planned work order (validated) to the context; also used when MRP firms a planned make.</summary>
    internal static async Task<WorkOrder> CreateCoreAsync(IWorkbenchDbContext db, SaveWorkOrderRequest r, Guid? plannedOrderId, string actor, DateTime utcNow, CancellationToken ct)
    {
        var part = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.PartId, ct) ?? throw WorkbenchNotFoundException.For("Part", r.PartId);
        if (part.Source != PartSource.Make || !part.IsActive)
        {
            throw new ArgumentException($"{part.Display} isn't an active make part.");
        }

        var quantity = MaterialRules.Quantity(r.Quantity, "Quantity");
        if (part.Tracking == TrackingMode.Serial && quantity != decimal.Truncate(quantity))
        {
            throw new ArgumentException("A serial-tracked part is built in whole units.");
        }

        if (r.DueDate < r.StartDate)
        {
            throw new ArgumentException("The due date is before the start date.");
        }

        var wo = new WorkOrder
        {
            Number = await NextNumberAsync(db, ct),
            PartId = part.Id,
            Quantity = quantity,
            StartDate = r.StartDate,
            DueDate = r.DueDate,
            Notes = PurchasingSupport.Optional(r.Notes, "Notes", 2000),
            PlannedOrderId = plannedOrderId,
            CreatedBy = actor,
            CreatedAtUtc = utcNow,
        };
        if (r.WbsElementId is { } wid)
        {
            var wbs = await StockEngine.ChargeableWbsAsync(db, wid, direct: true, ct);
            wo.ProjectId = wbs.ProjectId;
            wo.WbsElementId = wbs.Id;
        }
        else
        {
            if (r.LaborChargeWbsElementId is { } lid)
            {
                wo.LaborChargeWbsElementId = (await StockEngine.ChargeableWbsAsync(db, lid, direct: false, ct)).Id;
            }

            if (r.BinId is { } bid)
            {
                var bin = await db.Set<Bin>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bid, ct) ?? throw WorkbenchNotFoundException.For("Bin", bid);
                wo.WarehouseId = bin.WarehouseId;
                wo.BinId = bin.Id;
            }
        }

        db.Set<WorkOrder>().Add(wo);
        return wo;
    }

    private static async Task<string> NextNumberAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var last = await db.Set<WorkOrder>().OrderByDescending(w => w.Number).Select(w => w.Number).FirstOrDefaultAsync(ct);
        var local = db.Set<WorkOrder>().Local.Select(w => w.Number).DefaultIfEmpty("").Max(StringComparer.Ordinal);
        var max = new[] { last, local }.OfType<string>().Select(n => n.StartsWith("WO-", StringComparison.Ordinal) && int.TryParse(n[3..], out var i) ? i : 0).DefaultIfEmpty(0).Max();
        return $"WO-{max + 1:D6}";
    }

    private static async Task<WorkOrder> LoadAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<WorkOrder>().Include(w => w.Components).Include(w => w.Operations).FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw WorkbenchNotFoundException.For("Work order", id);

    private static void RequireOpen(WorkOrder wo)
    {
        if (!wo.IsOpen)
        {
            throw new InvalidOperationException(wo.Status == WorkOrderStatus.Planned ? $"Release {wo.Number} first." : $"{wo.Number} is {wo.Status}.");
        }
    }

    public async Task<WorkOrderDto> ReleaseAsync(Guid workOrderId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wo = await LoadAsync(db, workOrderId, cancellationToken);
        if (wo.Status != WorkOrderStatus.Planned)
        {
            throw new InvalidOperationException($"{wo.Number} is already {wo.Status}.");
        }

        var bom = await db.Set<BillOfMaterial>().AsNoTracking().Include(b => b.Components).FirstOrDefaultAsync(b => b.PartId == wo.PartId && b.IsActive, cancellationToken)
            ?? throw new ArgumentException("The part has no active BOM; add or activate one before releasing.");
        var routing = await db.Set<Routing>().AsNoTracking().Include(r => r.Operations).FirstOrDefaultAsync(r => r.PartId == wo.PartId && r.IsActive, cancellationToken);
        wo.BomId = bom.Id;
        wo.RoutingId = routing?.Id;
        foreach (var c in bom.Components.Where(c => c.IsEffectiveOn(wo.StartDate)).OrderBy(c => c.Sequence))
        {
            AddTo(db, wo.Components, new WorkOrderComponent
            {
                WorkOrderId = wo.Id,
                Sequence = c.Sequence,
                PartId = c.ComponentPartId,
                QuantityPer = c.QuantityPer,
                QuantityRequired = c.RequiredFor(wo.Quantity),
                OperationSequence = c.OperationSequence,
            });
        }

        foreach (var o in routing?.Operations.OrderBy(o => o.Sequence) ?? Enumerable.Empty<RoutingOperation>())
        {
            AddTo(db, wo.Operations, new WorkOrderOperation
            {
                WorkOrderId = wo.Id,
                Sequence = o.Sequence,
                Description = o.Description,
                WorkCenterId = o.WorkCenterId,
                LaborCategoryId = o.LaborCategoryId,
                SetupHours = o.SetupHours,
                RunHoursPerUnit = o.RunHoursPerUnit,
            });
        }

        wo.Status = WorkOrderStatus.Released;
        wo.ReleasedAtUtc = UtcNow;
        audit.Record(db, MaterialRules.WorkOrderEntity, wo.Id, "Released", new { Status = nameof(WorkOrderStatus.Planned) },
            new { Status = wo.Status.ToString(), Bom = bom.Revision, Routing = routing?.Revision, Components = wo.Components.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, wo.Id, cancellationToken);
    }

    public async Task<WorkOrderDto> IssueComponentsAsync(Guid workOrderId, IssueComponentsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wo = await LoadAsync(db, workOrderId, cancellationToken);
        RequireOpen(wo);
        var lines = request.Lines is { Count: > 0 }
            ? request.Lines.Where(l => l.Quantity != 0).Select(l => (Component: wo.Components.FirstOrDefault(c => c.Id == l.ComponentId) ?? throw new ArgumentException("That component isn't on this work order."),
                Quantity: MaterialRules.Quantity(l.Quantity, "Quantity"), l.BalanceId)).ToList()
            : wo.Components.Where(c => c.QuantityOpen > 0).OrderBy(c => c.Sequence).Select(c => (Component: c, Quantity: c.QuantityOpen, BalanceId: (Guid?)null)).ToList();
        if (lines.Count == 0)
        {
            throw new ArgumentException("Every component is already issued.");
        }

        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var created = new List<InventoryTransaction>();
        var debit = wo.IsForProject ? engine.Accounts?.DirectMaterial : engine.Accounts?.Wip;
        foreach (var (component, quantity, balanceId) in lines)
        {
            var part = await engine.LoadPartAsync(component.PartId, cancellationToken);
            foreach (var (balance, qty) in engine.Pick(part, quantity, wo.ProjectId, balanceId))
            {
                var t = engine.Issue(part, balance, qty, InventoryTransactionType.IssueToWorkOrder, request.Date, debit, $"issued to {wo.Number}", wo.ProjectId, wo.WbsElementId);
                t.WorkOrderId = wo.Id;
                wo.MaterialCost += -t.Amount;
                created.Add(t);
            }

            component.QuantityIssued += quantity;
        }

        wo.Status = WorkOrderStatus.InProgress;
        await engine.FlushAsync(JournalSource.Manufacturing, created[0].Id, request.Date, $"Components issued to {wo.Number} ({created[0].Number})", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, wo.Id, cancellationToken);
    }

    public async Task<WorkOrderDto> ReportLaborAsync(Guid workOrderId, ReportLaborRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Hours <= 0 || request.Hours > 24 || decimal.Round(request.Hours, 2) != request.Hours)
        {
            throw new ArgumentException("Hours are more than 0 and at most 24, in hundredths.");
        }

        MaterialRules.Quantity(request.QuantityCompleted, "Quantity completed", allowZero: true);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wo = await LoadAsync(db, workOrderId, cancellationToken);
        RequireOpen(wo);
        var op = wo.Operations.FirstOrDefault(o => o.Id == request.OperationId) ?? throw new ArgumentException("That operation isn't on this work order.");
        var employeeId = request.EmployeeId
            ?? (await EmployeeLinks.FindForUserAsync(db, audit.Actor, cancellationToken))?.Id
            ?? throw new ArgumentException("Choose the employee who did the work.");
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Employee", employeeId);
        var rate = await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => r.EmployeeId == employee.Id && r.EffectiveFrom <= request.WorkDate)
            .OrderByDescending(r => r.EffectiveFrom).Select(r => (decimal?)r.HourlyCost).FirstOrDefaultAsync(cancellationToken) ?? 0m;
        var report = new WorkOrderLaborReport
        {
            WorkOrderId = wo.Id,
            OperationId = op.Id,
            EmployeeId = employee.Id,
            WorkDate = request.WorkDate,
            Hours = request.Hours,
            QuantityCompleted = request.QuantityCompleted,
            Cost = Money.Round(request.Hours * rate),
            Note = PurchasingSupport.Optional(request.Note, "The note", 500),
            ReportedBy = audit.Actor,
            CreatedAtUtc = UtcNow,
        };
        db.Set<WorkOrderLaborReport>().Add(report);
        op.HoursReported += report.Hours;
        op.QuantityCompleted += report.QuantityCompleted;
        wo.LaborCost += report.Cost;
        wo.Status = WorkOrderStatus.InProgress;
        if (!wo.IsForProject && report.Cost != 0)
        {
            var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
            if (engine.Accounts is { } a)
            {
                engine.AddLedger(new LedgerAmount(a.Wip, report.Cost, $"{wo.Number} op {op.Sequence} labor"), new LedgerAmount(a.LaborApplied, -report.Cost, $"{wo.Number} op {op.Sequence} labor applied"));
                report.JournalEntryId = (await engine.FlushAsync(JournalSource.Manufacturing, report.Id, request.WorkDate, $"Shop labor applied to {wo.Number}", cancellationToken))?.Id;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, wo.Id, cancellationToken);
    }

    /// <summary>What <paramref name="quantity"/> units relieve from WIP: standard cost, or their share of the actual cost collected.</summary>
    private static decimal ReliefValue(WorkOrder wo, Part part, decimal quantity)
    {
        if (part.CostMethod == CostMethod.Standard)
        {
            return Money.Round(quantity * part.StandardCost);
        }

        var open = wo.QuantityOpen;
        var wip = Math.Max(0, wo.WipBalance);
        return quantity >= open ? wip : Money.Round(wip / open * quantity);
    }

    public async Task<WorkOrderDto> ScrapAsync(Guid workOrderId, ScrapRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = PurchasingSupport.Required(request.Reason, "A reason", 500);
        var quantity = MaterialRules.Quantity(request.Quantity, "Quantity");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wo = await LoadAsync(db, workOrderId, cancellationToken);
        RequireOpen(wo);
        if (quantity > wo.QuantityOpen)
        {
            throw new ArgumentException($"Only {wo.QuantityOpen:0.####} is still open on {wo.Number}.");
        }

        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var part = await engine.LoadPartAsync(wo.PartId, cancellationToken);
        var t = engine.NewTransaction(InventoryTransactionType.Scrap, request.Date, part);
        t.WorkOrderId = wo.Id;
        t.Quantity = quantity;
        t.Reason = reason;
        t.ProjectId = wo.ProjectId;
        t.WbsElementId = wo.WbsElementId;
        if (!wo.IsForProject)
        {
            var value = ReliefValue(wo, part, quantity);
            t.UnitCost = Math.Round(value / quantity, 4);
            t.Amount = value;
            wo.ScrapCost += value;
            if (engine.Accounts is { } a)
            {
                engine.AddLedger(new LedgerAmount(a.Adjustment, value, $"{wo.Number} scrap: {reason}"), new LedgerAmount(a.Wip, -value, $"{wo.Number} scrap"));
            }
        }

        wo.QuantityScrapped += quantity;
        MarkCompleted(wo);
        await engine.FlushAsync(JournalSource.Manufacturing, t.Id, request.Date, $"Scrap on {wo.Number} ({t.Number})", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, wo.Id, cancellationToken);
    }

    private void MarkCompleted(WorkOrder wo)
    {
        if (wo.QuantityOpen == 0)
        {
            wo.Status = WorkOrderStatus.Completed;
            wo.CompletedAtUtc = UtcNow;
        }
    }

    public async Task<WorkOrderDto> CompleteAsync(Guid workOrderId, CompleteWorkOrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var quantity = MaterialRules.Quantity(request.Quantity, "Quantity");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wo = await LoadAsync(db, workOrderId, cancellationToken);
        RequireOpen(wo);
        if (quantity > wo.QuantityOpen)
        {
            throw new ArgumentException($"Only {wo.QuantityOpen:0.####} is still open on {wo.Number}.");
        }

        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var part = await engine.LoadPartAsync(wo.PartId, cancellationToken);
        var serials = await engine.SerialsAsync(part, quantity, request.Serials, cancellationToken);
        var created = new List<InventoryTransaction>();
        if (wo.IsForProject)
        {
            foreach (var serial in serials)
            {
                var (lot, s) = part.Tracking == TrackingMode.None ? ("", "") : StockEngine.Tracking(part, request.Lot, serial.Length > 0 ? serial : null);
                var t = engine.NewTransaction(InventoryTransactionType.WorkOrderShipment, request.Date, part);
                t.WorkOrderId = wo.Id;
                t.Quantity = s.Length > 0 ? 1 : quantity;
                t.Lot = lot;
                t.Serial = s;
                t.ProjectId = wo.ProjectId;
                t.WbsElementId = wo.WbsElementId;
                t.Reason = "Delivered to the project";
                created.Add(t);
            }
        }
        else
        {
            var (warehouse, bin) = await engine.LocationAsync(request.WarehouseId ?? wo.WarehouseId, request.BinId ?? wo.BinId, part, cancellationToken);
            var total = ReliefValue(wo, part, quantity);
            var left = total;
            for (var i = 0; i < serials.Count; i++)
            {
                var (lot, s) = StockEngine.Tracking(part, request.Lot, serials[i].Length > 0 ? serials[i] : null);
                var qty = s.Length > 0 ? 1 : quantity;
                var value = i == serials.Count - 1 ? left : Money.Round(total / quantity * qty);
                left -= value;
                var key = new StockKey(part.Id, warehouse.Id, bin.Id, lot, s, InventoryOwnership.General, null);
                var t = engine.Receive(part, key, qty, value, InventoryTransactionType.WorkOrderCompletion, request.Date, engine.Accounts?.Wip, engine.Accounts?.ManufacturingVariance, $"completed on {wo.Number}");
                t.WorkOrderId = wo.Id;
                created.Add(t);
            }

            wo.CompletedCost += total;
        }

        var traced = await db.Set<InventoryTransaction>().AsNoTracking()
            .Where(t => t.WorkOrderId == wo.Id && t.Type == InventoryTransactionType.IssueToWorkOrder && (t.Lot != "" || t.Serial != "")).Select(t => t.Id).ToListAsync(cancellationToken);
        foreach (var completion in created)
        {
            foreach (var component in traced)
            {
                db.Set<WorkOrderGenealogy>().Add(new WorkOrderGenealogy { WorkOrderId = wo.Id, CompletionTransactionId = completion.Id, ComponentTransactionId = component });
            }
        }

        wo.QuantityCompleted += quantity;
        MarkCompleted(wo);
        await engine.FlushAsync(JournalSource.Manufacturing, created[0].Id, request.Date, $"Completion on {wo.Number} ({created[0].Number})", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, wo.Id, cancellationToken);
    }

    public async Task<WorkOrderDto> CloseAsync(Guid workOrderId, CloseWorkOrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wo = await LoadAsync(db, workOrderId, cancellationToken);
        if (wo.Status is not (WorkOrderStatus.Completed or WorkOrderStatus.InProgress))
        {
            throw new InvalidOperationException($"{wo.Number} is {wo.Status}; close completed (or short-close in-progress) work orders.");
        }

        if (wo.Status == WorkOrderStatus.InProgress && wo.QuantityCompleted == 0)
        {
            throw new InvalidOperationException($"Nothing is completed on {wo.Number}; complete or scrap it before closing.");
        }

        var variance = wo.IsForProject ? 0 : wo.WipBalance;
        if (variance != 0)
        {
            var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
            if (engine.Accounts is { } a)
            {
                engine.AddLedger(new LedgerAmount(a.ManufacturingVariance, variance, $"{wo.Number} production variance"), new LedgerAmount(a.Wip, -variance, $"{wo.Number} closed"));
                await engine.FlushAsync(JournalSource.Manufacturing, wo.Id, request.Date, $"Close {wo.Number}: production variance", cancellationToken);
            }

            wo.VarianceCost += variance;
        }

        var before = wo.Status.ToString();
        wo.Status = WorkOrderStatus.Closed;
        wo.ClosedAtUtc = UtcNow;
        audit.Record(db, MaterialRules.WorkOrderEntity, wo.Id, "Closed", new { Status = before }, new { Status = wo.Status.ToString(), Variance = variance });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, wo.Id, cancellationToken);
    }

    public async Task<WorkOrderDto> CancelAsync(Guid workOrderId, CancelWorkOrderRequest request, CancellationToken cancellationToken = default)
    {
        var reason = PurchasingSupport.Required(request?.Reason, "A reason", 500);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wo = await LoadAsync(db, workOrderId, cancellationToken);
        if (wo.Status is not (WorkOrderStatus.Planned or WorkOrderStatus.Released)
            || await db.Set<InventoryTransaction>().AnyAsync(t => t.WorkOrderId == wo.Id, cancellationToken)
            || await db.Set<WorkOrderLaborReport>().AnyAsync(r => r.WorkOrderId == wo.Id, cancellationToken))
        {
            throw new InvalidOperationException($"{wo.Number} has components issued or work reported; complete and close it instead.");
        }

        var before = wo.Status.ToString();
        wo.Status = WorkOrderStatus.Cancelled;
        audit.Record(db, MaterialRules.WorkOrderEntity, wo.Id, "Cancelled", new { Status = before }, new { Status = wo.Status.ToString() }, reason);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, wo.Id, cancellationToken);
    }
}
