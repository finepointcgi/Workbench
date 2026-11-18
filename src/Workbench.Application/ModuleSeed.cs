using Microsoft.EntityFrameworkCore;

namespace Workbench.Application;

/// <summary>Where in the development sample data a license module's seed runs (the sample data is built in a fixed order, and a module's rows read what came before).</summary>
public enum ModuleSeedStage
{
    /// <summary>After revenue (the ledgers, billing and payables are seeded).</summary>
    AfterRevenue,

    /// <summary>After planning (before multicurrency and multicompany).</summary>
    AfterPlanning,

    /// <summary>After the multicurrency and multicompany seeds (before travel).</summary>
    AfterMulticompany,

    /// <summary>After tax books and the CPSR sample purchase orders.</summary>
    AfterTaxBooks,

    /// <summary>After everything else.</summary>
    End,
}

/// <summary>
/// Development sample data that belongs to a license module. The module registers it (<c>services.AddScoped&lt;IModuleSeed, …&gt;()</c>)
/// and the database initializer runs it at <see cref="Stage"/>, in registration order, when it seeds the sample company. Like the built-in seeds it
/// does nothing when its rows already exist.
/// </summary>
public interface IModuleSeed
{
    ModuleSeedStage Stage { get; }

    /// <summary>Seeds the module's rows. <paramref name="db"/> is the host's context; <paramref name="services"/> resolves the application services.</summary>
    Task SeedAsync(IWorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken);
}
