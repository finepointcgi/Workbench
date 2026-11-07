using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Workbench.Application.Assistant;
using Workbench.Contracts.Assistant;

namespace Workbench.Infrastructure.Assistant;

/// <summary>
/// A model client for any server that speaks the OpenAI chat-completions API (OpenAI, Azure OpenAI, Ollama, LM Studio, vLLM,
/// a gateway): <c>POST {BaseUrl}/chat/completions</c> with the system prompt, messages, tools (function calling) and, for
/// structured output, a JSON-schema response format. Not streamed: the answer arrives whole. Tool calls are mapped both
/// ways, so Ask works on servers whose model supports function calling.
/// </summary>
internal sealed class OpenAiCompatibleModelClient(HttpClient http, string baseUrl, string? apiKey, ILogger<OpenAiCompatibleModelClient> logger) : IAssistantModelClient
{
    private readonly Uri _endpoint = new(baseUrl.TrimEnd('/') + "/chat/completions", UriKind.Absolute);

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await CompleteAsync(request, cancellationToken);
        if (response.Text.Length > 0)
        {
            yield return new ModelTextDelta(response.Text);
        }

        yield return response;
    }

    private async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        var withFormat = request.JsonSchema is not null;
        for (var attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new StringContent(Body(request, withFormat).ToJsonString(), Encoding.UTF8, "application/json") };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            HttpResponseMessage http1;
            try
            {
                http1 = await http.SendAsync(message, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new AssistantProviderException($"Couldn't reach the model server at {_endpoint.Host}. Is it running, and is BaseUrl right?", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new AssistantProviderException("The model server took too long to answer.", ex);
            }

            using (http1)
            {
                var text = await http1.Content.ReadAsStringAsync(ct);
                if (http1.IsSuccessStatusCode)
                {
                    return Parse(text);
                }

                // A server that doesn't know json_schema response formats: ask again with the schema only in the prompt.
                if (withFormat && attempt == 0 && http1.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                {
                    logger.LogInformation("The model server rejected a JSON-schema response format; retrying with the schema in the prompt.");
                    withFormat = false;
                    continue;
                }

                logger.LogWarning("OpenAI-compatible server answered {Status}: {Body}", (int)http1.StatusCode, text.Length > 500 ? text[..500] : text);
                throw new AssistantProviderException(http1.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The model server refused the API key.",
                    HttpStatusCode.NotFound => "The model server doesn't know that model or endpoint. Check BaseUrl and the model id.",
                    HttpStatusCode.TooManyRequests => "The model server is rate limiting requests. Try again shortly.",
                    _ => $"The model server returned an error ({(int)http1.StatusCode}).",
                });
            }
        }
    }

    internal static JsonObject Body(ModelRequest request, bool withResponseFormat)
    {
        var system = request.System;
        if (request.JsonSchema is { } schema && !withResponseFormat)
        {
            system += "\n\nReply with only one JSON object that matches this JSON Schema: no prose, no code fence.\n" + schema.ToJsonString();
        }

        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in request.Messages)
        {
            AddMessage(messages, m);
        }

        var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages, ["max_tokens"] = request.MaxTokens };
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.InputSchema.DeepClone() },
            }).ToArray());
        }

        if (request.JsonSchema is { } s && withResponseFormat)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "response", ["schema"] = s.DeepClone(), ["strict"] = false },
            };
        }

        return body;
    }

    private static void AddMessage(JsonArray messages, ModelMessage m)
    {
        if (m.Role == ModelRole.Assistant)
        {
            var calls = m.Content.OfType<ModelToolUse>().Select(u => (JsonNode)new JsonObject
            {
                ["id"] = u.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = u.Name, ["arguments"] = string.IsNullOrWhiteSpace(u.InputJson) ? "{}" : u.InputJson },
            }).ToArray();
            var text = string.Concat(m.Content.OfType<ModelText>().Select(t => t.Text));
            var node = new JsonObject { ["role"] = "assistant", ["content"] = text.Length == 0 && calls.Length > 0 ? null : text };
            if (calls.Length > 0)
            {
                node["tool_calls"] = new JsonArray(calls);
            }

            messages.Add(node);
            return;
        }

        // Tool results are their own messages; the rest of the user turn (text, images) is one message.
        foreach (var r in m.Content.OfType<ModelToolResult>())
        {
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.ToolUseId, ["content"] = r.IsError ? "Error: " + r.Content : r.Content });
        }

        var parts = new JsonArray();
        foreach (var c in m.Content)
        {
            switch (c)
            {
                case ModelText t when t.Text.Length > 0:
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = t.Text });
                    break;
                case ModelFile { MediaType: "application/pdf" }:
                    throw new AssistantProviderException("This connection can't read PDF files. Use an image, or an Anthropic or Bedrock connection.");
                case ModelFile image:
                    parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{image.Base64Data}" } });
                    break;
            }
        }

        if (parts.Count == 1 && parts[0]!["type"]!.ToString() == "text")
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts[0]!["text"]!.ToString() });
        }
        else if (parts.Count > 0)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
        }
    }

    internal static ModelResponse Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new AssistantProviderException("The model server's answer wasn't readable.", ex);
        }

        var choice = (root?["choices"] as JsonArray)?.FirstOrDefault();
        if (choice is null)
        {
            throw new AssistantProviderException("The model server returned no answer.");
        }

        var message = choice["message"];
        var content = new List<ModelContent>();
        if (message?["content"] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0)
        {
            content.Add(new ModelText(text));
        }

        foreach (var call in message?["tool_calls"] as JsonArray ?? [])
        {
            var fn = call?["function"];
            if (fn?["name"]?.ToString() is { Length: > 0 } name)
            {
                // Arguments arrive as a JSON string (some servers send an object).
                var args = fn["arguments"] is JsonValue av && av.TryGetValue<string>(out var s) ? s : fn["arguments"]?.ToJsonString() ?? "{}";
                content.Add(new ModelToolUse(call?["id"]?.ToString() is { Length: > 0 } id ? id : "call_" + Guid.NewGuid().ToString("N")[..12], name, string.IsNullOrWhiteSpace(args) ? "{}" : args));
            }
        }

        var reason = choice["finish_reason"]?.ToString();
        var stop = content.OfType<ModelToolUse>().Any() ? "tool_use"
            : reason == "length" ? "max_tokens"
            : reason == "content_filter" ? "refusal"
            : "end_turn";
        long N(JsonNode? n) => n is JsonValue x && x.TryGetValue<long>(out var l) ? l : 0;
        var usage = root!["usage"];
        var cached = N(usage?["prompt_tokens_details"]?["cached_tokens"]);
        return new ModelResponse(content, stop, new AssistantTokenUsage(Math.Max(0, N(usage?["prompt_tokens"]) - cached), N(usage?["completion_tokens"]), cached, 0));
    }
}
