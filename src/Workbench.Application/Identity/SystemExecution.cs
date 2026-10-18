using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Collaboration;

namespace Workbench.Application.Identity;

/// <summary>
/// Marks a DI scope as a trusted background job running as "Workbench System" (e.g. the hourly Limitation of Funds
/// check in the web host, where nobody is signed in).
/// <para>
/// It grants nothing by itself: the system actor has no organization roles and no workspace memberships, so every
/// permission check refuses it. A permission decorator accepts it only on a read method that explicitly checks
/// <see cref="AccessControl.IsSystemReader"/> (today: <c>IBurdenedCostService.GetProjectCostAsync</c>). Writes are
/// never allowed.
/// </para>
/// <para>
/// Only <see cref="SystemExecution.CreateScope"/> (internal to this assembly) can switch it on, and it refuses to inside
/// an HTTP request, so a request, API call or Blazor circuit can't obtain it.
/// </para>
/// </summary>
public sealed class SystemExecutionContext
{
    /// <summary>The actor name recorded in audit and security-event rows. Not an email, so no user can have it.</summary>
    public const string Actor = "Workbench System";

    public bool IsActive { get; private set; }

    internal void Activate() => IsActive = true;
}

/// <summary>Creates the scopes trusted background jobs run in.</summary>
internal static class SystemExecution
{
    /// <summary>
    /// A new scope whose <see cref="SystemExecutionContext"/> is active. Throws when called while an HTTP request is
    /// being handled: the system context is for hosted services only.
    /// </summary>
    public static AsyncServiceScope CreateScope(IServiceScopeFactory scopes)
    {
        var scope = scopes.CreateAsyncScope();
        try
        {
            if (scope.ServiceProvider.GetService<IHttpContextAccessor>()?.HttpContext is not null)
            {
                throw new InvalidOperationException("The Workbench System context is for background jobs, not HTTP requests.");
            }

            scope.ServiceProvider.GetRequiredService<SystemExecutionContext>().Activate();
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Reports <see cref="SystemExecutionContext.Actor"/> as the current user inside a system scope, so audit rows,
/// security events and notifications attribute background work to "Workbench System". Registered by
/// <see cref="IdentityRegistration.AddWorkbenchSystemExecutionHosting"/> after the host's real <see cref="ICurrentUser"/>.
/// </summary>
public sealed class SystemAwareCurrentUser(ICurrentUser inner, SystemExecutionContext system) : ICurrentUser
{
    public string Email => system.IsActive ? SystemExecutionContext.Actor : inner.Email;

    public string DisplayName => system.IsActive ? SystemExecutionContext.Actor : inner.DisplayName;

    public bool IsAuthenticated => system.IsActive || inner.IsAuthenticated;
}
