namespace Workbench.Contracts.Modules;

/// <summary>
/// A component a license module puts into a named place on a built-in page (a <c>UiSlot</c> with the same <see cref="Slot"/> name renders it),
/// so core pages never reference a module's components. The module registers instances as singletons in <c>ConfigureServices</c>. The
/// component must declare every parameter the slot passes. The slots the product offers are listed in docs/operations/module-plugins.md.
/// </summary>
/// <param name="Slot">The slot's name, such as <c>timesheet.summary</c>.</param>
/// <param name="Component">A Blazor component type (<c>IComponent</c>).</param>
/// <param name="Order">Components in one slot render in this order.</param>
public sealed record UiSlotContribution(string Slot, Type Component, int Order = 0);
