using System.Text.Json.Nodes;

namespace Workbench.Contracts.Assistant;

/// <summary>
/// One of the assistant's read-only tools as an outside AI client sees it (over MCP): its name, description, the API key
/// area it reads (<see cref="Integrations.ApiKeyScopes"/>) and its JSON input schema.
/// </summary>
/// <param name="IsWrite">The tool changes data: the key needs the area's write scope.</param>
/// <param name="Overwrites">The tool replaces something that was there (MCP destructiveHint).</param>
public sealed record AssistantToolInfo(string Name, string Title, string Description, string Area, JsonObject InputSchema, bool IsWrite = false, bool Overwrites = false);

/// <summary>
/// A tool call's result: the data as JSON text (with a <c>summary</c>; cut at the assistant's result limit), whether it
/// failed, and app-relative links to the pages that show it.
/// </summary>
public sealed record AssistantToolCallResult(bool IsError, string Summary, string DataJson, IReadOnlyList<AssistantCitation> Links);

/// <summary>
/// Runs the assistant's tools for an outside AI client (the MCP endpoint): the read-only tools, plus write tools for sheet
/// rows, comments and tasks. Every call runs as the signed-in caller (for an API key, its service user), through the same
/// permission-checked services as the pages, and is recorded in the security log. Tools that keys must never reach
/// (timesheet flags) are not offered, and nothing approves, signs, submits or pays.
/// </summary>
public interface IAssistantToolHost
{
    IReadOnlyList<AssistantToolInfo> Tools { get; }

    /// <summary>Runs a tool. Permission, validation and not-found problems come back as an error result, never as an exception.</summary>
    Task<AssistantToolCallResult> CallAsync(string name, string argumentsJson, CancellationToken cancellationToken = default);
}
