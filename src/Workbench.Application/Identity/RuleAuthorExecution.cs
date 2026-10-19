using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Workbench.Application.Identity;

/// <summary>
/// Marks a DI scope as the hourly date-trigger job acting for one rule's author: <see cref="AccessControl"/> answers
/// for <see cref="Email"/>, so the author's own workspace roles apply (Editor to change items) and nothing more.
/// <para>
/// It is not the "Workbench System" context (<see cref="SystemExecutionContext"/>) and grants nothing of its own: an
/// author who has lost access is refused like they would be in the app. Only <see cref="RuleAuthorExecution.CreateScope"/>
/// (internal to this assembly) can switch it on, and it refuses to inside an HTTP request.
/// </para>
/// </summary>
public sealed class RuleAuthorExecutionContext
{
    /// <summary>The author's email (lower-case), or null when the scope isn't acting for anyone.</summary>
    public string? Email { get; private set; }

    public bool IsActive => Email is not null;

    internal void Activate(string email) => Email = email;
}

/// <summary>Creates the scopes date rules run in, one per rule author.</summary>
internal static class RuleAuthorExecution
{
    /// <summary>True when <paramref name="email"/> can be acted for (it looks like a person's email).</summary>
    public static bool CanActFor(string? email) => AccessControl.Normalize(email) is { Length: > 2 } normalized && normalized.Contains('@');

    /// <summary>
    /// A new scope whose permission checks run as <paramref name="authorEmail"/>. Throws when called while an HTTP request
    /// is being handled, or for something that isn't a person's email.
    /// </summary>
    public static AsyncServiceScope CreateScope(IServiceScopeFactory scopes, string authorEmail)
    {
        if (!CanActFor(authorEmail))
        {
            throw new ArgumentException("A rule can only act for a person's email.", nameof(authorEmail));
        }

        var scope = scopes.CreateAsyncScope();
        try
        {
            if (scope.ServiceProvider.GetService<IHttpContextAccessor>()?.HttpContext is not null)
            {
                throw new InvalidOperationException("Acting for a rule's author is for background jobs, not HTTP requests.");
            }

            scope.ServiceProvider.GetRequiredService<RuleAuthorExecutionContext>().Activate(AccessControl.Normalize(authorEmail));
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}
