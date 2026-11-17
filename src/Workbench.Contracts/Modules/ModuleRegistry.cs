using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Licensing;

namespace Workbench.Contracts.Modules;

/// <summary>
/// The modules the host loaded, as data: an immutable set with the lookups <c>ModuleMap</c>, <c>PermissionCatalog</c>,
/// <c>ApiKeyScopes</c> and <c>NavCatalog</c> add to their built-in tables. <see cref="Empty"/> is the product with nothing loaded,
/// which behaves exactly as before modules existed. A set only ever holds contributions that passed <see cref="Validate"/>.
/// </summary>
public sealed class ModuleSet
{
    public static ModuleSet Empty { get; } = new([]);

    private readonly Dictionary<string, string> _apiSegments;
    private readonly List<(string Prefix, string Module)> _permissionPrefixes;
    private readonly Dictionary<string, string> _navSections;
    private readonly Dictionary<string, string> _navGroups;
    private readonly Dictionary<string, string> _navPages;
    private readonly Dictionary<Type, string> _services;

    private ModuleSet(IReadOnlyList<ModuleContribution> modules)
    {
        Modules = modules;
        _apiSegments = new(StringComparer.OrdinalIgnoreCase);
        _permissionPrefixes = [];
        _navSections = new(StringComparer.Ordinal);
        _navGroups = new(StringComparer.Ordinal);
        _navPages = new(StringComparer.OrdinalIgnoreCase);
        _services = [];
        foreach (var m in modules)
        {
            foreach (var s in m.ApiSegments) _apiSegments[s] = m.ModuleId;
            foreach (var p in m.PermissionPrefixes) _permissionPrefixes.Add((p, m.ModuleId));
            foreach (var s in m.NavSections) _navSections[s] = m.ModuleId;
            foreach (var g in m.NavGroups) _navGroups[g] = m.ModuleId;
            foreach (var p in m.NavPages.Concat(m.NavEntries.Select(e => e.Href))) _navPages[p] = m.ModuleId;
            foreach (var t in m.ServiceInterfaces) _services[t] = m.ModuleId;
        }
    }

    public IReadOnlyList<ModuleContribution> Modules { get; }

    public bool IsEmpty => Modules.Count == 0;

    public string? ForApiSegment(string segment) => _apiSegments.GetValueOrDefault(segment);

    public string? ForService(Type service) => _services.GetValueOrDefault(service);

    public IReadOnlyCollection<Type> ServiceInterfaces => _services.Keys;

    public string? ForNavPage(string href) => _navPages.GetValueOrDefault(href);

    public string? ForNavGroup(string sectionAndGroup) => _navGroups.GetValueOrDefault(sectionAndGroup);

    public string? ForNavSection(string section) => _navSections.GetValueOrDefault(section);

    /// <summary>The module of the longest permission prefix matching <paramref name="key"/>, with that prefix's length (-1 when none).</summary>
    public (string? Module, int PrefixLength) ForPermission(string key)
    {
        string? best = null;
        var length = -1;
        foreach (var (prefix, module) in _permissionPrefixes)
        {
            if (prefix.Length > length && key.StartsWith(prefix, StringComparison.Ordinal))
            {
                (best, length) = (module, prefix.Length);
            }
        }

        return (best, length);
    }

    /// <summary>A new set with <paramref name="contribution"/> added. Call <see cref="Validate"/> first.</summary>
    public ModuleSet With(ModuleContribution contribution) => new([.. Modules, contribution]);

