using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Materials;

/// <summary>
/// <see cref="ProcurementKit"/> (default chart, period 2026-10 open, vendors, analyst@ in OPS) plus materials@ (the
/// Materials org role, an employee in OPS), warehouse MAIN with bins A1 and A2, and helpers to make parts and stock.
/// Calls run as materials@ unless switched.
/// </summary>
internal sealed class MaterialsKit : IAsyncDisposable
{
    public const string MaterialsUser = "materials@example.com";
    public static readonly DateOnly Day = PurchasingKit.Day;

    private MaterialsKit(PurchasingKit kit) => Kit = kit;

    public PurchasingKit Kit { get; }
    public WarehouseDto Main { get; private set; } = default!;
    public Guid A1 => Main.Bins.Single(b => b.Code == "A1").Id;
    public Guid A2 => Main.Bins.Single(b => b.Code == "A2").Id;
    public Guid MaterialsEmployeeId { get; private set; }

    public IPartService Parts => Kit.Get<IPartService>();
    public IInventoryService Inventory => Kit.Get<IInventoryService>();
    public IManufacturingService Manufacturing => Kit.Get<IManufacturingService>();
    public IMrpService Mrp => Kit.Get<IMrpService>();
    public IMaterialReportService Reports => Kit.Get<IMaterialReportService>();

    public static async Task<MaterialsKit> CreateAsync()
    {
        var kit = new MaterialsKit(await ProcurementKit.CreateAsync());
        await using (var db = await kit.Kit.Factory.CreateAsync())
        {
            await RoleStore.AddUserAsync(db, new AppUser { Email = MaterialsUser, DisplayName = "Mia" }, OrgRoles.Materials);
            var employee = new Employee
            {
                EmployeeNumber = "E9", Email = MaterialsUser, DisplayName = "Mia Materials", OrgUnitId = kit.Kit.OrgId, HireDate = new DateOnly(2024, 1, 1),
            };
            db.Set<Employee>().Add(employee);
            await db.SaveChangesAsync();
            kit.MaterialsEmployeeId = employee.Id;
        }

        await using (var db = await kit.Kit.Factory.CreateAsync())
        {
            await kit.Kit.Get<Workbench.Application.Materials.MaterialsLedger>().AccountsAsync(db, default);
            kit._accounts = await db.Set<Account>().ToDictionaryAsync(a => a.Number, a => a.Id);
        }

        kit.As(MaterialsUser);
        var main = await kit.Parts.SaveWarehouseAsync(null, new SaveWarehouseRequest("main", "Main stockroom"));
        await kit.Parts.SaveBinAsync(main.Id, null, new SaveBinRequest("a1"));
        kit.Main = await kit.Parts.SaveBinAsync(main.Id, null, new SaveBinRequest("A2"));
        return kit;
    }

    public MaterialsKit As(string email)
    {
        Kit.As(email);
        return this;
    }

    private Dictionary<string, Guid> _accounts = [];

    /// <summary>Account ids by number, including the materials accounts (added to the chart on first use).</summary>
    public Guid Account(string number) => _accounts.TryGetValue(number, out var id) ? id : throw new KeyNotFoundException(number);

    public async Task<PartSummaryDto> PartAsync(
        string number, PartSourceKind source = PartSourceKind.Buy, PartCostMethod method = PartCostMethod.Average, decimal standard = 0,
        PartTracking tracking = PartTracking.None, int leadTime = 0, decimal safety = 0, Guid? vendor = null) =>
        (await Parts.CreateAsync(new SavePartRequest(number, null, $"{number} part", "EA", source, tracking, method, standard, vendor, leadTime, safety,
            DefaultWarehouseId: Main.Id, DefaultBinId: A1))).Summary;

    /// <summary>Puts stock in with an adjustment (average-cost parts at <paramref name="unitCost"/>).</summary>
    public Task<InventoryTransactionDto> StockAsync(Guid partId, decimal quantity, decimal unitCost, string? lot = null, string? serial = null, Guid? bin = null) =>
        Inventory.AdjustAsync(new AdjustInventoryRequest(partId, Main.Id, bin ?? A1, quantity, Day, "Opening stock", lot, serial, UnitCost: unitCost));

    public async Task<List<JournalEntry>> EntriesAsync(JournalSource? source = null) => await Kit.EntriesAsync(source);

    public async Task<decimal> BalanceAsync(string number) =>
        (await EntriesAsync()).SelectMany(e => e.Lines).Where(l => l.AccountId == Account(number)).Sum(l => l.Debit - l.Credit);

    public async Task<int> JournalCountAsync()
    {
        await using var db = await Kit.Factory.CreateAsync();
        return await db.Set<JournalEntry>().CountAsync();
    }

    /// <summary>An issued PO from ACME (finance@) with one account line on the inventory clearing account, linked to the part (materials@).</summary>
    public async Task<PurchaseOrderDto> PartOrderAsync(Guid partId, decimal quantity, decimal price)
    {
        As(Finance);
        var po = await Kit.Get<IPurchaseOrderService>().CreateAsync(new SavePurchaseOrderRequest(Kit.Acme.Id, Day,
            [new SavePurchaseOrderLineRequest("Parts", quantity, price, AccountId: Account("1405"), OrgUnitId: Kit.OrgId)]));
        po = await Kit.Get<IPurchaseOrderService>().IssueAsync(po.Order.Id);
        As(MaterialsUser);
        await Inventory.LinkPurchaseOrderLineAsync(new LinkPurchaseLineRequest(po.Lines[0].Id, partId));
        return po;
    }

    public ValueTask DisposeAsync() => Kit.DisposeAsync();
}
