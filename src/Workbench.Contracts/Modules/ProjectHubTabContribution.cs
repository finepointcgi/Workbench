using Workbench.Contracts.Projects;

namespace Workbench.Contracts.Modules;

/// <summary>
/// A tab a license module adds to the project page (<c>/projects/{id}/{slug}</c>). The module registers instances as singletons in
/// <c>ConfigureServices</c>; the project page lists a contribution only when <see cref="Allows"/> says the viewer may open it, and renders
/// <see cref="Component"/> as the tab's content. With the module absent the tab does not exist: its route falls back to the Overview.
/// </summary>
/// <param name="Slug">The URL segment, such as <c>evm</c>.</param>
/// <param name="Label">The tab's text.</param>
/// <param name="Component">A Blazor component with the parameters <c>ProjectId</c> (<see cref="Guid"/>), <c>SheetId</c> (<see cref="Guid"/>?) and <c>Changed</c> (an <c>EventCallback</c>, raised when the module changed something the page shows).</param>
/// <param name="Allows">Whether the viewer's tab flags (<see cref="ProjectHubTabsDto"/>) open it.</param>
/// <param name="Order">Position among the tabs; the built-in tabs are Overview 10, Plan 20, Budget 30, Financials 40, Costs 50, Schedule health 80 and Setup 200.</param>
/// <param name="Occasional">Kept under "More" until opened.</param>
public sealed record ProjectHubTabContribution(string Slug, string Label, Type Component, Func<ProjectHubTabsDto, bool> Allows, int Order, bool Occasional = false);
