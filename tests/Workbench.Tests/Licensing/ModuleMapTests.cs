using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Licensing;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Licensing;
using Workbench.Domain.Licensing;
using Workbench.Tests.Identity;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Licensing;

/// <summary>
/// <see cref="ModuleMap"/> is the one table that says which license module an API path, menu entry, permission and service belongs to.
/// These tests fail when something belongs to none, so a new feature can't be added unlicensed by accident.
/// </summary>
public sealed class ModuleMapTests
{
    private static readonly HashSet<string> Known = ModuleCatalog.AllIds.ToHashSet();

    [Fact]
    public void The_module_ids_in_Contracts_and_Domain_agree()
    {
        string[] Consts(Type t) => t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).Order().ToArray();

        Assert.Equal(Consts(typeof(ModuleIds)), Consts(typeof(ModuleKeys)));
        Assert.Equal(ModuleCatalog.AllIds.Order(), Consts(typeof(ModuleKeys)));
    }

    [Fact]
    public void Every_menu_entry_belongs_to_a_module_and_no_rule_is_stale()
    {
        var unmapped = NavCatalog.Entries.Where(e => ModuleMap.ForNav(e.Section, e.Group, e.Href) is null).Select(e => $"{e.Section}/{e.Group}/{e.Href}").ToList();
        Assert.True(unmapped.Count == 0, $"Add these menu entries (or their group or section) to ModuleMap: {string.Join(", ", unmapped)}");
        Assert.All(NavCatalog.Entries, e => Assert.Contains(e.ModuleId, Known));

        // A rule for a page, group or section that no longer exists would silently stop meaning anything.
        var hrefs = NavCatalog.Entries.Select(e => e.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Empty(ModuleMap.NavPages.Keys.Where(h => !hrefs.Contains(h)));
        var groups = NavCatalog.Entries.Where(e => e.Group is not null).Select(e => $"{e.Section}/{e.Group}").ToHashSet();
        Assert.Empty(ModuleMap.NavGroups.Keys.Where(g => !groups.Contains(g)));
        Assert.Empty(ModuleMap.NavSections.Keys.Where(s => NavCatalog.Sections.All(x => x.Key != s)));
        Assert.All(ModuleMap.NavPages.Values.Concat(ModuleMap.NavGroups.Values).Concat(ModuleMap.NavSections.Values), m => Assert.Contains(m, Known));
    }

    [Fact]
    public void Every_permission_belongs_to_a_module()
    {
        var unmapped = PermissionCatalog.All.Where(p => ModuleMap.ForPermission(p.Key) is null).Select(p => p.Key).ToList();
        Assert.True(unmapped.Count == 0, $"Add a rule for these permissions to ModuleMap.PermissionPrefixes: {string.Join(", ", unmapped)}");
        Assert.All(ModuleMap.PermissionPrefixes.Values, m => Assert.Contains(m, Known));
        Assert.All(ModuleMap.PermissionPrefixes.Keys, prefix => Assert.Contains(PermissionCatalog.All, p => p.Key.StartsWith(prefix, StringComparison.Ordinal))); // No rule for nothing.
    }

    [Fact]
    public void Every_service_interface_belongs_to_a_module()
    {
        var services = ModuleMap.ServiceInterfaces();
        Assert.True(services.Count > 150, "The scan should find the contracts' services.");
        var unmapped = services.Where(s => ModuleMap.ForService(s) is null).Select(s => s.FullName).ToList();
        Assert.True(unmapped.Count == 0, $"Add these service interfaces (or their namespace) to ModuleMap: {string.Join(", ", unmapped)}");
        Assert.All(ModuleMap.ServiceTypes.Values.Concat(ModuleMap.ServiceNamespaces.Values), m => Assert.Contains(m, Known));
        Assert.All(ModuleMap.ServiceTypes.Keys, t => Assert.Contains(t, services)); // A listed type that isn't a service is a mistake.

        var namespaces = typeof(ModuleMap).Assembly.GetExportedTypes().Select(t => t.Namespace ?? "").ToHashSet();
        Assert.Empty(ModuleMap.ServiceNamespaces.Keys.Where(ns => !namespaces.Contains("Workbench.Contracts." + ns)));
    }

    [Fact]
    public async Task Every_api_path_belongs_to_a_module()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        _ = factory.Server;
        var segments = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => "/" + (e.RoutePattern.RawText ?? "").TrimStart('/'))
            .Select(ApiKeyScopes.FirstSegment)
            .Where(s => s is not null && !s!.StartsWith('{'))
            .Distinct()
            .ToList();

        Assert.NotEmpty(segments);
        var unmapped = segments.Where(s => ModuleMap.ForApiSegment(s!) is null).ToList();
        Assert.True(unmapped.Count == 0, $"Add these API paths to ModuleMap.ApiSegments: {string.Join(", ", unmapped)}");
        Assert.All(ModuleMap.ApiSegments.Values, m => Assert.Contains(m, Known));

        // Every path an API key can be scoped to is in the table too, so a new key area can't skip the decision.
        Assert.All(ApiKeyScopes.Areas.SelectMany(a => a.PathSegments), s => Assert.NotNull(ModuleMap.ForApiSegment(s)));
    }

    [Theory]
    [InlineData("GetAsync", false)]
    [InlineData("ListInvoicesAsync", false)]
    [InlineData("ExportAsync", false)]
    [InlineData("PreviewAsync", false)]
    [InlineData("IsClosedAsync", false)]
    [InlineData("CreateAsync", true)]
    [InlineData("SaveAsync", true)]
    [InlineData("PostAsync", true)]
    [InlineData("Getaway", true)] // Not the verb Get: a word that merely starts with it.
    public void Reads_are_told_from_changes_by_the_verb_the_method_starts_with(string method, bool change) => Assert.Equal(change, ModuleMap.IsChange(method));

    [Fact]
    public void The_catalog_has_the_agreed_modules_and_only_govcon_and_enterprise_are_enforced_and_only_for_their_features()
    {
        Assert.Equal(["core", "billing", "accounting", "capture", "workforce", "projectcontrols", "materials", "govcon", "enterprise", "ai"], ModuleCatalog.AllIds);
        Assert.Equal(["enterprise", "govcon"], ModuleCatalog.Default.Where(m => m.Enforced).Select(m => m.Id).Order().ToArray());
        Assert.All(ModuleCatalog.Default.Where(m => m.Enforced), m => Assert.False(m.WholeModule));
        Assert.DoesNotContain(ModuleCatalog.Default, m => m.Id is "finance" or "controls");
        Assert.False(ModuleCatalog.Default.Single(m => m.Id == ModuleIds.Ai).Enforced); // Reserved for hosted credits; nothing is built.
        Assert.Equal(ModuleIds.Accounting, FeatureCatalog.ModuleOf(Feature.SupplierPortal)); // Now accounting, and still unenforced.
    }
}

