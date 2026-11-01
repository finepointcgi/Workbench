using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Companies;
using Workbench.Contracts.Identity;
using Workbench.Contracts.IncurredCost;
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
        services.Decorate<Workbench.Contracts.Licensing.ILicenseService, Identity.PermissionCheckingLicenseService>();
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
        return services;
    }

    /// <summary>Wraps the last registration of <typeparamref name="T"/> so the named methods first require <paramref name="feature"/>.</summary>
    private static void Gate<T>(this IServiceCollection services, Feature feature, params string[] methods)
        where T : class
    {
        var index = services.ToList().FindLastIndex(d => d.ServiceType == typeof(T) && !d.IsKeyedService);
        if (index < 0)
        {
            throw new InvalidOperationException($"Register {typeof(T).Name} before gating it.");
        }

        var original = services[index];
        var gated = methods.ToHashSet();
        foreach (var name in gated.Where(n => typeof(T).GetMethod(n) is null && !typeof(T).GetInterfaces().Any(i => i.GetMethod(n) is not null)))
        {
            throw new InvalidOperationException($"{typeof(T).Name} has no method {name} to gate.");
        }

        services[index] = ServiceDescriptor.Describe(
            typeof(T),
            sp =>
            {
                var inner = original.ImplementationInstance
                    ?? original.ImplementationFactory?.Invoke(sp)
                    ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
                var proxy = DispatchProxy.Create<T, GateProxy<T>>();
                ((GateProxy<T>)(object)proxy).Setup((T)inner, sp.GetRequiredService<FeatureGate>(), feature, gated);
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
    private Feature _feature;
    private HashSet<string> _methods = [];

    internal void Setup(T inner, FeatureGate gate, Feature feature, HashSet<string> methods) =>
        (_inner, _gate, _feature, _methods) = (inner, gate, feature, methods);

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
        await _gate.RequireAsync(_feature, TokenIn(args));
        await (Task)Forward(method, args)!;
    }

    private async Task<TResult> RunAsyncOf<TResult>(MethodInfo method, object?[]? args)
    {
        await _gate.RequireAsync(_feature, TokenIn(args));
        return await (Task<TResult>)Forward(method, args)!;
    }
}
