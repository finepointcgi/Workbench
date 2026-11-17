using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Licensing;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Licensing;
using Workbench.Contracts.Modules;
using Workbench.Domain.Licensing;
using Workbench.Infrastructure.Persistence;
using Workbench.Plugins;
using Workbench.Tests.Identity;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Modules;

/// <summary>The module tests change process-wide module state (the registry the menu, permissions and API key scopes read), so they never run beside other tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ModuleCollection
{
    public const string Name = "Modules";
}

/// <summary>
/// A license module that lives outside the repository, loaded through the real loader from a folder (the sample module in
/// tests/Workbench.SampleModule, copied next to the tests). Licensed, it contributes a service, an API endpoint, a menu entry, a
/// permission, an API key area and an entity with its own migration; unlicensed or missing, none of it exists.
/// </summary>
[Collection(ModuleCollection.Name)]
public sealed class ModulePluginTests : IDisposable
{
    private static readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string PublicKey = Signer.ExportSubjectPublicKeyInfoPem();
    private static readonly string SampleDll = Path.Combine(AppContext.BaseDirectory, "modules-sample", "Workbench.SampleModule.dll");
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        ModuleHost.Use(ModuleHost.Empty); // Whatever a test loaded, the next one starts with the product as shipped.
        foreach (var folder in _folders)
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A modules folder with one module folder in it: module.json, and the sample's assembly unless <paramref name="withAssembly"/> is false.</summary>
    private string Modules(string id = "govcon", bool withAssembly = true, string assembly = "Workbench.SampleModule.dll", byte[]? assemblyBytes = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"workbench-modules-{Guid.NewGuid():N}");
        var folder = Directory.CreateDirectory(Path.Combine(root, "sample")).FullName;
        _folders.Add(root);
        File.WriteAllText(Path.Combine(folder, "module.json"), $$"""{ "id": "{{id}}", "assembly": "{{assembly}}" }""");
        if (assemblyBytes is not null)
        {
            File.WriteAllBytes(Path.Combine(folder, assembly), assemblyBytes);
        }
        else if (withAssembly)
        {
            File.Copy(SampleDll, Path.Combine(folder, assembly));
        }

