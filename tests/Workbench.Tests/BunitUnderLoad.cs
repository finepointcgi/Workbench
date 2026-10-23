using System.Runtime.CompilerServices;
using AngleSharp;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Workbench.Tests;

/// <summary>
/// bUnit's defaults assume an idle machine; the suite runs its classes in parallel, where a render can take seconds.
/// </summary>
internal static class BunitUnderLoad
{
    /// <summary>
    /// WaitForAssertion, WaitForState and WaitForElement give up after one second by default, which a debounced search or
    /// a load after a click can miss under parallel load. A wait returns as soon as its check passes, so a longer limit
    /// only changes how long a genuinely failing test takes to fail.
    /// </summary>
    [ModuleInitializer]
    internal static void WaitLongerForRenders() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(15);
}

/// <summary>
/// The suite's Click, MouseDown, Change, Input, KeyDown and Submit. bUnit's queue the event on the renderer's dispatcher and return at
/// once, so while another render holds the dispatcher (a component's async continuation, common under parallel load) the
/// handler runs after the test's next line, and an assertion on what the handler did sees nothing yet. These shadow bUnit's
/// (extension methods in Workbench.Tests are found before those imported by <c>using Bunit;</c>) and return once the
/// event has been dispatched: the handler has run up to its first incomplete await, the point bUnit's reach when the
/// dispatcher is free. They don't wait for the whole handler, so a click that opens a dialog and awaits it still returns.
/// An event whose handler throws fails the test here instead of being lost.
/// </summary>
internal static class DispatchedEvents
{
    public static void Click(this IElement element, MouseEventArgs? eventArgs = null) => Dispatched(element, e => e.ClickAsync(eventArgs));

    public static void DoubleClick(this IElement element, MouseEventArgs? eventArgs = null) => Dispatched(element, e => e.DoubleClickAsync(eventArgs));

    public static void MouseDown(this IElement element, MouseEventArgs? eventArgs = null) => Dispatched(element, e => e.MouseDownAsync(eventArgs));

    public static void Change<T>(this IElement element, T value) => Dispatched(element, e => e.ChangeAsync(value));

    public static void Change(this IElement element, ChangeEventArgs? eventArgs = null) => Dispatched(element, e => e.ChangeAsync(eventArgs));

    public static void Input<T>(this IElement element, T value) => Dispatched(element, e => e.InputAsync(value));

    public static void Input(this IElement element, ChangeEventArgs? eventArgs = null) => Dispatched(element, e => e.InputAsync(eventArgs));

    public static void KeyDown(this IElement element, Key key, bool repeat = false, string? type = null) => Dispatched(element, e => e.KeyDownAsync(key, repeat, type));

    public static void KeyDown(this IElement element, KeyboardEventArgs? eventArgs = null) => Dispatched(element, e => e.KeyDownAsync(eventArgs));

    public static void Submit(this IElement element, EventArgs? eventArgs = null) => Dispatched(element, e => e.SubmitAsync(eventArgs));

    /// <summary>
    /// Raises the event, then waits for a no-op queued behind it: the dispatcher runs its work in order, so the no-op
    /// runs once the event's handler has yielded. On the dispatcher itself (inside a WaitFor check) the event already ran.
    /// The dispatcher is looked up first: a found element re-finds itself by selector, and the event may remove it.
    /// </summary>
    private static void Dispatched(IElement element, Func<IElement, Task> raise)
    {
        var dispatcher = element.Owner!.Context.GetService<BunitContext>()!.Renderer.Dispatcher;
        var dispatch = raise(element);
        if (!dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => { }).GetAwaiter().GetResult();
        }

        if (dispatch.IsFaulted)
        {
            dispatch.GetAwaiter().GetResult();
        }
    }
}
