using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Workbench.Application.Assistant;
using Workbench.Contracts.Assistant;

namespace Workbench.Infrastructure.Assistant;

/// <summary>
/// The Messages API through the official Anthropic SDK, for the Anthropic API and Amazon Bedrock alike. Adaptive
/// thinking (the model decides), effort from configuration, streaming, and prompt caching: the system prompt and tool
/// list are a stable prefix with a cache breakpoint, and automatic caching covers the growing conversation.
/// </summary>
internal sealed class AnthropicModelClient(Func<Task<IAnthropicClient>> clientFactory, ILogger logger) : IAssistantModelClient
{
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parameters = Build(request);
        var blocks = new SortedDictionary<long, BlockBuilder>();
        var stopReason = "end_turn";
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;

        IAsyncEnumerator<RawMessageStreamEvent>? stream = null;
        try
        {
            var client = await clientFactory();
            stream = client.Messages.CreateStreaming(parameters, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex) when (IsProviderError(ex))
        {
            throw Translate(ex);
        }

        await using (stream)
        {
            while (true)
            {
                RawMessageStreamEvent e;
                try
                {
                    if (!await stream.MoveNextAsync())
                    {
                        break;
                    }

                    e = stream.Current;
                }
                catch (Exception ex) when (IsProviderError(ex))
                {
                    throw Translate(ex);
                }

                if (e.TryPickStart(out var start))
                {
                    var u = start.Message.Usage;
                    input = u.InputTokens;
                    cacheRead = u.CacheReadInputTokens ?? 0;
                    cacheWrite = u.CacheCreationInputTokens ?? 0;
                    output = u.OutputTokens;
                }
                else if (e.TryPickContentBlockStart(out var blockStart))
                {
                    var builder = new BlockBuilder();
                    var cb = blockStart.ContentBlock;
                    if (cb.TryPickToolUse(out var toolUse))
                    {
                        builder.Kind = BlockKind.ToolUse;
                        builder.Id = toolUse.ID;
                        builder.Name = toolUse.Name;
                    }
                    else if (cb.TryPickThinking(out var thinking))
                    {
                        builder.Kind = BlockKind.Thinking;
                        builder.Text.Append(thinking.Thinking);
                        builder.Signature = thinking.Signature;
                    }
                    else if (cb.TryPickRedactedThinking(out var redacted))
                    {
                        builder.Kind = BlockKind.Redacted;
                        builder.Signature = redacted.Data;
                    }
                    else if (cb.TryPickText(out var text))
                    {
                        builder.Kind = BlockKind.Text;
                        builder.Text.Append(text.Text);
                    }

                    blocks[blockStart.Index] = builder;
                }
                else if (e.TryPickContentBlockDelta(out var delta))
                {
                    if (!blocks.TryGetValue(delta.Index, out var builder))
                    {
                        continue;
                    }

                    if (delta.Delta.TryPickText(out var textDelta))
                    {
                        builder.Text.Append(textDelta.Text);
                        yield return new ModelTextDelta(textDelta.Text);
                    }
                    else if (delta.Delta.TryPickInputJson(out var json))
                    {
                        builder.Json.Append(json.PartialJson);
                    }
                    else if (delta.Delta.TryPickThinking(out var thinkingDelta))
                    {
                        builder.Text.Append(thinkingDelta.Thinking);
                    }
                    else if (delta.Delta.TryPickSignature(out var signature))
                    {
                        builder.Signature += signature.Signature;
                    }
                }
                else if (e.TryPickDelta(out var messageDelta))
                {
                    if (messageDelta.Delta.StopReason is { } reason)
                    {
                        stopReason = reason.Raw() is string raw ? raw : reason.ToString();
                    }

                    output = messageDelta.Usage.OutputTokens;
                }
            }
        }

        var content = new List<ModelContent>();
        foreach (var b in blocks.Values)
        {
            switch (b.Kind)
            {
                case BlockKind.Text when b.Text.Length > 0:
                    content.Add(new ModelText(b.Text.ToString()));
                    break;
                case BlockKind.ToolUse:
                    content.Add(new ModelToolUse(b.Id!, b.Name!, b.Json.Length == 0 ? "{}" : b.Json.ToString()));
                    break;
                case BlockKind.Thinking:
                    content.Add(new ModelOpaque(new ThinkingBlockParam { Thinking = b.Text.ToString(), Signature = b.Signature ?? "" }));
                    break;
                case BlockKind.Redacted:
                    content.Add(new ModelOpaque(new RedactedThinkingBlockParam { Data = b.Signature ?? "" }));
                    break;
            }
        }

        logger.LogDebug("Assistant model call finished: {StopReason}, {InputTokens} in, {OutputTokens} out, {CacheRead} cache read", stopReason, input, output, cacheRead);
        yield return new ModelResponse(content, stopReason, new AssistantTokenUsage(input, output, cacheRead, cacheWrite));
    }

