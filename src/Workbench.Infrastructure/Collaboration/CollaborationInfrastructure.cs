using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;

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
        services.AddSingleton<IBlobStore>(_ => new LocalDiskBlobStore(string.IsNullOrWhiteSpace(root) ? DefaultStorageRoot : root));
        return services;
    }
}
