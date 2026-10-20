using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Accounting;
using Workbench.Application.Financials;
using Workbench.Domain.Accounting;

namespace Workbench.Application.Materials;

/// <summary>Materials settings, bound from config section <c>Materials</c>: the GL accounts inventory posts to.</summary>
public sealed class MaterialsOptions
{
    public const string Section = "Materials";

    /// <summary>Purchased parts and raw material.</summary>
    public string InventoryAccountNumber { get; set; } = "1410";

    /// <summary>Make parts (assemblies and finished goods).</summary>
    public string FinishedGoodsAccountNumber { get; set; } = "1420";

    public string WipAccountNumber { get; set; } = "1450";

    /// <summary>
    /// Received, not invoiced: purchase order lines for parts charge it (so the AP bill clears it), and receipts into
    /// inventory credit it at the PO price.
    /// </summary>
    public string ReceivedNotInvoicedAccountNumber { get; set; } = "1405";

    /// <summary>Material issued to projects (direct ODC, project and WBS segments).</summary>
    public string DirectMaterialAccountNumber { get; set; } = "5120";

    public string PurchasePriceVarianceAccountNumber { get; set; } = "6150";

    public string ManufacturingVarianceAccountNumber { get; set; } = "6151";

    /// <summary>Adjustments, cycle count differences, scrap and standard cost revaluations.</summary>
    public string InventoryAdjustmentAccountNumber { get; set; } = "6152";

    /// <summary>Contra overhead: shop floor labor absorbed into stock work orders' WIP.</summary>
    public string LaborAppliedAccountNumber { get; set; } = "6195";
}

/// <summary>The accounts materials posts to, resolved once per operation.</summary>
internal sealed record MaterialAccounts(
    Guid Inventory, Guid FinishedGoods, Guid Wip, Guid ReceivedNotInvoiced, Guid DirectMaterial, Guid PriceVariance, Guid ManufacturingVariance,
    Guid Adjustment, Guid LaborApplied);

/// <summary>One signed GL amount (+ debit, − credit) with its segments.</summary>
internal readonly record struct LedgerAmount(Guid AccountId, decimal Amount, string Memo, Guid? ProjectId = null, Guid? WbsElementId = null);

/// <summary>
/// Posts materials entries through <see cref="GeneralLedger"/> (balanced, open period, audited, sources
/// <see cref="JournalSource.Inventory"/> and <see cref="JournalSource.Manufacturing"/>, one entry per source record).
/// Without a chart of accounts nothing posts (the movement is still recorded).
/// </summary>
public sealed class MaterialsLedger(GeneralLedger ledger, IOptions<MaterialsOptions> options)
{
    private sealed record AccountDef(string Number, string Name, AccountType Type, string? Parent, CostClassification Class = CostClassification.None, string? Description = null);

    private AccountDef[] Defs()
    {
        var o = options.Value;
        return
        [
            new("1400", "Inventories", AccountType.Asset, "1000"),
            new(o.ReceivedNotInvoicedAccountNumber, "Inventory received not invoiced", AccountType.Asset, "1400", Description: "Clearing: PO receipts into inventory (credit) until the AP bill clears them (debit)"),
            new(o.InventoryAccountNumber, "Raw materials and purchased parts", AccountType.Asset, "1400"),
            new(o.FinishedGoodsAccountNumber, "Finished goods and assemblies", AccountType.Asset, "1400"),
            new(o.WipAccountNumber, "Work in process", AccountType.Asset, "1400", Description: "Stock work orders: material issued and labor applied, less completions"),
            new(o.DirectMaterialAccountNumber, "Direct materials", AccountType.Expense, "5100", CostClassification.DirectOdc),
            new(o.PurchasePriceVarianceAccountNumber, "Purchase price variance", AccountType.Expense, "6100", CostClassification.Overhead, "Standard-cost receipts: PO price less standard"),
            new(o.ManufacturingVarianceAccountNumber, "Manufacturing variances", AccountType.Expense, "6100", CostClassification.Overhead, "Stock work orders: WIP left at close"),
            new(o.InventoryAdjustmentAccountNumber, "Inventory adjustments, scrap and revaluation", AccountType.Expense, "6100", CostClassification.Overhead),
            new(o.LaborAppliedAccountNumber, "Manufacturing labor applied to inventory", AccountType.Expense, "6100", CostClassification.Overhead, "Contra: shop labor absorbed into WIP"),
        ];
    }

