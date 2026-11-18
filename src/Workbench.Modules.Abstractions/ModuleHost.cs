using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workbench.Contracts.Modules;

namespace Workbench.Plugins;

public enum ModuleLoadStatus
{
    /// <summary>Loaded and registered.</summary>
    Loaded,

    /// <summary>The license does not include the module. Its assembly was not loaded and nothing of it was registered.</summary>
    NotLicensed,

    /// <summary>The manifest names an assembly that is not there.</summary>
    MissingAssembly,

    /// <summary>A dependency (<see cref="IWorkbenchModule.DependsOn"/>) is not loaded.</summary>
    MissingDependency,

    /// <summary>The manifest, the module type, the contract version or the module's contribution is wrong.</summary>
    Invalid,

    /// <summary>The module threw while loading or registering.</summary>
    Failed,
}

/// <summary>What happened to one module folder, for the log and Setup.</summary>
public sealed record ModuleLoadResult(string Id, string Folder, ModuleLoadStatus Status, string Detail, string? Version = null);

/// <summary>Where the host looks for modules and how it decides which are licensed.</summary>
/// <param name="Folder">The modules folder: one subfolder per module, each with a <c>module.json</c>.</param>
/// <param name="IsLicensed">True when the license includes the module id. Asked <i>before</i> a module's assembly is loaded.</param>
/// <param name="Required">Ids that must load; the host refuses to start when one does not (use for modules the deployment can't run without).</param>
/// <param name="BuiltInNavSections">The menu sections the product has; checked against a module's menu entries. Null skips that check.</param>
/// <param name="BuiltInNavHrefs">The pages the menu already has.</param>
/// <param name="BuiltInRowKeys">The keys (<c>catalog/key</c>) of the built-in catalog rows, so a module can't add a row that already exists. Null skips that check.</param>
public sealed record ModuleHostOptions(
    string Folder,
    Func<string, bool> IsLicensed,
    IReadOnlyCollection<string>? Required = null,
    IReadOnlyCollection<string>? BuiltInNavSections = null,
    IReadOnlyCollection<string>? BuiltInNavHrefs = null,
    IReadOnlyCollection<string>? BuiltInRowKeys = null)
{
    public const string FolderKey = "Modules:Folder";
    public const string RequiredKey = "Modules:Required";
    public const string DefaultFolder = "modules";

    /// <summary>The folder from <c>Modules:Folder</c> (default <c>modules</c>, relative to the application's folder) and the ids from <c>Modules:Required</c>.</summary>
    public static ModuleHostOptions From(IConfiguration configuration, Func<string, bool> isLicensed,
        IReadOnlyCollection<string>? navSections = null, IReadOnlyCollection<string>? navHrefs = null, IReadOnlyCollection<string>? rowKeys = null)
    {
        var folder = configuration[FolderKey] is { Length: > 0 } configured ? configured : DefaultFolder;
        return new ModuleHostOptions(Path.GetFullPath(folder, AppContext.BaseDirectory), isLicensed,
            configuration.GetSection(RequiredKey).Get<string[]>() ?? [], navSections, navHrefs, rowKeys);
    }
}

/// <summary>
/// Finds, checks and loads the modules in the modules folder, and hands what they contribute to the rest of the host.
/// Everything is decided in this order, and a module that fails any step is skipped with a reason (the rest still load):
/// <list type="number">
/// <item>The folder's <c>module.json</c> (<c>{"id":"govcon","assembly":"Acme.GovCon.dll"}</c>) names the module. <b>The license is asked first</b>: an unlicensed module's assembly is never loaded.</item>
/// <item>The assembly is loaded into the default context; the folder's other files resolve its private dependencies. Host assemblies are never replaced.</item>
/// <item>Its single <see cref="IWorkbenchModule"/> must carry the manifest's id and a matching <see cref="ModuleContract.Version"/>.</item>
/// <item>Its <see cref="IWorkbenchModule.Describe"/> must not clash with anything built in or loaded earlier (<c>ModuleSet.Validate</c>).</item>
/// </list>
/// Load order is dependency order, then id. The accepted modules become <see cref="ModuleRegistry.Current"/>.
/// </summary>
public sealed class ModuleHost
{
    public static ModuleHost Empty { get; } = new([], []);

    private static ModuleHost _current = Empty;

    /// <summary>The loaded modules of this process (empty until <see cref="Load"/> runs). Read by the EF model and the migrator.</summary>
    public static ModuleHost Current => Volatile.Read(ref _current);

