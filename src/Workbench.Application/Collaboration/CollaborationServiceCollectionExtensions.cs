using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Collaboration;

public static class CollaborationServiceCollectionExtensions
{
    /// <summary>
    /// Registers comments, attachments, activity and in-process live updates, and decorates the item and
    /// column services so their changes are recorded. Call it after <see cref="IItemService"/> and
    /// <see cref="IColumnService"/> are registered. Needs an <see cref="IBlobStore"/> and <see cref="ICurrentUser"/>
    /// from the infrastructure layer.
    /// </summary>
    public static IServiceCollection AddWorkbenchCollaboration(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SheetEventHub>();
        services.AddSingleton<ISheetLiveUpdates>(sp => sp.GetRequiredService<SheetEventHub>());
        services.AddSingleton<ISheetChangePublisher>(sp => sp.GetRequiredService<SheetEventHub>());
        services.AddScoped<ActivityRecorder>();
        services.AddScoped<ICommentService, CommentService>();
        services.AddScoped<IAttachmentService, AttachmentService>();
        services.AddScoped<IActivityService, ActivityService>();
        services.Decorate<IItemService, ActivityRecordingItemService>();
        services.Decorate<IColumnService, ActivityRecordingColumnService>();
        return services;
    }

    /// <summary>
    /// Wraps the last registration of <typeparamref name="TService"/> in <typeparamref name="TDecorator"/>, which takes
    /// the original as a constructor parameter. Keeps the original lifetime.
    /// </summary>
    public static IServiceCollection Decorate<TService, TDecorator>(this IServiceCollection services)
        where TService : class
        where TDecorator : class, TService
    {
        var index = -1;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(TService) && !services[i].IsKeyedService)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            throw new InvalidOperationException($"Register {typeof(TService).Name} before decorating it.");
        }

        var original = services[index];
        services[index] = ServiceDescriptor.Describe(
            typeof(TService),
            sp => ActivatorUtilities.CreateInstance<TDecorator>(sp, CreateOriginal(sp, original)),
            original.Lifetime);
        return services;
    }

    private static object CreateOriginal(IServiceProvider sp, ServiceDescriptor original) =>
        original.ImplementationInstance
        ?? original.ImplementationFactory?.Invoke(sp)
        ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
}
