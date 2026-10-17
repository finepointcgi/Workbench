using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Collaboration;

namespace Workbench.Infrastructure.Collaboration;

public static class CollaborationInfrastructure
{
    public const string DefaultStorageRoot = "App_Data/blobs";

    /// <summary>Registers the local-disk blob store (<c>Storage:Root</c>) and the temporary dev user (<c>Dev:UserEmail</c>).</summary>
    public static IServiceCollection AddCollaborationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var root = configuration["Storage:Root"];
        services.AddSingleton<IBlobStore>(_ => new LocalDiskBlobStore(string.IsNullOrWhiteSpace(root) ? DefaultStorageRoot : root));
        services.TryAddSingleton<ICurrentUser, DevCurrentUser>();
        return services;
    }
}
