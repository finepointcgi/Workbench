using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.Security;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Security;
using Workbench.Domain.Assistant;

namespace Workbench.Application.Assistant;

/// <summary>Settings, usage and the audit log. Administrators only (<see cref="Identity.PermissionCheckingAssistantAdminService"/>).</summary>
public sealed class AssistantAdminService(
    IWorkbenchDbContextFactory dbFactory,
    AssistantGate gate,
    ICurrentUser currentUser,
    SecurityEventLog securityEvents,
    TimeProvider clock) : IAssistantAdminService
{
    public async Task<AssistantSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        ToDto(await gate.LoadSettingsAsync(cancellationToken));

    public async Task<AssistantSettingsDto> SaveSettingsAsync(SaveAssistantSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connections = AssistantConnections.Available(gate.Options);
        var connectionName = string.IsNullOrWhiteSpace(request.Connection) ? null : request.Connection.Trim();
        var connection = connectionName is null ? null : connections.FirstOrDefault(c => string.Equals(c.Name, connectionName, StringComparison.OrdinalIgnoreCase));
        if (connectionName is not null && connection is null)
        {
            throw new ArgumentException($"There is no assistant connection named '{connectionName}' on this server.", nameof(request));
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim();
        if (model is not null && (connection is null || !connection.Models.Contains(model, StringComparer.Ordinal)))
        {
            throw new ArgumentException($"Model '{model}' isn't offered by the chosen connection.", nameof(request));
        }

        if (request.MonthlyTokenLimit is <= 0 || request.MonthlyTokenLimitPerUser is <= 0 || request.DailyQuestionLimitPerUser is <= 0)
        {
            throw new ArgumentException("Limits must be positive (leave a limit empty for none).", nameof(request));
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var settings = await db.Set<AssistantSettings>().OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new AssistantSettings();
            db.Set<AssistantSettings>().Add(settings);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var me = AccessControl.Normalize(currentUser.Email);
        if (request.AcceptNotice)
        {
            settings.NoticeAcceptedBy = me;
            settings.NoticeAcceptedAtUtc = now;
            settings.NoticeVersion = AssistantNotice.Version;
        }

        if (request.IsEnabled)
        {
            if (settings.NoticeVersion != AssistantNotice.Version)
            {
                throw new ArgumentException("Read and accept the data-handling notice to turn the assistant on.", nameof(request));
            }

            if (connection is null)
            {
                throw new ArgumentException("Choose a connection to turn the assistant on.", nameof(request));
            }
        }

        var wasEnabled = settings.IsEnabled;
        var before = $"{settings.Connection}/{settings.Model}";
        settings.IsEnabled = request.IsEnabled;
        settings.Connection = connection?.Name;
        settings.Model = model;
        settings.MonthlyTokenLimit = request.MonthlyTokenLimit;
        settings.MonthlyTokenLimitPerUser = request.MonthlyTokenLimitPerUser;
        settings.DailyQuestionLimitPerUser = request.DailyQuestionLimitPerUser;
        settings.ExplainTimesheetFlags = request.ExplainTimesheetFlags;
        settings.UpdatedBy = me;
        settings.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);

        var detail = $"enabled {wasEnabled}->{settings.IsEnabled}; connection {before}->{settings.Connection}/{settings.Model}"
            + (connection is not null ? $" ({connection.Provider}{(connection.IsGovCloud ? ", GovCloud" : "")})" : "")
            + (request.AcceptNotice ? $"; notice {AssistantNotice.Version} accepted" : "");
        await securityEvents.RecordAsync(SecurityEventTypes.AssistantSettingsChanged, "assistant", SecurityOutcomes.Success, detail, cancellationToken: cancellationToken);
        return ToDto(settings);
    }

    public async Task<AssistantUsageDto> GetUsageAsync(int? year = null, int? month = null, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var y = year ?? now.Year;
        var m = month ?? now.Month;
        if (y is < 2000 or > 2100 || m is < 1 or > 12)
        {
            throw new ArgumentException("Pick a valid month.");
        }

        var start = new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<AssistantInteraction>().AsNoTracking()
            .Where(i => i.StartedAtUtc >= start && i.StartedAtUtc < end)
            .GroupBy(i => i.UserEmail)
            .Select(g => new
            {
                Email = g.Key,
                Questions = g.Count(i => i.Kind == AssistantInteractionKind.Question),
                Input = g.Sum(i => i.InputTokens),
                Output = g.Sum(i => i.OutputTokens),
                Read = g.Sum(i => i.CacheReadTokens),
                Write = g.Sum(i => i.CacheWriteTokens),
            })
            .ToListAsync(cancellationToken);
        var users = rows.Select(r => new AssistantUsageRowDto(r.Email, r.Questions, r.Input, r.Output, r.Read, r.Write))
            .OrderByDescending(r => r.Total).ThenBy(r => r.UserEmail, StringComparer.Ordinal).ToList();
        var settings = await gate.LoadSettingsAsync(cancellationToken);
        return new AssistantUsageDto(y, m, users.Sum(u => u.Total), settings?.MonthlyTokenLimit, settings?.MonthlyTokenLimitPerUser, users);
    }

    public async Task<IReadOnlyList<AssistantInteractionDto>> ListAuditAsync(AssistantAuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var take = Math.Clamp(query.Take, 1, 1_000);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = db.Set<AssistantInteraction>().AsNoTracking();
        if (query.From is { } from)
        {
            var f = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            rows = rows.Where(i => i.StartedAtUtc >= f);
        }

        if (query.To is { } to)
        {
            var t = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            rows = rows.Where(i => i.StartedAtUtc < t);
        }

        if (!string.IsNullOrWhiteSpace(query.UserEmail))
        {
            var email = AccessControl.Normalize(query.UserEmail);
            rows = rows.Where(i => i.UserEmail == email);
        }

        var interactions = await rows.OrderByDescending(i => i.StartedAtUtc).Take(take).ToListAsync(cancellationToken);
        var ids = interactions.Select(i => i.Id).ToList();
        var calls = (await db.Set<AssistantToolCall>().AsNoTracking().Where(c => ids.Contains(c.InteractionId)).ToListAsync(cancellationToken))
            .GroupBy(c => c.InteractionId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Sequence).ToList());
        return interactions.Select(i => new AssistantInteractionDto(
                i.Id, i.ConversationId, i.Kind.ToString(), i.UserEmail, i.PagePath, i.Prompt, i.Answer, i.Outcome.ToString(), i.OutcomeDetail, i.Provider, i.Model,
                new AssistantTokenUsage(i.InputTokens, i.OutputTokens, i.CacheReadTokens, i.CacheWriteTokens), i.StartedAtUtc, i.CompletedAtUtc,
                calls.GetValueOrDefault(i.Id, []).Select(c => new AssistantToolCallDto(c.Sequence, c.ToolName, c.Arguments, c.Outcome.ToString(), c.RecordsRead, c.Summary, c.CalledAtUtc)).ToList()))
            .ToList();
    }

    private AssistantSettingsDto ToDto(AssistantSettings? s) => new(
        s?.IsEnabled ?? false,
        s?.Connection,
        s?.Model,
        s?.MonthlyTokenLimit,
        s?.MonthlyTokenLimitPerUser,
        s?.DailyQuestionLimitPerUser,
        s?.ExplainTimesheetFlags ?? true,
        s?.NoticeAcceptedBy,
        s?.NoticeAcceptedAtUtc,
        s?.NoticeVersion == AssistantNotice.Version,
        s?.UpdatedBy,
        s?.UpdatedAtUtc,
        AssistantConnections.Available(gate.Options));
}
