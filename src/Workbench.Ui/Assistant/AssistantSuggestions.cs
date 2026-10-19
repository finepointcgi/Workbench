using System.Text.RegularExpressions;

namespace Workbench.Ui.Assistant;

/// <summary>Suggested questions for the page the panel was opened on. Pure, so it's easy to test.</summary>
public static partial class AssistantSuggestions
{
    public static IReadOnlyList<string> For(string? path)
    {
        var p = (path ?? "").Split('?', '#')[0].Trim('/').ToLowerInvariant();
        if (ProjectPage().IsMatch(p))
        {
            if (p.EndsWith("/costs", StringComparison.Ordinal) || p.EndsWith("/odcs", StringComparison.Ordinal))
            {
                return ["Show unallowable costs on this project", "Explain this project's variance", "What's driving cost on this project?"];
            }

            return ["Explain this project's variance", "Which ceilings or funding limits are at risk on this project?", "Is this project fully staffed?", "What changed in this project's EAC?"];
        }

        if (p.StartsWith("time", StringComparison.Ordinal) || p.StartsWith("approvals", StringComparison.Ordinal))
        {
            return ["Who hasn't submitted their timesheet?", "Which timesheets are flagged for review?", "What's waiting on my approval?"];
        }

        if (p.StartsWith("setup/contracts", StringComparison.Ordinal) || p.StartsWith("billing", StringComparison.Ordinal) || p.StartsWith("reports/standard/funding", StringComparison.Ordinal))
        {
            return ["Which CLINs will run out of funding this quarter?", "Which ceilings are close to their limit?"];
        }

        return ["Which CLINs will run out of funding this quarter?", "Who hasn't submitted their timesheet?", "What's waiting on my approval?", "Which timesheets are flagged for review?"];
    }

    [GeneratedRegex(@"^projects/[0-9a-f\-]{36}")]
    private static partial Regex ProjectPage();
}
