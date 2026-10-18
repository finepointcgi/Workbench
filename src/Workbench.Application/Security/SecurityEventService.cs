using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Security;

namespace Workbench.Application.Security;

public sealed class SecurityEventService(IWorkbenchDbContextFactory dbFactory, AccessControl access, SecurityEventLog securityLog, TimeProvider clock)
    : ISecurityEventService
{
    public async Task<IReadOnlyList<SecurityEventDto>> SearchAsync(SecurityEventQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        return await QueryAsync(query, Math.Clamp(query.Limit, 1, 5000), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListEventTypesAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var types = await db.Set<SecurityEvent>().AsNoTracking().Select(e => e.EventType).Distinct().ToListAsync(cancellationToken);
        return types.Order(StringComparer.Ordinal).ToList();
    }

    public async Task<SecurityEventExport> ExportCsvAsync(SecurityEventQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        var rows = await QueryAsync(query, 100_000, cancellationToken);
        var csv = new StringBuilder("OccurredAtUtc,EventType,Outcome,Actor,Target,IpAddress,UserAgent,Detail\r\n");
        foreach (var e in rows)
        {
            csv.AppendJoin(',',
                    e.OccurredAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                    Csv(e.EventType), Csv(e.Outcome), Csv(e.Actor), Csv(e.Target), Csv(e.IpAddress), Csv(e.UserAgent), Csv(e.Detail))
                .Append("\r\n");
        }

        await securityLog.RecordAsync(SecurityEventTypes.ExportSecurityEvents, detail: $"{rows.Count} events", cancellationToken: cancellationToken);
        var stamp = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return new SecurityEventExport($"security-events-{stamp}.csv", "text/csv", csv.ToString());
    }

    private async Task<IReadOnlyList<SecurityEventDto>> QueryAsync(SecurityEventQuery query, int limit, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var events = db.Set<SecurityEvent>().AsNoTracking();
        if (query.FromUtc is { } from)
        {
            events = events.Where(e => e.OccurredAtUtc >= from);
        }

        if (query.ToUtc is { } to)
        {
            events = events.Where(e => e.OccurredAtUtc < to);
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            events = events.Where(e => e.EventType == query.EventType);
        }

        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            events = events.Where(e => e.Outcome == query.Outcome);
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            var actor = query.Actor.Trim().ToLowerInvariant();
            events = events.Where(e => e.Actor.Contains(actor));
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var text = query.Text.Trim();
            events = events.Where(e => (e.Target != null && e.Target.Contains(text)) || (e.Detail != null && e.Detail.Contains(text)) || (e.IpAddress != null && e.IpAddress.Contains(text)));
        }

        var rows = await events.OrderByDescending(e => e.OccurredAtUtc).Take(limit).ToListAsync(cancellationToken);
        return rows.Select(e => new SecurityEventDto(e.Id, e.OccurredAtUtc, e.EventType, e.Actor, e.Target, e.Outcome, e.IpAddress, e.UserAgent, e.Detail)).ToList();
    }

    /// <summary>Quotes a CSV field and defuses spreadsheet formulas (CSV injection) in logged, user-controlled text.</summary>
    internal static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
