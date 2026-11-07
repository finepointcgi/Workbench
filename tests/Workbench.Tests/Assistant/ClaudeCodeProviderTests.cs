using Microsoft.Extensions.Configuration;
using Workbench.Application.Assistant;
using Workbench.Contracts.Assistant;
using Workbench.Infrastructure.Assistant;

namespace Workbench.Tests.Assistant;

/// <summary>The development-only Claude Code provider: offered only when enabled, never in Production, and its answers are read safely.</summary>
public sealed class ClaudeCodeProviderTests
{
    [Fact]
    public void The_local_connection_is_offered_only_when_enabled_and_its_name_is_reserved()
    {
        Assert.DoesNotContain(AssistantConnections.Available(new AssistantOptions()), c => c.Name == "claude-code");
        Assert.Contains(AssistantConnections.Available(new AssistantOptions { EnableClaudeCodeProvider = true }), c => c.Name == "claude-code" && c.Provider == AssistantProviderKind.ClaudeCode);
        Assert.Contains(AssistantConnections.Validate("claude-code", new AssistantConnectionOptions { Provider = AssistantProviderKind.Anthropic, ApiKey = "k" }), e => e.Contains("reserved"));
    }

    [Fact]
    public void Production_refuses_the_local_provider()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:EnableClaudeCodeProvider"] = "true" }).Build();
        Assert.Contains(Workbench.Web.Security.ProductionConfigurationCheck.AssistantProblems(config), p => p.Contains("EnableClaudeCodeProvider"));
    }

    [Fact]
    public void An_answer_is_read_from_the_result_with_its_usage_and_a_code_fence_is_stripped()
    {
        var (text, usage) = ClaudeCodeModelClient.Parse("""{"result":"```json\n{\"a\":1}\n```","is_error":false,"usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":2,"cache_creation_input_tokens":3}}""");

        Assert.Equal("""{"a":1}""", text);
        Assert.Equal(20, usage.Total);
    }

    [Fact]
    public void An_error_or_unreadable_answer_is_a_provider_error_not_a_crash()
    {
        Assert.Throws<AssistantProviderException>(() => ClaudeCodeModelClient.Parse("""{"result":"Not logged in","is_error":true}"""));
        Assert.Throws<AssistantProviderException>(() => ClaudeCodeModelClient.Parse("not json"));
    }
}
