using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workbench.Contracts.Modules;

namespace Workbench.Plugins;

/// <summary>The version of the contract below. The host loads a module only when its major version matches.</summary>
public static class ModuleContract
{
    public const int Version = 1;
}

/// <summary>What the host tells a module while it starts up.</summary>
/// <param name="Configuration">The host's configuration (the module reads its own section, conventionally <c>Modules:&lt;id&gt;</c>).</param>
/// <param name="Folder">The folder the module was loaded from (its own files, such as templates, sit there).</param>
/// <param name="Logger">A logger named for the module.</param>
/// <summary>
/// A module's request to wrap a service the <b>host</b> already registered (the module's own scratch collection cannot see it): register one as
/// a singleton instance in <see cref="IWorkbenchModule.ConfigureServices"/>. The host applies it right after copying the module's services,
/// so the decorator sits where a built-in decorator registered at that point would: inside the permission and edition decorators. The
/// decorator takes the original as a constructor parameter; the original's lifetime is kept. A service the host does not have refuses the module.
/// </summary>
public sealed record ModuleServiceDecoration(Type Service, Type Decorator);

public sealed record ModuleContext(IConfiguration Configuration, string Folder, ILogger Logger);

/// <summary>
/// A module's own database changes. The module has its own <see cref="DbContext"/> (<see cref="ContextType"/>) whose migrations, one set
/// per provider, live in the assemblies named below and are recorded in the module's own history table, so the module's schema never
/// touches the core migration history. The host applies them at startup, after the core migrations, for every database (each tenant's too).
/// </summary>
/// <param name="ContextType">
/// A <see cref="DbContext"/> with a public constructor taking a (non-generic) <see cref="DbContextOptions"/>. Its model holds the module's
/// entities; core entities it refers to are mapped with <c>ToTable(name, t =&gt; t.ExcludeFromMigrations())</c> so the module never creates them.
/// </param>
/// <param name="SqliteMigrationsAssembly">The assembly (simple name) with the module's SQLite migrations for <paramref name="ContextType"/>.</param>
/// <param name="PostgreSqlMigrationsAssembly">The assembly (simple name) with its PostgreSQL migrations.</param>
/// <param name="HistoryTable">The history table; default <c>__EFMigrationsHistory_&lt;module id&gt;</c>.</param>
public sealed record ModuleMigrations(Type ContextType, string SqliteMigrationsAssembly, string PostgreSqlMigrationsAssembly, string? HistoryTable = null)
{
    public string HistoryTableFor(string moduleId) => HistoryTable ?? $"__EFMigrationsHistory_{moduleId}";
}

/// <summary>
/// <b>A license module that lives outside this repository.</b> The host finds the module's assembly in its modules folder, checks that
/// the license key includes <see cref="Id"/> (and loads nothing otherwise), and then asks the module for everything it contributes:
/// <list type="bullet">
/// <item><see cref="Describe"/>: the rows the rest of the product reads as data (ModuleMap rows, menu entries, permissions, API key areas, routable assemblies).</item>
/// <item><see cref="ConfigureServices"/>: its services, registered after the core services and before the permission and edition decorators, so they are wrapped like in-repo ones.</item>
/// <item><see cref="MapEndpoints"/>: its REST endpoints, on the <c>/api/v1</c> group (already authorized, with the host's error mapping).</item>
/// <item><see cref="ModelAssemblies"/> / <see cref="OnModelCreating"/>: its entities, added to the host's EF model so services reach them through <c>Set&lt;T&gt;()</c>.</item>
/// <item><see cref="Migrations"/>: its own migrations (see <see cref="ModuleMigrations"/>).</item>
/// </list>
/// Implementations need a public parameterless constructor and must not do work in it.
/// </summary>
public interface IWorkbenchModule
{
    /// <summary>The license module id (<c>ModuleIds</c> in Workbench.Domain, <c>ModuleKeys</c> in Workbench.Contracts), such as <c>govcon</c>.</summary>
    string Id { get; }

    /// <summary>Shown in logs and Setup.</summary>
    string DisplayName => Id;

    /// <summary>The module's own version.</summary>
    string Version => "0.0.0";

    /// <summary>The <see cref="ModuleContract.Version"/> the module was built against.</summary>
    int ContractVersion => ModuleContract.Version;

    /// <summary>Ids of other modules that must be loaded first; the module is skipped (and says why) when one is missing or not licensed.</summary>
    IReadOnlyList<string> DependsOn => [];

    /// <summary>Everything that is data: ModuleMap rows, menu entries, permissions, API key areas, routable assemblies.</summary>
    ModuleContribution Describe();

    /// <summary>Registers the module's services. Runs once, against a scratch collection first: if it throws, nothing it registered is kept and the module is skipped.</summary>
    void ConfigureServices(IServiceCollection services, ModuleContext context);

    /// <summary>Maps the module's endpoints. <paramref name="api"/> is <c>/api/v1</c>; use first segments the module declared in <see cref="ModuleContribution.ApiSegments"/>.</summary>
    void MapEndpoints(IEndpointRouteBuilder api) { }

    /// <summary>Assemblies whose <c>IEntityTypeConfiguration&lt;T&gt;</c> classes are applied to the host's model (<c>ApplyConfigurationsFromAssembly</c>).</summary>
    IReadOnlyList<Assembly> ModelAssemblies => [];

    /// <summary>Anything else the module needs in the model.</summary>
    void OnModelCreating(ModelBuilder modelBuilder) { }

    /// <summary>The module's migrations, or null when it adds no tables.</summary>
    ModuleMigrations? Migrations => null;
}
