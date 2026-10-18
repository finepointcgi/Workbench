using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Tenancy;

namespace Workbench.Infrastructure.Tenancy;

/// <summary>
/// MultiTenant Data Protection: one key ring (in the catalog database), with every protector isolated per tenant by
/// purpose. A protector created at startup (the auth cookie's, antiforgery's, Blazor's, the vendor TIN protector)
/// resolves the tenant on each Protect/Unprotect call, so a cookie, token or encrypted value made for tenant A never
/// unprotects for tenant B. The host admin site uses its own purpose. <see cref="Root"/> is the undecorated provider,
/// for the catalog's own secrets (tenant connection strings).
/// </summary>
public sealed class TenantDataProtectionProvider(IDataProtectionProvider root) : IDataProtectionProvider
{
    public const string HostPurpose = "(host)";

    public IDataProtectionProvider Root { get; } = root;

    public IDataProtector CreateProtector(string purpose) => new TenantProtector(Root, [purpose]);

    /// <summary>Replaces the registered <see cref="IDataProtectionProvider"/> with the tenant-aware one.</summary>
    public static void Decorate(IServiceCollection services)
    {
        var descriptor = services.Last(d => d.ServiceType == typeof(IDataProtectionProvider));
        services.Remove(descriptor);
        services.AddSingleton<IDataProtectionProvider>(sp => new TenantDataProtectionProvider(Create(sp, descriptor)));
    }

    /// <summary>The provider for non-tenant secrets: the root when decorated, else the provider itself.</summary>
    public static IDataProtectionProvider RootOf(IDataProtectionProvider provider) =>
        provider is TenantDataProtectionProvider tenantAware ? tenantAware.Root : provider;

    private static IDataProtectionProvider Create(IServiceProvider sp, ServiceDescriptor descriptor) => descriptor switch
    {
        { ImplementationInstance: IDataProtectionProvider instance } => instance,
        { ImplementationFactory: { } factory } => (IDataProtectionProvider)factory(sp),
        { ImplementationType: { } type } => (IDataProtectionProvider)ActivatorUtilities.CreateInstance(sp, type),
        _ => throw new InvalidOperationException("Unexpected IDataProtectionProvider registration."),
    };

    private sealed class TenantProtector(IDataProtectionProvider root, string[] purposes) : IDataProtector
    {
        private readonly ConcurrentDictionary<string, IDataProtector> _byTenant = new(StringComparer.Ordinal);

        public IDataProtector CreateProtector(string purpose) => new TenantProtector(root, [.. purposes, purpose]);

        public byte[] Protect(byte[] plaintext) => Current().Protect(plaintext);

        public byte[] Unprotect(byte[] protectedData) => Current().Unprotect(protectedData);

        private IDataProtector Current()
        {
            var tenant = TenantScope.Current is { } t ? $"tenant:{t.Id:N}" : HostPurpose;
            return _byTenant.GetOrAdd(tenant, key => root.CreateProtector("Workbench.Tenant", [key, .. purposes]));
        }
    }
}
