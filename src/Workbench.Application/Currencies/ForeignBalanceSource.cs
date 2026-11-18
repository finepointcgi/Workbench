using Workbench.Domain.Currencies;

namespace Workbench.Application.Currencies;

/// <summary>
/// Open foreign currency balances that a license module owns, for the revaluation: the accounting module's bank accounts kept in a foreign currency.
/// Receivables and payables are core's. Registered in the module's <c>ConfigureServices</c>; a host without one revalues receivables and payables only.
/// </summary>
public interface IForeignBalanceSource
{
    /// <summary>Which kind of balance this source adds (<see cref="RevaluationItemKind.Bank"/> for bank accounts).</summary>
    RevaluationItemKind Kind { get; }

    /// <summary>Adds the open balances as of <paramref name="asOf"/> (carrying amount and transaction balance; the engine values them) and any warnings.</summary>
    Task AddOpenBalancesAsync(IWorkbenchDbContext db, DateOnly asOf, string functionalCurrency, IReadOnlyDictionary<Guid, string> accountNumbers,
        List<OpenForeignBalance> balances, List<string> warnings, CancellationToken cancellationToken);

    /// <summary>The GL cash account a foreign payment moves through: the one active account kept in this currency, or null when there is none or more than one (the caller uses the default cash account).</summary>
    Task<Guid?> CashAccountAsync(IWorkbenchDbContext db, string currency, CancellationToken cancellationToken);

    /// <summary>The names (for the revaluation lines) of the records with these ids.</summary>
    Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
