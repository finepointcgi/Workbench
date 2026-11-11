using System.Text.Json;
using Microsoft.Extensions.Options;
using Workbench.Application.Security;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Security;
using Workbench.Domain.Assistant;

namespace Workbench.Application.Assistant;

/// <summary>
/// The assistant's read-only tools for outside AI clients (the MCP endpoint, <c>/api/v1/mcp</c>). It runs the same
/// <see cref="AssistantToolbox"/> as the in-app assistant, so every call goes through the caller's permission checks; the
/// Workbench model, prompts and token limits aren't involved, because the client brings its own model. Each tool reads one
/// API key area, which the endpoint checks against the key's scopes. Every call is recorded in the security log.
/// </summary>
public sealed class AssistantToolHost(AssistantToolbox toolbox, SecurityEventLog securityLog, IOptions<AssistantOptions> options) : IAssistantToolHost
{
    /// <summary>
    /// The API key area each tool reads. A tool missing here is never offered: timesheet flags stay inside Workbench, as
    /// their REST path is closed to keys.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Areas = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["list_projects"] = "projects",
        ["get_project_financials"] = "projects",
        ["get_budget_versions"] = "projects",
        ["get_earned_value"] = "projects",
        ["list_clin_funding"] = "projects",
        ["get_ceilings"] = "projects",
        ["get_project_staffing"] = "projects",
        ["get_project_cost_ledger"] = "accounting",
        ["get_timesheet_status"] = "time",
        ["get_hours_by_charge_code"] = "time",
        ["list_unallowable_costs"] = "time",
        ["list_pending_approvals"] = "setup",
        ["get_custom_fields"] = "setup",
        ["list_custom_field_matches"] = "setup",
        ["list_sheets"] = "sheets",
        ["read_sheet"] = "sheets",
    };

    private static readonly IReadOnlyList<AssistantToolInfo> All = AssistantToolbox.Specs
        .Where(s => Areas.ContainsKey(s.Name))
        .Select(s => new AssistantToolInfo(s.Name, s.Activity, s.Description, Areas[s.Name], s.Schema))
        .ToList();

    public IReadOnlyList<AssistantToolInfo> Tools => All;

    public async Task<AssistantToolCallResult> CallAsync(string name, string argumentsJson, CancellationToken cancellationToken = default)
    {
        AssistantToolResult result = Areas.ContainsKey(name)
            ? await toolbox.RunAsync(name, argumentsJson, cancellationToken)
            : AssistantToolResult.Error(AssistantToolOutcome.Invalid, $"There is no tool named '{AssistantToolbox.Clip(name, 40)}'.");

        var outcome = result.Outcome switch
        {
            AssistantToolOutcome.Ok => SecurityOutcomes.Success,
            AssistantToolOutcome.Denied => SecurityOutcomes.Denied,
            _ => SecurityOutcomes.Failure,
        };
        await securityLog.RecordAsync(SecurityEventTypes.McpToolCall, AssistantToolbox.Clip(name, 60), outcome,
            $"Arguments: {AssistantToolbox.Clip(argumentsJson, 400)}; outcome: {result.Outcome}", cancellationToken: cancellationToken);

        var json = JsonSerializer.Serialize(result.Data, AssistantPrompts.DataJson);
        var max = options.Value.MaxToolResultChars;
        if (json.Length > max)
        {
            json = json[..max] + " …(cut: the result was too long; ask a narrower question)";
        }

        return new AssistantToolCallResult(result.Outcome != AssistantToolOutcome.Ok, result.Summary, json, result.Citations);
    }
}
