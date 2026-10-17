using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Collaboration;

namespace Workbench.ApiClient;

public static class CollaborationClients
{
    /// <summary>Comments, attachments and activity over HTTP, and live updates over the server's SignalR hub.</summary>
    public static IServiceCollection AddCollaborationClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ICommentService, HttpCommentService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IAttachmentService, HttpAttachmentService>(client =>
        {
            client.BaseAddress = serverBaseAddress;
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddHttpClient<IActivityService, HttpActivityService>(client => client.BaseAddress = serverBaseAddress);
        services.AddSingleton<ISheetLiveUpdates>(_ => new SignalRSheetLiveUpdates(serverBaseAddress));
        return services;
    }
}
