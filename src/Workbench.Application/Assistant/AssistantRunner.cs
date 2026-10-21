using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Identity;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Collaboration;
using Workbench.Domain.Assistant;

namespace Workbench.Application.Assistant;

public sealed record AssistantCompletion(string Text, AssistantRoute Route, Guid InteractionId);

/// <summary>
/// The assistant audit log and single-shot completions. Every prompt, tool call (with the records it read) and answer
/// is written here as the signed-in user; rows are only ever added (an interaction is completed once, in place).
/// </summary>
public sealed class AssistantRunner(
    IWorkbenchDbContextFactory dbFactory,
    AssistantGate gate,
    IAssistantModelClientFactory models,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<AssistantRunner> logger)
{
    private static readonly Counter<long> Tokens = Operations.WorkbenchTelemetry.Meter.CreateCounter<long>(
        "workbench.assistant.tokens", "{token}", "Assistant tokens used, by provider and kind of request.");

    private static readonly Counter<long> Requests = Operations.WorkbenchTelemetry.Meter.CreateCounter<long>(
        "workbench.assistant.requests", "{request}", "Assistant requests, by kind and outcome.");

    public AssistantGate Gate => gate;

    public string UserEmail => AccessControl.Normalize(currentUser.Email);

    public IAssistantModelClient ClientFor(AssistantRoute route) => models.Create(route.ConnectionName, route.Options);

    public async Task<AssistantInteraction> StartAsync(
        AssistantInteractionKind kind, Guid conversationId, string prompt, string? pagePath, AssistantRoute? route, CancellationToken ct)
    {
        var row = new AssistantInteraction
        {
            ConversationId = conversationId,
            Kind = kind,
            UserEmail = UserEmail,
            PagePath = Clip(pagePath, 500),
            Prompt = Clip(prompt, 8_000)!,
            Outcome = AssistantOutcome.Failed,
            OutcomeDetail = "In progress",
            Provider = route?.ProviderName ?? "none",
            Model = route?.Model,
            StartedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        await using var db = await dbFactory.CreateAsync(ct);
        db.Set<AssistantInteraction>().Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task RecordToolCallAsync(Guid interactionId, int sequence, string tool, string? arguments, AssistantToolOutcome outcome, IReadOnlyList<string> records, string? summary)
    {
        await using var db = await dbFactory.CreateAsync(CancellationToken.None);
        db.Set<AssistantToolCall>().Add(new AssistantToolCall
        {
            InteractionId = interactionId,
            Sequence = sequence,
            ToolName = Clip(tool, 64)!,
            Arguments = Clip(arguments, 2_000),
            Outcome = outcome,
            RecordsRead = Clip(string.Join(' ', records.Distinct()), 4_000),
            Summary = Clip(summary, 500),
            CalledAtUtc = clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    public async Task CompleteAsync(Guid interactionId, AssistantOutcome outcome, string? answer, string? detail, AssistantTokenUsage usage, int toolCalls)
    {
        // Completed even when the caller went away, so the audit log never loses a row's outcome.
        await using var db = await dbFactory.CreateAsync(CancellationToken.None);
        var row = await db.Set<AssistantInteraction>().SingleAsync(i => i.Id == interactionId, CancellationToken.None);
        row.Outcome = outcome;
        row.Answer = Clip(answer, 32_000);
        row.OutcomeDetail = Clip(detail, 500);
        row.InputTokens = usage.InputTokens;
        row.OutputTokens = usage.OutputTokens;
        row.CacheReadTokens = usage.CacheReadTokens;
        row.CacheWriteTokens = usage.CacheWriteTokens;
        row.ToolCallCount = toolCalls;
        row.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(CancellationToken.None);
        Tokens.Add(usage.Total, new KeyValuePair<string, object?>("provider", row.Provider), new KeyValuePair<string, object?>("kind", row.Kind.ToString()));
        Requests.Add(1, new KeyValuePair<string, object?>("kind", row.Kind.ToString()), new KeyValuePair<string, object?>("outcome", outcome.ToString()));
    }

    /// <summary>Records a request that was refused before reaching a provider (turned off, over a limit).</summary>
    public async Task RecordRefusedAsync(AssistantInteractionKind kind, Guid conversationId, string prompt, string? pagePath, AssistantOutcome outcome, string reason, CancellationToken ct)
    {
        var row = await StartAsync(kind, conversationId, prompt, pagePath, null, ct);
        await CompleteAsync(row.Id, outcome, null, reason, AssistantTokenUsage.Zero, 0);
    }

    /// <summary>
    /// One request without tools (narratives, flag explanations): null when the assistant can't be used right now (the
    /// caller falls back to its deterministic text). The facts sent are recorded as a pseudo tool call with their records.
    /// </summary>
    public async Task<AssistantCompletion?> CompleteTextAsync(
        AssistantInteractionKind kind,
        string auditPrompt,
        string system,
        string userMessage,
        JsonObject? jsonSchema,
        string? pagePath,
        string factsSource,
        IReadOnlyList<string> records,
        CancellationToken ct,
        IReadOnlyList<ModelContent>? attachments = null)
    {
        var settings = await gate.LoadSettingsAsync(ct);
        if (await gate.CheckAsync(settings, UserEmail, kind, ct) is not null || gate.Route(settings) is not { } route)
        {
            return null;
        }

        var interaction = await StartAsync(kind, Guid.CreateVersion7(), auditPrompt, pagePath, route, ct);
        await RecordToolCallAsync(interaction.Id, 1, factsSource, null, AssistantToolOutcome.Ok, records, "Facts read as the signed-in user and sent with the request");
        var usage = AssistantTokenUsage.Zero;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(gate.Options.RequestTimeout);
            var message = attachments is { Count: > 0 }
                ? new ModelMessage(ModelRole.User, [.. attachments, new ModelText(userMessage)])
                : ModelMessage.UserText(userMessage);
            var request = new ModelRequest(route.Model, system, [message], [], gate.Options.MaxOutputTokens, "low", jsonSchema);
            ModelResponse? response = null;
            await foreach (var e in ClientFor(route).StreamAsync(request, timeout.Token))
            {
                if (e is ModelResponse r)
                {
                    response = r;
                }
            }

            usage = response?.Usage ?? usage;
            if (response is null || response.StopReason == "refusal" || string.IsNullOrWhiteSpace(response.Text))
            {
                await CompleteAsync(interaction.Id, AssistantOutcome.Refused, null, "The model returned no text.", usage, 1);
                return null;
            }

            await CompleteAsync(interaction.Id, AssistantOutcome.Answered, response.Text, null, usage, 1);
            return new AssistantCompletion(response.Text, route, interaction.Id);
        }
        catch (Exception ex) when (ex is AssistantProviderException or OperationCanceledException or HttpRequestException)
        {
            logger.LogWarning("Assistant {Kind} request failed: {Reason}", kind, ex.GetType().Name);
            await CompleteAsync(interaction.Id, AssistantOutcome.Failed, null, ex is AssistantProviderException ? ex.Message : "The provider didn't answer in time.", usage, 1);
            return null;
        }
    }

    internal static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..(max - 1)] + "…";
}