    private readonly List<IWorkbenchModule> _modules;
    private readonly List<ModuleLoadResult> _results;
    private readonly Dictionary<string, string> _folders = new(StringComparer.Ordinal);

    private ModuleHost(List<IWorkbenchModule> modules, List<ModuleLoadResult> results)
    {
        _modules = modules;
        _results = results;
    }

    public IReadOnlyList<IWorkbenchModule> Modules => _modules;

    public IReadOnlyList<ModuleLoadResult> Results => _results;

    public bool Has(string moduleId) => _modules.Any(m => m.Id == moduleId);

    /// <summary>
    /// The static files of the loaded modules' pages: a module's <c>wwwroot</c> folder (next to its <c>module.json</c>) is served at
    /// <c>/_content/&lt;assembly name&gt;</c> for each assembly in its <see cref="ModuleContribution.RoutableAssemblies"/>, the path a Razor class
    /// library's files have in a project reference. (A host built with the library as a reference has those files from its own static web
    /// assets; a module loaded from a folder does not, so the host serves them from here.)
    /// </summary>
    public IReadOnlyList<(string RequestPath, string PhysicalPath)> StaticAssets()
    {
        var assets = new List<(string, string)>();
        foreach (var module in _modules)
        {
            if (!_folders.TryGetValue(module.Id, out var folder) || !Directory.Exists(Path.Combine(folder, "wwwroot")))
            {
                continue;
            }

            foreach (var assembly in module.Describe().RoutableAssemblies)
            {
                assets.Add(($"/_content/{assembly.GetName().Name}", Path.Combine(folder, "wwwroot")));
            }
        }

        return assets;
    }

    /// <summary>
    /// The scoped style sheets of the loaded modules' pages (<c>&lt;assembly name&gt;.bundle.scp.css</c> in the module's <c>wwwroot</c>, which the module's
    /// build copies there from the Razor class library's build output), as paths the host's pages link. A host built with the library as a reference gets
    /// that bundle from its own style sheet; a module loaded from a folder has to be linked.
    /// </summary>
    public IReadOnlyList<string> StyleSheets()
    {
        var sheets = new List<string>();
        foreach (var module in _modules)
        {
            if (!_folders.TryGetValue(module.Id, out var folder))
            {
                continue;
            }

            foreach (var assembly in module.Describe().RoutableAssemblies)
            {
                var name = assembly.GetName().Name;
                if (File.Exists(Path.Combine(folder, "wwwroot", $"{name}.bundle.scp.css")))
                {
                    sheets.Add($"_content/{name}/{name}.bundle.scp.css");
                }
            }
        }

        return sheets;
    }

    /// <summary>True when a loaded module adds entities to the EF model.</summary>
    public bool HasModel => _modules.Any(m => m.ModelAssemblies.Count > 0);

    /// <summary>Identifies the module set in the EF model cache: the model differs when the modules do.</summary>
    public string ModelKey => string.Join('|', _modules.Where(m => m.ModelAssemblies.Count > 0).Select(m => $"{m.Id}:{m.Version}"));

    /// <summary>Makes this host the process's (and its contributions the registry's). <see cref="Load"/> does this itself.</summary>
    public static IDisposable Use(ModuleHost host)
    {
        var previous = Current;
        var previousSet = ModuleRegistry.Current;
        Publish(host);
        return new Restore(previous, previousSet);
    }

    private static void Publish(ModuleHost host)
    {
        Volatile.Write(ref _current, host);
        ModuleRegistry.Set(host._modules.Aggregate(ModuleSet.Empty, (set, m) => set.With(m.Describe())));
    }

    private sealed class Restore(ModuleHost host, ModuleSet set) : IDisposable
    {
        public void Dispose()
        {
            Volatile.Write(ref _current, host);
            ModuleRegistry.Set(set);
        }
    }

    // ───────────────────────────────────────── Loading ─────────────────────────────────────────

    /// <summary>Loads the licensed, valid modules from <see cref="ModuleHostOptions.Folder"/> and publishes them. Never throws for a bad module; throws only when a <see cref="ModuleHostOptions.Required"/> module is missing.</summary>
    public static ModuleHost Load(ModuleHostOptions options)
    {
        // A module is checked against what is built in and what loaded before it in this call, not against the set an earlier Load
        // published (a process that loads twice, such as a test that starts several hosts, would otherwise find every module "already
        // taken" by itself). So the published set is cleared while loading and put back if loading throws.
        var previous = ModuleRegistry.Current;
        ModuleRegistry.Set(ModuleSet.Empty);
        try
        {
            return LoadCore(options);
        }
        catch
        {
            ModuleRegistry.Set(previous);
            throw;
        }
    }

