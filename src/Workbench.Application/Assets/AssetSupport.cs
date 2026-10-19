using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Assets;
using Workbench.Domain.Accounting;
using Workbench.Domain.Assets;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Assets;

/// <summary>Fixed asset settings, bound from config section <c>Assets</c>.</summary>
public sealed class AssetOptions
{
    public const string Section = "Assets";

    /// <summary>Bill lines at or above this amount can be capitalized.</summary>
    public decimal CapitalizationThreshold { get; set; } = 5_000m;

    /// <summary>Where disposal gains and losses go (created when missing).</summary>
    public string GainLossAccountNumber { get; set; } = "4900";

    /// <summary>Debited with disposal proceeds.</summary>
    public string CashAccountNumber { get; set; } = "1010";
}

internal static class AssetSupport
{
    public const string AssetEntity = nameof(FixedAsset);
    public const string CategoryEntity = nameof(AssetCategory);
    public const string RunEntity = nameof(AssetDepreciationRun);

    public static AssetDepreciationMethod ToContract(this DepreciationMethod m) => (AssetDepreciationMethod)(int)m;
    public static DepreciationMethod ToDomain(this AssetDepreciationMethod m) => (DepreciationMethod)(int)m;
    public static AssetState ToContract(this AssetStatus s) => (AssetState)(int)s;
    public static AssetStatus ToDomain(this AssetState s) => (AssetStatus)(int)s;

    private sealed record AccountDef(string Number, string Name, AccountType Type, string? Parent, CostClassification Class = CostClassification.None);

    private static readonly AccountDef[] Accounts =
    [
        new("1500", "Property and equipment", AccountType.Asset, "1000"),
        new("1510", "Computer equipment", AccountType.Asset, "1500"),
        new("1520", "Furniture and office equipment", AccountType.Asset, "1500"),
        new("1530", "Leasehold improvements", AccountType.Asset, "1500"),
        new("1590", "Accumulated depreciation", AccountType.Asset, "1500"),
        new("6140", "Depreciation", AccountType.Expense, "6100", CostClassification.Overhead),
        new("4900", "Gain (loss) on disposal of assets", AccountType.Revenue, null),
    ];

    private sealed record CategoryDef(string Code, string Name, DepreciationMethod Method, int Life, int Macrs, string Asset);

    private static readonly CategoryDef[] Categories =
    [
        new("COMP", "Computer equipment", DepreciationMethod.StraightLine, 3, 5, "1510"),
        new("FURN", "Furniture and office equipment", DepreciationMethod.StraightLine, 7, 7, "1520"),
        new("LHI", "Leasehold improvements", DepreciationMethod.StraightLine, 10, 15, "1530"),
    ];

    /// <summary>
    /// Adds the fixed asset accounts and default categories when a chart exists without them (charts installed before
    /// fixed assets). Does nothing without a chart. Saves.
    /// </summary>
    public static async Task EnsureSetupAsync(IWorkbenchDbContext db, DateTime utcNow, CancellationToken ct)
    {
        if (!await db.Set<Account>().AnyAsync(ct))
        {
            return;
        }

        var existing = await db.Set<Account>().ToDictionaryAsync(a => a.Number, ct);
        var added = false;
        foreach (var def in Accounts.Where(d => !existing.ContainsKey(d.Number)))
        {
            var account = new Account
            {
                Number = def.Number,
                Name = def.Name,
                Type = def.Type,
                NormalBalance = Account.DefaultNormalBalance(def.Type),
                ParentId = def.Parent is { } p && existing.TryGetValue(p, out var parent) ? parent.Id : null,
                CostClass = def.Class,
                CreatedAtUtc = utcNow,
            };
            db.Set<Account>().Add(account);
            existing[def.Number] = account;
            added = true;
        }

        if (!await db.Set<AssetCategory>().AnyAsync(ct))
        {
            foreach (var def in Categories)
            {
                db.Set<AssetCategory>().Add(new AssetCategory
                {
                    Code = def.Code,
                    Name = def.Name,
                    DefaultMethod = def.Method,
                    DefaultLifeYears = def.Life,
                    MacrsRecoveryYears = def.Macrs,
                    AssetAccountId = existing[def.Asset].Id,
                    AccumulatedDepreciationAccountId = existing["1590"].Id,
                    ExpenseAccountId = existing["6140"].Id,
                });
            }

            added = true;
        }

        if (added)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    public static async Task<Account> AccountByNumberAsync(IWorkbenchDbContext db, string number, string what, CancellationToken ct) =>
        await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == number && a.IsActive, ct)
            ?? throw new ArgumentException($"No active {what} account {number} in the chart of accounts.");

    public static async Task<FixedAsset> FindAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<FixedAsset>().FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Asset", id);

    /// <summary>The depreciation this asset still needs for a period: its scheduled total through the period less what's posted (catch-up).</summary>
    public static decimal DueFor(FixedAsset asset, FiscalPeriod period)
    {
        if (asset.Status != AssetStatus.InService || asset.IsGovernmentProperty || asset.InServiceDate > period.End)
        {
            return 0;
        }

        return Math.Max(0, ExpectedThrough(asset, period.Start) - asset.AccumulatedDepreciation);
    }

    /// <summary>Scheduled accumulated depreciation through <paramref name="month"/>'s month (opening balance included).</summary>
    public static decimal ExpectedThrough(FixedAsset asset, DateOnly month) =>
        DepreciationCalculator.CumulativeThrough(Schedule(asset), DepreciationCalculator.MonthIndex(asset.InServiceDate, month));

    /// <summary>
    /// The asset's monthly book schedule from its in-service month, with its convention; for assets with an opening
    /// balance, the opening amount in the cut-over month and the rest spread over the remaining life.
    /// </summary>
    public static IReadOnlyList<decimal> Schedule(FixedAsset asset)
    {
        var schedule = DepreciationCalculator.MonthlySchedule(asset.Cost, asset.SalvageValue, asset.UsefulLifeYears, asset.Method, asset.Convention);
        return asset.OpeningBalanceDate is { } cutover
            ? DepreciationCalculator.OpeningBalanceSchedule(schedule, DepreciationCalculator.MonthIndex(asset.InServiceDate, cutover) + 1,
                asset.OpeningAccumulatedDepreciation, Math.Max(0, asset.Cost - asset.SalvageValue))
            : schedule;
    }

    public static DepreciationConvention ToDomain(this AssetDepreciationConvention c) => (DepreciationConvention)(int)c;
    public static AssetDepreciationConvention ToContract(this DepreciationConvention c) => (AssetDepreciationConvention)(int)c;

    public static string Truncate(string text, int max) => text.Length > max ? text[..max] : text;
}