    /// <summary>
    /// Why <paramref name="candidate"/> can't join this set (and the built-in product): every row it adds must be new, every permission
    /// and menu entry must resolve to the module itself, and an API key area may only open segments the module owns. Empty means it can.
    /// </summary>
    /// <param name="builtInNavSections">The menu sections the product has (the contracts project can't see the menu); null skips the menu checks that need them.</param>
    /// <param name="builtInNavHrefs">The pages the menu already has.</param>
    public IReadOnlyList<string> Validate(ModuleContribution candidate, IReadOnlyCollection<string>? builtInNavSections = null, IReadOnlyCollection<string>? builtInNavHrefs = null)
    {
        var problems = new List<string>();
        var id = candidate.ModuleId;
        if (id == ModuleKeys.Core || !KnownIds.Contains(id))
        {
            problems.Add($"'{id}' is not a license module a plugin can belong to (core never is).");
        }

        foreach (var segment in candidate.ApiSegments)
        {
            if (ModuleMap.ApiSegments.ContainsKey(segment) || _apiSegments.ContainsKey(segment))
            {
                problems.Add($"API path /{segment} already belongs to a module.");
            }
        }

        foreach (var prefix in candidate.PermissionPrefixes)
        {
            if (ModuleMap.PermissionPrefixes.ContainsKey(prefix) || _permissionPrefixes.Any(p => p.Prefix == prefix))
            {
                problems.Add($"Permission prefix '{prefix}' already belongs to a module.");
            }
        }

        var areas = PermissionCatalog.BuiltInAreas.Concat(Modules.SelectMany(m => m.PermissionAreas)).Concat(candidate.PermissionAreas).ToHashSet();
        foreach (var permission in candidate.Permissions)
        {
            var length = candidate.PermissionPrefixes.Where(p => permission.Key.StartsWith(p, StringComparison.Ordinal)).Select(p => p.Length).DefaultIfEmpty(-1).Max();
            if (length < 0)
            {
                problems.Add($"Permission {permission.Key} matches none of the module's permission prefixes.");
            }
            else if (ModuleMap.PermissionPrefixes.Keys.Any(p => p.Length > length && permission.Key.StartsWith(p, StringComparison.Ordinal)))
            {
                problems.Add($"Permission {permission.Key} would belong to a built-in module (a longer built-in prefix matches).");
            }

            if (PermissionCatalog.BuiltIn.Any(p => p.Key == permission.Key) || Modules.Any(m => m.Permissions.Any(p => p.Key == permission.Key)))
            {
                problems.Add($"Permission {permission.Key} already exists.");
            }

            if (!areas.Contains(permission.Area))
            {
                problems.Add($"Permission {permission.Key} names the area '{permission.Area}', which is not a permission area.");
            }
        }

        var sections = (builtInNavSections ?? []).Concat(Modules.SelectMany(m => m.NewNavSections.Select(s => s.Key))).ToHashSet(StringComparer.Ordinal);
        foreach (var section in candidate.NewNavSections)
        {
            if (!sections.Add(section.Key))
            {
                problems.Add($"Menu section '{section.Key}' already exists.");
            }
        }

        var hrefs = Modules.SelectMany(m => m.NavEntries.Select(e => e.Href)).Concat(ModuleMap.NavPages.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in candidate.NavEntries)
        {
            if (builtInNavSections is not null && !sections.Contains(entry.Section))
            {
                problems.Add($"Menu entry '{entry.Title}' is in section '{entry.Section}', which does not exist.");
            }

            if (hrefs.Contains(entry.Href) || (builtInNavHrefs ?? []).Contains(entry.Href, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"Menu entry '{entry.Title}': the page '{entry.Href}' is already in the menu.");
            }

            if (entry.Permissions.Any(p => !PermissionCatalog.BuiltIn.Any(b => b.Key == p) && !candidate.Permissions.Any(c => c.Key == p) && !Modules.Any(m => m.Permissions.Any(c => c.Key == p))))
            {
                problems.Add($"Menu entry '{entry.Title}' names a permission that doesn't exist.");
            }
        }

        foreach (var service in candidate.ServiceInterfaces)
        {
            if (ModuleMap.ForService(service) is not null || _services.ContainsKey(service))
            {
                problems.Add($"Service {service.FullName} already belongs to a module.");
            }
        }

        var owned = candidate.ApiSegments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var areaKeys = ApiKeyScopes.BuiltInAreas.Select(a => a.Key).Concat(Modules.SelectMany(m => m.ApiKeyAreas.Select(a => a.Key))).ToHashSet(StringComparer.Ordinal);
        foreach (var area in candidate.ApiKeyAreas)
        {
            if (!areaKeys.Add(area.Key))
            {
                problems.Add($"API key area '{area.Key}' already exists.");
            }
        }

        foreach (var segment in candidate.ApiKeyAreas.SelectMany(a => a.PathSegments).Concat(candidate.ApiKeySegments.Values.SelectMany(v => v)))
        {
            if (!owned.Contains(segment))
            {
                problems.Add($"API key areas may only open the module's own API paths; /{segment} is not one of them.");
            }
        }

        foreach (var key in candidate.ApiKeySegments.Keys.Where(k => !areaKeys.Contains(k)))
        {
            problems.Add($"API key segments name the area '{key}', which does not exist.");
        }

        return problems.Distinct().ToList();
    }

    private static readonly HashSet<string> KnownIds = typeof(ModuleKeys).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();
}

/// <summary>
/// The module set in force for this process. The host sets it once while it starts (<c>ModuleHost.Load</c>); everything that reads
/// <c>ModuleMap</c>, the permission catalog, API key scopes or the menu then sees the built-in tables plus the set's rows.
/// Tests that load a module use <see cref="Use"/> and dispose it, so they never leak into other tests.
/// </summary>
public static class ModuleRegistry
{
    private static ModuleSet _current = ModuleSet.Empty;

    public static ModuleSet Current => Volatile.Read(ref _current);

    public static void Set(ModuleSet set) => Volatile.Write(ref _current, set);

    /// <summary>Makes <paramref name="set"/> the current set until the returned handle is disposed, which restores the one before.</summary>
    public static IDisposable Use(ModuleSet set)
    {
        var previous = Current;
        Set(set);
        return new Restore(previous);
    }

    private sealed class Restore(ModuleSet previous) : IDisposable
    {
        public void Dispose() => Set(previous);
    }
}
