using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Contracts;
using Workbench.Contracts.Materials;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Materials;

/// <summary>Material cost charged to projects, for the financials and burdened cost (direct ODC, like vendor materials).</summary>
public static class MaterialCostActuals
{
    public sealed record Row(Guid ProjectId, Guid WbsElementId, DateOnly Date, decimal Amount);

    /// <summary>
    /// Material issued to project WBS elements (directly or through project work orders) at cost: the issues' amounts,
    /// positive. Government-furnished material is issued at 0 and never appears.
    /// </summary>
    public static async Task<List<Row>> LoadAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid>? projectIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var q = db.Set<InventoryTransaction>().AsNoTracking()
            .Where(t => t.ProjectId != null && t.WbsElementId != null && t.Amount != 0 && t.Date >= from && t.Date <= to
                && (t.Type == InventoryTransactionType.IssueToProject || t.Type == InventoryTransactionType.IssueToWorkOrder || t.Type == InventoryTransactionType.ReturnFromWorkOrder
                    || t.Type == InventoryTransactionType.SalesShipment // Cost of goods shipped on a project's sales order.
                    || t.Type == InventoryTransactionType.LandedCost)); // Landed cost that followed units already issued to the project.
        if (projectIds is not null)
        {
            var ids = projectIds.ToList();
            q = q.Where(t => ids.Contains(t.ProjectId!.Value));
        }

        return (await q.Select(t => new { t.ProjectId, t.WbsElementId, t.Date, t.Amount }).ToListAsync(ct))
            .Select(t => new Row(t.ProjectId!.Value, t.WbsElementId!.Value, t.Date, -t.Amount)).ToList();
    }
}

public sealed class MaterialReportService(IWorkbenchDbContextFactory dbFactory, MaterialsLedger ledger, TimeProvider clock) : IMaterialReportService
{
    public async Task<InventoryValuationDto> ValuationAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var parts = await db.Set<Part>().AsNoTracking().ToListAsync(cancellationToken);
        var balances = await db.Set<InventoryBalance>().AsNoTracking().Where(b => b.Ownership != InventoryOwnership.GovernmentFurnished && b.Quantity > 0).ToListAsync(cancellationToken);
        var rows = parts.Select(p =>
            {
                var mine = balances.Where(b => b.PartId == p.Id).ToList();
                var contract = mine.Where(b => b.Ownership == InventoryOwnership.Contract).Sum(b => b.Quantity);
                return new ValuationRowDto(p.Id, p.Display, p.Description, p.CostMethod.ToContract(), mine.Sum(b => b.Quantity), p.CurrentUnitCost, p.InventoryValue,
                    contract, Money.Round(contract * p.CurrentUnitCost));
            })
            .Where(r => r.Quantity != 0 || r.Value != 0)
            .OrderBy(r => r.PartNumber, StringComparer.Ordinal).ToList();
        var wip = (await db.Set<WorkOrder>().AsNoTracking().Where(w => w.ProjectId == null && w.Status != WorkOrderStatus.Cancelled && w.Status != WorkOrderStatus.Closed).ToListAsync(cancellationToken))
            .Sum(w => w.WipBalance);
        var notes = new List<string> { "Government-furnished material is listed separately and never valued (FAR 52.245-1)." };
        decimal glInventory = 0, glWip = 0;
        if (await ledger.AccountsAsync(db, cancellationToken) is { } a)
        {
            glInventory = await BalanceAsync(db, [a.Inventory, a.FinishedGoods], cancellationToken);
            glWip = await BalanceAsync(db, [a.Wip], cancellationToken);
            var clearing = await BalanceAsync(db, [a.ReceivedNotInvoiced], cancellationToken);
            if (clearing != 0)
            {
                notes.Add($"Received not invoiced (clearing): {-clearing:C} received into inventory and not yet billed by vendors (purchase price differences on billed receipts stay here until cleared).");
            }

            var total = rows.Sum(r => r.Value);
            if (total != glInventory)
            {
                notes.Add($"The GL inventory accounts differ from the subledger by {glInventory - total:C} (manual entries to the inventory accounts).");
            }
        }
        else
        {
            notes.Add("There's no chart of accounts, so nothing posts to the GL.");
        }

