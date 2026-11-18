using System.Reflection;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;

namespace Workbench.Contracts.Modules;

/// <summary>A menu section a module adds (a section of its own, such as a module that has a whole area of pages).</summary>
public sealed record ModuleNavSection(string Key, string Title, string Icon, string? Href = null);

/// <summary>
/// A page a module puts in the menu and the command palette. Data only (the icon is the MudBlazor icon string), so the contract
/// assembly needs no UI reference. <see cref="Permissions"/> are the permissions that reveal it (any one); none means everyone signed in.
/// <see cref="AlsoWhenFlag"/> names a relationship the module reports through an <c>INavAccessFlag</c> (for example "manages an opportunity"):
/// a user who has it sees the page too, without a permission. <see cref="Note"/> says so in the role help.
/// </summary>
public sealed record ModuleNavEntry(
    string Section,
    string? Group,
    string Title,
    string Href,
    string Icon,
    IReadOnlyList<string> Permissions,
    bool MatchAll = false,
    string? Detail = null,
    string? Keywords = null,
    bool InMenu = true,
    bool Government = false,
    string? AlsoWhenFlag = null,
    string? Note = null);

/// <summary>
/// A change on one of the module's service interfaces that needs an edition feature (a <c>Feature</c> name from Workbench.Domain, such as
/// <c>IncurredCost</c>) on top of the module being licensed. <see cref="Methods"/> are method names of <see cref="Service"/>.
/// </summary>
public sealed record ModuleFeatureGate(Type Service, string Feature, IReadOnlyList<string> Methods);

/// <summary>
/// <b>Everything a module says about itself that the rest of the product reads as data</b>: the rows it adds to <c>ModuleMap</c>,
/// its menu entries, permissions, API key areas and the assemblies that hold its routable pages. The behavior (services, endpoints,
/// model) is on <c>IWorkbenchModule</c> (Workbench.Modules.Abstractions). Every list defaults to empty.
/// </summary>
public sealed record ModuleContribution
{
    /// <summary>The license module id (<c>ModuleKeys</c>/<c>ModuleIds</c>). It must be a module the catalog knows; the license key decides whether it loads.</summary>
    public required string ModuleId { get; init; }

    // ── ModuleMap rows ─────────────────────────────────────────────────────────────────────────────

    /// <summary>First path segments after <c>/api/v1/</c> that belong to the module. Each must be new: a segment already mapped is a conflict.</summary>
    public IReadOnlyList<string> ApiSegments { get; init; } = [];

    /// <summary>Permission key prefixes (longest match wins, like <c>ModuleMap.PermissionPrefixes</c>). Every permission the module adds must match one.</summary>
    public IReadOnlyList<string> PermissionPrefixes { get; init; } = [];

    /// <summary>Menu sections whose every entry belongs to the module (a section the module adds, or an existing one that is wholly its own).</summary>
    public IReadOnlyList<string> NavSections { get; init; } = [];

    /// <summary>Groups within a section, as <c>section/group</c>.</summary>
    public IReadOnlyList<string> NavGroups { get; init; } = [];

    /// <summary>Single pages by href. The hrefs of <see cref="NavEntries"/> count automatically, so this is only for pages the module does not list itself.</summary>
    public IReadOnlyList<string> NavPages { get; init; } = [];

    /// <summary>
    /// The module's service interfaces (public interfaces named <c>I…Service</c>). The edition gates wrap every change on each, like the
    /// in-repo services. <see cref="ServiceInterfacesIn"/> collects them from an assembly.
    /// </summary>
    public IReadOnlyList<Type> ServiceInterfaces { get; init; } = [];

    /// <summary>Edition features that gate particular changes on the module's services, on top of the module gate every change already gets.</summary>
    public IReadOnlyList<ModuleFeatureGate> FeatureGates { get; init; } = [];

    // ── Menu, permissions, API keys ────────────────────────────────────────────────────────────────

    public IReadOnlyList<ModuleNavSection> NewNavSections { get; init; } = [];

    public IReadOnlyList<ModuleNavEntry> NavEntries { get; init; } = [];

    /// <summary>Permissions the module adds to the catalog (Admin › Roles lists them; their <c>GrantedTo</c> roles hold them).</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; init; } = [];

    /// <summary>Permission areas the module adds (the group heading in Admin › Roles). A permission may also use an existing area.</summary>
    public IReadOnlyList<string> PermissionAreas { get; init; } = [];

    /// <summary>API key areas the module adds. Segments of these areas are open to keys with the scope; unlisted paths stay closed to keys.</summary>
    public IReadOnlyList<ApiKeyAreaDto> ApiKeyAreas { get; init; } = [];

    /// <summary>Segments the module adds to an <i>existing</i> area, by the area's key (for example <c>time</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ApiKeySegments { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    // ── Catalog rows ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rows the module adds to the product's catalogs: custom lists, built-in forms (and their layouts), document numbering types,
    /// automation entities, integration events, approval document types, "+ New" actions, report sources, security event names and home
    /// widgets (<see cref="ModuleCatalogs"/>). Every row implements <see cref="IModuleRow"/>; its key must be new in its catalog.
    /// </summary>
    public IReadOnlyList<IModuleRow> Rows { get; init; } = [];

    // ── Blazor ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Assemblies with the module's routable pages and components (the router's and the app's <c>AdditionalAssemblies</c>).</summary>
    public IReadOnlyList<Assembly> RoutableAssemblies { get; init; } = [];

    /// <summary>The public service interfaces (<c>I…Service</c>, not generic) an assembly exports; a convenient value for <see cref="ServiceInterfaces"/>.</summary>
    public static IReadOnlyList<Type> ServiceInterfacesIn(Assembly assembly) =>
        assembly.GetExportedTypes()
            .Where(t => t is { IsInterface: true, IsGenericType: false } && t.Name.StartsWith('I') && t.Name.EndsWith("Service", StringComparison.Ordinal))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
}