    private static ModuleHost LoadCore(ModuleHostOptions options)
    {
        var results = new List<ModuleLoadResult>();
        var candidates = new List<Candidate>();
        if (Directory.Exists(options.Folder))
        {
            foreach (var folder in Directory.GetDirectories(options.Folder).Order(StringComparer.Ordinal))
            {
                var manifestPath = Path.Combine(folder, "module.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                if (!TryReadManifest(manifestPath, out var manifest, out var problem))
                {
                    results.Add(new ModuleLoadResult(Path.GetFileName(folder), folder, ModuleLoadStatus.Invalid, problem!));
                    continue;
                }

                // The license comes before the assembly: an unlicensed module is never loaded, so none of its code runs.
                if (!options.IsLicensed(manifest!.Id))
                {
                    results.Add(new ModuleLoadResult(manifest.Id, folder, ModuleLoadStatus.NotLicensed, "The license key does not include this module, so its assembly was not loaded."));
                    continue;
                }

                var assemblyPath = Path.GetFullPath(Path.Combine(folder, manifest.Assembly));
                if (!File.Exists(assemblyPath))
                {
                    results.Add(new ModuleLoadResult(manifest.Id, folder, ModuleLoadStatus.MissingAssembly, $"{manifest.Assembly} is not in {folder}."));
                    continue;
                }

                candidates.Add(new Candidate(manifest, folder, assemblyPath));
            }
        }

        var accepted = new List<IWorkbenchModule>();
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        var set = ModuleSet.Empty;
        foreach (var candidate in candidates)
        {
            DependencyResolver.AddFolder(candidate.Folder);
        }

        // Loaded in dependency order; a module whose dependency is missing is skipped. Two passes over the candidates cover chains of any length.
        var pending = candidates.OrderBy(c => c.Manifest.Id, StringComparer.Ordinal).ToList();
        var progress = true;
        while (pending.Count > 0 && progress)
        {
            progress = false;
            foreach (var candidate in pending.ToList())
            {
                IWorkbenchModule? module;
                try
                {
                    module = Instantiate(candidate, results);
                }
                catch (Exception ex)
                {
                    results.Add(new ModuleLoadResult(candidate.Manifest.Id, candidate.Folder, ModuleLoadStatus.Failed, $"Loading failed: {ex.GetType().Name}: {ex.Message}"));
                    pending.Remove(candidate);
                    progress = true;
                    continue;
                }

                if (module is null)
                {
                    pending.Remove(candidate);
                    progress = true;
                    continue;
                }

                var waiting = module.DependsOn.Where(d => accepted.All(a => a.Id != d)).ToList();
                if (waiting.Count > 0 && waiting.Any(d => pending.Any(p => p.Manifest.Id == d && p != candidate)))
                {
                    continue; // Its dependency may still load; try again on the next pass.
                }

                pending.Remove(candidate);
                progress = true;
                if (waiting.Count > 0)
                {
                    results.Add(new ModuleLoadResult(module.Id, candidate.Folder, ModuleLoadStatus.MissingDependency, $"Needs {string.Join(", ", waiting)}, which is not loaded."));
                    continue;
                }

                ModuleContribution contribution;
                try
                {
                    contribution = module.Describe();
                }
                catch (Exception ex)
                {
                    results.Add(new ModuleLoadResult(module.Id, candidate.Folder, ModuleLoadStatus.Failed, $"Describe() failed: {ex.GetType().Name}: {ex.Message}"));
                    continue;
                }

                var problems = contribution.ModuleId != module.Id
                    ? [$"Describe() says module '{contribution.ModuleId}', not '{module.Id}'."]
                    : set.Validate(contribution, options.BuiltInNavSections, options.BuiltInNavHrefs, options.BuiltInRowKeys);
                if (problems.Count > 0)
                {
                    results.Add(new ModuleLoadResult(module.Id, candidate.Folder, ModuleLoadStatus.Invalid, string.Join(" ", problems), module.Version));
                    continue;
                }

                set = set.With(contribution);
                accepted.Add(module);
                folders[module.Id] = candidate.Folder;
                results.Add(new ModuleLoadResult(module.Id, candidate.Folder, ModuleLoadStatus.Loaded, "Loaded.", module.Version));
            }
        }

        foreach (var left in pending)
        {
            results.Add(new ModuleLoadResult(left.Manifest.Id, left.Folder, ModuleLoadStatus.MissingDependency, "Its dependencies form a cycle or never loaded."));
        }

        var missing = (options.Required ?? []).Where(id => accepted.All(m => m.Id != id)).ToList();
        if (missing.Count > 0)
        {
            var why = missing.Select(id => results.FirstOrDefault(r => r.Id == id) is { } r ? $"{id}: {r.Status} ({r.Detail})" : $"{id}: no module.json for it in {options.Folder}");
            throw new InvalidOperationException($"Modules:Required lists modules that did not load: {string.Join("; ", why)}");
        }

        var host = new ModuleHost(accepted, results);
        foreach (var (id, folder) in folders)
        {
            host._folders[id] = folder;
        }

        Publish(host);
        return host;
    }

    private sealed record Candidate(ModuleManifest Manifest, string Folder, string AssemblyPath);

    private static IWorkbenchModule? Instantiate(Candidate candidate, List<ModuleLoadResult> results)
    {
        var assembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => string.Equals(a.Location, candidate.AssemblyPath, StringComparison.OrdinalIgnoreCase))
            ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate.AssemblyPath);
        var types = assembly.GetExportedTypes().Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IWorkbenchModule).IsAssignableFrom(t)).ToList();
        if (candidate.Manifest.Type is { } name)
        {
            types = types.Where(t => t.FullName == name).ToList();
        }

        if (types.Count != 1)
        {
            results.Add(new ModuleLoadResult(candidate.Manifest.Id, candidate.Folder, ModuleLoadStatus.Invalid,
                types.Count == 0 ? $"{candidate.Manifest.Assembly} has no public class implementing IWorkbenchModule." : $"{candidate.Manifest.Assembly} has several IWorkbenchModule classes; name one as \"type\" in module.json."));
            return null;
        }

        var module = (IWorkbenchModule)Activator.CreateInstance(types[0])!;
        if (module.Id != candidate.Manifest.Id)
        {
            results.Add(new ModuleLoadResult(candidate.Manifest.Id, candidate.Folder, ModuleLoadStatus.Invalid, $"module.json says '{candidate.Manifest.Id}' but the module says '{module.Id}'."));
            return null;
        }

        if (module.ContractVersion != ModuleContract.Version)
        {
            results.Add(new ModuleLoadResult(module.Id, candidate.Folder, ModuleLoadStatus.Invalid,
                $"Built for module contract {module.ContractVersion}; this host speaks {ModuleContract.Version}. Rebuild the module against this release.", module.Version));
            return null;
        }

        return module;
    }

    private sealed record ModuleManifest(string Id, string Assembly, string? Type);

    private static bool TryReadManifest(string path, out ModuleManifest? manifest, out string? problem)
    {
        manifest = null;
        problem = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var id = doc.RootElement.TryGetProperty("id", out var i) ? i.GetString()?.Trim().ToLowerInvariant() : null;
            var assembly = doc.RootElement.TryGetProperty("assembly", out var a) ? a.GetString()?.Trim() : null;
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(assembly))
            {
                problem = "module.json needs an \"id\" and an \"assembly\".";
                return false;
            }

            if (assembly != Path.GetFileName(assembly) || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                problem = "module.json's \"assembly\" must be the file name of a .dll in the module's own folder.";
                return false;
            }

            manifest = new ModuleManifest(id, assembly, type);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            problem = $"module.json can't be read: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Finds a module's private dependencies in its own folder, once the default context could not. It is only asked for assemblies the
    /// host does not already have, so a module can never replace an assembly of the product.
    /// </summary>
    private static class DependencyResolver
    {
        private static readonly List<string> _folders = [];

        static DependencyResolver() => AssemblyLoadContext.Default.Resolving += Resolve;

        public static void AddFolder(string folder)
        {
            lock (_folders)
            {
                if (!_folders.Contains(folder))
                {
                    _folders.Add(folder);
                }
            }
        }

        private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            string[] folders;
            lock (_folders)
            {
                folders = [.. _folders];
            }

            foreach (var folder in folders)
            {
                var path = Path.Combine(folder, name.Name + ".dll");
                if (File.Exists(path))
                {
                    return context.LoadFromAssemblyPath(path);
                }
            }

            return null;
        }
    }

    // ───────────────────────────────────── What the host asks of the modules ─────────────────────────────────────

    /// <summary>
    /// Registers every module's services. Each module registers into a scratch collection first and is kept only if it did not throw;
    /// a module that throws is dropped (its menu, permissions and the rest go with it) and reported in <see cref="Results"/>.
    /// </summary>
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var dropped = false;
        foreach (var module in _modules.ToList())
        {
            var scratch = new ServiceCollection();
            try
            {
                module.ConfigureServices(scratch, new ModuleContext(configuration, _folders[module.Id], new DeferredLogger(module.Id)));
            }
            catch (Exception ex)
            {
                Drop(module, $"ConfigureServices failed: {ex.GetType().Name}: {ex.Message}");
                dropped = true;
                continue;
            }

            var decorations = scratch.Where(d => d.ServiceType == typeof(ModuleServiceDecoration)).Select(d => d.ImplementationInstance).OfType<ModuleServiceDecoration>().ToList();
            if (decorations.FirstOrDefault(d => !services.Any(s => s.ServiceType == d.Service && !s.IsKeyedService)) is { } missing)
            {
                Drop(module, $"ConfigureServices failed: the host has no {missing.Service.Name} to decorate");
                dropped = true;
                continue;
            }

            foreach (var descriptor in scratch)
            {
                if (descriptor.ServiceType == typeof(ModuleServiceDecoration))
                {
                    continue;
                }

                // Framework registries that are added with TryAdd (such as the one AddHttpClient makes) already exist in the host's collection:
                // a second copy would make the host's own later AddHttpClient fail. Public service types are never skipped (modules add several).
                if (descriptor.ImplementationInstance is not null && !descriptor.ServiceType.IsPublic && services.Any(d => d.ServiceType == descriptor.ServiceType))
                {
                    continue;
                }

                services.Add(descriptor);
            }

            foreach (var decoration in decorations)
            {
                Decorate(services, decoration);
            }
        }

        if (dropped)
        {
            Publish(this);
        }
    }

    /// <summary>Wraps the last registration of the service in the decorator (which takes the original as a constructor parameter), keeping its lifetime.</summary>
    private static void Decorate(IServiceCollection services, ModuleServiceDecoration decoration)
    {
        var index = -1;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == decoration.Service && !services[i].IsKeyedService)
            {
                index = i;
                break;
            }
        }

        var original = services[index];
        services[index] = ServiceDescriptor.Describe(
            decoration.Service,
            sp => ActivatorUtilities.CreateInstance(sp, decoration.Decorator, original.ImplementationInstance
                ?? original.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)),
            original.Lifetime);
    }

    /// <summary>Maps every module's endpoints on <paramref name="api"/> (the <c>/api/v1</c> group).</summary>
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        foreach (var module in _modules.ToList())
        {
            try
            {
                module.MapEndpoints(api);
            }
            catch (Exception ex)
            {
                Drop(module, $"MapEndpoints failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Adds every module's entities to the host's EF model.</summary>
    public void ApplyModel(ModelBuilder modelBuilder)
    {
        foreach (var module in _modules)
        {
            foreach (var assembly in module.ModelAssemblies)
            {
                modelBuilder.ApplyConfigurationsFromAssembly(assembly);
            }

            module.OnModelCreating(modelBuilder);
        }
    }

    private void Drop(IWorkbenchModule module, string why)
    {
        _modules.Remove(module);
        _results.RemoveAll(r => r.Id == module.Id && r.Status == ModuleLoadStatus.Loaded);
        _results.Add(new ModuleLoadResult(module.Id, _folders.GetValueOrDefault(module.Id, ""), ModuleLoadStatus.Failed, why, module.Version));
    }

    /// <summary>Writes one line per module folder to <paramref name="logger"/>: what loaded, and why anything did not.</summary>
    public void LogResults(ILogger logger)
    {
        if (_results.Count == 0)
        {
            logger.LogDebug("No modules found.");
        }

        foreach (var r in _results)
        {
            switch (r.Status)
            {
                case ModuleLoadStatus.Loaded:
                    logger.LogInformation("Module {Module} {Version} loaded from {Folder}.", r.Id, r.Version, r.Folder);
                    break;
                case ModuleLoadStatus.NotLicensed:
                    logger.LogWarning("Module {Module} skipped: {Detail}", r.Id, r.Detail);
                    break;
                default:
                    logger.LogError("Module {Module} not loaded ({Status}): {Detail}", r.Id, r.Status, r.Detail);
                    break;
            }
        }
    }

    /// <summary>A logger for the module's own startup code, before the host's logging exists: it keeps nothing and writes to the console's debug trace.</summary>
    private sealed class DeferredLogger(string module) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Console.Error.WriteLine($"[module {module}] {logLevel}: {formatter(state, exception)}{(exception is null ? "" : " " + exception.Message)}");
    }
}
