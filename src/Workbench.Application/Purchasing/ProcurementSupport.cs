using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>Shared rules for requisitions, purchase orders, receiving, the three-way match and commitments.</summary>
internal static class ProcurementSupport
{
    public const string RequisitionEntity = nameof(PurchaseRequisition);
    public const string OrderEntity = nameof(PurchaseOrder);

    public static RequisitionState ToContract(this RequisitionStatus s) => (RequisitionState)(int)s;
    public static RequisitionStatus ToDomain(this RequisitionState s) => (RequisitionStatus)(int)s;
    public static RequisitionApprovalStage ToContract(this RequisitionStage s) => (RequisitionApprovalStage)(int)s;
    public static PurchaseOrderState ToContract(this PurchaseOrderStatus s) => (PurchaseOrderState)(int)s;
    public static PurchaseOrderStatus ToDomain(this PurchaseOrderState s) => (PurchaseOrderStatus)(int)s;

    /// <summary>Order statuses that carry commitments and can be received and billed.</summary>
    public static readonly PurchaseOrderStatus[] OpenStatuses = [PurchaseOrderStatus.Issued, PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Received];

    public static decimal Quantity(decimal quantity, string what)
    {
        if (quantity <= 0)
        {
            throw new ArgumentException($"{what} must be greater than 0.");
        }

        return decimal.Round(quantity, 4) != quantity ? throw new ArgumentException($"{what} can have at most four decimal places.") : quantity;
    }

    public static decimal UnitPrice(decimal price, string what)
    {
        if (price <= 0)
        {
            throw new ArgumentException($"{what} must be greater than 0.");
        }

        return decimal.Round(price, 4) != price ? throw new ArgumentException($"{what} can have at most four decimal places.") : price;
    }

