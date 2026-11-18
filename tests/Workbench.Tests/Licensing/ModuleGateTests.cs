using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Licensing;
using Workbench.Contracts;
using Workbench.Contracts.Licensing;
using Workbench.Domain.Licensing;
using Workbench.Tests.MyWork;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Licensing;

/// <summary>
/// Flipping a module's <c>Enforced</c> flag is the whole pricing change: every change to a service in the module is refused without the
/// license, reads are never refused, and with the flags as shipped nothing that was open before is closed.
/// </summary>
public sealed class ModuleGateTests
{
    private static readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string PublicKey = Signer.ExportSubjectPublicKeyInfoPem();
    private static readonly DateTimeOffset Today = new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Issue(params string[] modules)
    {
        var payload = Encoding.UTF8.GetBytes($$"""{"v":2,"licenseId":"0f8b1c52-6e1a-4d0e-9a3b-5c1d2e3f4a5b","licensee":"Acme","issuedAt":"2026-10-01","expires":"2027-11-01","seats":25,"lightSeats":5,"modules":[{{string.Join(",", modules.Select(m => $"\"{m}\""))}}]}""");
        return $"WB2.{B64(payload)}.{B64(Signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}";
    }

    private static Task<Identity.SeededHost> HostAsync(IReadOnlyList<ModuleInfo> modules) => CreateAsync(s =>
    {
        s.Replace(ServiceDescriptor.Singleton(new LicenseOptions { PublicKeyPem = PublicKey, Modules = modules }));
        s.Replace(ServiceDescriptor.Singleton<TimeProvider>(new MyWorkServiceTests.FixedClock(Today)));
    });

    /// <summary>Every module enforced for its whole self except the ones named, which are left as shipped.</summary>
    private static IReadOnlyList<ModuleInfo> Flip(string module) =>
        ModuleCatalog.Default.Select(m => m.Id == module ? m with { Enforced = true, WholeModule = true } : m).ToList();

    private static IEnumerable<Type> ServicesOf(string module, IServiceProvider services) =>
        ModuleMap.ServiceInterfaces().Where(t => ModuleMap.ForService(t) == module && services.GetService(t) is not null);

    private static IEnumerable<MethodInfo> Methods(Type service) => ModuleMap.AsyncMethods(service);

    /// <summary>Calls a method with default arguments. Null when it went through (or failed for any other reason), the refusal otherwise.</summary>
    private static async Task<WorkbenchEditionException?> TryAsync(object service, MethodInfo method)
    {
        var args = method.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        try
        {
            await ((Task)method.Invoke(service, args)!).WaitAsync(TimeSpan.FromSeconds(20));
            return null;
        }
        catch (WorkbenchEditionException refused)
        {
            return refused;
        }
        catch (Exception)
        {
            return null; // The real service may complain about the empty arguments; only the license check is on trial.
        }
    }

    public static TheoryData<string> GatedModules() => new(ModuleCatalog.AllIds.Where(m => m != ModuleIds.Core));

    [Theory]
    [MemberData(nameof(GatedModules))]
    public async Task Flipping_a_modules_flag_refuses_every_change_to_its_services_and_never_a_read(string module)
    {
        await using var seeded = await HostAsync(Flip(module));
        var provider = seeded.As(Admin).Host.Get<IServiceProvider>();
        var services = ServicesOf(module, provider).ToList();
        if (module == ModuleIds.Ai)
        {
            Assert.Empty(services); // Reserved: no service belongs to it.
            return;
        }

        if (module is ModuleIds.GovCon or ModuleIds.Capture or ModuleIds.Materials or ModuleIds.ProjectControls or ModuleIds.Workforce)
        {
            // Every government contracting, capture, materials, project controls and workforce service lives in its module (private repository), and its gate is tested there. In the shipped
            // product no service maps to it: the assertion is that nothing is gated by a module this build does not contain.
            Assert.Empty(services);
            return;
        }

        Assert.NotEmpty(services);
        var changes = 0;
        var reads = 0;
        foreach (var type in services)
        {
            var service = provider.GetRequiredService(type);
            foreach (var method in Methods(type).Where(m => ModuleMap.IsChange(m.Name)))
            {
                var refused = await TryAsync(service, method);
                Assert.True(refused is not null, $"{type.Name}.{method.Name} should be refused without the {module} module.");
                Assert.Contains("module", refused!.Message);
                changes++;
            }

            // Reads are not stopped by the license (a few per service is enough: the gate only looks at the method's name).
            foreach (var method in Methods(type).Where(m => !ModuleMap.IsChange(m.Name) && m.Name.StartsWith("List", StringComparison.Ordinal)).Take(2))
            {
                Assert.Null(await TryAsync(service, method));
                reads++;
            }
        }

        Assert.True(changes > 0);
        Assert.True(reads > 0 || module is ModuleIds.GovCon or ModuleIds.ProjectControls or ModuleIds.Materials or ModuleIds.Capture or ModuleIds.Enterprise or ModuleIds.Workforce, $"No reads were tried in {module}.");
    }