/// <summary>Keys issued before the catalog was split keep working: legacy module ids map at parse time.</summary>
public sealed class LegacyModuleIdTests
{
    private static readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string PublicKey = Signer.ExportSubjectPublicKeyInfoPem();

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static LicenseClaims Parse(string payloadJson)
    {
        var payload = Encoding.UTF8.GetBytes(payloadJson);
        var key = $"WB2.{B64(payload)}.{B64(Signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}";
        Assert.True(LicenseKeys.TryVerify(key, PublicKey, out var claims, out var problem), problem);
        return claims!;
    }

    private static string Payload(string modules, string? extra = null) =>
        $$"""{"v":2,"licenseId":"0f8b1c52-6e1a-4d0e-9a3b-5c1d2e3f4a5b","licensee":"Old Key Inc","issuedAt":"2026-10-01","expires":"2027-10-01","seats":25,"lightSeats":3,"modules":[{{modules}}]{{extra}},"trial":false}""";

    [Fact]
    public void Finance_in_an_existing_key_means_billing_and_accounting()
    {
        var claims = Parse(Payload("\"finance\",\"capture\""));

        Assert.Equal(["accounting", "billing", "capture", "core"], claims.Modules!.Order().ToArray());
        Assert.True(claims.IncludesModule(ModuleIds.Billing) && claims.IncludesModule(ModuleIds.Accounting));
        Assert.DoesNotContain("finance", claims.Modules!);
    }

