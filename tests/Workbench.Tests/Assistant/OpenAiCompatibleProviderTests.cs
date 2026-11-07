using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Application.Assistant;
using Workbench.Contracts.Assistant;
using Workbench.Infrastructure.Assistant;

namespace Workbench.Tests.Assistant;

/// <summary>The OpenAI-compatible provider: validated configuration, requests and answers mapped both ways, tools included.</summary>
public sealed class OpenAiCompatibleProviderTests
{
    private static AssistantConnectionOptions Connection(string? baseUrl = "https://api.example.com/v1", string? model = "gpt-4o") =>
        new() { Provider = AssistantProviderKind.OpenAiCompatible, BaseUrl = baseUrl, DefaultModel = model, ApiKey = "k" };

    [Fact]
    public void A_connection_needs_a_server_address_and_a_model_and_only_this_computer_may_use_plain_http()
    {
        Assert.Empty(AssistantConnections.Validate("openai", Connection()));
        Assert.Empty(AssistantConnections.Validate("ollama", new AssistantConnectionOptions { Provider = AssistantProviderKind.OpenAiCompatible, BaseUrl = "http://localhost:11434/v1", DefaultModel = "llama3.1" }));
        Assert.Contains(AssistantConnections.Validate("x", Connection(baseUrl: null)), e => e.Contains("BaseUrl"));
        Assert.Contains(AssistantConnections.Validate("x", Connection(baseUrl: "http://models.example.com/v1")), e => e.Contains("BaseUrl"));
        Assert.Contains(AssistantConnections.Validate("x", Connection(model: null)), e => e.Contains("DefaultModel"));
    }

    [Fact]
    public void It_is_offered_to_administrators_and_says_where_the_data_goes()
    {
        var options = new AssistantOptions { Connections = { ["openai"] = Connection(), ["local"] = new() { Provider = AssistantProviderKind.OpenAiCompatible, BaseUrl = "http://127.0.0.1:1234/v1", DefaultModel = "m" } } };

        var available = AssistantConnections.Available(options);

        Assert.Contains(available, c => c.Name == "openai" && c.DataHandling.Contains("api.example.com"));
        Assert.Contains(available, c => c.Name == "local" && c.DataHandling.Contains("this computer"));
    }

    [Fact]
    public void Messages_tools_and_a_schema_are_sent_in_the_chat_completions_shape()
    {
        var tool = new ModelToolDefinition("get_budget", "Budget for a project", new JsonObject { ["type"] = "object" });
        var request = new ModelRequest("gpt-4o", "Be brief.",
        [
            ModelMessage.UserText("How is 1001?"),
            new ModelMessage(ModelRole.Assistant, [new ModelText("Looking."), new ModelToolUse("c1", "get_budget", """{"project":"1001"}""")]),
            new ModelMessage(ModelRole.User, [new ModelToolResult("c1", "Budget is $5", false)]),
        ], [tool], 500, "low", new JsonObject { ["type"] = "object" });

        var body = OpenAiCompatibleModelClient.Body(request, withResponseFormat: true);

        Assert.Equal("gpt-4o", body["model"]!.ToString());
        var roles = body["messages"]!.AsArray().Select(m => m!["role"]!.ToString()).ToList();
        Assert.Equal(["system", "user", "assistant", "tool"], roles);
        Assert.Equal("c1", body["messages"]![2]!["tool_calls"]![0]!["id"]!.ToString());
        Assert.Equal("c1", body["messages"]![3]!["tool_call_id"]!.ToString());
        Assert.Equal("get_budget", body["tools"]![0]!["function"]!["name"]!.ToString());
        Assert.Equal("json_schema", body["response_format"]!["type"]!.ToString());

        var inPrompt = OpenAiCompatibleModelClient.Body(request, withResponseFormat: false);
        Assert.Null(inPrompt["response_format"]);
        Assert.Contains("JSON Schema", inPrompt["messages"]![0]!["content"]!.ToString());
    }

    [Fact]
    public void An_answer_with_tool_calls_is_a_tool_use_and_usage_is_read()
    {
        var response = OpenAiCompatibleModelClient.Parse("""
            {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[{"id":"call_9","type":"function","function":{"name":"get_budget","arguments":"{\"project\":\"1001\"}"}}]}}],
             "usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":40}}}
            """);

        Assert.Equal("tool_use", response.StopReason);
        var use = Assert.Single(response.ToolUses);
        Assert.Equal(("call_9", "get_budget"), (use.Id, use.Name));
        Assert.Equal("""{"project":"1001"}""", use.InputJson);
        Assert.Equal((60L, 20L, 40L), (response.Usage.InputTokens, response.Usage.OutputTokens, response.Usage.CacheReadTokens));

        Assert.Equal("max_tokens", OpenAiCompatibleModelClient.Parse("""{"choices":[{"finish_reason":"length","message":{"content":"cut"}}]}""").StopReason);
        Assert.Throws<AssistantProviderException>(() => OpenAiCompatibleModelClient.Parse("""{"choices":[]}"""));
    }

    [Fact]
    public async Task A_server_that_rejects_the_schema_format_is_asked_again_with_the_schema_in_the_prompt()
    {
        var seen = new List<string>();
        var handler = new StubHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            seen.Add(body);
            return body.Contains("response_format")
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("unsupported") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"{\"a\":1}"}}]}""", Encoding.UTF8, "application/json") };
        });
        var client = new OpenAiCompatibleModelClient(new HttpClient(handler), "https://api.example.com/v1/", "k", NullLogger<OpenAiCompatibleModelClient>.Instance);

        ModelResponse? done = null;
        await foreach (var e in client.StreamAsync(new ModelRequest("m", "s", [ModelMessage.UserText("go")], [], 100, "low", new JsonObject { ["type"] = "object" })))
        {
            done = e as ModelResponse ?? done;
        }

        Assert.Equal("""{"a":1}""", done!.Text);
        Assert.Equal(2, seen.Count);
        Assert.Equal("https://api.example.com/v1/chat/completions", handler.LastUri);
    }

    [Fact]
    public async Task A_refused_key_or_unreachable_server_is_a_provider_error_with_a_plain_message()
    {
        var unauthorized = new OpenAiCompatibleModelClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))), "https://api.example.com/v1", "bad", NullLogger<OpenAiCompatibleModelClient>.Instance);
        var ex = await Assert.ThrowsAsync<AssistantProviderException>(async () =>
        {
            await foreach (var _ in unauthorized.StreamAsync(new ModelRequest("m", "s", [ModelMessage.UserText("go")], [], 100, "low")))
            {
            }
        });
        Assert.Contains("API key", ex.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri!.ToString();
            return Task.FromResult(respond(request));
        }
    }
}
