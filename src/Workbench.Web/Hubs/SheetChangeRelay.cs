using Microsoft.AspNetCore.SignalR;
using Workbench.Application.Collaboration;

namespace Workbench.Web.Hubs;

/// <summary>Forwards every change from the in-process <see cref="SheetEventHub"/> to the sheet's SignalR group.</summary>
internal sealed class SheetChangeRelay(SheetEventHub events, IHubContext<SheetsHub> hub) : IHostedService
{
    private IAsyncDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = events.SubscribeAll(change =>
            hub.Clients.Group(SheetsHub.GroupFor(change.SheetId)).SendAsync(SheetsHub.ChangedMethod, change));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
        }
    }
}

public static class LiveUpdatesHosting
{
    public static IServiceCollection AddWorkbenchLiveUpdatesHub(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddHostedService<SheetChangeRelay>();
        return services;
    }

    public static IEndpointRouteBuilder MapWorkbenchLiveUpdatesHub(this IEndpointRouteBuilder app)
    {
        app.MapHub<SheetsHub>(SheetsHub.Path);
        return app;
    }
}