    [Fact]
    public void Controls_in_an_existing_key_means_enterprise()
    {
        var claims = Parse(Payload("\"controls\""));

        Assert.Equal(["core", "enterprise"], claims.Modules!.Order().ToArray());
    }

    [Fact]
    public void Legacy_and_current_ids_together_are_one_set_and_unknown_ids_are_still_ignored()
    {
        var claims = Parse(Payload("\"finance\",\"billing\",\"controls\",\"enterprise\",\"made-up-later\",\"FINANCE\""));

        Assert.Equal(["accounting", "billing", "core", "enterprise"], claims.Modules!.Order().ToArray());
    }

    [Fact]
    public void A_legacy_key_with_the_whole_old_list_gets_exactly_what_the_old_modules_meant()
    {
        var claims = Parse(Payload("\"finance\",\"capture\",\"workforce\",\"controls\",\"govcon\",\"enterprise\""));

        Assert.Equal(["accounting", "billing", "capture", "core", "enterprise", "govcon", "workforce"], claims.Modules!.Order().ToArray());
        Assert.False(claims.IncludesModule(ModuleIds.Materials)); // New modules are not granted by an old key...
        Assert.False(claims.IncludesModule(ModuleIds.ProjectControls)); // ...though none of them is enforced, so nobody loses anything.
    }

    [Fact]
    public void A_key_without_moduleSeats_uses_seats_for_every_module()
    {
        var claims = Parse(Payload("\"finance\""));

        Assert.Null(claims.ModuleSeats);
        Assert.Equal((25, 25), (claims.SeatsFor(ModuleIds.Billing), claims.SeatsFor(ModuleIds.Accounting)));
    }

    [Fact]
    public void ModuleSeats_gives_each_module_its_own_seats_and_the_rest_fall_back_to_seats()
    {
        var claims = Parse(Payload("\"billing\",\"accounting\",\"capture\"", ",\"moduleSeats\":{\"accounting\":4,\"billing\":0,\"nonsense\":9,\"capture\":\"many\"}"));

        Assert.Equal((4, 0, 25), (claims.SeatsFor(ModuleIds.Accounting), claims.SeatsFor(ModuleIds.Billing), claims.SeatsFor(ModuleIds.Capture)));
        Assert.DoesNotContain("nonsense", claims.ModuleSeats!.Keys); // An id this build doesn't know is ignored, a non-number too.
    }

    [Fact]
    public void Legacy_ids_in_moduleSeats_map_too_and_an_explicit_id_wins()
    {
        var claims = Parse(Payload("\"finance\",\"controls\"", ",\"moduleSeats\":{\"finance\":7,\"billing\":3,\"controls\":2}"));

        Assert.Equal((3, 7, 2), (claims.SeatsFor(ModuleIds.Billing), claims.SeatsFor(ModuleIds.Accounting), claims.SeatsFor(ModuleIds.Enterprise)));
    }

    [Fact]
    public void A_WB1_key_is_unchanged_every_module_and_no_module_seats()
    {
        var payload = Encoding.UTF8.GetBytes("""{"v":1,"licensee":"Old","edition":"business","seats":9,"expires":"2027-11-01"}""");
        var key = $"WB1.{B64(payload)}.{B64(Signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}";

        Assert.True(LicenseKeys.TryVerify(key, PublicKey, out var claims, out _));
        Assert.Null(claims!.Modules);
        Assert.All(ModuleCatalog.AllIds, id => Assert.True(claims.IncludesModule(id)));
        Assert.Equal(9, claims.SeatsFor(ModuleIds.Accounting));
    }
}
