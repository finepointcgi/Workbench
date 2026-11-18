using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Financials;
using Workbench.Application.Purchasing;
using Workbench.Application.Customization;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Licensing;
using Workbench.Contracts.Modules;
using Workbench.Domain.Integrations;
using Workbench.Infrastructure.Persistence;
using Workbench.Plugins;
using Workbench.Tests.Identity;
using Workbench.Ui.Customization;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Modules;

/// <summary>
/// The product as shipped, without the materials module (parts, inventory, manufacturing, quality, engineering changes, sales orders and shipping live in a
/// private repository and load from the modules folder): its tables, pages, API paths, permissions, catalog rows and cost source are simply absent, and
/// nothing else notices. See docs/operations/module-extraction-materials.md.
/// </summary>
[Collection(ModuleCollection.Name)]
public sealed class MaterialsAbsentTests
{
    private static readonly string[] MaterialsTables =
    [
        "BackflushSetting", "BackflushShortage", "BillOfMaterial", "Bin",
        "BomComponent", "CapacityScheduleRun", "CountTag", "InventoryBalance",
        "InventoryTransaction", "Kit", "KitLine", "KitPick",
        "LandedCharge", "LandedChargeLine", "MaterialPurchaseLink", "MrpRun",
        "OperationLoad", "OperationSchedule", "Part", "PartForecast",
        "PhysicalInventory", "PhysicalInventoryBin", "PickList", "PickListLine",
        "PlannedOrder", "ProjectMaterialRequirement", "Routing", "RoutingOperation",
        "SalesOrder", "SalesOrderLine", "Shipment", "ShipmentLine",
        "ShipmentPackage", "Warehouse", "WawfReceivingReport", "WorkCenter",
        "WorkCenterCalendar", "WorkCenterCapacityException", "WorkOrder", "WorkOrderComponent",
        "WorkOrderGenealogy", "WorkOrderLaborReport", "WorkOrderOperation", "QualityCorrectiveActions",
        "QualityDefectCodes", "EngineeringChanges", "EngineeringChangeItems", "EngineeringChangeWorkOrderReviews",
        "QualityInspections", "QualityInspectionCharacteristics", "QualityInspectionItems", "QualityInspectionPlans",
        "QualityInspectionResults", "QualityNonconformances", "QualityNonconformanceItems", "QualityAttachments",
        "QualityVendorDebitMemos",
    ];

    [Fact]
    public void A_new_database_has_none_of_the_modules_tables()
    {
        using var empty = ModuleHost.Use(ModuleHost.Empty);
        var connectionString = TestDb.NewConnectionString(DatabaseProvider.Sqlite);
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Equal(57, MaterialsTables.Length);
        Assert.Contains("PurchaseOrder", tables); // A migrated core database...
        Assert.Contains("RfqLine", tables); // ...including the sourcing table that names a part (its column is a plain key without the module)...
        Assert.DoesNotContain(tables, t => MaterialsTables.Contains(t)); // ...without the tables core used to create for the module.
        Assert.DoesNotContain("__EFMigrationsHistory_materials", tables);
    }

    [Fact]
    public void Menu_permissions_catalog_rows_and_api_paths_of_the_module_are_not_there()
    {
        using var empty = ModuleHost.Use(ModuleHost.Empty);
        Assert.All(new[] { "materials", "quality", "engineering-changes", "sales-orders", "shipping" }, s => Assert.Null(ModuleMap.ForApiSegment(s)));
        Assert.DoesNotContain(NavCatalog.Entries, e => e.Href == "materials" || e.Href.StartsWith("materials/", StringComparison.Ordinal) || e.Href.StartsWith("m/receive", StringComparison.Ordinal)
            || e.Href.StartsWith("m/count", StringComparison.Ordinal) || e.Href.StartsWith("m/pick", StringComparison.Ordinal));
        Assert.DoesNotContain(NavCatalog.MenuEntries(new NavAccess(new CurrentUserInfo("a@example.com", "A", OrgRoles.Admin, null))), e => e.Section == NavCatalog.Materials); // The section hides when empty.
        Assert.All(new[] { "materials.view", "materials.manage", "materials.costing", "quality.dispositions.apply", "sales.orders.view", "sales.orders.manage", "sales.credit_hold" },
            k => Assert.False(PermissionCatalog.Exists(k), k));
        Assert.DoesNotContain(ApiKeyScopes.Areas.SelectMany(a => a.PathSegments), s => s is "materials" or "quality" or "engineering-changes");
        Assert.DoesNotContain(NewCatalog.Actions, a => a.Key == "engineering-change");
        Assert.DoesNotContain(CustomizableLists.All, k => k is "parts" or "sales-orders");
        Assert.DoesNotContain(ListSources.All, s => s.ListKey is "parts" or "sales-orders");
        Assert.DoesNotContain(DocumentNumberTypes.All, t => t.Key is "sales-order" or "pick-list" or "shipment" or "ncr" or "car" or "eco" or "inspection" or "debit-memo");
        Assert.DoesNotContain(WebhookEventCatalog.All, e => e.Key is "shipment.shipped" or "ncr.dispositioned");
        Assert.DoesNotContain(IntegrationEventType.All, e => e.Key is "shipment.shipped" or "ncr.dispositioned");
        Assert.DoesNotContain(ApprovalDocumentTypes.All, k => k is "ncr-disposition" or "engineering-change");
        Assert.DoesNotContain(BuiltInForms.All, f => f.EntityType == "part");
        Assert.DoesNotContain(BuiltInForms.ListOnly, e => e == "sales-order");
        Assert.DoesNotContain(new BuiltInEntityFieldCatalog().EntityTypes, e => e is "part" or "sales-order");
        Assert.Contains(PermissionCatalog.AreaMaterials, PermissionCatalog.Areas); // The area name and the Materials role belong to core (roles are stored by the role editor).
        Assert.Empty(ModuleRegistry.Current.RowsOf<ProjectCostSource>());
    }

    [Fact]
    public async Task The_host_starts_the_modules_api_paths_are_404_and_the_rest_of_core_still_works()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = factory.CreateClientAs("pm@example.com");

        foreach (var path in new[] { "api/v1/materials/parts", "api/v1/materials/work-orders", "api/v1/quality/inspections", "api/v1/engineering-changes", "api/v1/sales-orders", "api/v1/shipping/shipments" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await finance.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await finance.GetAsync("api/v1/migration/checklist")).StatusCode); // The rest of the same area works.
        Assert.Equal(HttpStatusCode.OK, (await finance.GetAsync("api/v1/purchasing/vendors")).StatusCode);

        // Without the module core reads no items and no returns to vendors, and counts no material cost on projects.
        await using var host = await SeededHost.CreateAsync();
        Assert.IsType<NoItemReader>(host.Host.Get<IItemReader>());
        Assert.Null(host.Host.Get<IServiceProvider>().GetService(typeof(IVendorReturnSource)));
        Assert.Empty(host.Host.Get<IServiceProvider>().GetServices(typeof(IBillLineGuard)));
        Assert.Empty(await ProjectCostSources.LoadAsync(null!, null, DateOnly.MinValue, DateOnly.MaxValue, default));
    }

    [Fact]
    public async Task A_quality_return_is_not_found_without_the_module()
    {
        await using var host = await SeededHost.CreateAsync();
        await Assert.ThrowsAsync<Workbench.Contracts.WorkbenchNotFoundException>(() => host.As(SeededHost.Finance).Host.Get<Workbench.Contracts.Purchasing.IApCreditService>().CreateFromQualityReturnAsync(Guid.NewGuid()));
    }
}