    [Theory]
    [MemberData(nameof(GatedModules))]
    public async Task A_license_that_lists_the_module_lets_its_changes_through(string module)
    {
        await using var seeded = await HostAsync(Flip(module));
        var host = seeded.As(Admin).Host;
        await host.Get<ILicenseService>().ActivateAsync(new ActivateLicenseRequest(Issue(module)));
        var provider = host.Get<IServiceProvider>();

        foreach (var type in ServicesOf(module, provider))
        {
            var service = provider.GetRequiredService(type);
            foreach (var method in Methods(type).Where(m => ModuleMap.IsChange(m.Name)).Take(3))
            {
                Assert.Null(await TryAsync(service, method)); // Whatever else it says about the empty arguments, it is not the license.
            }
        }
    }

    [Fact]
    public async Task Core_is_never_gated_even_with_every_other_module_enforced()
    {
        var everything = ModuleCatalog.Default.Select(m => m with { Enforced = m.Id != ModuleIds.Core, WholeModule = true }).ToList();
        await using var seeded = await HostAsync(everything);
        var provider = seeded.As(Admin).Host.Get<IServiceProvider>();
        var core = ServicesOf(ModuleIds.Core, provider).ToList();
        var refused = new List<string>();

        foreach (var type in core)
        {
            var service = provider.GetRequiredService(type);
            foreach (var method in Methods(type).Where(m => ModuleMap.IsChange(m.Name)))
            {
                if (await TryAsync(service, method) is not null)
                {
                    refused.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        Assert.True(core.Count > 40);
        Assert.All(refused, r => Assert.Contains(r, FeatureGated)); // Only the feature gates that were always there.
    }

    /// <summary>The actions gated by feature before the module map existed. They are the only changes refused with the shipped flags.</summary>
    private static readonly HashSet<string> FeatureGated =
    [
        "ICompanyService.CreateAsync", "ISignInSettingsService.SaveProviderAsync", "IConnectorService.CreateAsync", "IConnectorService.UpdateAsync",
        "IJiraConnectionService.SaveAsync", "IGitLabConnectionService.SaveAsync", "ISlackNotificationService.ConnectAsync", "ITeamsNotificationService.ConnectAsync",
        "ICustomerShareLinkService.CreateAsync", "ISupplierPortalAdminService.AddContactAsync", "ISupplierPortalAdminService.SendSignInLinkAsync",
        "ISupplierPortalAdminService.ReactivateContactAsync", "IApprovalWorkflowService.SaveAsync", "IApprovalWorkflowService.CreateRecordWorkflowAsync",
    ];

    [Fact]
    public async Task With_the_flags_as_shipped_on_Community_nothing_is_refused_that_was_open_before()
    {
        await using var seeded = await HostAsync(ModuleCatalog.Default);
        var provider = seeded.As(Admin).Host.Get<IServiceProvider>();
        var tried = 0;
        var refusedNow = new List<string>();

        foreach (var type in ModuleMap.ServiceInterfaces().Where(t => provider.GetService(t) is not null))
        {
            var service = provider.GetRequiredService(type);
            foreach (var method in Methods(type).Where(m => ModuleMap.IsChange(m.Name)))
            {
                tried++;
                if (await TryAsync(service, method) is not null)
                {
                    refusedNow.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        Assert.True(tried > 500, $"Only {tried} changes were tried.");
        Assert.Equal(FeatureGated.Order(), refusedNow.Order()); // Exactly the feature gates, which are always on.
    }

    [Fact]
    public async Task With_every_flag_off_the_module_gates_do_nothing_at_all()
    {
        var off = ModuleCatalog.Default.Select(m => m with { Enforced = false }).ToList();
        await using var seeded = await HostAsync(off);
        var provider = seeded.As(Admin).Host.Get<IServiceProvider>();
        var refusedNow = new List<string>();

        foreach (var type in ModuleMap.ServiceInterfaces().Where(t => provider.GetService(t) is not null))
        {
            var service = provider.GetRequiredService(type);
            foreach (var method in Methods(type).Where(m => ModuleMap.IsChange(m.Name)))
            {
                if (await TryAsync(service, method) is not null)
                {
                    refusedNow.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        Assert.Equal(FeatureGated.Order(), refusedNow.Order()); // Features keep their own gates; modules add none.
    }
}
