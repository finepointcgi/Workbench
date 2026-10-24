using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Tenancy;
using Workbench.Infrastructure.Storage;

namespace Workbench.Infrastructure.Collaboration;

public static class CollaborationInfrastructure
{
    public const string DefaultStorageRoot = BlobStorageSettings.DefaultRoot;

    /// <summary>
    /// Registers the blob store chosen by <c>Storage:Provider</c>: <c>Local</c> (a folder, <c>Storage:Root</c>), <c>Gcs</c>
    /// or <c>S3</c> (<c>Storage:Bucket</c>; <see cref="BlobStorageSettings"/>). Run more than one server only with a bucket.
    /// The current user comes from the host: the web host's claims-based user, or the system user in the worker.
    /// </summary>
    public static IServiceCollection AddCollaborationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = BlobStorageSettings.FromConfiguration(configuration);
        if (settings.Problems().FirstOrDefault() is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        services.AddSingleton(settings);
        services.AddSingleton(sp => CreateUnscopedStore(settings));
        services.AddSingleton<IBlobStore>(sp => sp.GetRequiredService<TenancyOptions>().IsMultiTenant
            ? new TenantBlobStore(sp.GetRequiredService<UnscopedBlobStore>().Store)
            : sp.GetRequiredService<UnscopedBlobStore>().Store);
        return services;
    }

    /// <summary>The configured store without the tenant prefix: for <c>--copy-blobs</c> and the demo reset, which see every key.</summary>
    public static UnscopedBlobStore CreateUnscopedStore(BlobStorageSettings settings) => new(settings.ProviderKind switch
    {
        BlobStorageProvider.Gcs => GcsBlobStore.Create(settings),
        BlobStorageProvider.S3 => S3BlobStore.Create(settings),
        _ => new LocalDiskBlobStore(settings.Root),
    });
}

/// <summary>The configured blob store before tenant scoping (keys include <c>tenants/{slug}/</c> in MultiTenant mode).</summary>
public sealed record UnscopedBlobStore(IBlobStore Store);

/// <summary>MultiTenant: each tenant's blobs live under <c>tenants/{slug}/</c>, and nothing is stored without a tenant.</summary>
internal sealed class TenantBlobStore(IBlobStore inner) : IBlobStore
{
    public Task<long> SaveAsync(string key, Stream content, CancellationToken cancellationToken = default) => inner.SaveAsync(Qualify(key), content, cancellationToken);

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) => inner.OpenReadAsync(Qualify(key), cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(Qualify(key), cancellationToken);

    private static string Qualify(string key) =>
        TenantScope.Current is { } tenant ? $"tenants/{tenant.Slug}/{key}" : throw new TenantNotResolvedException();
}
