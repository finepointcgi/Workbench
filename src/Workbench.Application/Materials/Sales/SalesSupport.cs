using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Contracts.Materials;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Materials;

/// <summary>Sales and shipping settings, bound from config section <c>Materials:Sales</c>.</summary>
public sealed class SalesShippingOptions
{
    public const string Section = "Materials:Sales";

    /// <summary>Cost of goods sold: what shipments that aren't cost-type work relieve inventory to.</summary>
    public string CostOfGoodsSoldAccountNumber { get; set; } = "5160";

    /// <summary>The three letters DD250 shipment numbers start with (the contractor's choice, DFARS appendix F).</summary>
    public string ShipmentNumberPrefix { get; set; } = "SHP";
}

internal static class SalesMapping
{
    public static SalesOrderState ToContract(this SalesOrderStatus v) => (SalesOrderState)(int)v;
    public static SalesOrderStatus ToDomain(this SalesOrderState v) => (SalesOrderStatus)(int)v;
    public static GovernmentInspectionPoint ToContract(this InspectionPoint v) => (GovernmentInspectionPoint)(int)v;
    public static InspectionPoint ToDomain(this GovernmentInspectionPoint v) => Enum.IsDefined(v) ? (InspectionPoint)(int)v : throw new ArgumentException("Unknown inspection point.");
    public static PickListState ToContract(this PickListStatus v) => (PickListState)(int)v;
    public static ShipmentState ToContract(this ShipmentStatus v) => (ShipmentState)(int)v;
    public static ShipmentStatus ToDomain(this ShipmentState v) => (ShipmentStatus)(int)v;
    public static WawfDocument ToContract(this WawfDocumentType v) => (WawfDocument)(int)v;
    public static WawfDocumentType ToDomain(this WawfDocument v) => Enum.IsDefined(v) ? (WawfDocumentType)(int)v : throw new ArgumentException("Unknown WAWF document type.");
    public static WawfReportState ToContract(this WawfStatus v) => (WawfReportState)(int)v;
    public static WawfStatus ToDomain(this WawfReportState v) => Enum.IsDefined(v) ? (WawfStatus)(int)v : throw new ArgumentException("Unknown WAWF status.");
}

/// <summary>Rules shared by sales orders and shipping.</summary>
internal static class SalesRules
{
    public const string OrderEntity = nameof(SalesOrder);
    public const string ShipmentEntity = nameof(Shipment);

    /// <summary>DFARS 252.211-7003: items with a unit price of $5,000 or more are UID-marked.</summary>
    public const decimal UidThreshold = 5_000m;

    public static bool IsCostType(ClinPricing p) => p != ClinPricing.FirmFixedPrice;

    public static bool IsCostType(ContractType t) => t != ContractType.FirmFixedPrice;

    /// <summary>
    /// Whether a line ships to the order's WBS element (cost-type work: cost-plus, T&amp;M and labor-hour CLINs or contracts)
    /// rather than cost of goods sold (firm-fixed-price items and commercial orders).
    /// </summary>
    public static bool ChargesWbs(SalesOrderLine line, Clin? clin, Contract? contract) =>
        clin is not null ? IsCostType(clin.Pricing) : contract is not null && IsCostType(contract.Type);

    /// <summary>Stock a sales order for <paramref name="projectId"/> may ship: general stock, or that project's contract inventory and government-furnished material.</summary>
    public static bool Usable(InventoryOwnership ownership, Guid? owner, Guid? projectId) =>
        ownership == InventoryOwnership.General || (projectId is { } p && owner == p);

    public static async Task<string> NextNumberAsync<T>(IQueryable<T> set, Func<IQueryable<T>, IQueryable<string>> numbers, string prefix, CancellationToken ct)
    {
        var last = await numbers(set).Where(n => n.StartsWith(prefix)).OrderByDescending(n => n).FirstOrDefaultAsync(ct);
        var next = last is not null && int.TryParse(last[prefix.Length..], out var n) ? n + 1 : 1;
        return $"{prefix}{next:D6}";
    }

