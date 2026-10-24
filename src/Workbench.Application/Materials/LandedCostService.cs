using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Financials;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Materials;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.Planning;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Materials;

/// <summary>Landed cost accounts, bound from config section <c>Materials</c> (next to <see cref="MaterialsOptions"/>).</summary>
public sealed class LandedCostOptions
{
    /// <summary>Freight, duty and handling bills are coded here; allocating the charge clears it into inventory.</summary>
    public string LandedCostClearingAccountNumber { get; set; } = "1406";

    /// <summary>Where landed cost of units already sold or otherwise gone (not to a project, work order or adjustment) goes.</summary>
    public string CostOfGoodsSoldAccountNumber { get; set; } = "5160";
}

/// <summary>
/// Landed cost: charges from AP bill lines or entered by hand, spread over receipt lines by value, quantity or weight in
/// whole cents (largest remainder). Posting raises the value of the units still on hand (re-averaging), sends the share of
/// units already issued after them (the project's WBS element, the work order's WIP, adjustments, or cost of goods sold) and
/// takes standard-cost parts' share as purchase price variance; one journal entry credits the charge's account.
/// </summary>
public sealed class LandedCostService(
    IWorkbenchDbContextFactory dbFactory, MaterialsLedger ledger, AuditRecorder audit, IOptions<LandedCostOptions> options, TimeProvider clock) : ILandedCostService
{
    private const string ChargeEntity = nameof(LandedCharge);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    // ---- Accounts ----

    private async Task<(Guid Clearing, Guid CostOfSales)?> AccountsAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        if (await ledger.AccountsAsync(db, ct) is null)
        {
            return null;
        }

        var o = options.Value;
        var existing = await db.Set<Account>().ToDictionaryAsync(a => a.Number, ct);
        var defs = new[]
        {
            (o.LandedCostClearingAccountNumber, "Landed cost clearing", AccountType.Asset, "1400", "Freight, duty and handling bills until allocated to receipts"),
            (o.CostOfGoodsSoldAccountNumber, "Cost of goods sold", AccountType.Expense, "5000", "Landed cost of units no longer in stock"),
        };
        var added = false;
        foreach (var (number, name, type, parent, description) in defs.Where(d => !existing.ContainsKey(d.Item1)))
        {
            var account = new Account
            {
                Number = number, Name = name, Type = type, NormalBalance = Account.DefaultNormalBalance(type),
                ParentId = existing.TryGetValue(parent, out var p) ? p.Id : null, Description = description, CreatedAtUtc = UtcNow,
            };
            db.Set<Account>().Add(account);
            existing[number] = account;
            added = true;
        }

        if (added)
        {
            await db.SaveChangesAsync(ct);
        }

        return (existing[o.LandedCostClearingAccountNumber].Id, existing[o.CostOfGoodsSoldAccountNumber].Id);
    }

    public async Task<LandedCostOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var accounts = await AccountsAsync(db, cancellationToken);
        var all = await db.Set<Account>().AsNoTracking().Where(a => a.IsActive).ToListAsync(cancellationToken);
        var parents = all.Where(a => a.ParentId != null).Select(a => a.ParentId!.Value).ToHashSet();
        var vendors = await db.Set<Vendor>().AsNoTracking().OrderBy(v => v.Code).Select(v => new MaterialOption(v.Id, v.Code, v.Name)).ToListAsync(cancellationToken);
        return new LandedCostOptionsDto(
            all.Where(a => !parents.Contains(a.Id) && a.Type is AccountType.Asset or AccountType.Liability or AccountType.Expense)
                .OrderBy(a => a.Number, StringComparer.Ordinal).Select(a => new MaterialOption(a.Id, a.Number, a.Name)).ToList(),
            accounts?.Clearing, vendors);
    }

    // ---- Reads ----

    public async Task<IReadOnlyList<LandedChargeSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await SummariesAsync(db, await db.Set<LandedCharge>().AsNoTracking().Include(c => c.Lines).ToListAsync(cancellationToken), cancellationToken);
    }

    private static async Task<IReadOnlyList<LandedChargeSummaryDto>> SummariesAsync(IWorkbenchDbContext db, IReadOnlyList<LandedCharge> charges, CancellationToken ct)
    {
        var vendorIds = charges.Where(c => c.VendorId != null).Select(c => c.VendorId!.Value).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        var lineIds = charges.Where(c => c.ApBillLineId != null).Select(c => c.ApBillLineId!.Value).ToList();
        var bills = await db.Set<ApBillLine>().AsNoTracking().Where(l => lineIds.Contains(l.Id))
            .Join(db.Set<ApBill>().AsNoTracking(), l => l.BillId, b => b.Id, (l, b) => new { l.Id, b.Number, l.LineNumber })
            .ToDictionaryAsync(x => x.Id, x => $"{x.Number} line {x.LineNumber}", ct);
        var accountIds = charges.Select(c => c.CreditAccountId).Distinct().ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => $"{a.Number} {a.Name}", ct);
        var jeIds = charges.Where(c => c.JournalEntryId != null).Select(c => c.JournalEntryId!.Value).ToList();
        var jes = await db.Set<JournalEntry>().AsNoTracking().Where(e => jeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct);
        return charges.OrderByDescending(c => c.Number, StringComparer.Ordinal)
            .Select(c => new LandedChargeSummaryDto(c.Id, c.Number, c.Kind.ToContract(), c.Description, c.Date, c.Amount, c.Method.ToContract(), c.Status.ToContract(),
                c.VendorId is { } v ? vendors.GetValueOrDefault(v) : null, c.ApBillLineId is { } l ? bills.GetValueOrDefault(l) : null,
                accounts.GetValueOrDefault(c.CreditAccountId) ?? "", c.Lines.Count, c.JournalEntryId is { } je ? jes.GetValueOrDefault(je) : null))
            .ToList();
    }

    public async Task<LandedChargeDto> GetAsync(Guid chargeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, chargeId, cancellationToken);
    }

    private async Task<LandedChargeDto> DetailAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var charge = await db.Set<LandedCharge>().AsNoTracking().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Landed charge", id);
        var summary = (await SummariesAsync(db, [charge], ct))[0];
        var receipts = (await ReceiptLinesAsync(db, charge.Lines.Select(l => l.ReceiptTransactionId).ToList(), ct)).ToDictionary(r => r.ReceiptTransactionId);
        var notes = new List<string>();
        IReadOnlyList<AllocationRow> rows;
        if (charge.Status == LandedChargeStatus.Draft)
        {
            // A preview: what posting would do with today's stock.
            var transactions = await db.Set<InventoryTransaction>().AsNoTracking().Where(t => charge.Lines.Select(l => l.ReceiptTransactionId).Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
            var partIds = transactions.Values.Select(t => t.PartId).Distinct().ToList();
            var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var onHand = (await db.Set<InventoryBalance>().AsNoTracking().Where(b => partIds.Contains(b.PartId) && b.Ownership != InventoryOwnership.GovernmentFurnished && b.Quantity > 0)
                .Select(b => new { b.PartId, b.Quantity }).ToListAsync(ct)).GroupBy(b => b.PartId).ToDictionary(g => g.Key, g => g.Sum(b => b.Quantity));
            rows = Allocate(charge, transactions, parts, onHand);
            notes.Add("This is a preview with today's stock. Posting works it out again.");
        }
        else
        {
            rows = charge.Lines.Select(l => new AllocationRow(l, l.Amount, l.ToInventory, l.ToIssued, l.ToVariance, 0)).ToList();
        }

        if (rows.Any(r => r.ToVariance != 0))
        {
            notes.Add("Standard-cost parts stay at standard: their share is purchase price variance.");
        }

        if (rows.Any(r => r.ToIssued != 0))
        {
            notes.Add("Units already issued take their share where they went: the project, the work order, adjustments or cost of goods sold.");
        }

        return new LandedChargeDto(summary, charge.ApBillLineId, charge.CreditAccountId, charge.VendorId,
            rows.Where(r => receipts.ContainsKey(r.Line.ReceiptTransactionId)).OrderBy(r => receipts[r.Line.ReceiptTransactionId].TransactionNumber, StringComparer.Ordinal)
                .Select(r => new LandedChargeLineDto(r.Line.Id, receipts[r.Line.ReceiptTransactionId], r.Line.Weight, r.Amount, r.ToInventory, r.ToIssued, r.ToVariance)).ToList(),
            notes);
    }

    /// <summary>
    /// The allocation: each line's share of the charge in whole cents (largest remainder), then the split between units
    /// still on hand and units already issued (newest receipts are taken to be the ones on hand), or variance for standard cost.
    /// </summary>
    internal sealed record AllocationRow(LandedChargeLine Line, decimal Amount, decimal ToInventory, decimal ToIssued, decimal ToVariance, decimal QuantityGone);

    internal static List<AllocationRow> Allocate(
        LandedCharge charge, IReadOnlyDictionary<Guid, InventoryTransaction> receipts, IReadOnlyDictionary<Guid, Part> parts, IReadOnlyDictionary<Guid, decimal> onHand)
    {
        // A fixed order (oldest receipt transaction first), so leftover cents land on the same lines in the preview and when posting.
        var lines = charge.Lines.Where(l => receipts.ContainsKey(l.ReceiptTransactionId)).OrderBy(l => receipts[l.ReceiptTransactionId].Number, StringComparer.Ordinal).ToList();
        var weights = lines.Select(l =>
        {
            var t = receipts[l.ReceiptTransactionId];
            return charge.Method switch
            {
                LandedCostMethod.Quantity => t.Quantity,
                LandedCostMethod.Weight => l.Weight ?? 0,
                _ => Math.Abs(t.Amount + t.Variance),
            };
        }).ToList();
        var shares = Allocation.Split(charge.Amount, weights);
        var left = onHand.ToDictionary(kv => kv.Key, kv => kv.Value);
        var result = new AllocationRow[lines.Count];
        var order = lines.Select((l, i) => (l, i)).OrderByDescending(x => receipts[x.l.ReceiptTransactionId].Date).ThenByDescending(x => receipts[x.l.ReceiptTransactionId].Number, StringComparer.Ordinal);
        foreach (var (line, i) in order)
        {
            var t = receipts[line.ReceiptTransactionId];
            var part = parts[t.PartId];
            var share = shares[i];
            if (part.CostMethod == CostMethod.Standard)
            {
                result[i] = new AllocationRow(line, share, 0, 0, share, 0);
                continue;
            }

            var available = left.GetValueOrDefault(part.Id);
            var stillHere = Math.Min(t.Quantity, available);
            left[part.Id] = available - stillHere;
            var toInventory = t.Quantity <= 0 ? 0 : stillHere >= t.Quantity ? share : Money.Round(share * stillHere / t.Quantity);
            result[i] = new AllocationRow(line, share, toInventory, share - toInventory, 0, t.Quantity - stillHere);
        }

        return result.ToList();
    }

    public async Task<IReadOnlyList<LandedReceiptLineDto>> ListReceiptLinesAsync(DateOnly? from = null, string? search = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var since = from ?? DateOnly.FromDateTime(UtcNow).AddDays(-180);
        var ids = await db.Set<InventoryTransaction>().AsNoTracking()
            .Where(t => t.Type == InventoryTransactionType.PurchaseReceipt && t.PurchaseReceiptId != null && t.Date >= since && t.Ownership != InventoryOwnership.GovernmentFurnished)
            .OrderByDescending(t => t.Number).Take(500).Select(t => t.Id).ToListAsync(cancellationToken);
        var rows = await ReceiptLinesAsync(db, ids, cancellationToken);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            rows = rows.Where(r => r.PartNumber.Contains(s, StringComparison.OrdinalIgnoreCase) || r.ReceiptNumber.Contains(s, StringComparison.OrdinalIgnoreCase)
                || r.PurchaseOrderNumber.Contains(s, StringComparison.OrdinalIgnoreCase) || r.PartDescription.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return rows;
    }

    private static async Task<IReadOnlyList<LandedReceiptLineDto>> ReceiptLinesAsync(IWorkbenchDbContext db, IReadOnlyList<Guid> transactionIds, CancellationToken ct)
    {
        if (transactionIds.Count == 0)
        {
            return [];
        }

        var ids = transactionIds.ToList();
        var transactions = await db.Set<InventoryTransaction>().AsNoTracking().Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var receiptIds = transactions.Where(t => t.PurchaseReceiptId != null).Select(t => t.PurchaseReceiptId!.Value).Distinct().ToList();
        var receipts = await db.Set<PurchaseReceipt>().AsNoTracking().Where(r => receiptIds.Contains(r.Id))
            .Join(db.Set<PurchaseOrder>().AsNoTracking(), r => r.PurchaseOrderId, o => o.Id, (r, o) => new { r.Id, r.Number, Po = o.Number })
            .ToDictionaryAsync(x => x.Id, ct);
        var partIds = transactions.Select(t => t.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var landed = (await db.Set<LandedChargeLine>().AsNoTracking().Where(l => ids.Contains(l.ReceiptTransactionId))
                .Join(db.Set<LandedCharge>().AsNoTracking().Where(c => c.Status == LandedChargeStatus.Posted), l => l.LandedChargeId, c => c.Id, (l, c) => new { l.ReceiptTransactionId, l.Amount })
                .ToListAsync(ct))
            .GroupBy(x => x.ReceiptTransactionId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        return transactions.Where(t => t.PurchaseReceiptId is { } r && receipts.ContainsKey(r))
            .OrderByDescending(t => t.Number, StringComparer.Ordinal)
            .Select(t =>
            {
                var r = receipts[t.PurchaseReceiptId!.Value];
                var p = parts[t.PartId];
                return new LandedReceiptLineDto(t.Id, r.Id, r.Number, r.Po, t.Number, t.Date, p.Id, p.Display, p.Description, p.CostMethod.ToContract(), t.Lot, t.Serial,
                    t.Quantity, t.Amount + t.Variance, landed.GetValueOrDefault(t.Id));
            }).ToList();
    }

    public async Task<IReadOnlyList<LandedBillLineDto>> ListBillLinesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var clearing = (await AccountsAsync(db, cancellationToken))?.Clearing;
        var used = await db.Set<LandedCharge>().AsNoTracking().Where(c => c.ApBillLineId != null && c.Status != LandedChargeStatus.Cancelled).Select(c => c.ApBillLineId!.Value).ToListAsync(cancellationToken);
        var since = DateOnly.FromDateTime(UtcNow).AddDays(-365);
        var rows = await db.Set<ApBillLine>().AsNoTracking().Where(l => l.Kind == ApBillLineKind.Account && l.AccountId != null && l.Amount > 0)
            .Join(db.Set<ApBill>().AsNoTracking().Where(b => (b.Status == ApBillStatus.Approved || b.Status == ApBillStatus.Posted) && b.Kind == ApDocumentKind.Bill && b.InvoiceDate >= since),
                l => l.BillId, b => b.Id, (l, b) => new { l, b })
            .ToListAsync(cancellationToken);
        var vendorIds = rows.Select(r => r.b.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, cancellationToken);
        var accountIds = rows.Select(r => r.l.AccountId!.Value).Distinct().ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => $"{a.Number} {a.Name}", cancellationToken);
        return rows.Where(r => !used.Contains(r.l.Id))
            .OrderByDescending(r => r.l.AccountId == clearing).ThenByDescending(r => r.b.InvoiceDate).ThenBy(r => r.b.Number, StringComparer.Ordinal).ThenBy(r => r.l.LineNumber)
            .Select(r => new LandedBillLineDto(r.l.Id, r.b.Id, r.b.Number, vendors.GetValueOrDefault(r.b.VendorId) ?? "", r.b.VendorId, r.b.InvoiceNumber, r.b.InvoiceDate, r.l.LineNumber,
                r.l.Description, r.l.Amount, accounts.GetValueOrDefault(r.l.AccountId!.Value) ?? "", r.l.AccountId == clearing))
            .ToList();
    }

    public async Task<IReadOnlyList<ReceiptLandedCostDto>> ListReceiptsAsync(DateOnly? from = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var since = from ?? DateOnly.FromDateTime(UtcNow).AddDays(-180);
        var receiptIds = await db.Set<InventoryTransaction>().AsNoTracking()
            .Where(t => t.Type == InventoryTransactionType.PurchaseReceipt && t.PurchaseReceiptId != null && t.Date >= since)
            .Select(t => t.PurchaseReceiptId!.Value).Distinct().ToListAsync(cancellationToken);
        var result = new List<ReceiptLandedCostDto>();
        foreach (var id in receiptIds)
        {
            result.Add(await ReceiptAsync(db, id, cancellationToken));
        }

        return result.OrderByDescending(r => r.ReceivedOn).ThenByDescending(r => r.ReceiptNumber, StringComparer.Ordinal).ToList();
    }

    public async Task<ReceiptLandedCostDto> GetReceiptAsync(Guid purchaseReceiptId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await ReceiptAsync(db, purchaseReceiptId, cancellationToken);
    }

    private static async Task<ReceiptLandedCostDto> ReceiptAsync(IWorkbenchDbContext db, Guid receiptId, CancellationToken ct)
    {
        var receipt = await db.Set<PurchaseReceipt>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == receiptId, ct) ?? throw WorkbenchNotFoundException.For("Receipt", receiptId);
        var order = await db.Set<PurchaseOrder>().AsNoTracking().FirstAsync(o => o.Id == receipt.PurchaseOrderId, ct);
        var vendor = await db.Set<Vendor>().AsNoTracking().Where(v => v.Id == order.VendorId).Select(v => v.Name).FirstOrDefaultAsync(ct) ?? "";
        var ids = await db.Set<InventoryTransaction>().AsNoTracking().Where(t => t.PurchaseReceiptId == receiptId && t.Type == InventoryTransactionType.PurchaseReceipt)
            .Select(t => t.Id).ToListAsync(ct);
        var lines = await ReceiptLinesAsync(db, ids, ct);
        var chargeIds = await db.Set<LandedChargeLine>().AsNoTracking().Where(l => l.PurchaseReceiptId == receiptId).Select(l => l.LandedChargeId).Distinct().ToListAsync(ct);
        var charges = await db.Set<LandedCharge>().AsNoTracking().Include(c => c.Lines).Where(c => chargeIds.Contains(c.Id)).ToListAsync(ct);
        return new ReceiptLandedCostDto(receipt.Id, receipt.Number, order.Number, vendor, receipt.ReceivedOn, lines.Sum(l => l.Value), lines.Sum(l => l.LandedCost),
            lines.OrderBy(l => l.TransactionNumber, StringComparer.Ordinal).ToList(), await SummariesAsync(db, charges, ct));
    }

    // ---- Changes ----

    public async Task<LandedChargeDto> CreateAsync(SaveLandedChargeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var last = await db.Set<LandedCharge>().OrderByDescending(c => c.Number).Select(c => c.Number).FirstOrDefaultAsync(cancellationToken);
        var next = last is not null && last.StartsWith("LC-", StringComparison.Ordinal) && int.TryParse(last[3..], out var n) ? n + 1 : 1;
        var charge = new LandedCharge { Number = $"LC-{next:D6}", Description = "", CreatedBy = audit.Actor, CreatedAtUtc = UtcNow };
        await ApplyAsync(db, charge, request, cancellationToken);
        db.Set<LandedCharge>().Add(charge);
        audit.Record(db, ChargeEntity, charge.Id, "Created", after: new { charge.Number, charge.Amount, Method = charge.Method.ToString(), Lines = charge.Lines.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, charge.Id, cancellationToken);
    }

    public async Task<LandedChargeDto> UpdateAsync(Guid chargeId, SaveLandedChargeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var charge = await LoadDraftAsync(db, chargeId, cancellationToken);
        var before = new { charge.Amount, Method = charge.Method.ToString(), Lines = charge.Lines.Count };
        db.Set<LandedChargeLine>().RemoveRange(charge.Lines);
        charge.Lines.Clear();
        await ApplyAsync(db, charge, request, cancellationToken);
        audit.Record(db, ChargeEntity, charge.Id, "Updated", before, new { charge.Amount, Method = charge.Method.ToString(), Lines = charge.Lines.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, charge.Id, cancellationToken);
    }

    private static async Task<LandedCharge> LoadDraftAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var charge = await db.Set<LandedCharge>().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Landed charge", id);
        if (charge.Status != LandedChargeStatus.Draft)
        {
            throw new InvalidOperationException($"{charge.Number} is {charge.Status}.");
        }

        return charge;
    }

    private async Task ApplyAsync(IWorkbenchDbContext db, LandedCharge charge, SaveLandedChargeRequest r, CancellationToken ct)
    {
        charge.Kind = r.Kind.ToDomain();
        charge.Description = PurchasingSupport.Required(r.Description, "A description", 500);
        charge.Date = r.Date;
        charge.Method = r.Method.ToDomain();
        if (r.ApBillLineId is { } billLineId)
        {
            var line = await db.Set<ApBillLine>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == billLineId, ct) ?? throw WorkbenchNotFoundException.For("Bill line", billLineId);
            var bill = await db.Set<ApBill>().AsNoTracking().FirstAsync(b => b.Id == line.BillId, ct);
            if (bill.Status is not (ApBillStatus.Approved or ApBillStatus.Posted) || bill.Kind != ApDocumentKind.Bill || line.Kind != ApBillLineKind.Account || line.AccountId is null)
            {
                throw new ArgumentException($"Line {line.LineNumber} of {bill.Number} isn't an account line of an approved or posted bill.");
            }

            if (await db.Set<LandedCharge>().AnyAsync(c => c.ApBillLineId == line.Id && c.Status != LandedChargeStatus.Cancelled && c.Id != charge.Id, ct))
            {
                throw new ArgumentException($"Line {line.LineNumber} of {bill.Number} is already allocated as landed cost.");
            }

            charge.ApBillLineId = line.Id;
            charge.Amount = line.Amount;
            charge.CreditAccountId = line.AccountId.Value;
            charge.VendorId = bill.VendorId;
            charge.CompanyId = bill.CompanyId;
        }
        else
        {
            var amount = r.Amount ?? 0;
            if (amount <= 0 || decimal.Round(amount, 2) != amount)
            {
                throw new ArgumentException("Enter the charge amount (more than 0, in cents).");
            }

            var accountId = r.CreditAccountId ?? (await AccountsAsync(db, ct))?.Clearing ?? throw new ArgumentException("Set up the chart of accounts first.");
            if (!await db.Set<Account>().AnyAsync(a => a.Id == accountId && a.IsActive, ct))
            {
                throw new ArgumentException("Choose an active account for the charge.");
            }

            if (r.VendorId is { } vid && !await db.Set<Vendor>().AnyAsync(v => v.Id == vid, ct))
            {
                throw WorkbenchNotFoundException.For("Vendor", vid);
            }

            charge.ApBillLineId = null;
            charge.Amount = amount;
            charge.CreditAccountId = accountId;
            charge.VendorId = r.VendorId;
            charge.CompanyId = null;
        }

        var targets = r.Lines ?? [];
        if (targets.Count == 0)
        {
            throw new ArgumentException("Choose the receipt lines the charge covers.");
        }

        if (targets.Select(t => t.ReceiptTransactionId).Distinct().Count() != targets.Count)
        {
            throw new ArgumentException("The same receipt line is chosen twice.");
        }

        var ids = targets.Select(t => t.ReceiptTransactionId).ToList();
        var transactions = await db.Set<InventoryTransaction>().AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        foreach (var target in targets)
        {
            if (!transactions.TryGetValue(target.ReceiptTransactionId, out var t) || t.Type != InventoryTransactionType.PurchaseReceipt || t.PurchaseReceiptId is null)
            {
                throw new ArgumentException("Landed cost goes on purchase receipts into inventory only.");
            }

            if (t.Ownership == InventoryOwnership.GovernmentFurnished)
            {
                throw new ArgumentException("Government-furnished material isn't costed, so it takes no landed cost.");
            }

            if (charge.Method == LandedCostMethod.Weight && target.Weight is not > 0)
            {
                throw new ArgumentException($"Enter the weight of {t.Number} (weight-based charges need one for every line).");
            }

            charge.Lines.Add(new LandedChargeLine
            {
                LandedChargeId = charge.Id, ReceiptTransactionId = t.Id, PurchaseReceiptId = t.PurchaseReceiptId.Value, PartId = t.PartId,
                Weight = charge.Method == LandedCostMethod.Weight ? MaterialRules.Quantity(target.Weight!.Value, "Weight") : null,
            });
        }

        if (charge.Method == LandedCostMethod.Value && transactions.Values.Sum(t => Math.Abs(t.Amount + t.Variance)) == 0)
        {
            throw new ArgumentException("Those receipts have no value to allocate by; allocate by quantity instead.");
        }
    }

    public async Task<LandedChargeDto> CancelAsync(Guid chargeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var charge = await LoadDraftAsync(db, chargeId, cancellationToken);
        charge.Status = LandedChargeStatus.Cancelled;
        audit.Record(db, ChargeEntity, charge.Id, "Cancelled", new { Status = nameof(LandedChargeStatus.Draft) }, new { Status = charge.Status.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, charge.Id, cancellationToken);
    }

    public async Task<LandedChargeDto> PostAsync(Guid chargeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var charge = await LoadDraftAsync(db, chargeId, cancellationToken);
        var costOfSales = (await AccountsAsync(db, cancellationToken))?.CostOfSales;
        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        if (engine.Accounts is not { } accounts)
        {
            throw new ArgumentException("Set up the chart of accounts before posting landed cost.");
        }

        var ids = charge.Lines.Select(l => l.ReceiptTransactionId).ToList();
        var receipts = await db.Set<InventoryTransaction>().AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
        var parts = new Dictionary<Guid, Part>();
        foreach (var partId in receipts.Values.Select(t => t.PartId).Distinct())
        {
            parts[partId] = await engine.LoadPartAsync(partId, cancellationToken);
        }

        var onHand = parts.Keys.ToDictionary(id => id, engine.CostedOnHand);
        var allocation = Allocate(charge, receipts, parts, onHand);
        var memo = $"{charge.Number} {charge.Kind.ToString().ToLowerInvariant()}";
        var created = new List<InventoryTransaction>();
        var workOrders = new Dictionary<Guid, WorkOrder>();
        var followed = new Dictionary<Guid, decimal>();
        foreach (var (line, amount, toInventory, toIssued, toVariance, gone) in allocation)
        {
            var receipt = receipts[line.ReceiptTransactionId];
            var part = parts[receipt.PartId];
            line.Amount = amount;
            line.ToInventory = toInventory;
            line.ToIssued = toIssued;
            line.ToVariance = toVariance;
            var inn = engine.NewTransaction(InventoryTransactionType.LandedCost, charge.Date, part);
            inn.PurchaseReceiptId = receipt.PurchaseReceiptId;
            inn.PurchaseOrderLineId = receipt.PurchaseOrderLineId;
            inn.Lot = receipt.Lot;
            inn.Serial = receipt.Serial;
            inn.Ownership = receipt.Ownership;
            inn.OwnerProjectId = receipt.OwnerProjectId;
            inn.Reference = charge.Number;
            inn.Reason = $"Landed cost on {receipt.Number}";
            created.Add(inn);
            if (toVariance != 0)
            {
                inn.Variance = toVariance;
                engine.AddLedger(new LedgerAmount(accounts.PriceVariance, toVariance, $"{part.Display} {memo} (standard cost)"));
                continue;
            }

            inn.Amount = toInventory + toIssued;
            part.InventoryValue += toInventory;
            var qty = engine.CostedOnHand(part.Id);
            if (qty > 0)
            {
                part.AverageCost = Math.Round(part.InventoryValue / qty, 4, MidpointRounding.AwayFromZero);
            }

            part.UpdatedAtUtc = UtcNow;
            if (engine.InventoryAccount(part) is { } inventory && toInventory != 0)
            {
                engine.AddLedger(new LedgerAmount(inventory, toInventory, $"{part.Display} {memo}"));
            }

            if (toIssued != 0)
            {
                created.AddRange(await FollowIssuesAsync(db, engine, accounts, charge, receipt, part, toIssued, gone, costOfSales, workOrders, followed, cancellationToken));
            }
        }

        engine.AddLedger(new LedgerAmount(charge.CreditAccountId, -charge.Amount, $"{memo}: {charge.Description}"));
        var entry = await engine.FlushAsync(JournalSource.Inventory, charge.Id, charge.Date, $"Landed cost {charge.Number}: {charge.Description}", cancellationToken, charge.CompanyId);
        charge.JournalEntryId = entry?.Id;
        charge.Status = LandedChargeStatus.Posted;
        charge.PostedBy = audit.Actor;
        charge.PostedAtUtc = UtcNow;
        audit.Record(db, ChargeEntity, charge.Id, "Posted", new { Status = nameof(LandedChargeStatus.Draft) },
            new { Status = charge.Status.ToString(), ToInventory = allocation.Sum(a => a.ToInventory), ToIssued = allocation.Sum(a => a.ToIssued), ToVariance = allocation.Sum(a => a.ToVariance) });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, charge.Id, cancellationToken);
    }

    /// <summary>
    /// The issued share follows the part's latest outgoing movements since the receipt (as many units as are gone): issues to
    /// a project WBS element (direct material, with its segments), stock work orders (WIP while open, the variance once closed),
    /// adjustments and counts (the adjustment account), anything else to cost of goods sold.
    /// </summary>
    private static async Task<List<InventoryTransaction>> FollowIssuesAsync(
        IWorkbenchDbContext db, StockEngine engine, MaterialAccounts a, LandedCharge charge, InventoryTransaction receipt, Part part, decimal amount, decimal gone,
        Guid? costOfSales, Dictionary<Guid, WorkOrder> workOrders, Dictionary<Guid, decimal> followed, CancellationToken ct)
    {
        var outgoing = await db.Set<InventoryTransaction>().AsNoTracking()
            .Where(t => t.PartId == part.Id && t.Quantity < 0 && t.Date >= receipt.Date && t.Type != InventoryTransactionType.TransferOut
                && t.Ownership != InventoryOwnership.GovernmentFurnished)
            .OrderByDescending(t => t.Number).Take(200).ToListAsync(ct);
        var targets = new List<(InventoryTransaction Issue, decimal Quantity)>();
        var left = gone;
        foreach (var t in outgoing)
        {
            if (left <= 0)
            {
                break;
            }

            var take = Math.Min(left, -t.Quantity - followed.GetValueOrDefault(t.Id)); // Another line of the charge may have followed it already.
            if (take <= 0)
            {
                continue;
            }

            followed[t.Id] = followed.GetValueOrDefault(t.Id) + take;
            targets.Add((t, take));
            left -= take;
        }

        var weights = targets.Select(t => t.Quantity).ToList();
        if (left > 0 || targets.Count == 0)
        {
            weights.Add(Math.Max(left, targets.Count == 0 ? 1 : 0)); // Units with no outgoing movement found: cost of goods sold.
        }

        var shares = Allocation.Split(amount, weights);
        var created = new List<InventoryTransaction>();
        for (var i = 0; i < shares.Count; i++)
        {
            if (shares[i] == 0)
            {
                continue;
            }

            var issue = i < targets.Count ? targets[i].Issue : null;
            var t = engine.NewTransaction(InventoryTransactionType.LandedCost, charge.Date, part);
            t.Amount = -shares[i];
            t.PurchaseReceiptId = receipt.PurchaseReceiptId;
            t.Reference = charge.Number;
            t.Ownership = receipt.Ownership;
            t.OwnerProjectId = receipt.OwnerProjectId;
            var memo = $"{part.Display} {charge.Number} landed cost";
            Guid account;
            if (issue?.ProjectId is { } project && issue.WbsElementId is { } wbs)
            {
                account = a.DirectMaterial;
                t.ProjectId = project;
                t.WbsElementId = wbs;
                t.WorkOrderId = issue.WorkOrderId;
                t.Reason = $"Landed cost on units issued by {issue.Number}";
                engine.AddLedger(new LedgerAmount(account, shares[i], memo, project, wbs));
                if (issue.WorkOrderId is { } pwo && await WorkOrderAsync(db, workOrders, pwo, ct) is { } projectWo)
                {
                    projectWo.MaterialCost += shares[i];
                }

                created.Add(t);
                continue;
            }

            if (issue?.WorkOrderId is { } woId && await WorkOrderAsync(db, workOrders, woId, ct) is { } wo)
            {
                t.WorkOrderId = wo.Id;
                if (wo.Status is WorkOrderStatus.Closed or WorkOrderStatus.Cancelled)
                {
                    account = a.ManufacturingVariance;
                    t.Reason = $"Landed cost on units used by closed {wo.Number}";
                }
                else
                {
                    account = a.Wip;
                    wo.MaterialCost += shares[i];
                    t.Reason = $"Landed cost on units issued to {wo.Number}";
                }
            }
            else if (issue?.Type is InventoryTransactionType.Adjustment or InventoryTransactionType.CycleCount or InventoryTransactionType.PhysicalCount)
            {
                account = a.Adjustment;
                t.Reason = $"Landed cost on units adjusted out by {issue.Number}";
            }
            else
            {
                account = costOfSales ?? a.Adjustment;
                t.Reason = issue is null ? "Landed cost on units no longer in stock" : $"Landed cost on units shipped by {issue.Number}";
            }

            engine.AddLedger(new LedgerAmount(account, shares[i], memo));
            created.Add(t);
        }

        return created; // The issued share passes through the part's value: in on the receipt's landed cost transaction, out on these.
    }

    private static async Task<WorkOrder?> WorkOrderAsync(IWorkbenchDbContext db, Dictionary<Guid, WorkOrder> cache, Guid id, CancellationToken ct)
    {
        if (!cache.TryGetValue(id, out var wo))
        {
            wo = await db.Set<WorkOrder>().FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wo is not null)
            {
                cache[id] = wo;
            }
        }

        return wo;
    }
}
