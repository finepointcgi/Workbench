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
    private static readonly AsyncLocal<int> CurrentDepth = new();

    public static int Depth => CurrentDepth.Value;

    public static bool IsActive => CurrentDepth.Value > 0;

    /// <summary>Runs the rest of this flow one level deeper until the result is disposed.</summary>
    public static IDisposable Enter()
    {
        var previous = CurrentDepth.Value;
        CurrentDepth.Value = previous + 1;
        return new Restore(previous);
    }

    /// <summary>
    /// Runs the rest of this flow as a person's change again until disposed. Used around live-update handlers:
    /// a Blazor circuit that captures this flow (timers, InvokeAsync) must not keep acting as the automation.
    /// </summary>
    public static IDisposable Suspend()
    {
        var previous = CurrentDepth.Value;
        CurrentDepth.Value = 0;
        return new Restore(previous);
    }

    private sealed class Restore(int previous) : IDisposable
    {
        public void Dispose() => CurrentDepth.Value = previous;
    }
}

/// <summary>
/// Decorates <see cref="ICurrentUser"/> so changes made by rules are attributed to <see cref="RuleFields.AutomationActor"/>
/// (activity, comments, "you didn't make this change" checks). Registered by <c>AddWorkbenchAutomationHosting</c>
/// after the real <see cref="ICurrentUser"/>.
/// </summary>
public sealed class AutomationAwareCurrentUser(ICurrentUser inner) : ICurrentUser
{
    public string Email => AutomationContext.IsActive ? RuleFields.AutomationActor : inner.Email;
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
