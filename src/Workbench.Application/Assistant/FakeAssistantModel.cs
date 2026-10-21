using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Workbench.Contracts.Assistant;

namespace Workbench.Application.Assistant;

/// <summary>
/// The development and test provider: no network, no randomness. It picks tools from keywords in the question (and the
/// page the question was asked on), then answers by listing each tool result's <c>summary</c>, or its error. Narrative
/// and explanation requests get the deterministic draft back. It never acts on text found in tool results.
/// </summary>
public sealed partial class FakeAssistantModel : IAssistantModelClient
{
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var last = request.Messages.LastOrDefault() ?? throw new ArgumentException("No messages.", nameof(request));
        var lastText = string.Concat(last.Content.OfType<ModelText>().Select(t => t.Text));
        List<ModelContent> content;
        if (request.JsonSchema is not null)
        {
            content = [new ModelText(Between(lastText, "<draft-json>", "</draft-json>") ?? "{}")];
        }
        else if (Between(lastText, "<flag-json>", "</flag-json>") is { } flag)
        {
            content = [new ModelText(Explain(flag))];
        }
        else if (last.Content.OfType<ModelToolResult>().ToList() is { Count: > 0 } results)
        {
            content = [new ModelText(Answer(results))];
        }
        else
        {
            var calls = Plan(lastText, request.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal));
            content = calls.Count == 0
                ? [new ModelText("I can answer questions about projects, budgets, earned value, funding, ceilings, timesheets, approvals, costs and sheets. Try “Why is project 1001 over budget?”")]
                : [new ModelText("Let me look that up. "), .. calls];
        }

        foreach (var text in content.OfType<ModelText>())
        {
            foreach (var chunk in Chunks(text.Text, 24))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new ModelTextDelta(chunk);
            }
        }

        var inputChars = request.System.Length + request.Messages.Sum(m => m.Content.Sum(Length)) + request.Tools.Sum(t => t.Description.Length + t.InputSchema.ToJsonString().Length);
        var outputChars = content.Sum(Length);
        var stop = content.OfType<ModelToolUse>().Any() ? "tool_use" : "end_turn";
        yield return new ModelResponse(content, stop, new AssistantTokenUsage(Math.Max(1, inputChars / 4), Math.Max(1, outputChars / 4), 0, 0));
    }

    private static int Length(ModelContent c) => c switch
    {
        ModelText t => t.Text.Length,
        ModelToolUse u => u.Name.Length + u.InputJson.Length,
        ModelToolResult r => r.Content.Length,
        ModelFile f => f.Base64Data.Length / 8,
        _ => 0,
    };

    private static List<ModelContent> Plan(string question, IReadOnlySet<string> available)
    {
        var context = Between(question, "<page-context>", "</page-context>") ?? "";
        var asked = Between(question, "<question>", "</question>") ?? question;
        var q = asked.ToLowerInvariant();
        var project = ProjectCode().Match(asked) is { Success: true } m ? m.Groups[1].Value
            : ContextProject().Match(context) is { Success: true } c ? c.Groups[1].Value.Trim()
            : null;
        var contract = ContractNumber().Match(asked) is { Success: true } k ? k.Groups[1].Value.TrimEnd('.', '…') : null;

        var calls = new List<(string Name, object Args)>();
        void Add(string name, object args)
        {
            if (available.Contains(name) && calls.All(x => x.Name != name))
            {
                calls.Add((name, args));
            }
        }

        if (q.Contains("timesheet") && (q.Contains("submit") || q.Contains("missing") || q.Contains("hasn")))
        {
            Add("get_timesheet_status", new { });
        }

        if (q.Contains("flag") || q.Contains("odd") || q.Contains("anomal") || q.Contains("unusual"))
        {
            Add("get_timesheet_flags", new { });
        }

        if (q.Contains("unallowable"))
        {
            Add("list_unallowable_costs", contract is not null ? new { contract } : project is not null ? new { project } : new { });
        }

        if (q.Contains("clin") || q.Contains("funding") || q.Contains("run out"))
        {
            Add("list_clin_funding", contract is not null ? new { contract, within_days = 92 } : new { within_days = 92 });
        }

        if (q.Contains("ceiling"))
        {
            Add("get_ceilings", project is not null ? new { project } : contract is not null ? new { contract } : new { });
        }

        if (q.Contains("approv") || q.Contains("waiting"))
        {
            Add("list_pending_approvals", new { });
        }

        if (project is not null && (q.Contains("staff") || q.Contains("allocat")))
        {
            Add("get_project_staffing", new { project });
        }

        if (project is not null && q.Contains("ledger"))
        {
            Add("get_project_cost_ledger", new { project });
        }

        if (q.Contains("sheet") && !q.Contains("timesheet"))
        {
            Add("list_sheets", new { });
        }

        if (project is not null && (calls.Count == 0 || q.Contains("budget") || q.Contains("variance") || q.Contains("eac") || q.Contains("cost") || q.Contains("financ")))
        {
            Add("get_project_financials", new { project });
            Add("get_earned_value", new { project });
        }

        if (calls.Count == 0 && (q.Contains("project") || q.Contains("list")))
        {
            Add("list_projects", new { });
        }

        return calls.Select((c, i) => (ModelContent)new ModelToolUse($"fake_{i + 1}", c.Name, JsonSerializer.Serialize(c.Args))).ToList();
    }

    private static string Answer(IReadOnlyList<ModelToolResult> results)
    {
        var lines = new List<string>();
        foreach (var result in results)
        {
            var json = Between(result.Content, ">", "</workbench-data>") ?? result.Content;
            JsonNode? node = null;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
            }

            if (node?["error"] is { } error)
            {
                lines.Add($"- I couldn't read that: {node["message"]?.GetValue<string>() ?? error.ToString()}");
            }
            else if (node?["summary"]?.GetValue<string>() is { Length: > 0 } summary)
            {
                lines.Add($"- {summary}");
            }
            else
            {
                lines.Add("- (no data)");
            }
        }

        var answer = new StringBuilder("Here's what Workbench shows:\n");
        answer.AppendJoin('\n', lines);
        if (lines.Any(l => !l.StartsWith("- I couldn't", StringComparison.Ordinal)))
        {
            answer.Append("\n\nThe pages listed under this answer show the figures I used.");
        }
        return answer.ToString();
    }

    private static string Explain(string flagJson)
    {
        try
        {
            var node = JsonNode.Parse(flagJson);
            var title = node?["title"]?.GetValue<string>() ?? "This flag";
            var detail = node?["detail"]?.GetValue<string>() ?? "";
            return $"{title}: {detail} This is a prompt to look closer, not a finding of wrongdoing; check it with the employee before deciding.";
        }
        catch (JsonException)
        {
            return "This flag marks something unusual on the timesheet. Check it with the employee before deciding.";
        }
    }

    internal static string? Between(string text, string start, string end)
    {
        var i = text.IndexOf(start, StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }

        i += start.Length;
        var j = text.LastIndexOf(end, StringComparison.Ordinal);
        return j < i ? null : text[i..j].Trim();
    }

    private static IEnumerable<string> Chunks(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }

    [GeneratedRegex(@"\b(\d{4}(?:[.\-]\d+)*)\b")]
    private static partial Regex ProjectCode();

    [GeneratedRegex(@"project:\s*([^\n;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContextProject();

    [GeneratedRegex(@"\bcontract\s+([A-Z0-9][A-Z0-9\-]{2,}(?:…|\.\.\.)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ContractNumber();
}
