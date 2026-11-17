using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Companies;
using Workbench.Contracts.Identity;
using Workbench.Contracts.IncurredCost;
using Workbench.Contracts.Licensing;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Integrations.GitLab;
using Workbench.Contracts.Integrations.Jira;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Security;
using Workbench.Domain.Licensing;

namespace Workbench.Application.Licensing;

/// <summary>
/// Which actions need the Business edition. Only actions that add or change something are listed; reading what is already
/// there is never gated, so a lapsed license can't lock anyone out of their own records. To gate another action, add a line.
/// </summary>
public static class EditionGates
{
    public static IServiceCollection AddWorkbenchEditionGates(this IServiceCollection services)
    {
        services.AddWorkbenchLicensing();
        services.AddScoped<ModuleSeatService>();
        services.Decorate<Workbench.Contracts.Licensing.ILicenseService, Identity.PermissionCheckingLicenseService>();
        services.Decorate<Workbench.Contracts.Licensing.ILicenseService, ModuleSeatLicenseService>(); // Seats used and allowed per module in the status.
        services.Gate<ICompanyService>(Feature.MultiCompany, nameof(ICompanyService.CreateAsync)); // A first company always exists, so any new one is a second.
        services.Gate<ISignInSettingsService>(Feature.SingleSignOn, nameof(ISignInSettingsService.SaveProviderAsync));
        services.Gate<IConnectorService>(Feature.Integrations, nameof(IConnectorService.CreateAsync), nameof(IConnectorService.UpdateAsync));
        services.Gate<IJiraConnectionService>(Feature.Integrations, nameof(IJiraConnectionService.SaveAsync));
        services.Gate<IGitLabConnectionService>(Feature.Integrations, nameof(IGitLabConnectionService.SaveAsync));
        services.Gate<ISlackNotificationService>(Feature.Integrations, nameof(IChatNotificationService.ConnectAsync));
        services.Gate<ITeamsNotificationService>(Feature.Integrations, nameof(IChatNotificationService.ConnectAsync));
        services.Gate<ICustomerShareLinkService>(Feature.CustomerLinks, nameof(ICustomerShareLinkService.CreateAsync));
        services.Gate<ISupplierPortalAdminService>(Feature.SupplierPortal,
            nameof(ISupplierPortalAdminService.AddContactAsync), nameof(ISupplierPortalAdminService.SendSignInLinkAsync), nameof(ISupplierPortalAdminService.ReactivateContactAsync));
        services.Gate<IApprovalWorkflowService>(Feature.ApprovalWorkflows,
            nameof(IApprovalWorkflowService.SaveAsync), nameof(IApprovalWorkflowService.CreateRecordWorkflowAsync));
        services.Gate<IIncurredCostService>(Feature.IncurredCost, nameof(IIncurredCostService.CreateAsync), nameof(IIncurredCostService.RegenerateAsync));
        services.AddWorkbenchModuleGates();
        return services;
    }