        return new InventoryValuationDto(rows, rows.Sum(r => r.Value), wip, glInventory, glWip, notes);
    }

    private static async Task<decimal> BalanceAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> accounts, CancellationToken ct)
    {
        var ids = accounts.ToList();
        var lines = await db.Set<JournalLine>().AsNoTracking().Where(l => ids.Contains(l.AccountId))
            .Join(db.Set<JournalEntry>().AsNoTracking().Where(e => e.Status != JournalStatus.Draft), l => l.JournalEntryId, e => e.Id, (l, e) => new { l.Debit, l.Credit })
            .ToListAsync(ct);
        return lines.Sum(l => l.Debit - l.Credit);
    }

    public async Task<InventoryAgingDto> AgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        var date = asOf ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var parts = await db.Set<Part>().AsNoTracking().ToDictionaryAsync(p => p.Id, cancellationToken);
        var balances = await db.Set<InventoryBalance>().AsNoTracking().Where(b => b.Ownership != InventoryOwnership.GovernmentFurnished && b.Quantity > 0).ToListAsync(cancellationToken);
        var rows = balances.GroupBy(b => b.PartId).Select(g =>
            {
                var p = parts[g.Key];
                decimal Bucket(int min, int max) => Money.Round(g.Where(b => date.DayNumber - b.ReceivedOn.DayNumber is var d && d >= min && d <= max).Sum(b => b.Quantity) * p.CurrentUnitCost);
                var a = Bucket(int.MinValue, 90);
                var b = Bucket(91, 180);
                var c = Bucket(181, 365);
                var d = Bucket(366, int.MaxValue);
                return new AgingRowDto(p.Id, p.Display, p.Description, a, b, c, d, a + b + c + d);
            })
            .OrderByDescending(r => r.Over365).ThenBy(r => r.PartNumber, StringComparer.Ordinal).ToList();
        return new InventoryAgingDto(date, rows, rows.Sum(r => r.Days0To90), rows.Sum(r => r.Days91To180), rows.Sum(r => r.Days181To365), rows.Sum(r => r.Over365), rows.Sum(r => r.Total));
    }

    public async Task<IReadOnlyList<GfmRowDto>> GovernmentFurnishedAsync(Guid? projectId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var balances = await db.Set<InventoryBalance>().AsNoTracking()
            .Where(b => b.Ownership == InventoryOwnership.GovernmentFurnished && b.Quantity > 0 && (projectId == null || b.OwnerProjectId == projectId)).ToListAsync(cancellationToken);
        var dtos = await MaterialQueries.BalancesAsync(db, balances, cancellationToken);
        var projects = await db.Set<Project>().AsNoTracking().ToDictionaryAsync(p => p.Id, cancellationToken);
        return dtos.Select(b => new GfmRowDto(b.OwnerProjectCode ?? "", b.OwnerProjectId is { } op && projects.TryGetValue(op, out var p) ? p.Name : "",
                b.PartNumber, b.PartDescription, b.UnitOfMeasure, b.Location, b.Lot, b.Serial, b.Quantity, b.ReceivedOn, b.GovernmentReference))
            .OrderBy(r => r.ProjectCode, StringComparer.Ordinal).ThenBy(r => r.PartNumber, StringComparer.Ordinal).ToList();
    }

    public async Task<WipReportDto> WipAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var orders = await db.Set<WorkOrder>().AsNoTracking().Where(w => w.Status == WorkOrderStatus.Released || w.Status == WorkOrderStatus.InProgress || w.Status == WorkOrderStatus.Completed)
            .ToListAsync(cancellationToken);
        var partIds = orders.Select(o => o.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Display, cancellationToken);
        var projects = await db.Set<Project>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);
        var rows = orders.OrderBy(o => o.Number, StringComparer.Ordinal).Select(o => new WipRowDto(o.Id, o.Number, parts.GetValueOrDefault(o.PartId) ?? "", o.Status.ToContract(),
            o.ProjectId is { } p ? projects.GetValueOrDefault(p) : null, o.Quantity, o.QuantityCompleted, o.MaterialCost, o.LaborCost, o.ScrapCost, o.CompletedCost, o.VarianceCost,
            o.IsForProject ? 0 : o.WipBalance, o.IsForProject)).ToList();
        var glWip = 0m;
        if (await ledger.AccountsAsync(db, cancellationToken) is { } a)
        {
            glWip = await BalanceAsync(db, [a.Wip], cancellationToken);
        }

        return new WipReportDto(rows, rows.Where(r => !r.IsForProject).Sum(r => r.WipBalance), rows.Where(r => r.IsForProject).Sum(r => r.MaterialCost + r.LaborCost), glWip);
    }

    public async Task<ProjectMaterialCostDto> ProjectMaterialCostAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Project", projectId);
        var txns = await db.Set<InventoryTransaction>().AsNoTracking()
            .Where(t => t.ProjectId == projectId && (t.Type == InventoryTransactionType.IssueToProject || t.Type == InventoryTransactionType.IssueToWorkOrder || t.Type == InventoryTransactionType.ReturnFromWorkOrder
                || t.Type == InventoryTransactionType.LandedCost))
            .ToListAsync(cancellationToken);
        var partIds = txns.Select(t => t.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToDictionaryAsync(w => w.Id, w => w.Code, cancellationToken);
        var rows = txns.GroupBy(t => (t.WbsElementId, t.PartId)).Select(g => new ProjectMaterialCostRowDto(
                g.Key.WbsElementId is { } w ? wbs.GetValueOrDefault(w) ?? "" : "", parts[g.Key.PartId].Display, parts[g.Key.PartId].Description,
                -g.Where(t => t.Ownership != InventoryOwnership.GovernmentFurnished).Sum(t => t.Quantity), -g.Sum(t => t.Amount),
                -g.Where(t => t.Ownership == InventoryOwnership.GovernmentFurnished).Sum(t => t.Quantity)))
            .OrderBy(r => r.WbsCode, StringComparer.Ordinal).ThenBy(r => r.PartNumber, StringComparer.Ordinal).ToList();
        return new ProjectMaterialCostDto(projectId, project.Code, rows, rows.Sum(r => r.Amount));
    }
}
