using Microsoft.Extensions.Logging;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Security;
using Workbench.Domain.Security;

namespace Workbench.Application.Security;

/// <summary>Where a security event came from. The web host fills it from the HTTP request or the Blazor circuit.</summary>
public interface ISecurityEventContext
{
    string? IpAddress { get; }
    string? UserAgent { get; }

    /// <summary>Called after a permission denial is recorded, so the host doesn't record the 403 response again.</summary>
    void MarkDenialRecorded() { }
}

internal sealed class NoSecurityEventContext : ISecurityEventContext
{
    public string? IpAddress => null;
    public string? UserAgent => null;
}

/// <summary>
/// Writes append-only <see cref="SecurityEvent"/> rows (ISO 27001 A.8.15) in their own unit of work, so failures and
/// denials are recorded even when the caller's change is not. A logging failure is logged, never thrown: it must not
/// take the application down, but operators see it.
/// </summary>
public sealed class SecurityEventLog(
    IWorkbenchDbContextFactory dbFactory,
    ICurrentUser currentUser,
    ISecurityEventContext context,
    TimeProvider clock,
    ILogger<SecurityEventLog> logger)
{
    public async Task RecordAsync(
        string eventType,
        string? target = null,
        string outcome = SecurityOutcomes.Success,
        string? detail = null,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var row = new SecurityEvent
        {
            OccurredAtUtc = clock.GetUtcNow().UtcDateTime,
            EventType = eventType,
            Actor = Clip(actor ?? CurrentActor(), 320)!,
            Target = Clip(target, 512),
            Outcome = outcome,
            IpAddress = Clip(context.IpAddress, 64),
            UserAgent = Clip(context.UserAgent, 512),
            Detail = Clip(detail, 2000),
        };

        try
        {
            await using var db = await dbFactory.CreateAsync(CancellationToken.None);
            db.Set<SecurityEvent>().Add(row);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Event type and outcome only: the detail may name people.
            logger.LogError(ex, "Couldn't write security event {EventType} ({Outcome})", eventType, outcome);
        }

        if (eventType == SecurityEventTypes.PermissionDenied)
        {
            context.MarkDenialRecorded();
        }
    }

    private string CurrentActor()
    {
        try
        {
            return string.IsNullOrWhiteSpace(currentUser.Email) ? "anonymous"
                : currentUser.Email == Identity.SystemExecutionContext.Actor ? Identity.SystemExecutionContext.Actor
                : currentUser.Email.Trim().ToLowerInvariant();
        }
        catch (Exception)
        {
            return "anonymous";
        }
    }

    private static string? Clip(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
