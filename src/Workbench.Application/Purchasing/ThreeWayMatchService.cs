using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>
/// The three-way match between a bill line, the PO line it bills and the receipts against it: the quantity billed to date
/// (on live bills, this one included) can't exceed the quantity received, and the unit price must be within
/// <see cref="PurchasingOptions.MatchPriceTolerancePercent"/> of the PO price.
/// </summary>
internal static class ThreeWayMatch
{
    /// <summary>
    /// Fills in each PO-linked line's charging from the PO line (the bill's vendor must be the PO's), and its amount as
    /// quantity × unit price (the PO price unless given). Throws <see cref="ArgumentException"/> when it can't.
    /// </summary>
    public static async Task ApplyPoLineAsync(IWorkbenchDbContext db, ApBill bill, ApBillLine line, CancellationToken ct)
    {
        var n = line.LineNumber;
        var poLine = await db.Set<PurchaseOrderLine>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == line.PurchaseOrderLineId, ct)
            ?? throw WorkbenchNotFoundException.For("PO line", line.PurchaseOrderLineId!.Value);
        var po = await db.Set<PurchaseOrder>().AsNoTracking().FirstAsync(o => o.Id == poLine.PurchaseOrderId, ct);
        if (po.VendorId != bill.VendorId)
        {
            throw new ArgumentException($"Line {n}: {po.Number} is with another vendor.");
        }

        if (!ProcurementSupport.OpenStatuses.Contains(po.Status))
        {
            throw new ArgumentException($"Line {n}: {po.Number} is {po.Status}; only issued orders can be billed.");
        }