    /// <summary>
    /// Puts a module gate on every change a service interface offers (every method that isn't a read, see
    /// <see cref="ModuleMap.IsChange"/>), for every service interface whose module in <see cref="ModuleMap"/> isn't core. Reads are
    /// never gated. While a module isn't enforced, or the license includes it, the gate does nothing, so flipping the module's
    /// <c>Enforced</c> flag is the only change needed. Interfaces nothing registered are skipped.
    /// </summary>
    public static IServiceCollection AddWorkbenchModuleGates(this IServiceCollection services)
    {
        foreach (var service in ModuleMap.ServiceInterfaces())
        {
            if (ModuleMap.ForService(service) is { } module && module != ModuleKeys.Core)
            {
                typeof(EditionGates).GetMethod(nameof(GateModule), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(service).Invoke(null, [services, module]);
            }
        }

        return services;
    }

    private static void GateModule<T>(IServiceCollection services, string module)
        where T : class
    {
        var changes = ModuleMap.AsyncMethods(typeof(T)).Where(m => ModuleMap.IsChange(m.Name)).Select(m => m.Name).ToHashSet();
        if (changes.Count > 0)
        {
            Wrap<T>(services, required: false, (proxy, inner, sp) => proxy.Setup(inner, sp.GetRequiredService<FeatureGate>(), module, changes));
        }
    }


    /// <summary>Wraps the last registration of <typeparamref name="T"/> so the named methods first require <paramref name="feature"/>.</summary>
    private static void Gate<T>(this IServiceCollection services, Feature feature, params string[] methods)
        where T : class
    {
        var gated = methods.ToHashSet();
        foreach (var name in gated.Where(n => typeof(T).GetMethod(n) is null && !typeof(T).GetInterfaces().Any(i => i.GetMethod(n) is not null)))
        {
            throw new InvalidOperationException($"{typeof(T).Name} has no method {name} to gate.");
        }

        Wrap<T>(services, required: true, (proxy, inner, sp) => proxy.Setup(inner, sp.GetRequiredService<FeatureGate>(), feature, gated));
    }

    /// <summary>Replaces the last registration of <typeparamref name="T"/> with a proxy over it. Not registered: an error when <paramref name="required"/>, otherwise skipped.</summary>
    private static void Wrap<T>(IServiceCollection services, bool required, Action<GateProxy<T>, T, IServiceProvider> setup)
        where T : class
    {
        var index = services.ToList().FindLastIndex(d => d.ServiceType == typeof(T) && !d.IsKeyedService);
        if (index < 0)
        {
            if (required)
            {
                throw new InvalidOperationException($"Register {typeof(T).Name} before gating it.");
            }

            return;
        }

        var original = services[index];
        services[index] = ServiceDescriptor.Describe(
            typeof(T),
            sp =>
            {
                var inner = original.ImplementationInstance
                    ?? original.ImplementationFactory?.Invoke(sp)
                    ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
                var proxy = DispatchProxy.Create<T, GateProxy<T>>();
                setup((GateProxy<T>)(object)proxy, (T)inner, sp);
                return proxy;
            },
            original.Lifetime);
    }

}

/// <summary>Forwards every call to the real service, first checking the feature for the listed methods.</summary>
public class GateProxy<T> : DispatchProxy
    where T : class
{
    private T _inner = null!;
    private FeatureGate _gate = null!;
    private Feature? _feature;
    private string? _module;
    private HashSet<string> _methods = [];

    internal void Setup(T inner, FeatureGate gate, Feature feature, HashSet<string> methods) =>
        (_inner, _gate, _feature, _methods) = (inner, gate, feature, methods);

    /// <summary>Gates the named methods on a whole module instead of one feature.</summary>
    internal void Setup(T inner, FeatureGate gate, string module, HashSet<string> methods) =>
        (_inner, _gate, _module, _methods) = (inner, gate, module, methods);

    private Task RequireAsync(CancellationToken ct) =>
        _module is not null ? _gate.RequireModuleAsync(_module, ct) : _gate.RequireAsync(_feature!.Value, ct);

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method is null)
        {
            return null;
        }

        if (!_methods.Contains(method.Name))
        {
            return Forward(method, args);
        }

        var returnType = method.ReturnType;
        if (returnType == typeof(Task))
        {
            return RunAsync(method, args);
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return typeof(GateProxy<T>).GetMethod(nameof(RunAsyncOf), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(this, [method, args]);
        }

        throw new InvalidOperationException($"{typeof(T).Name}.{method.Name} can't be gated: it isn't asynchronous.");
    }

    private static CancellationToken TokenIn(object?[]? args) => args?.OfType<CancellationToken>().FirstOrDefault() ?? CancellationToken.None;

    private object? Forward(MethodInfo method, object?[]? args)
    {
        try
        {
            return method.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private async Task RunAsync(MethodInfo method, object?[]? args)
    {
        await RequireAsync(TokenIn(args));
        await (Task)Forward(method, args)!;
    }

    private async Task<TResult> RunAsyncOf<TResult>(MethodInfo method, object?[]? args)
    {
        await RequireAsync(TokenIn(args));
        return await (Task<TResult>)Forward(method, args)!;
    }
}
