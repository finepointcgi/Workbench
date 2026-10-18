using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Tenancy;

namespace Workbench.Infrastructure.Collaboration;

public static class CollaborationInfrastructure
{
    public const string DefaultStorageRoot = "App_Data/blobs";

    /// <summary>
    /// Registers the local-disk blob store (<c>Storage:Root</c>). The current user comes from the host: the web host's
    /// claims-based user, or the system user in the worker.
    /// </summary>
    public static IServiceCollection AddCollaborationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var root = configuration["Storage:Root"];
        root = string.IsNullOrWhiteSpace(root) ? DefaultStorageRoot : root;
        services.AddSingleton<IBlobStore>(sp => sp.GetRequiredService<TenancyOptions>().IsMultiTenant
            ? new TenantBlobStore(new LocalDiskBlobStore(root))
            : new LocalDiskBlobStore(root));
        return services;
    }
}

/// <summary>MultiTenant: each tenant's blobs live under <c>tenants/{slug}/</c>, and nothing is stored without a tenant.</summary>
internal sealed class TenantBlobStore(IBlobStore inner) : IBlobStore
{
    public Task<long> SaveAsync(string key, Stream content, CancellationToken cancellationToken = default) => inner.SaveAsync(Qualify(key), content, cancellationToken);

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) => inner.OpenReadAsync(Qualify(key), cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(Qualify(key), cancellationToken);

    private static string Qualify(string key) =>
        TenantScope.Current is { } tenant ? $"tenants/{tenant.Slug}/{key}" : throw new TenantNotResolvedException();
}
