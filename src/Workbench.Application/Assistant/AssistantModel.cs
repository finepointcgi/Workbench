using System.Text.Json.Nodes;
using Workbench.Contracts.Assistant;

namespace Workbench.Application.Assistant;

// A small provider-neutral view of the Messages API, so the orchestration, the audit log and the fake provider don't
// depend on the Anthropic SDK (which lives in Infrastructure).

public enum ModelRole
{
    User,
    Assistant,
}

public abstract record ModelContent;

public sealed record ModelText(string Text) : ModelContent;

public sealed record ModelToolUse(string Id, string Name, string InputJson) : ModelContent;

public sealed record ModelToolResult(string ToolUseId, string Content, bool IsError) : ModelContent;

/// <summary>A provider block that must be sent back unchanged in the next request (e.g. a signed thinking block).</summary>
public sealed record ModelOpaque(object Block) : ModelContent;

/// <summary>A file sent with a user message (a receipt image or PDF): base64 bytes and the media type, e.g. image/png or application/pdf.</summary>
public sealed record ModelFile(string MediaType, string Base64Data) : ModelContent;

public sealed record ModelMessage(ModelRole Role, IReadOnlyList<ModelContent> Content)
{
    public static ModelMessage UserText(string text) => new(ModelRole.User, [new ModelText(text)]);

    public static ModelMessage AssistantText(string text) => new(ModelRole.Assistant, [new ModelText(text)]);
}

public sealed record ModelToolDefinition(string Name, string Description, JsonObject InputSchema);

/// <summary>
/// One Messages API call. <see cref="System"/> and <see cref="Tools"/> are stable across calls (cached); anything that
/// changes per question goes in the messages. <see cref="JsonSchema"/> asks for structured JSON output.
/// </summary>
public sealed record ModelRequest(
    string Model,
    string System,
    IReadOnlyList<ModelMessage> Messages,
    IReadOnlyList<ModelToolDefinition> Tools,
    int MaxTokens,
    string Effort,
    JsonObject? JsonSchema = null);

public abstract record ModelStreamEvent;

public sealed record ModelTextDelta(string Text) : ModelStreamEvent;

public sealed record ModelResponse(IReadOnlyList<ModelContent> Content, string StopReason, AssistantTokenUsage Usage) : ModelStreamEvent
{
    public string Text => string.Concat(Content.OfType<ModelText>().Select(t => t.Text));

    public IReadOnlyList<ModelToolUse> ToolUses => Content.OfType<ModelToolUse>().ToList();
}

/// <summary>Streams one model call: text deltas as they arrive, then the complete <see cref="ModelResponse"/>.</summary>
public interface IAssistantModelClient
{
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Creates the client for a connection. The Application registers one for the fake provider; Infrastructure adds the real ones.</summary>
public interface IAssistantModelClientFactory
{
    IAssistantModelClient Create(string connectionName, AssistantConnectionOptions? connection);
}

/// <summary>Thrown by a model client for a provider problem (bad key, quota, network). The message is safe to show and log.</summary>
public sealed class AssistantProviderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Only the fake provider: the Application's default, replaced by Infrastructure's factory.</summary>
public sealed class FakeOnlyModelClientFactory : IAssistantModelClientFactory
{
    public IAssistantModelClient Create(string connectionName, AssistantConnectionOptions? connection) =>
        connection is null || connection.Provider == AssistantProviderKind.Fake
            ? new FakeAssistantModel()
            : throw new AssistantProviderException("This server can't reach model providers (no provider package is installed).");
}
