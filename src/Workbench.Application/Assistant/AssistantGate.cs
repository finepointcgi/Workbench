using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Contracts.Assistant;
using Workbench.Domain.Assistant;

namespace Workbench.Application.Assistant;

/// <summary>The connection a question will use: its configured options (never shown), model and label.</summary>
public sealed record AssistantRoute(string ConnectionName, AssistantConnectionOptions Options, string Model, string ProviderLabel)
{
    public string ProviderName => Options.Provider.ToString();
}

/// <summary>
/// Decides whether the assistant may answer right now for the signed-in user: on for the organization, notice accepted,
/// a usable connection, and within the organization's and the person's limits. Usage is metered from the audit log.
/// </summary>
public sealed class AssistantGate(IWorkbenchDbContextFactory dbFactory, IOptions<AssistantOptions> options, TimeProvider clock)
{
    public AssistantOptions Options => options.Value;

    public async Task<AssistantSettings?> LoadSettingsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<AssistantSettings>().AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>The configured connection the settings point at, or null when it isn't available on this server.</summary>
    public AssistantRoute? Route(AssistantSettings? settings)
    {
        if (settings?.Connection is not { Length: > 0 } name)
        {
            return null;
        }

        AssistantConnectionOptions connection;
        if (string.Equals(name, AssistantOptions.FakeConnection, StringComparison.OrdinalIgnoreCase))
        {
            if (!Options.EnableFakeProvider)
            {
                return null;
            }

            connection = new AssistantConnectionOptions { Provider = AssistantProviderKind.Fake };
        }
        else if (!Options.Connections.TryGetValue(name, out var configured) || AssistantConnections.Validate(name, configured).Count > 0)
        {
            return null;
        }
        else
        {
            connection = configured;
        }

        var model = !string.IsNullOrWhiteSpace(settings.Model) && connection.AllowedModels.Contains(settings.Model.Trim(), StringComparer.Ordinal)
            ? settings.Model.Trim()
            : connection.EffectiveDefaultModel;
        var label = connection.Provider == AssistantProviderKind.Fake ? AssistantConnections.Fake.Label : AssistantConnections.Describe(name, connection).Label;
        return new AssistantRoute(name, connection, model, label);
    }

    /// <summary>Why the user can't ask right now (null when they can), with the outcome to record.</summary>
    public async Task<(string Reason, AssistantOutcome Outcome)?> CheckAsync(AssistantSettings? settings, string userEmail, AssistantInteractionKind kind, CancellationToken ct)
    {
        if (settings is null || !settings.IsEnabled)
        {
            return ("The assistant is turned off for your organization. An administrator can turn it on in Admin › Assistant.", AssistantOutcome.Disabled);
        }

        if (settings.NoticeVersion != AssistantNotice.Version)
        {
            return ("An administrator needs to accept the updated data-handling notice in Admin › Assistant.", AssistantOutcome.Disabled);
        }

        if (Route(settings) is null)
        {
            return ("The assistant's connection isn't configured on this server. Ask an administrator to check Admin › Assistant.", AssistantOutcome.Disabled);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var db = await dbFactory.CreateAsync(ct);
        var month = db.Set<AssistantInteraction>().AsNoTracking().Where(i => i.StartedAtUtc >= monthStart);
        if (settings.MonthlyTokenLimit is { } orgLimit && await SumAsync(month, ct) >= orgLimit)
        {
            return ("Your organization has used this month's assistant allowance. It resets on the 1st; an administrator can raise it.", AssistantOutcome.LimitReached);
        }

        if (settings.MonthlyTokenLimitPerUser is { } userLimit && await SumAsync(month.Where(i => i.UserEmail == userEmail), ct) >= userLimit)
        {
            return ("You've used your assistant allowance for this month. It resets on the 1st; an administrator can raise it.", AssistantOutcome.LimitReached);
        }

        if (kind == AssistantInteractionKind.Question && settings.DailyQuestionLimitPerUser is { } daily)
        {
            var dayStart = now.Date;
            var asked = await db.Set<AssistantInteraction>().AsNoTracking()
                .CountAsync(i => i.UserEmail == userEmail && i.Kind == AssistantInteractionKind.Question && i.StartedAtUtc >= dayStart
                    && i.Outcome != AssistantOutcome.Disabled && i.Outcome != AssistantOutcome.LimitReached, ct);
            if (asked >= daily)
            {
                return ($"You've asked {daily} questions today, the daily limit. Try again tomorrow.", AssistantOutcome.LimitReached);
            }
        }

        return null;
    }

    private static Task<long> SumAsync(IQueryable<AssistantInteraction> rows, CancellationToken ct) =>
        rows.SumAsync(i => i.InputTokens + i.OutputTokens + i.CacheReadTokens + i.CacheWriteTokens, ct);
}
