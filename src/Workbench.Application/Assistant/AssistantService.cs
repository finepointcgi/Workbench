using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Assistant;

namespace Workbench.Application.Assistant;

/// <summary>
/// Answers questions with the read-only <see cref="AssistantToolbox"/>: a manual tool-use loop (so every call can be
/// checked, limited and audited), streaming text to the panel as it arrives. Tool results go back to the model as
/// delimited, escaped, untrusted data; the tool list never changes, whatever the data says.
/// </summary>
public sealed partial class AssistantService(
    AssistantRunner runner,
    AssistantToolbox toolbox,
    IServiceProvider services,
    IWorkbenchDbContextFactory dbFactory,
    TimeProvider clock,
    ILogger<AssistantService> logger) : IAssistantService
{
    public async Task<AssistantStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var settings = await runner.Gate.LoadSettingsAsync(cancellationToken);
        var blocked = await runner.Gate.CheckAsync(settings, runner.UserEmail, AssistantInteractionKind.Question, cancellationToken);
        var enabled = settings?.IsEnabled == true;
        var route = runner.Gate.Route(settings);
        return new AssistantStatusDto(
            enabled,
            blocked is null,
            blocked?.Reason,
            enabled ? route?.ProviderLabel : null,
            enabled && blocked?.Outcome != AssistantOutcome.Disabled && settings!.ExplainTimesheetFlags,
            AssistantNotice.Text);
    }

    public async IAsyncEnumerable<AssistantEvent> AskAsync(AskAssistantRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var channel = Channel.CreateUnbounded<AssistantEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var work = Task.Run(async () =>
        {
            try
            {
                await RunAsync(request, channel.Writer, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Assistant question failed");
                channel.Writer.TryWrite(new AssistantEvent(AssistantEventKind.Failed, "Something went wrong while answering. Try again in a moment."));
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var e in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return e;
        }

        await work;
    }

    private async Task RunAsync(AskAssistantRequest request, ChannelWriter<AssistantEvent> events, CancellationToken ct)
    {
        var options = runner.Gate.Options;
        var question = (request.Question ?? "").Trim();
        var pagePath = CleanPath(request.PagePath);
        if (question.Length == 0)
        {
            await events.WriteAsync(new AssistantEvent(AssistantEventKind.Failed, "Type a question first."), ct);
            return;
        }

        if (question.Length > options.MaxQuestionLength)
        {
            await events.WriteAsync(new AssistantEvent(AssistantEventKind.Failed, $"Keep questions under {options.MaxQuestionLength:N0} characters."), ct);
            return;
        }

        var conversationId = request.ConversationId ?? Guid.CreateVersion7();
        var settings = await runner.Gate.LoadSettingsAsync(ct);
        if (await runner.Gate.CheckAsync(settings, runner.UserEmail, AssistantInteractionKind.Question, ct) is { } blocked)
        {
            await runner.RecordRefusedAsync(AssistantInteractionKind.Question, conversationId, question, pagePath, blocked.Outcome, blocked.Reason, ct);
            await events.WriteAsync(new AssistantEvent(AssistantEventKind.Failed, blocked.Reason, conversationId), ct);
            return;
        }

        var route = runner.Gate.Route(settings)!;
        var history = await HistoryAsync(conversationId, options.ConversationTurns, ct);
        var interaction = await runner.StartAsync(AssistantInteractionKind.Question, conversationId, question, pagePath, route, ct);
        await events.WriteAsync(new AssistantEvent(AssistantEventKind.Started, null, conversationId, interaction.Id), ct);

        var messages = new List<ModelMessage>(history) { ModelMessage.UserText(await UserMessageAsync(question, pagePath, ct)) };
        var tools = AssistantToolbox.Specs.Select(s => s.Definition).ToList();
        var client = runner.ClientFor(route);
        var usage = AssistantTokenUsage.Zero;
        var answer = new StringBuilder();
        var citations = new List<AssistantCitation>();
        var toolsUsed = new List<string>();
        var toolCalls = 0;
        var outcome = AssistantOutcome.Answered;
        string? detail = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.RequestTimeout);
            for (var round = 0; ; round++)
            {
                ModelResponse? response = null;
                var modelRequest = new ModelRequest(route.Model, AssistantPrompts.System, messages.ToList(), tools, options.MaxOutputTokens, options.Effort);
                await foreach (var e in client.StreamAsync(modelRequest, timeout.Token))
                {
                    switch (e)
                    {
                        case ModelTextDelta delta:
                            answer.Append(delta.Text);
                            await events.WriteAsync(new AssistantEvent(AssistantEventKind.Text, delta.Text), ct);
                            break;
                        case ModelResponse r:
                            response = r;
                            break;
                    }
                }

                if (response is null)
                {
                    throw new AssistantProviderException("The provider ended the answer early.");
                }

                usage = usage.Add(response.Usage);
                if (response.StopReason == "refusal")
                {
                    outcome = AssistantOutcome.Refused;
                    detail = "The model declined to answer.";
                    if (answer.Length == 0)
                    {
                        answer.Append("I can't help with that request.");
                    }

                    break;
                }

                var uses = response.ToolUses;
                if (uses.Count == 0 || response.StopReason != "tool_use")
                {
                    break;
                }

                if (round + 1 >= options.MaxRoundTrips)
                {
                    detail = "Stopped at the round-trip limit.";
                    answer.Append("\n\nI couldn't finish looking this up within the assistant's limits. Try a narrower question.");
                    break;
                }

                messages.Add(new ModelMessage(ModelRole.Assistant, response.Content));
                var results = new List<ModelContent>();
                foreach (var use in uses)
                {
                    toolCalls++;
                    var spec = AssistantToolbox.Find(use.Name);
                    AssistantToolResult result;
                    if (toolCalls > options.MaxToolCallsPerQuestion)
                    {
                        result = AssistantToolResult.Error(AssistantToolOutcome.Invalid, "Tool call limit reached for this question. Answer with what you have.");
                    }
                    else
                    {
                        await events.WriteAsync(new AssistantEvent(AssistantEventKind.Tool, spec?.Activity ?? "Checking"), ct);
                        result = await toolbox.RunAsync(use.Name, use.InputJson, timeout.Token);
                    }

                    await runner.RecordToolCallAsync(interaction.Id, toolCalls, use.Name, use.InputJson, result.Outcome, result.Records, result.Summary);
                    if (result.Outcome == AssistantToolOutcome.Ok)
                    {
                        toolsUsed.Add(use.Name);
                        foreach (var c in result.Citations.Where(c => citations.All(x => x.Href != c.Href)))
                        {
                            citations.Add(c);
                        }
                    }

                    results.Add(new ModelToolResult(use.Id, AssistantPrompts.WrapData(use.Name, toolCalls, result.Data, options.MaxToolResultChars), result.Outcome != AssistantToolOutcome.Ok));
                }

                messages.Add(new ModelMessage(ModelRole.User, results));
                if (answer.Length > 0 && !char.IsWhiteSpace(answer[^1]))
                {
                    answer.Append(' ');
                    await events.WriteAsync(new AssistantEvent(AssistantEventKind.Text, " "), ct);
                }
            }
        }
        catch (Exception ex) when (ex is AssistantProviderException or HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var message = ex is AssistantProviderException ? ex.Message : "The AI provider didn't answer in time. Try again.";
            await runner.CompleteAsync(interaction.Id, AssistantOutcome.Failed, answer.ToString(), message, usage, toolCalls);
            await events.WriteAsync(new AssistantEvent(AssistantEventKind.Failed, message, conversationId, interaction.Id), CancellationToken.None);
            return;
        }
        catch (OperationCanceledException)
        {
            await runner.CompleteAsync(interaction.Id, AssistantOutcome.Failed, answer.ToString(), "Cancelled by the user.", usage, toolCalls);
            throw;
        }

        var text = answer.ToString().Trim();
        await runner.CompleteAsync(interaction.Id, outcome, text, detail, usage, toolCalls);
        var dto = new AssistantAnswerDto(interaction.Id, conversationId, text, citations, toolsUsed.Distinct().ToList(), outcome.ToString(), usage);
        await events.WriteAsync(new AssistantEvent(AssistantEventKind.Completed, null, conversationId, interaction.Id, dto), CancellationToken.None);
    }

    private async Task<IReadOnlyList<ModelMessage>> HistoryAsync(Guid conversationId, int turns, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var email = runner.UserEmail;
        var earlier = await db.Set<AssistantInteraction>().AsNoTracking()
            .Where(i => i.ConversationId == conversationId && i.UserEmail == email && i.Kind == AssistantInteractionKind.Question
                && i.Outcome == AssistantOutcome.Answered && i.Answer != null)
            .OrderByDescending(i => i.StartedAtUtc)
            .Take(turns)
            .ToListAsync(ct);
        return earlier
            .OrderBy(i => i.StartedAtUtc)
            .SelectMany(i => new[] { ModelMessage.UserText($"<question>\n{i.Prompt}\n</question>"), ModelMessage.AssistantText(i.Answer!) })
            .ToList();
    }

    /// <summary>The per-question part of the prompt: today, who is asking (roles only) and the page they're on.</summary>
    private async Task<string> UserMessageAsync(string question, string? pagePath, CancellationToken ct)
    {
        var context = new StringBuilder();
        context.AppendLine($"today: {DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime):yyyy-MM-dd}");
        try
        {
            var me = await services.GetRequiredService<ICurrentUserContext>().GetAsync(ct);
            var roles = me.AssignedRoles?.Select(r => r.Name).ToList() ?? OrgRoleChecks.All.Where(r => me.Roles.HasFlag(r)).Select(OrgRoleChecks.Label).ToList();
            context.AppendLine($"signed in as: {AssistantToolbox.Clip(me.DisplayName, 120)} (roles: {(roles.Count == 0 ? "Employee" : string.Join(", ", roles))})");
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (pagePath is not null)
        {
            context.AppendLine($"page: {pagePath}");
            if (ProjectPath().Match(pagePath) is { Success: true } m && Guid.TryParse(m.Groups[1].Value, out var projectId))
            {
                var project = (await services.GetRequiredService<IProjectService>().ListProjectsAsync(ct)).FirstOrDefault(p => p.Id == projectId);
                if (project is not null)
                {
                    context.AppendLine($"project: {project.Code}");
                    context.AppendLine($"project name: {AssistantToolbox.Clip(project.Name, 120)}");
                }
            }
        }

        return $"<page-context>\n{context.ToString().TrimEnd()}\n</page-context>\n<question>\n{question}\n</question>";
    }

    /// <summary>App-relative path only: no scheme, host, query string or fragment.</summary>
    internal static string? CleanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var p = path.Trim();
        var cut = p.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            p = p[..cut];
        }

        if (p.StartsWith("//", StringComparison.Ordinal) || p.Contains("//", StringComparison.Ordinal) || p.Contains(':', StringComparison.Ordinal) || p.Contains('\\'))
        {
            return null;
        }

        return AssistantRunner.Clip(p.TrimStart('/'), 500);
    }

    [GeneratedRegex(@"^projects/([0-9a-fA-F\-]{36})(?:/|$)")]
    private static partial Regex ProjectPath();
}
