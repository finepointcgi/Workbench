using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Workbench.Application;

/// <summary>Where in the development sample data a license module's seed runs (the sample data is built in a fixed order, and a module's rows read what came before).</summary>
public enum ModuleSeedStage
{
    /// <summary>After the procurement sample data, before fixed assets and cash (the accounting module's assets and bank accounts).</summary>
    AfterProcurement,

    /// <summary>After cash and bank accounts, before leave (the sample pay group, pay periods and payroll profiles the leave sample data follows).</summary>
    AfterCash,

    /// <summary>After resourcing and skills (the ledgers, billing, indirect rates and payables are seeded): the billing module's revenue recognition sample data.</summary>
    AfterResourcing,

    /// <summary>After revenue (the ledgers, billing and payables are seeded).</summary>
    AfterRevenue,

    /// <summary>After HR (the sample people exist; before the assistant and planning seeds).</summary>
    AfterHr,

    /// <summary>After planning (before multicurrency and multicompany).</summary>
    AfterPlanning,

    /// <summary>After the multicurrency and multicompany seeds (before travel).</summary>
    AfterMulticompany,

    /// <summary>After travel (before sourcing, bank files and tax books).</summary>
    AfterTravel,

    /// <summary>After sourcing (the accounting module's bank files, sales tax and tax books; before the CPSR sample purchase orders).</summary>
    AfterSourcing,

    /// <summary>After tax books and the CPSR sample purchase orders.</summary>
    AfterTaxBooks,

    /// <summary>After everything else.</summary>
    End,

    /// <summary>At the end of the commercial demo dataset (<c>Demo:Dataset=Commercial</c>, the Kestrel Bay Consulting firm), after its projects, contracts and invoices exist. The government sample data never runs these.</summary>
    CommercialDemo,
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

/// <summary>Runs the registered module seeds of one stage (the database initializer and the commercial demo call it at their points).</summary>
public static class ModuleSeeds
{
    public static async Task RunAsync(IServiceProvider services, IWorkbenchDbContext db, ModuleSeedStage stage, CancellationToken cancellationToken)
    {
        foreach (var seed in services.GetServices<IModuleSeed>().Where(m => m.Stage == stage))
        {
            await seed.SeedAsync(db, services, cancellationToken);
        }
    }
}