    public string Actor => ledger.Actor;

    public DateTime UtcNow => ledger.UtcNow;

    /// <summary>
    /// The materials accounts, adding any that are missing when a chart exists (it is created lazily, like the fixed
    /// asset accounts). Null when there's no chart of accounts at all. Saves when it adds accounts.
    /// </summary>
    internal async Task<MaterialAccounts?> AccountsAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        if (!await db.Set<Account>().AnyAsync(ct))
        {
            return null;
        }

        var existing = await db.Set<Account>().ToDictionaryAsync(a => a.Number, ct);
        var added = false;
        foreach (var def in Defs().Where(d => !existing.ContainsKey(d.Number)))
        {
            var account = new Account
            {
                Number = def.Number,
                Name = def.Name,
                Type = def.Type,
                NormalBalance = Account.DefaultNormalBalance(def.Type),
                ParentId = def.Parent is { } p && existing.TryGetValue(p, out var parent) ? parent.Id : null,
                CostClass = def.Class,
                Description = def.Description,
                CreatedAtUtc = UtcNow,
            };
            db.Set<Account>().Add(account);
            existing[def.Number] = account;
            added = true;
        }

        if (added)
        {
            await db.SaveChangesAsync(ct);
        }

        var o = options.Value;
        Guid Id(string number) => existing[number].Id;
        return new MaterialAccounts(Id(o.InventoryAccountNumber), Id(o.FinishedGoodsAccountNumber), Id(o.WipAccountNumber), Id(o.ReceivedNotInvoicedAccountNumber),
            Id(o.DirectMaterialAccountNumber), Id(o.PurchasePriceVarianceAccountNumber), Id(o.ManufacturingVarianceAccountNumber),
            Id(o.InventoryAdjustmentAccountNumber), Id(o.LaborAppliedAccountNumber));
    }

    /// <summary>The account part purchase order lines charge (received, not invoiced), adding the materials accounts when missing.</summary>
    public async Task<Guid?> ReceivedNotInvoicedAccountAsync(IWorkbenchDbContext db, CancellationToken ct) => (await AccountsAsync(db, ct))?.ReceivedNotInvoiced;

    /// <summary>
    /// Adds and posts an entry from signed amounts (lines on the same account and segments are combined; zero lines are
    /// dropped). Returns null when nothing is left to post. The entry must balance.
    /// </summary>
    internal async Task<JournalEntry?> PostAsync(
        IWorkbenchDbContext db, JournalSource source, Guid sourceId, DateOnly date, string description, IEnumerable<LedgerAmount> amounts, CancellationToken ct,
        Guid? companyId = null)
    {
        var lines = amounts
            .GroupBy(a => (a.AccountId, a.ProjectId, a.WbsElementId))
            .Select(g => (g.Key, Amount: Money.Round(g.Sum(a => a.Amount)), Memo: g.First().Memo))
            .Where(l => l.Amount != 0)
            .ToList();
        if (lines.Count == 0)
        {
            return null;
        }

        var entry = new JournalEntry
        {
            Date = date,
            Source = source,
            SourceId = sourceId,
            Description = description.Length > 500 ? description[..500] : description,
            CreatedBy = ledger.Actor,
            CreatedAtUtc = ledger.UtcNow,
        };
        if (companyId is { } company)
        {
            entry.CompanyId = company;
        }

        var n = 0;
        foreach (var l in lines.OrderByDescending(l => l.Amount > 0))
        {
            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = ++n,
                AccountId = l.Key.AccountId,
                ProjectId = l.Key.ProjectId,
                WbsElementId = l.Key.WbsElementId,
                Debit = l.Amount > 0 ? l.Amount : 0,
                Credit = l.Amount < 0 ? -l.Amount : 0,
                Memo = l.Memo.Length > 500 ? l.Memo[..500] : l.Memo,
            });
        }

        db.Set<JournalEntry>().Add(entry);
        await ledger.PostAsync(db, entry, ct);
        return entry;
    }
}
