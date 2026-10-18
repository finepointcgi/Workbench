using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.ProjectAccounting;

/// <summary>Writes append-only <see cref="AuditEvent"/> rows in the caller's unit of work.</summary>
public sealed class AuditRecorder(ICurrentUser currentUser, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Actor => currentUser.Email;

    /// <summary>Adds an audit row to <paramref name="db"/>; it's saved with the caller's SaveChanges.</summary>
    public void Record(IWorkbenchDbContext db, string entityType, Guid entityId, string action, object? before = null, object? after = null, string? reason = null) =>
        db.Set<AuditEvent>().Add(new AuditEvent
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before, Json),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after, Json),
            Actor = currentUser.Email,
            OccurredAtUtc = clock.GetUtcNow().UtcDateTime,
            Reason = reason,
        });
}

public sealed class AuditLogService(IWorkbenchDbContextFactory dbFactory) : IAuditLogService
{
    public async Task<IReadOnlyList<AuditEventDto>> ListAsync(string? entityType = null, Guid? entityId = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<AuditEvent>().AsNoTracking();
        if (entityType is not null)
        {
            query = query.Where(a => a.EntityType == entityType);
        }

        if (entityId is { } id)
        {
            query = query.Where(a => a.EntityId == id);
        }

        var rows = await query.OrderByDescending(a => a.OccurredAtUtc).Take(Math.Clamp(limit, 1, 1000)).ToListAsync(cancellationToken);
        return rows.Select(a => new AuditEventDto(a.Id, a.EntityType, a.EntityId, a.Action, a.BeforeJson, a.AfterJson, a.Actor, a.OccurredAtUtc, a.Reason)).ToList();
    }
}
