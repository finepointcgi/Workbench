using Workbench.Application.Collaboration;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Collaboration;

namespace Workbench.Application.Automation;

/// <summary>
/// Tracks, per async flow, how deep in a rule chain the current change is. Depth 0 is a person's change; each rule
/// that runs adds one. While it is above 0, <see cref="AutomationAwareCurrentUser"/> reports the automation actor.
/// </summary>
public static class AutomationContext
{
    private static readonly AsyncLocal<State?> Current = new();

    public static int Depth => Current.Value?.Depth ?? 0;

    public static bool IsActive => Depth > 0;

    /// <summary>
    /// Who changes made now are recorded as: <see cref="RuleFields.AutomationActor"/> with the running rule and its
    /// author, e.g. <c>Workbench Automation (rule "Escalate", author pm@example.com)</c>. Null outside a rule.
    /// </summary>
    public static string? Actor => Current.Value is { Depth: > 0 } state ? state.Actor : null;

    /// <summary>Runs the rest of this flow one level deeper, as <paramref name="ruleName"/> by <paramref name="author"/>, until disposed.</summary>
    public static IDisposable Enter(string? ruleName = null, string? author = null)
    {
        var previous = Current.Value;
        Current.Value = new State((previous?.Depth ?? 0) + 1, Describe(ruleName, author));
        return new Restore(previous);
    }

    /// <summary>
    /// Runs the rest of this flow as a person's change again until disposed. Used around live-update handlers:
    /// a Blazor circuit that captures this flow (timers, InvokeAsync) must not keep acting as the automation.
    /// </summary>
    public static IDisposable Suspend()
    {
        var previous = Current.Value;
        Current.Value = null;
        return new Restore(previous);
    }

    internal static string Describe(string? ruleName, string? author)
    {
        if (string.IsNullOrWhiteSpace(ruleName))
        {
            return RuleFields.AutomationActor;
        }

        // Actor columns hold 320 characters; emails are at most 254.
        var name = ruleName.Trim();
        name = name.Length > 50 ? name[..49] + "…" : name;
        var by = string.IsNullOrWhiteSpace(author) ? "" : $", author {author.Trim().ToLowerInvariant()}";
        var actor = $"{RuleFields.AutomationActor} (rule \"{name}\"{by})";
        return actor.Length > 320 ? actor[..320] : actor;
    }

    private sealed record State(int Depth, string Actor);

    private sealed class Restore(State? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>
/// Decorates <see cref="ICurrentUser"/> so changes made by rules are attributed to the automation, naming the rule and
/// its author (<see cref="AutomationContext.Actor"/>: activity, comments, "you didn't make this change" checks).
/// Permission checks don't use this name: date rules are checked as their author (see
/// <see cref="Identity.RuleAuthorExecutionContext"/>). Registered by <c>AddWorkbenchAutomationHosting</c>
/// after the real <see cref="ICurrentUser"/>.
/// </summary>
public sealed class AutomationAwareCurrentUser(ICurrentUser inner) : ICurrentUser
{
    public string Email => AutomationContext.Actor ?? inner.Email;
}

/// <summary>
/// Decorates <see cref="ISheetChangePublisher"/> so subscribers (Blazor circuits, the SignalR relay) run outside
/// the automation context of the rule that caused the change.
/// </summary>
public sealed class AutomationNeutralSheetChangePublisher(ISheetChangePublisher inner) : ISheetChangePublisher
{
    public async Task PublishAsync(SheetChange change)
    {
        using (AutomationContext.Suspend())
        {
            await inner.PublishAsync(change);
        }
    }
}