    public static string? Code(string? value, string what)
    {
        var v = Purchasing.PurchasingSupport.Optional(value, what, 16);
        return v?.ToUpperInvariant();
    }

    public static string Location(string warehouse, string bin) => $"{warehouse} / {bin}";

    public static string Describe(string lot, string serial) =>
        (lot.Length > 0 ? $"lot {lot}" : "") + (lot.Length > 0 && serial.Length > 0 ? ", " : "") + (serial.Length > 0 ? $"serial {serial}" : "");

    /// <summary>
    /// The cost of goods sold account, added under Direct ODCs (5100) when missing and a chart exists (null without a chart).
    /// Saves when it adds the account.
    /// </summary>
    public static async Task<Guid?> CostOfGoodsSoldAsync(IWorkbenchDbContext db, IOptions<SalesShippingOptions> options, DateTime utcNow, CancellationToken ct)
    {
        var number = options.Value.CostOfGoodsSoldAccountNumber;
        if (await db.Set<Account>().FirstOrDefaultAsync(a => a.Number == number, ct) is { } existing)
        {
            return existing.Id;
        }

        if (!await db.Set<Account>().AnyAsync(ct))
        {
            return null;
        }

        var parent = await db.Set<Account>().FirstOrDefaultAsync(a => a.Number == "5100", ct);
        var account = new Account
        {
            Number = number,
            Name = "Cost of goods sold",
            Type = AccountType.Expense,
            NormalBalance = Account.DefaultNormalBalance(AccountType.Expense),
            ParentId = parent?.Id,
            CostClass = CostClassification.DirectOdc,
            Description = "Inventory shipped on sales orders (firm-fixed-price and commercial deliveries)",
            CreatedAtUtc = utcNow,
        };
        db.Set<Account>().Add(account);
        await db.SaveChangesAsync(ct);
        return account.Id;
    }
}

/// <summary>Stock reserved by open pick lists and by shipments still being packed, by location key.</summary>
internal static class SalesReservations
{
    internal readonly record struct Key(Guid PartId, Guid BinId, string Lot, string Serial, InventoryOwnership Ownership, Guid? OwnerProjectId);

    public static async Task<Dictionary<Key, decimal>> LoadAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> partIds, Guid? excludePickListId, CancellationToken ct)
    {
        var ids = partIds.ToList();
        var picks = await db.Set<PickListLine>().AsNoTracking().Where(l => ids.Contains(l.PartId))
            .Join(db.Set<PickList>().AsNoTracking().Where(p => p.Status == PickListStatus.Open && p.Id != excludePickListId), l => l.PickListId, p => p.Id, (l, p) => l)
            .ToListAsync(ct);
        var packing = await db.Set<ShipmentLine>().AsNoTracking().Where(l => ids.Contains(l.PartId))
            .Join(db.Set<Shipment>().AsNoTracking().Where(s => s.Status == ShipmentStatus.Packing), l => l.ShipmentId, s => s.Id, (l, s) => l)
            .ToListAsync(ct);
        var result = new Dictionary<Key, decimal>();
        foreach (var l in picks)
        {
            var k = new Key(l.PartId, l.BinId, l.Lot, l.Serial, l.Ownership, l.OwnerProjectId);
            result[k] = result.GetValueOrDefault(k) + (l.PickedAtUtc is null ? l.Quantity : l.QuantityPicked);
        }

        foreach (var l in packing)
        {
            var k = new Key(l.PartId, l.BinId, l.Lot, l.Serial, l.Ownership, l.OwnerProjectId);
            result[k] = result.GetValueOrDefault(k) + l.Quantity;
        }

        return result;
    }

    public static Key Of(InventoryBalance b) => new(b.PartId, b.BinId, b.Lot, b.Serial, b.Ownership, b.OwnerProjectId);
}