    public static decimal Extend(decimal quantity, decimal unitPrice) => decimal.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);

    /// <summary>The validated charging of a requisition or PO line.</summary>
    public sealed record Charge(Guid? ProjectId, Guid? WbsElementId, OdcType? OdcType, Guid? AccountId, Guid? OrgUnitId);

    /// <summary>
    /// A line charges either an open, chargeable lowest-level WBS element of an active project (with an ODC type, default
    /// Materials) or an active expense/asset detail account with an organization (<paramref name="defaultOrg"/> when none).
    /// </summary>
    public static async Task<Charge> ValidateChargeAsync(
        IWorkbenchDbContext db, string prefix, Guid? wbsId, OdcType? odcType, Guid? accountId, Guid? orgId, Guid? defaultOrg, CancellationToken ct)
    {
        if ((wbsId is null) == (accountId is null))
        {
            throw new ArgumentException($"{prefix}: choose either a project WBS element or a GL account.");
        }

        if (wbsId is { } wid)
        {
            var wbs = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == wid, ct) ?? throw WorkbenchNotFoundException.For("WBS element", wid);
            var project = await db.Set<Project>().AsNoTracking().FirstAsync(p => p.Id == wbs.ProjectId, ct);
            if (project.Status != ProjectStatus.Active || !wbs.IsOpen || !wbs.IsChargeable || await db.Set<WbsElement>().AnyAsync(w => w.ParentId == wbs.Id, ct))
            {
                throw new ArgumentException($"{prefix}: {wbs.Code} isn't an open, chargeable lowest-level WBS element of an active project.");
            }

            return new Charge(project.Id, wbs.Id, odcType ?? OdcType.Materials, null, null);
        }

        var account = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct) ?? throw WorkbenchNotFoundException.For("Account", accountId!.Value);
        if (!account.IsActive || account.Type is not (AccountType.Expense or AccountType.Asset))
        {
            throw new ArgumentException($"{prefix}: {account.Number} {account.Name} must be an active expense or asset account.");
        }

        if (await db.Set<Account>().AnyAsync(a => a.ParentId == account.Id, ct))
        {
            throw new ArgumentException($"{prefix}: {account.Number} is a summary account; choose one of its detail accounts.");
        }

        var org = orgId ?? defaultOrg;
        if (org is not { } o || !await db.Set<OrgUnit>().AnyAsync(u => u.Id == o, ct))
        {
            throw new ArgumentException($"{prefix}: choose the organization the cost belongs to.");
        }

        return new Charge(null, null, null, account.Id, org);
    }

    /// <summary>A PO line's billed quantity and amount on live bills (submitted, approved or posted), and on posted bills alone.</summary>
    public sealed record Billed(decimal Quantity, decimal Amount, decimal PostedAmount);

    /// <summary>What's billed against each PO line, leaving out <paramref name="exceptBillId"/>.</summary>
    public static async Task<Dictionary<Guid, Billed>> BilledAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> poLineIds, CancellationToken ct, Guid? exceptBillId = null)
    {
        if (poLineIds.Count == 0)
        {
            return [];
        }

        var ids = poLineIds.Distinct().ToList();
        var rows = await db.Set<ApBillLine>().AsNoTracking()
            .Where(l => l.PurchaseOrderLineId != null && ids.Contains(l.PurchaseOrderLineId.Value) && l.BillId != exceptBillId)
            .Join(db.Set<ApBill>().AsNoTracking(), l => l.BillId, b => b.Id, (l, b) => new { PoLineId = l.PurchaseOrderLineId!.Value, l.Quantity, l.Amount, b.Status })
            .ToListAsync(ct);
        return rows.Where(r => PurchasingSupport.BilledStatuses.Contains(r.Status))
            .GroupBy(r => r.PoLineId)
            .ToDictionary(g => g.Key, g => new Billed(g.Sum(r => r.Quantity ?? 0), g.Sum(r => r.Amount), g.Where(r => r.Status == ApBillStatus.Posted).Sum(r => r.Amount)));
    }

    /// <summary>
    /// Open commitment on a PO line: what's left of its amount once posted bills are taken off, on an open order. Billed
    /// cost moves from committed to actual when the bill posts (as an approved vendor ODC), never double counted.
    /// </summary>
    public static decimal Committed(PurchaseOrder po, PurchaseOrderLine line, Billed? billed) =>
        OpenStatuses.Contains(po.Status) ? Math.Max(0, line.Amount - (billed?.PostedAmount ?? 0)) : 0;

    public sealed record OpenCommitment(Guid PurchaseOrderId, string PurchaseOrderNumber, Guid VendorId, DateOnly OrderDate, PurchaseOrderLine Line, decimal Committed);

    /// <summary>Open commitments on project lines, optionally limited to some WBS elements or one project.</summary>
    public static async Task<IReadOnlyList<OpenCommitment>> OpenCommitmentsAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid>? wbsIds, Guid? projectId, CancellationToken ct)
    {
        var q = from l in db.Set<PurchaseOrderLine>().AsNoTracking()
                join o in db.Set<PurchaseOrder>().AsNoTracking() on l.PurchaseOrderId equals o.Id
                where l.WbsElementId != null && (o.Status == PurchaseOrderStatus.Issued || o.Status == PurchaseOrderStatus.PartiallyReceived || o.Status == PurchaseOrderStatus.Received)
                select new { l, o };
        if (wbsIds is not null)
        {
            var ids = wbsIds.ToList();
            q = q.Where(x => ids.Contains(x.l.WbsElementId!.Value));
        }

        if (projectId is { } pid)
        {
            q = q.Where(x => x.l.ProjectId == pid);
        }

        var rows = await q.ToListAsync(ct);
        var billed = await BilledAsync(db, rows.Select(r => r.l.Id).ToList(), ct);
        return rows.Select(r => new OpenCommitment(r.o.Id, r.o.Number, r.o.VendorId, r.o.OrderDate, r.l, Committed(r.o, r.l, billed.GetValueOrDefault(r.l.Id))))
            .Where(c => c.Committed > 0).ToList();
    }

    /// <summary>
    /// Open PO commitments per WBS element (for CLIN funding status): the order date and committed amount of each open line.
    /// </summary>
    public static async Task<IReadOnlyList<(Guid WbsElementId, DateOnly OrderDate, decimal Committed)>> CommitmentsByWbsAsync(
        IWorkbenchDbContext db, IReadOnlyCollection<Guid> wbsIds, CancellationToken ct) =>
        wbsIds.Count == 0 ? [] : (await OpenCommitmentsAsync(db, wbsIds, null, ct)).Select(c => (c.Line.WbsElementId!.Value, c.OrderDate, c.Committed)).ToList();

    /// <summary>Codes for the lookups DTOs show (projects, WBS, accounts, orgs, vendors).</summary>
    public sealed class Lookups
    {
        public Dictionary<Guid, string> Projects { get; private init; } = [];
        public Dictionary<Guid, string> Wbs { get; private init; } = [];
        public Dictionary<Guid, string> Accounts { get; private init; } = [];
        public Dictionary<Guid, string> Orgs { get; private init; } = [];
        public Dictionary<Guid, Vendor> Vendors { get; private init; } = [];

        public static async Task<Lookups> LoadAsync(
            IWorkbenchDbContext db, IEnumerable<Guid?> projects, IEnumerable<Guid?> wbs, IEnumerable<Guid?> accounts, IEnumerable<Guid?> orgs, IEnumerable<Guid?> vendors, CancellationToken ct)
        {
            static List<Guid> Ids(IEnumerable<Guid?> ids) => ids.Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
            var (p, w, a, o, v) = (Ids(projects), Ids(wbs), Ids(accounts), Ids(orgs), Ids(vendors));
            return new Lookups
            {
                Projects = await db.Set<Project>().AsNoTracking().Where(x => p.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, ct),
                Wbs = await db.Set<WbsElement>().AsNoTracking().Where(x => w.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, ct),
                Accounts = await db.Set<Account>().AsNoTracking().Where(x => a.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Number, ct),
                Orgs = await db.Set<OrgUnit>().AsNoTracking().Where(x => o.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, ct),
                Vendors = await db.Set<Vendor>().AsNoTracking().Where(x => v.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct),
            };
        }

        public string? Project(Guid? id) => id is { } i ? Projects.GetValueOrDefault(i) : null;
        public string? WbsCode(Guid? id) => id is { } i ? Wbs.GetValueOrDefault(i) : null;
        public string? Account(Guid? id) => id is { } i ? Accounts.GetValueOrDefault(i) : null;
        public string? Org(Guid? id) => id is { } i ? Orgs.GetValueOrDefault(i) : null;
        public string? VendorName(Guid? id) => id is { } i ? Vendors.GetValueOrDefault(i)?.Name : null;
    }

    public static IReadOnlyList<string> SplitEmails(string? emails) =>
        (emails ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(PurchasingSupport.Normalize).Where(e => e.Length > 0).Distinct().ToList();

    public static string? JoinEmails(IEnumerable<string>? emails)
    {
        var list = (emails ?? []).Select(PurchasingSupport.Normalize).Where(e => e.Length > 0).Distinct().ToList();
        if (list.Any(e => !e.Contains('@')))
        {
            throw new ArgumentException("Receivers must be email addresses.");
        }

        var joined = string.Join(',', list);
        return joined.Length == 0 ? null : joined.Length > 1000 ? throw new ArgumentException("Too many receivers.") : joined;
    }
}
