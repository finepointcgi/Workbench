using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Materials;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Materials;

internal static class MaterialRules
{
    public const string PartEntity = nameof(Part);
    public const string WarehouseEntity = nameof(Warehouse);
    public const string WorkOrderEntity = nameof(WorkOrder);
    public const string BomEntity = nameof(BillOfMaterial);

    public static string Code(string? value, string what, int max) =>
        PurchasingSupport.Required(value, what, max).ToUpperInvariant();

    public static decimal Quantity(decimal q, string what, bool allowZero = false)
    {
        if (q < 0 || (!allowZero && q == 0))
        {
            throw new ArgumentException($"{what} must be {(allowZero ? "zero or more" : "greater than 0")}.");
        }

        return decimal.Round(q, 4) != q ? throw new ArgumentException($"{what} can have at most four decimal places.") : q;
    }

    public static decimal Cost(decimal c, string what)
    {
        if (c < 0)
        {
            throw new ArgumentException($"{what} can't be negative.");
        }

        return decimal.Round(c, 4) != c ? throw new ArgumentException($"{what} can have at most four decimal places.") : c;
    }
}

/// <summary>Part master, warehouses and bins, forecasts and project material requirements.</summary>
public sealed class PartService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : IPartService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<PartSummaryDto>> ListAsync(PartQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<Part>().AsNoTracking();
        if (query.ActiveOnly)
        {
            q = q.Where(p => p.IsActive);
        }

        if (query.Source is { } source)
        {
            var s = source.ToDomain();
            q = q.Where(p => p.Source == s);
        }

        var parts = await q.ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            parts = parts.Where(p => p.Number.Contains(term, StringComparison.OrdinalIgnoreCase) || p.Description.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        parts = parts.OrderBy(p => p.Number, StringComparer.Ordinal).ThenBy(p => p.Revision, StringComparer.Ordinal).ToList();
        return await MaterialQueries.PartSummariesAsync(db, parts, cancellationToken);
    }

    public async Task<PartDto> GetAsync(Guid partId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, partId, cancellationToken);
    }

    internal static async Task<PartDto> DetailAsync(IWorkbenchDbContext db, Guid partId, CancellationToken ct)
    {
        var part = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == partId, ct) ?? throw WorkbenchNotFoundException.For("Part", partId);
        var summary = (await MaterialQueries.PartSummariesAsync(db, [part], ct))[0];
        var balances = await db.Set<InventoryBalance>().AsNoTracking().Where(b => b.PartId == partId).ToListAsync(ct);
        var recent = await db.Set<InventoryTransaction>().AsNoTracking().Where(t => t.PartId == partId)
            .OrderByDescending(t => t.Number).Take(25).ToListAsync(ct);
        string? location = null;
        if (part.DefaultBinId is { } bid)
        {
            location = await db.Set<Bin>().AsNoTracking().Where(b => b.Id == bid)
                .Join(db.Set<Warehouse>().AsNoTracking(), b => b.WarehouseId, w => w.Id, (b, w) => w.Code + " / " + b.Code).FirstOrDefaultAsync(ct);
        }

        var supply = await MrpData.OpenPurchaseSupplyAsync(db, ct);
        var onOrder = supply.Where(s => s.PartId == partId).Sum(s => s.Quantity);
        var allocated = await db.Set<WorkOrderComponent>().AsNoTracking().Where(c => c.PartId == partId)
            .Join(db.Set<WorkOrder>().AsNoTracking().Where(w => w.Status == WorkOrderStatus.Released || w.Status == WorkOrderStatus.InProgress), c => c.WorkOrderId, w => w.Id, (c, w) => c)
            .SumAsync(c => c.QuantityRequired - c.QuantityIssued, ct);
        return new PartDto(summary, part.PreferredVendorId, part.SafetyStock, part.MinimumOrderQuantity, part.OrderMultiple, part.DefaultWarehouseId, part.DefaultBinId,
            location, part.Notes, onOrder, Math.Max(0, allocated),
            await MaterialQueries.BalancesAsync(db, balances, ct), await MaterialQueries.TransactionsAsync(db, recent, ct),
            await MaterialQueries.WhereUsedAsync(db, partId, ct));
    }

    public async Task<PartDto> CreateAsync(SavePartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var part = new Part { Number = "", Description = "", CreatedAtUtc = UtcNow };
        await ApplyAsync(db, part, request, isNew: true, cancellationToken);
        db.Set<Part>().Add(part);
        audit.Record(db, MaterialRules.PartEntity, part.Id, "Created", after: Snapshot(part));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, part.Id, cancellationToken);
    }

    public async Task<PartDto> UpdateAsync(Guid partId, SavePartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var part = await db.Set<Part>().FirstOrDefaultAsync(p => p.Id == partId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Part", partId);
        var before = Snapshot(part);
        await ApplyAsync(db, part, request, isNew: false, cancellationToken);
        audit.Record(db, MaterialRules.PartEntity, part.Id, "Updated", before, Snapshot(part));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, part.Id, cancellationToken);
    }

    private async Task ApplyAsync(IWorkbenchDbContext db, Part part, SavePartRequest r, bool isNew, CancellationToken ct)
    {
        var number = MaterialRules.Code(r.Number, "Part number", 64);
        var revision = (r.Revision ?? "").Trim().ToUpperInvariant();
        if (revision.Length > 16)
        {
            throw new ArgumentException("A revision is at most 16 characters.");
        }

        if (await db.Set<Part>().AnyAsync(p => p.Number == number && p.Revision == revision && p.Id != part.Id, ct))
        {
            throw new ArgumentException($"There's already a part {number}{(revision.Length > 0 ? $" rev {revision}" : "")}.");
        }

        var hasStock = !isNew && await db.Set<InventoryBalance>().AnyAsync(b => b.PartId == part.Id, ct);
        var hasHistory = !isNew && await db.Set<InventoryTransaction>().AnyAsync(t => t.PartId == part.Id, ct);
        var tracking = r.Tracking.ToDomain();
        var method = r.CostMethod.ToDomain();
        var standard = MaterialRules.Cost(r.StandardCost, "Standard cost");
        if (hasStock && tracking != part.Tracking)
        {
            throw new ArgumentException("Tracking can't change while the part has stock.");
        }

        if (hasHistory && method != part.CostMethod)
        {
            throw new ArgumentException("The cost method can't change once the part has inventory history.");
        }

        if (hasStock && method == CostMethod.Standard && standard != part.StandardCost)
        {
            throw new ArgumentException("The part has stock: change its standard cost with a revaluation (Finance), which posts the difference.");
        }

        if ((part.Number != number || part.Revision != revision) && hasHistory)
        {
            throw new ArgumentException("The part number and revision can't change once the part has inventory history; add a new revision instead.");
        }

        part.Number = number;
        part.Revision = revision;
        part.Description = PurchasingSupport.Required(r.Description, "Description", 500);
        part.UnitOfMeasure = MaterialRules.Code(r.UnitOfMeasure, "Unit of measure", 16);
        part.Source = r.Source.ToDomain();
        part.Tracking = tracking;
        part.CostMethod = method;
        part.StandardCost = standard;
        if (!hasHistory)
        {
            part.AverageCost = r.AverageCost is { } avg ? MaterialRules.Cost(avg, "Average cost") : standard;
        }

        if (r.LeadTimeDays is < 0 or > 3650)
        {
            throw new ArgumentException("Lead time is 0 to 3650 days.");
        }

        part.LeadTimeDays = r.LeadTimeDays;
        part.SafetyStock = MaterialRules.Quantity(r.SafetyStock, "Safety stock", allowZero: true);
        part.MinimumOrderQuantity = MaterialRules.Quantity(r.MinimumOrderQuantity, "Minimum order quantity", allowZero: true);
        part.OrderMultiple = MaterialRules.Quantity(r.OrderMultiple, "Order multiple", allowZero: true);
        if (r.PreferredVendorId is { } vid && !await db.Set<Vendor>().AnyAsync(v => v.Id == vid, ct))
        {
            throw WorkbenchNotFoundException.For("Vendor", vid);
        }

        part.PreferredVendorId = r.PreferredVendorId;
        if (r.DefaultBinId is { } bid)
        {
            var bin = await db.Set<Bin>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bid, ct) ?? throw WorkbenchNotFoundException.For("Bin", bid);
            if (r.DefaultWarehouseId is { } w && w != bin.WarehouseId)
            {
                throw new ArgumentException("The default bin isn't in the default warehouse.");
            }

            part.DefaultWarehouseId = bin.WarehouseId;
            part.DefaultBinId = bin.Id;
        }
        else
        {
            if (r.DefaultWarehouseId is { } w && !await db.Set<Warehouse>().AnyAsync(x => x.Id == w, ct))
            {
                throw WorkbenchNotFoundException.For("Warehouse", w);
            }

            part.DefaultWarehouseId = r.DefaultWarehouseId;
            part.DefaultBinId = null;
        }

        part.Notes = PurchasingSupport.Optional(r.Notes, "Notes", 2000);
        part.IsActive = r.IsActive;
        part.UpdatedAtUtc = UtcNow;
    }

    private static object Snapshot(Part p) => new
    {
        p.Number,
        p.Revision,
        p.Description,
        p.UnitOfMeasure,
        Source = p.Source.ToString(),
        Tracking = p.Tracking.ToString(),
        CostMethod = p.CostMethod.ToString(),
        p.StandardCost,
        p.PreferredVendorId,
        p.LeadTimeDays,
        p.SafetyStock,
        p.MinimumOrderQuantity,
        p.OrderMultiple,
        p.DefaultBinId,
        p.IsActive,
    };

    // ---- Warehouses and bins ----

    public async Task<IReadOnlyList<WarehouseDto>> ListWarehousesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await WarehousesAsync(db, cancellationToken);
    }

    internal static async Task<IReadOnlyList<WarehouseDto>> WarehousesAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var bins = await db.Set<Bin>().AsNoTracking().ToListAsync(ct);
        return (await db.Set<Warehouse>().AsNoTracking().ToListAsync(ct)).OrderBy(w => w.Code, StringComparer.Ordinal)
            .Select(w => new WarehouseDto(w.Id, w.Code, w.Name, w.Address, w.IsActive,
                bins.Where(b => b.WarehouseId == w.Id).OrderBy(b => b.Code, StringComparer.Ordinal).Select(b => new BinDto(b.Id, b.WarehouseId, b.Code, b.Description, b.IsActive)).ToList()))
            .ToList();
    }

    public async Task<WarehouseDto> SaveWarehouseAsync(Guid? warehouseId, SaveWarehouseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var w = warehouseId is { } id
            ? await db.Set<Warehouse>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Warehouse", id)
            : new Warehouse { Code = "", Name = "" };
        var before = warehouseId is null ? null : new { w.Code, w.Name, w.Address, w.IsActive };
        w.Code = MaterialRules.Code(request.Code, "Warehouse code", 32);
        w.Name = PurchasingSupport.Required(request.Name, "Name", 200);
        w.Address = PurchasingSupport.Optional(request.Address, "Address", 500);
        if (!request.IsActive && w.IsActive && await db.Set<InventoryBalance>().AnyAsync(b => b.WarehouseId == w.Id, cancellationToken))
        {
            throw new ArgumentException($"{w.Code} has stock; move it before deactivating the warehouse.");
        }

        w.IsActive = request.IsActive;
        if (await db.Set<Warehouse>().AnyAsync(x => x.Code == w.Code && x.Id != w.Id, cancellationToken))
        {
            throw new ArgumentException($"There's already a warehouse {w.Code}.");
        }

        if (warehouseId is null)
        {
            db.Set<Warehouse>().Add(w);
        }

        audit.Record(db, MaterialRules.WarehouseEntity, w.Id, warehouseId is null ? "Created" : "Updated", before, new { w.Code, w.Name, w.Address, w.IsActive });
        await db.SaveChangesAsync(cancellationToken);
        return (await WarehousesAsync(db, cancellationToken)).First(x => x.Id == w.Id);
    }

    public async Task<WarehouseDto> SaveBinAsync(Guid warehouseId, Guid? binId, SaveBinRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Warehouse>().AnyAsync(w => w.Id == warehouseId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Warehouse", warehouseId);
        }

        var bin = binId is { } id
            ? await db.Set<Bin>().FirstOrDefaultAsync(b => b.Id == id && b.WarehouseId == warehouseId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Bin", id)
            : new Bin { WarehouseId = warehouseId, Code = "" };
        bin.Code = MaterialRules.Code(request.Code, "Bin code", 32);
        bin.Description = PurchasingSupport.Optional(request.Description, "Description", 200);
        if (!request.IsActive && bin.IsActive && await db.Set<InventoryBalance>().AnyAsync(b => b.BinId == bin.Id, cancellationToken))
        {
            throw new ArgumentException($"Bin {bin.Code} has stock; move it before deactivating the bin.");
        }

        bin.IsActive = request.IsActive;
        if (await db.Set<Bin>().AnyAsync(b => b.WarehouseId == warehouseId && b.Code == bin.Code && b.Id != bin.Id, cancellationToken))
        {
            throw new ArgumentException($"There's already a bin {bin.Code} in that warehouse.");
        }

        if (binId is null)
        {
            db.Set<Bin>().Add(bin);
        }

        audit.Record(db, MaterialRules.WarehouseEntity, warehouseId, binId is null ? "BinCreated" : "BinUpdated", after: new { bin.Code, bin.Description, bin.IsActive });
        await db.SaveChangesAsync(cancellationToken);
        return (await WarehousesAsync(db, cancellationToken)).First(x => x.Id == warehouseId);
    }

    // ---- Forecasts and requirements ----

    public async Task<IReadOnlyList<ForecastDto>> ListForecastsAsync(Guid? partId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<PartForecast>().AsNoTracking().Where(f => partId == null || f.PartId == partId)
            .Join(db.Set<Part>().AsNoTracking(), f => f.PartId, p => p.Id, (f, p) => new { f, p.Number }).ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.f.Date).ThenBy(r => r.Number, StringComparer.Ordinal)
            .Select(r => new ForecastDto(r.f.Id, r.f.PartId, r.Number, r.f.Date, r.f.Quantity, r.f.Note)).ToList();
    }

    public async Task<ForecastDto> AddForecastAsync(SaveForecastRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var part = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PartId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Part", request.PartId);
        var f = new PartForecast
        {
            PartId = part.Id,
            Date = request.Date,
            Quantity = MaterialRules.Quantity(request.Quantity, "Forecast quantity"),
            Note = PurchasingSupport.Optional(request.Note, "Note", 500),
            CreatedBy = audit.Actor,
            CreatedAtUtc = UtcNow,
        };
        db.Set<PartForecast>().Add(f);
        await db.SaveChangesAsync(cancellationToken);
        return new ForecastDto(f.Id, f.PartId, part.Number, f.Date, f.Quantity, f.Note);
    }

    public async Task DeleteForecastAsync(Guid forecastId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var f = await db.Set<PartForecast>().FirstOrDefaultAsync(x => x.Id == forecastId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Forecast", forecastId);
        db.Set<PartForecast>().Remove(f);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MaterialRequirementDto>> ListRequirementsAsync(Guid? projectId = null, bool openOnly = true, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<ProjectMaterialRequirement>().AsNoTracking()
            .Where(r => (projectId == null || r.ProjectId == projectId) && (!openOnly || !r.IsClosed)).ToListAsync(cancellationToken);
        return await RequirementDtosAsync(db, rows, cancellationToken);
    }

    private static async Task<IReadOnlyList<MaterialRequirementDto>> RequirementDtosAsync(IWorkbenchDbContext db, IReadOnlyList<ProjectMaterialRequirement> rows, CancellationToken ct)
    {
        var partIds = rows.Select(r => r.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var wbsIds = rows.Select(r => r.WbsElementId).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var projectIds = rows.Select(r => r.ProjectId).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        return rows.OrderBy(r => r.NeededBy).ThenBy(r => parts[r.PartId].Number, StringComparer.Ordinal)
            .Select(r => new MaterialRequirementDto(r.Id, r.ProjectId, projects.GetValueOrDefault(r.ProjectId) ?? "", r.WbsElementId, wbs.GetValueOrDefault(r.WbsElementId) ?? "",
                r.PartId, parts[r.PartId].Display, parts[r.PartId].Description, r.Quantity, r.QuantityIssued, r.OpenQuantity, r.NeededBy, r.Note, r.IsClosed)).ToList();
    }

    public async Task<MaterialRequirementDto> AddRequirementAsync(SaveMaterialRequirementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wbs = await StockEngine.ChargeableWbsAsync(db, request.WbsElementId, direct: true, cancellationToken);
        if (!await db.Set<Part>().AnyAsync(p => p.Id == request.PartId && p.IsActive, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Part", request.PartId);
        }

        var r = new ProjectMaterialRequirement
        {
            ProjectId = wbs.ProjectId,
            WbsElementId = wbs.Id,
            PartId = request.PartId,
            Quantity = MaterialRules.Quantity(request.Quantity, "Quantity"),
            NeededBy = request.NeededBy,
            Note = PurchasingSupport.Optional(request.Note, "Note", 500),
            CreatedBy = audit.Actor,
            CreatedAtUtc = UtcNow,
        };
        db.Set<ProjectMaterialRequirement>().Add(r);
        await db.SaveChangesAsync(cancellationToken);
        return (await RequirementDtosAsync(db, [r], cancellationToken))[0];
    }

    public async Task<MaterialRequirementDto> CloseRequirementAsync(Guid requirementId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var r = await db.Set<ProjectMaterialRequirement>().FirstOrDefaultAsync(x => x.Id == requirementId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Material requirement", requirementId);
        r.IsClosed = true;
        await db.SaveChangesAsync(cancellationToken);
        return (await RequirementDtosAsync(db, [r], cancellationToken))[0];
    }

    public async Task<MaterialsOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendors = (await db.Set<Vendor>().AsNoTracking().ToListAsync(cancellationToken)).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => new MaterialOption(v.Id, v.Code, v.Name)).ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => p.Status == ProjectStatus.Active).ToListAsync(cancellationToken);
        var projectCodes = projects.ToDictionary(p => p.Id, p => p.Code);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.IsOpen && w.IsChargeable).ToListAsync(cancellationToken);
        var parents = (await db.Set<WbsElement>().AsNoTracking().Where(w => w.ParentId != null).Select(w => w.ParentId!.Value).ToListAsync(cancellationToken)).ToHashSet();
        var leaves = wbs.Where(w => projectCodes.ContainsKey(w.ProjectId) && !parents.Contains(w.Id)).OrderBy(w => w.Code, StringComparer.Ordinal).ToList();
        MaterialChargeOption Charge(WbsElement w) => new(w.Id, w.ProjectId, projectCodes[w.ProjectId], w.Code, w.Name);
        var categories = (await db.Set<LaborCategory>().AsNoTracking().Where(c => c.IsActive).ToListAsync(cancellationToken)).OrderBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => new MaterialOption(c.Id, c.Code, c.Name)).ToList();
        var centers = (await db.Set<WorkCenter>().AsNoTracking().Where(c => c.IsActive).ToListAsync(cancellationToken)).OrderBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => new MaterialOption(c.Id, c.Code, c.Name)).ToList();
        var employees = (await db.Set<Employee>().AsNoTracking().Where(e => e.TerminationDate == null).ToListAsync(cancellationToken)).OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(e => new MaterialOption(e.Id, e.EmployeeNumber, e.DisplayName)).ToList();
        var orgs = (await db.Set<OrgUnit>().AsNoTracking().ToListAsync(cancellationToken)).OrderBy(o => o.Code, StringComparer.Ordinal)
            .Select(o => new MaterialOption(o.Id, o.Code, o.Name)).ToList();
        return new MaterialsOptionsDto(vendors,
            projects.OrderBy(p => p.Code, StringComparer.Ordinal).Select(p => new MaterialOption(p.Id, p.Code, p.Name)).ToList(),
            leaves.Where(w => w.Kind == ChargeKind.Direct).Select(Charge).ToList(),
            leaves.Where(w => w.Kind == ChargeKind.Indirect).Select(Charge).ToList(),
            await WarehousesAsync(db, cancellationToken), categories, centers, employees, orgs);
    }
}
