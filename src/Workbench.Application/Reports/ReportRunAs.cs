using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Collaboration;

namespace Workbench.Application.Reports;

/// <summary>
/// Runs a scheduled report as the user who scheduled it, with exactly that user's permissions (never as the Workbench
/// System). Only <see cref="ReportScheduleRunner"/> switches it on, from a background job; it refuses inside an HTTP
/// request, so no request, API call or Blazor circuit can act as someone else.
/// </summary>
public static class ReportRunAs
{
    private static readonly AsyncLocal<Identity?> Current = new();

    internal sealed record Identity(string Email, string DisplayName);

    internal static Identity? Active => Current.Value;

    /// <summary>Acts as <paramref name="email"/> for the rest of this async flow until disposed.</summary>
    internal static IDisposable Enter(IServiceProvider services, string email, string displayName)
    {
        if (services.GetService<IHttpContextAccessor>()?.HttpContext is not null)
        {
            throw new InvalidOperationException("Reports run as their owner only in background jobs, not HTTP requests.");
        }

        var previous = Current.Value;
        Current.Value = new Identity(email, displayName);
        return new Restore(previous);
    }

    private sealed class Restore(Identity? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>
/// Reports the scheduled report's owner as the current user while <see cref="ReportRunAs"/> is active. Registered by
/// <see cref="ReportsRegistration.AddWorkbenchReportsHosting"/> after the host's own <see cref="ICurrentUser"/>.
/// </summary>
public sealed class RunAsAwareCurrentUser(ICurrentUser inner) : ICurrentUser
{
    public string Email => ReportRunAs.Active?.Email ?? inner.Email;

    public string DisplayName => ReportRunAs.Active?.DisplayName ?? inner.DisplayName;

    public bool IsAuthenticated => ReportRunAs.Active is not null || inner.IsAuthenticated;
}