        line.Kind = poLine.AccountId is null ? ApBillLineKind.ProjectCost : ApBillLineKind.Account;
        line.WbsElementId = poLine.WbsElementId;
        line.OdcType = poLine.OdcType;
        line.AccountId = poLine.AccountId;
        line.OrgUnitId = poLine.OrgUnitId;
        line.SubcontractId = null;
        line.OdcEntryId = null;
        line.Quantity = ProcurementSupport.Quantity(line.Quantity ?? 0, $"Line {n}: quantity billed");
        line.UnitPrice = ProcurementSupport.UnitPrice(line.UnitPrice ?? poLine.UnitPrice, $"Line {n}: unit price");
        line.Amount = ProcurementSupport.Extend(line.Quantity.Value, line.UnitPrice.Value);
        if (string.IsNullOrWhiteSpace(line.Description))
        {
            line.Description = poLine.Description;
        }
    }

    /// <summary>Re-checks every PO-linked line and records <see cref="ApBillLine.MatchProblem"/> (null when it matches).</summary>
    public static async Task EvaluateAsync(IWorkbenchDbContext db, ApBill bill, decimal tolerancePercent, CancellationToken ct)
    {
        var lines = bill.Lines.Where(l => l.PurchaseOrderLineId != null).ToList();
        if (lines.Count == 0)
        {
            return;
        }

        var ids = lines.Select(l => l.PurchaseOrderLineId!.Value).Distinct().ToList();
        var poLines = await db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => ids.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        var poIds = poLines.Values.Select(l => l.PurchaseOrderId).Distinct().ToList();
        var orders = await db.Set<PurchaseOrder>().AsNoTracking().Where(o => poIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var others = await ProcurementSupport.BilledAsync(db, ids, ct, exceptBillId: bill.Id);
        foreach (var line in lines)
        {
            var problems = new List<string>();
            if (!poLines.TryGetValue(line.PurchaseOrderLineId!.Value, out var poLine))
            {
                problems.Add("The PO line no longer exists.");
            }
            else
            {
                var po = orders[poLine.PurchaseOrderId];
                if (po.Status is PurchaseOrderStatus.Cancelled or PurchaseOrderStatus.Draft)
                {
                    problems.Add($"{po.Number} is {po.Status}.");
                }

                var billedToDate = (others.GetValueOrDefault(poLine.Id)?.Quantity ?? 0)
                    + bill.Lines.Where(l => l.PurchaseOrderLineId == poLine.Id).Sum(l => l.Quantity ?? 0);
                if (billedToDate > poLine.QuantityReceived)
                {
                    problems.Add($"Billed quantity {Q(billedToDate)} is more than the {Q(poLine.QuantityReceived)} received on {po.Number} line {poLine.LineNumber}.");
                }

                var price = line.UnitPrice ?? 0;
                var allowed = poLine.UnitPrice * tolerancePercent / 100m;
                if (Math.Abs(price - poLine.UnitPrice) > allowed)
                {
                    var pct = (price - poLine.UnitPrice) / poLine.UnitPrice * 100m;
                    problems.Add($"Unit price {Q(price)} is {Math.Abs(pct):0.##}% {(pct > 0 ? "over" : "under")} the PO price {Q(poLine.UnitPrice)} (tolerance {tolerancePercent:0.##}%).");
                }
            }

            line.MatchProblem = problems.Count == 0 ? null : string.Join(" ", problems);
            if (line.MatchProblem is null)
            {
                line.MatchOverrideBy = null;
                line.MatchOverrideReason = null;
                line.MatchOverrideAtUtc = null;
            }
        }
    }

    /// <summary>Lines among <paramref name="lines"/> that fail the match and haven't been overridden.</summary>
    public static IReadOnlyList<ApBillLine> Blocking(IEnumerable<ApBillLine> lines) =>
        lines.Where(l => l.PurchaseOrderLineId != null && l.MatchProblem != null && l.MatchOverrideBy == null).ToList();

    public static string Q(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>Match exceptions, Finance overrides, and "create bill from PO".</summary>
public sealed class ThreeWayMatchService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    AccessControl access,
    IOptions<PurchasingOptions> options,
    TimeProvider clock) : IThreeWayMatchService
{
    public async Task<IReadOnlyList<MatchExceptionDto>> ListExceptionsAsync(bool includeOverridden = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var live = new[] { ApBillStatus.Draft, ApBillStatus.PendingApproval, ApBillStatus.Rejected };
        var bills = await db.Set<ApBill>().Include(b => b.Lines)
            .Where(b => live.Contains(b.Status) && b.Lines.Any(l => l.PurchaseOrderLineId != null)).ToListAsync(cancellationToken);

        // Receipts may have arrived since the bill was submitted: re-check before listing.
        foreach (var bill in bills)
        {
            await ThreeWayMatch.EvaluateAsync(db, bill, options.Value.MatchPriceTolerancePercent, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        var rows = bills.SelectMany(b => b.Lines.Where(l => l.MatchProblem != null && (includeOverridden || l.MatchOverrideBy == null)).Select(l => (b, l))).ToList();
        return await ToDtosAsync(db, rows, cancellationToken);
    }

    public async Task<MatchExceptionDto> OverrideAsync(Guid billLineId, MatchOverrideRequest request, CancellationToken cancellationToken = default)
    {
        var reason = PurchasingSupport.Required(request?.Reason, "A reason", 1000);
        if (!(await access.GetOrgRolesAsync(cancellationToken)).IsFinance())
        {
            throw new WorkbenchForbiddenException("Only Finance can override a three-way match.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var line = await db.Set<ApBillLine>().FirstOrDefaultAsync(l => l.Id == billLineId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Bill line", billLineId);
        var bill = await db.Set<ApBill>().Include(b => b.Lines).FirstAsync(b => b.Id == line.BillId, cancellationToken);
        if (bill.Status is not (ApBillStatus.Draft or ApBillStatus.PendingApproval or ApBillStatus.Rejected))
        {
            throw new InvalidOperationException($"Bill {bill.Number} is {bill.Status}; there's nothing to override.");
        }

        if (PurchasingSupport.SameEmail(bill.EnteredBy, access.Email))
        {
            throw new WorkbenchForbiddenException("You can't override the match on a bill you entered.");
        }

        line = bill.Lines.First(l => l.Id == billLineId);
        await ThreeWayMatch.EvaluateAsync(db, bill, options.Value.MatchPriceTolerancePercent, cancellationToken);
        if (line.MatchProblem is null)
        {
            throw new InvalidOperationException($"Line {line.LineNumber} of {bill.Number} matches; no override is needed.");
        }

        line.MatchOverrideBy = access.Email;
        line.MatchOverrideReason = reason;
        line.MatchOverrideAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, PurchasingSupport.BillEntity, bill.Id, "MatchOverridden", new { line.LineNumber, line.MatchProblem }, new { line.MatchOverrideBy }, reason);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync(db, [(bill, line)], cancellationToken))[0];
    }

    public async Task<SaveBillRequest> BuildBillAsync(CreateBillFromPurchaseOrderRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var po = await db.Set<PurchaseOrder>().AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == request.PurchaseOrderId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Purchase order", request.PurchaseOrderId);
        if (!ProcurementSupport.OpenStatuses.Contains(po.Status))
        {
            throw new ArgumentException($"{po.Number} is {po.Status}; only issued orders can be billed.");
        }

        var billed = await ProcurementSupport.BilledAsync(db, po.Lines.Select(l => l.Id).ToList(), cancellationToken);
        var lines = new List<SaveBillLineRequest>();
        foreach (var line in po.Lines.OrderBy(l => l.LineNumber))
        {
            var open = line.QuantityReceived - (billed.GetValueOrDefault(line.Id)?.Quantity ?? 0);
            if (open <= 0)
            {
                continue;
            }

            lines.Add(new SaveBillLineRequest(
                line.AccountId is null ? BillLineKind.ProjectCost : BillLineKind.Account,
                line.Description,
                ProcurementSupport.Extend(open, line.UnitPrice),
                WbsElementId: line.WbsElementId,
                OdcType: line.OdcType?.ToContract(),
                AccountId: line.AccountId,
                OrgUnitId: line.OrgUnitId,
                PurchaseOrderLineId: line.Id,
                Quantity: open,
                UnitPrice: line.UnitPrice));
        }

        if (lines.Count == 0)
        {
            throw new ArgumentException($"Everything received on {po.Number} has already been billed.");
        }

        return new SaveBillRequest(po.VendorId, request.InvoiceNumber ?? "", request.InvoiceDate, lines,
            Description: PurchasingSupport.Optional(request.Description, "Description", 500) ?? $"{po.Number}");
    }

    private static async Task<IReadOnlyList<MatchExceptionDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyList<(ApBill Bill, ApBillLine Line)> rows, CancellationToken ct)
    {
        var poLineIds = rows.Select(r => r.Line.PurchaseOrderLineId!.Value).Distinct().ToList();
        var poLines = await db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => poLineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        var poIds = poLines.Values.Select(l => l.PurchaseOrderId).Distinct().ToList();
        var orders = await db.Set<PurchaseOrder>().AsNoTracking().Where(o => poIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var vendorIds = rows.Select(r => r.Bill.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        var billedElsewhere = await ProcurementSupport.BilledAsync(db, poLineIds, ct);
        return rows.Where(r => poLines.ContainsKey(r.Line.PurchaseOrderLineId!.Value)).OrderBy(r => r.Bill.Number).ThenBy(r => r.Line.LineNumber).Select(r =>
        {
            var pl = poLines[r.Line.PurchaseOrderLineId!.Value];
            var po = orders[pl.PurchaseOrderId];

            // Billed to date: other live bills plus this bill (which may still be a draft).
            var others = billedElsewhere.GetValueOrDefault(pl.Id)?.Quantity ?? 0;
            var thisBill = r.Bill.Lines.Where(l => l.PurchaseOrderLineId == pl.Id).Sum(l => l.Quantity ?? 0);
            var counted = PurchasingSupport.BilledStatuses.Contains(r.Bill.Status) ? 0 : thisBill;
            return new MatchExceptionDto(r.Bill.Id, r.Bill.Number, r.Bill.Status.ToContract(), vendors.GetValueOrDefault(r.Bill.VendorId) ?? "",
                r.Line.Id, r.Line.LineNumber, r.Line.Description, po.Id, po.Number, pl.LineNumber,
                others + counted, pl.QuantityReceived, pl.Quantity, r.Line.UnitPrice ?? 0, pl.UnitPrice,
                r.Line.MatchProblem ?? "", r.Line.MatchOverrideBy, r.Line.MatchOverrideReason);
        }).ToList();
    }
}

/// <summary>Committed cost from open purchase orders on a project.</summary>
public sealed class CommitmentService(IWorkbenchDbContextFactory dbFactory) : ICommitmentService
{
    public async Task<ProjectCommitmentsDto> GetProjectCommitmentsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var open = await ProcurementSupport.OpenCommitmentsAsync(db, null, projectId, cancellationToken);
        var lookups = await ProcurementSupport.Lookups.LoadAsync(db, [], open.Select(c => c.Line.WbsElementId), [], [], open.Select(c => (Guid?)c.VendorId), cancellationToken);
        var lines = open.OrderBy(c => c.PurchaseOrderNumber).ThenBy(c => c.Line.LineNumber)
            .Select(c => new CommitmentLineDto(c.PurchaseOrderId, c.PurchaseOrderNumber, lookups.VendorName(c.VendorId) ?? "", c.Line.LineNumber, c.Line.Description,
                c.Line.WbsElementId, lookups.WbsCode(c.Line.WbsElementId), c.Committed)).ToList();
        var byWbs = open.GroupBy(c => c.Line.WbsElementId!.Value)
            .Select(g => new WbsCommitmentDto(g.Key, lookups.WbsCode(g.Key) ?? "", g.Sum(c => c.Committed)))
            .OrderBy(w => w.WbsCode, StringComparer.Ordinal).ToList();
        return new ProjectCommitmentsDto(projectId, lines.Sum(l => l.Committed), byWbs, lines);
    }
}