        return root;
    }

    private static ModuleHostOptions Options(string folder, params string[] licensed) =>
        new(folder, licensed.Contains, Required: null, NavCatalog.BuiltInSectionKeys, NavCatalog.BuiltInHrefs);

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Issue(params string[] modules)
    {
        var payload = Encoding.UTF8.GetBytes($$"""{"v":2,"licenseId":"0f8b1c52-6e1a-4d0e-9a3b-5c1d2e3f4a5b","licensee":"Acme","issuedAt":"2026-10-01","expires":"2099-11-01","seats":25,"lightSeats":5,"modules":[{{string.Join(",", modules.Select(m => $"\"{m}\""))}}]}""");
        return $"WB2.{B64(payload)}.{B64(Signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}";
    }

    // ───────────────────────────────────── The loader ─────────────────────────────────────

    [Fact]
    public void A_licensed_module_adds_its_rows_to_every_table_the_product_reads()
    {
        Assert.Null(ModuleMap.ForApiSegment("sample-widgets"));
        Assert.DoesNotContain(NavCatalog.Entries, e => e.Href == "sample-widgets");
        var builtIn = (Permissions: PermissionCatalog.All.Count, Areas: ApiKeyScopes.Areas.Count, Nav: NavCatalog.Entries.Count, Services: ModuleMap.ServiceInterfaces().Count);

        var host = ModuleHost.Load(Options(Modules(), ModuleIds.GovCon));

        var result = Assert.Single(host.Results);
        Assert.Equal((ModuleLoadStatus.Loaded, "1.2.3"), (result.Status, result.Version));
        Assert.Equal(ModuleIds.GovCon, ModuleMap.ForApiSegment("sample-widgets"));
        Assert.Equal(ModuleIds.GovCon, ModuleMap.ForPermission("sample.widgets.view"));
        Assert.Equal(ModuleIds.Core, ModuleMap.ForApiSegment("sheets")); // Built-in rows are untouched.

        // Permission catalog, with the area it adds, and the roles it is granted to.
        Assert.Equal(builtIn.Permissions + 1, PermissionCatalog.All.Count);
        Assert.Equal("See sample widgets", PermissionCatalog.NameOf("sample.widgets.view"));
        Assert.Contains("Sample module", PermissionCatalog.Areas);
        Assert.True(PermissionCatalog.ForBuiltInRoles(OrgRoles.Finance).Contains("sample.widgets.view"));
        Assert.False(PermissionCatalog.ForBuiltInRoles(OrgRoles.Employee).Contains("sample.widgets.view"));

        // API key scopes: a new area, and a key with it may call the module's path and nothing beside it.
        Assert.Equal(builtIn.Areas + 1, ApiKeyScopes.Areas.Count);
        Assert.True(ApiKeyScopes.Check(["sample:read"], "/api/v1/sample-widgets", "GET").Allowed);
        Assert.False(ApiKeyScopes.Check(["sample:read"], "/api/v1/sample-widgets", "POST").Allowed);
        Assert.False(ApiKeyScopes.Check(["sample:read"], "/api/v1/time", "GET").Allowed);

        // The menu: the entry, in its module, shown to someone who holds the permission.
        var entry = Assert.Single(NavCatalog.Entries, e => e.Href == "sample-widgets");
        Assert.Equal(ModuleIds.GovCon, entry.ModuleId);
        Assert.Equal(builtIn.Nav + 1, NavCatalog.Entries.Count);
        var finance = new NavAccess(new CurrentUserInfo("f@example.com", "F", OrgRoles.Finance, null));
        Assert.Contains(NavCatalog.MenuEntries(finance), e => e.Href == "sample-widgets");
        Assert.DoesNotContain(NavCatalog.MenuEntries(new NavAccess(new CurrentUserInfo("e@example.com", "E", OrgRoles.Employee, null))), e => e.Href == "sample-widgets");

        // The service, so the edition gates wrap it like an in-repo one.
        var service = Assert.Single(ModuleMap.ServiceInterfaces(), t => t.Name == "ISampleWidgetService");
        Assert.Equal(ModuleIds.GovCon, ModuleMap.ForService(service));
        Assert.Equal(builtIn.Services + 1, ModuleMap.ServiceInterfaces().Count);

        // Blazor: the module's assembly is routable.
        Assert.Contains(ModuleRegistry.Current.Modules.SelectMany(m => m.RoutableAssemblies), a => a.GetName().Name == "Workbench.SampleModule");
    }

    [Fact]
    public void The_coverage_rules_hold_for_the_union_of_built_in_and_module_rows()
    {
        ModuleHost.Load(Options(Modules(), ModuleIds.GovCon));
        var known = ModuleCatalog.AllIds.ToHashSet();

        Assert.All(NavCatalog.Entries, e => Assert.NotNull(ModuleMap.ForNav(e.Section, e.Group, e.Href)));
        Assert.All(NavCatalog.Entries, e => Assert.Contains(e.ModuleId, known));
        Assert.All(PermissionCatalog.All, p => Assert.NotNull(ModuleMap.ForPermission(p.Key)));
        Assert.All(ModuleMap.ServiceInterfaces(), s => Assert.NotNull(ModuleMap.ForService(s)));
        Assert.All(PermissionCatalog.All, p => Assert.Contains(p.Area, PermissionCatalog.Areas));
    }

    [Fact]
    public void An_unlicensed_module_is_never_loaded_and_adds_nothing()
    {
        // The assembly is not even a valid assembly: had the loader tried to load it, the result would be Failed, not NotLicensed.
        var host = ModuleHost.Load(Options(Modules(assemblyBytes: [1, 2, 3]), ModuleIds.Billing));

        var result = Assert.Single(host.Results);
        Assert.Equal(ModuleLoadStatus.NotLicensed, result.Status);
        Assert.Empty(host.Modules);
        Assert.True(ModuleRegistry.Current.IsEmpty);
        Assert.Null(ModuleMap.ForApiSegment("sample-widgets"));
        Assert.Null(ModuleMap.ForPermission("sample.widgets.view"));
        Assert.False(PermissionCatalog.Exists("sample.widgets.view"));
        Assert.DoesNotContain(NavCatalog.Entries, e => e.Href == "sample-widgets");
        Assert.DoesNotContain(ModuleMap.ServiceInterfaces(), t => t.Name == "ISampleWidgetService");
        Assert.False(ApiKeyScopes.Check(["sample:read"], "/api/v1/sample-widgets", "GET").Allowed);
        Assert.DoesNotContain(ApiKeyScopes.Areas, a => a.Key == "sample");
    }

    [Fact]
    public void A_module_whose_assembly_is_missing_adds_nothing_and_the_host_goes_on()
    {
        var host = ModuleHost.Load(Options(Modules(withAssembly: false), ModuleIds.GovCon));

        Assert.Equal(ModuleLoadStatus.MissingAssembly, Assert.Single(host.Results).Status);
        Assert.Empty(host.Modules);
        Assert.Null(ModuleMap.ForApiSegment("sample-widgets"));
        Assert.DoesNotContain(NavCatalog.Entries, e => e.Href == "sample-widgets");
    }

    [Fact]
    public void A_required_module_that_did_not_load_stops_the_host()
    {
        var options = Options(Modules(withAssembly: false), ModuleIds.GovCon) with { Required = [ModuleIds.GovCon] };

        var ex = Assert.Throws<InvalidOperationException>(() => ModuleHost.Load(options));
        Assert.Contains("govcon", ex.Message);
        Assert.Contains("MissingAssembly", ex.Message);
        Assert.Null(ModuleMap.ForApiSegment("sample-widgets")); // Nothing half-published.
    }

    [Fact]
    public void A_module_that_says_another_id_than_its_manifest_is_refused()
    {
        var host = ModuleHost.Load(Options(Modules(id: "billing"), ModuleIds.Billing));

        var result = Assert.Single(host.Results);
        Assert.Equal(ModuleLoadStatus.Invalid, result.Status);
        Assert.Contains("govcon", result.Detail);
        Assert.Empty(host.Modules);
    }

    [Fact]
    public void A_manifest_may_only_name_a_file_in_its_own_folder()
    {
        var root = Modules();
        Assert.Single(ModuleHost.Load(Options(root, ModuleIds.GovCon)).Modules); // The ordinary manifest loads...

        File.WriteAllText(Path.Combine(root, "sample", "module.json"), """{ "id": "govcon", "assembly": "../../elsewhere.dll" }""");
        var refused = ModuleHost.Load(Options(root, ModuleIds.GovCon));
        Assert.Equal(ModuleLoadStatus.Invalid, Assert.Single(refused.Results).Status); // ...a path out of the folder does not.
    }

    [Fact]
    public void Contributions_that_clash_with_the_product_or_each_other_are_refused()
    {
        var clash = new ModuleContribution { ModuleId = ModuleIds.GovCon, ApiSegments = ["billing", "sample"], PermissionPrefixes = ["sample."] };
        Assert.Contains(ModuleSet.Empty.Validate(clash), p => p.Contains("/billing"));
        Assert.DoesNotContain(ModuleSet.Empty.Validate(clash), p => p.Contains("/sample"));

        var strayPermission = new ModuleContribution
        {
            ModuleId = ModuleIds.GovCon, PermissionPrefixes = ["sample."], PermissionAreas = ["Sample"],
            Permissions = [new PermissionDefinition("billing.invoices.stolen", "Sample", "x", "x", OrgRoles.Finance), new PermissionDefinition("sample.ok", "Nope", "x", "x", OrgRoles.Finance)],
        };
        var problems = ModuleSet.Empty.Validate(strayPermission);
        Assert.Contains(problems, p => p.Contains("billing.invoices.stolen") && p.Contains("prefixes"));
        Assert.Contains(problems, p => p.Contains("sample.ok") && p.Contains("area"));

        var core = new ModuleContribution { ModuleId = ModuleKeys.Core };
        Assert.NotEmpty(ModuleSet.Empty.Validate(core)); // Core is never a plugin.
        var unknown = new ModuleContribution { ModuleId = "made-up" };
        Assert.NotEmpty(ModuleSet.Empty.Validate(unknown));

        var keyArea = new ModuleContribution { ModuleId = ModuleIds.GovCon, ApiSegments = ["sample-widgets"], ApiKeyAreas = [new ApiKeyAreaDto("sample", "S", "S", ["sheets"])] };
        Assert.Contains(ModuleSet.Empty.Validate(keyArea), p => p.Contains("own API paths"));

        var service = new ModuleContribution { ModuleId = ModuleIds.GovCon, ServiceInterfaces = [typeof(Workbench.Contracts.Billing.IInvoiceService)] };
        Assert.Contains(ModuleSet.Empty.Validate(service), p => p.Contains("already belongs"));

        var twice = new ModuleContribution { ModuleId = ModuleIds.GovCon, ApiSegments = ["sample-widgets"] };
        Assert.Contains(ModuleSet.Empty.With(twice).Validate(twice with { ModuleId = ModuleIds.Capture }), p => p.Contains("/sample-widgets"));
    }

    // ───────────────────────────────────── The license ─────────────────────────────────────

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void The_installs_license_key_decides_which_module_assemblies_may_load()
    {
        var clock = TimeProvider.System;
        var withKey = (string key) => Config(("License:PublicKeyPem", PublicKey), ("Modules:LicenseKey", key));

        Assert.True(ModuleEntitlement.Check(withKey(Issue("govcon", "billing")), clock, out _)("govcon"));
        Assert.False(ModuleEntitlement.Check(withKey(Issue("billing")), clock, out _)("govcon"));
        Assert.False(ModuleEntitlement.Check(withKey(Issue("govcon")), clock, out _)("core")); // Core is never a plugin.

        Assert.False(ModuleEntitlement.Check(Config(), clock, out var none)("govcon"));
        Assert.Contains("No license key", none);

        var wrongSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfoPem();
        Assert.False(ModuleEntitlement.Check(Config(("License:PublicKeyPem", wrongSigner), ("License:Key", Issue("govcon"))), clock, out var bad)("govcon"));
        Assert.Contains("not valid", bad);

        Assert.True(ModuleEntitlement.Check(Config(("License:EditionOverride", "Business")), clock, out _)("govcon"));

        var later = new Workbench.Tests.MyWork.MyWorkServiceTests.FixedClock(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.False(ModuleEntitlement.Check(withKey(Issue("govcon")), later, out var expired)("govcon"));
        Assert.Contains("expired", expired);
    }

    // ───────────────────────────────────── Migrations ─────────────────────────────────────

    [Fact]
    public void A_modules_history_table_is_its_own()
    {
        Assert.Equal("__EFMigrationsHistory_govcon", new ModuleMigrations(typeof(DbContext), "A", "B").HistoryTableFor("govcon"));
        Assert.Equal("custom", new ModuleMigrations(typeof(DbContext), "A", "B", "custom").HistoryTableFor("govcon"));
    }

    // ───────────────────────────────────── The real host ─────────────────────────────────────

    private WorkbenchWebFactory Host(string folder, string? key) =>
        new("Development", testAuth: true, seededTemplate: true, settings: new Dictionary<string, string>
        {
            ["Modules:Folder"] = folder,
            ["License:EditionOverride"] = "", // Development turns Business on for everyone; the key decides here.
            ["License:PublicKeyPem"] = PublicKey,
            ["Modules:LicenseKey"] = key ?? "",
        });

    [Fact]
    public async Task A_licensed_module_serves_its_endpoint_registers_its_service_and_migrates_its_own_table()
    {
        await using var factory = Host(Modules(), Issue("govcon"));
        using var client = factory.CreateClientAs("pm@example.com");

        var response = await client.GetAsync("/api/v1/sample-widgets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", (await response.Content.ReadAsStringAsync()).Trim());

        // The service is registered, and its entity is in the host's model on the table its own migration made.
        var module = factory.Services.GetRequiredService<ModuleHost>().Modules.Single();
        var service = module.GetType().Assembly.GetType("Workbench.SampleModule.ISampleWidgetService")!;
        await using var scope = factory.Services.CreateAsyncScope();
        var widgets = scope.ServiceProvider.GetRequiredService(service);
        await (Task)service.GetMethod("AddAsync")!.Invoke(widgets, ["Gadget", CancellationToken.None])!;
        Assert.Equal("[\"Gadget\"]", (await (await client.GetAsync("/api/v1/sample-widgets")).Content.ReadAsStringAsync()).Trim());

        // Its history is in its own table, not core's.
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""select count(*) as "Value" from "__EFMigrationsHistory_govcon" """).SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""select count(*) as "Value" from "__EFMigrationsHistory" where "MigrationId" like '%SampleWidgets' """).SingleAsync());

        // Applying again changes nothing.
        await factory.Services.GetRequiredService<DatabaseMigrator>().MigrateCurrentAsync();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""select count(*) as "Value" from "__EFMigrationsHistory_govcon" """).SingleAsync());

        // The menu and the API key scopes see it, and the router gets its assembly.
        Assert.Contains(NavCatalog.Entries, e => e.Href == "sample-widgets");
        Assert.Contains(ModuleRegistry.Current.Modules.SelectMany(m => m.RoutableAssemblies), a => a.GetName().Name == "Workbench.SampleModule");
        Assert.True(ApiKeyScopes.Check(["sample:write"], "/api/v1/sample-widgets", "POST").Allowed);
    }

    [Fact]
    public async Task An_unlicensed_module_has_no_endpoint_no_service_no_menu_entry_and_no_table()
    {
        await using var factory = Host(Modules(), Issue("billing")); // A key without govcon.
        using var client = factory.CreateClientAs("pm@example.com");

        Assert.NotEqual("application/json", (await client.GetAsync("/api/v1/sample-widgets")).Content.Headers.ContentType?.MediaType); // The page router answers an unknown path; there is no endpoint.
        Assert.Empty(factory.Services.GetRequiredService<ModuleHost>().Modules);
        Assert.Contains(factory.Services.GetRequiredService<ModuleHost>().Results, r => r.Status == ModuleLoadStatus.NotLicensed);
        Assert.DoesNotContain(factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(), e => e.RoutePattern.RawText?.Contains("sample-widgets") == true);
        Assert.DoesNotContain(NavCatalog.Entries, e => e.Href == "sample-widgets");
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes().Where(t => t.IsInterface)), t => t.Name == "ISampleWidgetService" && factory.Services.GetService(t) is not null);

        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        if (db.Database.IsSqlite())
        {
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""select count(*) as "Value" from sqlite_master where name like '%SampleWidgets%' """).SingleAsync());
        }
    }

    [Fact]
    public async Task A_host_whose_module_assembly_is_missing_starts_without_it()
    {
        await using var factory = Host(Modules(withAssembly: false), Issue("govcon"));
        using var client = factory.CreateClientAs("pm@example.com");

        Assert.NotEqual("application/json", (await client.GetAsync("/api/v1/sample-widgets")).Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(), e => e.RoutePattern.RawText?.Contains("sample-widgets") == true);
        Assert.Contains(factory.Services.GetRequiredService<ModuleHost>().Results, r => r.Status == ModuleLoadStatus.MissingAssembly);
    }
}