    private static MessageCreateParams Build(ModelRequest request)
    {
        var tools = new List<ToolUnion>();
        for (var i = 0; i < request.Tools.Count; i++)
        {
            var t = request.Tools[i];
            var properties = t.InputSchema["properties"]?.AsObject()
                .ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)) ?? [];
            var required = t.InputSchema["required"]?.AsArray().Select(r => r!.GetValue<string>()).ToList() ?? [];
            tools.Add(new Tool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = new() { Properties = properties, Required = required },

                // Breakpoint after the (fixed) tool list: tools + system are cached across every question.
                CacheControl = i == request.Tools.Count - 1 ? new CacheControlEphemeral() : null,
            });
        }

        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            System = new List<TextBlockParam> { new() { Text = request.System, CacheControl = new CacheControlEphemeral() } },
            Messages = request.Messages.Select(ToParam).ToList(),
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig
            {
                Effort = request.Effort.ToLowerInvariant() switch
                {
                    "low" => Effort.Low,
                    "high" => Effort.High,
                    "max" => Effort.Max,
                    _ => Effort.Medium,
                },
                Format = request.JsonSchema is { } schema
                    ? new JsonOutputFormat { Schema = schema.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)) }
                    : null,
            },

            // Automatic caching of the conversation so far (tool loops resend it each round).
            CacheControl = new CacheControlEphemeral(),
        };
        if (tools.Count > 0)
        {
            parameters = parameters with { Tools = tools };
        }

        return parameters;
    }

    private static MessageParam ToParam(ModelMessage message)
    {
        var content = new List<ContentBlockParam>();
        foreach (var c in message.Content)
        {
            switch (c)
            {
                case ModelText t:
                    content.Add(new TextBlockParam { Text = t.Text });
                    break;
                case ModelToolUse u:
                    var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(string.IsNullOrWhiteSpace(u.InputJson) ? "{}" : u.InputJson) ?? [];
                    content.Add(new ToolUseBlockParam { ID = u.Id, Name = u.Name, Input = input });
                    break;
                case ModelToolResult r:
                    content.Add(new ToolResultBlockParam { ToolUseID = r.ToolUseId, Content = r.Content, IsError = r.IsError });
                    break;
                case ModelOpaque { Block: ThinkingBlockParam thinking }:
                    content.Add(thinking);
                    break;
                case ModelOpaque { Block: RedactedThinkingBlockParam redacted }:
                    content.Add(redacted);
                    break;
            }
        }

        return new MessageParam { Role = message.Role == ModelRole.User ? Role.User : Role.Assistant, Content = content };
    }

    private static bool IsProviderError(Exception ex) => ex is AnthropicApiException or HttpRequestException or AnthropicException;

    /// <summary>A message that's safe to show and log: status only, never the key, the prompt or the provider's body.</summary>
    private static AssistantProviderException Translate(Exception ex) => ex switch
    {
        AnthropicRateLimitException => new("The AI provider is busy (rate limited). Try again in a minute.", ex),
        AnthropicUnauthorizedException or AnthropicForbiddenException => new("The AI provider refused the server's credentials. An administrator needs to check the assistant connection.", ex),
        Anthropic5xxException => new("The AI provider had an error. Try again in a minute.", ex),
        AnthropicApiException => new("The AI provider rejected the request.", ex),
        _ => new("The AI provider couldn't be reached.", ex),
    };

    private enum BlockKind
    {
        Unknown,
        Text,
        ToolUse,
        Thinking,
        Redacted,
    }

    private sealed class BlockBuilder
    {
        public BlockKind Kind { get; set; }

        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Signature { get; set; }

        public StringBuilder Text { get; } = new();

        public StringBuilder Json { get; } = new();
    }
}
