using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Currencies;
using Workbench.Domain.Accounting;
using Workbench.Domain.Cash;
using Workbench.Domain.Currencies;

namespace Workbench.Application.Currencies;

/// <summary>A rate found for a conversion: functional units per foreign unit, and where it came from.</summary>
public sealed record RateQuote(decimal Rate, ExchangeRateType RateType, DateOnly EffectiveDate, bool IsFallback);

/// <summary>
/// A document's currency once resolved: <see cref="Code"/> null means the functional currency (rate 1), which is how every
/// document posted before multicurrency reads.
/// </summary>
public sealed record DocumentCurrency(string? Code, decimal Rate, int Decimals)
{
    public static readonly DocumentCurrency Functional = new(null, 1m, 2);

    public bool IsForeign => Code is not null;

    /// <summary>A transaction amount in the functional currency, to the cent.</summary>
    public decimal ToFunctional(decimal transactionAmount) => CurrencyMath.ToFunctional(transactionAmount, Rate);

    public decimal ToTransaction(decimal functionalAmount) => CurrencyMath.ToTransaction(functionalAmount, Rate, Decimals);

    public decimal RoundTransaction(decimal amount) => CurrencyMath.Round(amount, Decimals);
}

/// <summary>Conversion and rounding (half away from zero, like the rest of the ledger).</summary>
public static class CurrencyMath
{
    public const int RateDecimals = 8;

    public static decimal Round(decimal amount, int decimals) => Math.Round(amount, decimals, MidpointRounding.AwayFromZero);

    public static decimal RoundRate(decimal rate) => Math.Round(rate, RateDecimals, MidpointRounding.AwayFromZero);

    public static decimal ToFunctional(decimal transactionAmount, decimal rate) => Round(transactionAmount * rate, 2);

    public static decimal ToTransaction(decimal functionalAmount, decimal rate, int decimals) =>
        rate == 0 ? throw new ArgumentException("An exchange rate can't be zero.") : Round(functionalAmount / rate, decimals);

    public static decimal Invert(decimal rate) => RoundRate(1m / rate);

    /// <summary>
    /// The functional amount a payment of <paramref name="transactionApplied"/> relieves on a document carried at
    /// <paramref name="documentRate"/>: the whole remaining functional balance when it settles the document, so a paid
    /// document never keeps a rounding cent.
    /// </summary>
    public static decimal Relieved(decimal transactionApplied, decimal transactionOpen, decimal functionalOpen, decimal documentRate) =>
        transactionApplied == transactionOpen ? functionalOpen : Math.Min(functionalOpen, ToFunctional(transactionApplied, documentRate));
}

public static class CurrencySupport
{
    public const string DefaultFunctional = "USD";
    public const string RealizedAccountNumber = "4920";
    public const string UnrealizedAccountNumber = "4930";
    public const string AuditEntityRate = nameof(ExchangeRate);
    public const string AuditEntityCurrency = nameof(Currency);
    public const string AuditEntitySettings = nameof(CurrencySettings);
    public const string AuditEntityRevaluation = nameof(CurrencyRevaluation);

    public static ExchangeRateType ToDomain(this RateKind v) => (ExchangeRateType)(int)v;

    public static RateKind ToContract(this ExchangeRateType v) => (RateKind)(int)v;

    public static RevaluationKind ToContract(this RevaluationItemKind v) => (RevaluationKind)(int)v;

    /// <summary>An ISO 4217 code: three letters, upper-cased. Blank is null.</summary>
    public static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var c = code.Trim().ToUpperInvariant();
        return c.Length == 3 && c.All(ch => ch is >= 'A' and <= 'Z')
            ? c
            : throw new ArgumentException($"\"{code.Trim()}\" isn't a currency code; use the three-letter ISO code, for example EUR.");
    }

    public static async Task<CurrencySettings?> SettingsAsync(IWorkbenchDbContext db, CancellationToken ct) =>
        await db.Set<CurrencySettings>().AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);

    /// <summary>The functional currency (USD unless the tenant changed it).</summary>
    public static async Task<string> FunctionalAsync(IWorkbenchDbContext db, CancellationToken ct) =>
        (await SettingsAsync(db, ct))?.FunctionalCurrency ?? DefaultFunctional;

    /// <summary>
    /// The latest rate of <paramref name="type"/> converting <paramref name="from"/> into <paramref name="to"/> on or before
    /// <paramref name="date"/> (the inverse pair works too); then any other type, flagged as a fallback. Null when none.
    /// </summary>
    public static async Task<RateQuote?> FindRateAsync(IWorkbenchDbContext db, string from, string to, DateOnly date, ExchangeRateType type, CancellationToken ct)
    {
        if (from == to)
        {
            return new RateQuote(1m, type, date, false);
        }

        var rows = await db.Set<ExchangeRate>().AsNoTracking()
            .Where(r => ((r.FromCurrency == from && r.ToCurrency == to) || (r.FromCurrency == to && r.ToCurrency == from)) && r.EffectiveDate <= date)
            .ToListAsync(ct);
        RateQuote? Pick(Func<ExchangeRate, bool> filter, bool fallback)
        {
            // The direct pair wins over the inverse on the same date.
            var best = rows.Where(filter).OrderByDescending(r => r.EffectiveDate).ThenBy(r => r.FromCurrency == from ? 0 : 1).FirstOrDefault();
            return best is null ? null
                : new RateQuote(best.FromCurrency == from ? best.Rate : CurrencyMath.Invert(best.Rate), best.RateType, best.EffectiveDate, fallback);
        }

        return Pick(r => r.RateType == type, false) ?? Pick(_ => true, true);
    }

    /// <summary>The newest rate of any type on or before <paramref name="date"/> (month-end first on the same date); null when none.</summary>
    public static async Task<RateQuote?> FindLatestRateAsync(IWorkbenchDbContext db, string from, string to, DateOnly date, CancellationToken ct)
    {
        if (from == to)
        {
            return new RateQuote(1m, ExchangeRateType.Spot, date, false);
        }

        var best = (await db.Set<ExchangeRate>().AsNoTracking()
                .Where(r => ((r.FromCurrency == from && r.ToCurrency == to) || (r.FromCurrency == to && r.ToCurrency == from)) && r.EffectiveDate <= date)
                .ToListAsync(ct))
            .OrderByDescending(r => r.EffectiveDate).ThenByDescending(r => r.RateType == ExchangeRateType.MonthEnd).ThenBy(r => r.FromCurrency == from ? 0 : 1)
            .FirstOrDefault();
        return best is null ? null : new RateQuote(best.FromCurrency == from ? best.Rate : CurrencyMath.Invert(best.Rate), best.RateType, best.EffectiveDate, false);
    }

    public static async Task<RateQuote> RequireRateAsync(IWorkbenchDbContext db, string from, string to, DateOnly date, ExchangeRateType type, CancellationToken ct) =>
        await FindRateAsync(db, from, to, date, type, ct)
            ?? throw new ArgumentException($"There's no {from} to {to} exchange rate on or before {date:yyyy-MM-dd}. Enter one under Setup › Currencies and rates.");

    /// <summary>
    /// Resolves a document's currency on <paramref name="date"/>: null or the functional currency is
    /// <see cref="DocumentCurrency.Functional"/>; otherwise an active currency at <paramref name="explicitRate"/> (when given)
    /// or the spot rate in effect.
    /// </summary>
    public static async Task<DocumentCurrency> ResolveAsync(IWorkbenchDbContext db, string? code, DateOnly date, decimal? explicitRate, CancellationToken ct)
    {
        var normalized = NormalizeCode(code);
        if (normalized is null)
        {
            return DocumentCurrency.Functional;
        }

        var functional = await FunctionalAsync(db, ct);
        if (normalized == functional)
        {
            return DocumentCurrency.Functional;
        }

        var currency = await db.Set<Currency>().AsNoTracking().FirstOrDefaultAsync(c => c.Code == normalized, ct)
            ?? throw new ArgumentException($"{normalized} isn't set up. Add it under Setup › Currencies and rates.");
        if (!currency.IsActive)
        {
            throw new ArgumentException($"{normalized} is inactive.");
        }

        decimal rate;
        if (explicitRate is { } r)
        {
            rate = r > 0 ? CurrencyMath.RoundRate(r) : throw new ArgumentException("The exchange rate must be positive.");
        }
        else
        {
            rate = (await RequireRateAsync(db, normalized, functional, date, ExchangeRateType.Spot, ct)).Rate;
        }

        return new DocumentCurrency(normalized, rate, currency.Decimals);
    }

    /// <summary>A stored document's currency (no lookups): the code, rate and the currency's decimals.</summary>
    public static async Task<DocumentCurrency> OfAsync(IWorkbenchDbContext db, string? code, decimal rate, CancellationToken ct)
    {
        if (code is null)
        {
            return DocumentCurrency.Functional;
        }

        var decimals = await db.Set<Currency>().AsNoTracking().Where(c => c.Code == code).Select(c => (int?)c.Decimals).FirstOrDefaultAsync(ct) ?? 2;
        return new DocumentCurrency(code, rate, decimals);
    }

    /// <summary>
    /// The GL cash account a foreign payment moves through: the active bank account kept in that currency (its GL account),
    /// else the default cash account.
    /// </summary>
    public static async Task<Guid?> ForeignCashAccountAsync(IWorkbenchDbContext db, string currency, CancellationToken ct)
    {
        var accounts = await db.Set<BankAccount>().AsNoTracking().Where(b => b.IsActive && b.Currency == currency).Select(b => b.GlAccountId).Distinct().ToListAsync(ct);
        return accounts.Count == 1 ? accounts[0] : null;
    }

    /// <summary>
    /// Adds 4920 and 4930 to the chart (under 4000 Revenue) when they're needed and missing, in a unit of work of its own so
    /// the ledger can validate lines against them. Call before <see cref="GainLossAccountsAsync"/>.
    /// </summary>
    public static async Task EnsureGainLossAccountsAsync(IWorkbenchDbContextFactory dbFactory, DateTime utcNow, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var settings = await SettingsAsync(db, ct);
        var numbers = new List<string>();
        if (settings?.RealizedGainLossAccountId is null)
        {
            numbers.Add(RealizedAccountNumber);
        }

        if (settings?.UnrealizedGainLossAccountId is null)
        {
            numbers.Add(UnrealizedAccountNumber);
        }

        var existing = await db.Set<Account>().AsNoTracking().Where(a => numbers.Contains(a.Number)).Select(a => a.Number).ToListAsync(ct);
        if (numbers.All(existing.Contains))
        {
            return;
        }

        await GainLossAccountsAsync(db, utcNow, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The realized and unrealized gain/loss accounts: the settings' choice, else 4920 and 4930, which are added to the chart
    /// (under 4000 Revenue) when missing; the caller saves (use <see cref="EnsureGainLossAccountsAsync"/> before posting).
    /// </summary>
    public static async Task<(Account Realized, Account Unrealized)> GainLossAccountsAsync(IWorkbenchDbContext db, DateTime utcNow, CancellationToken ct)
    {
        var settings = await SettingsAsync(db, ct);
        var realized = await AccountAsync(db, settings?.RealizedGainLossAccountId, RealizedAccountNumber, "Realized foreign exchange gain (loss)",
            "Rate differences settled when foreign currency bills and invoices are paid", utcNow, ct);
        var unrealized = await AccountAsync(db, settings?.UnrealizedGainLossAccountId, UnrealizedAccountNumber, "Unrealized foreign exchange gain (loss)",
            "Period-end revaluation of open foreign currency balances, reversed the next period", utcNow, ct);
        return (realized, unrealized);
    }

    private static async Task<Account> AccountAsync(IWorkbenchDbContext db, Guid? chosen, string number, string name, string description, DateTime utcNow, CancellationToken ct)
    {
        if (chosen is { } id)
        {
            var account = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct)
                ?? throw new ArgumentException("The foreign exchange gain/loss account no longer exists; choose another under Setup › Currencies and rates.");
            return account.IsActive ? account : throw new ArgumentException($"Foreign exchange gain/loss account {account.Number} is inactive.");
        }

        var existing = db.Set<Account>().Local.FirstOrDefault(a => a.Number == number)
            ?? await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == number, ct);
        if (existing is not null)
        {
            return existing.IsActive ? existing : throw new ArgumentException($"Foreign exchange gain/loss account {existing.Number} is inactive.");
        }

        if (!await db.Set<Account>().AnyAsync(ct))
        {
            throw new InvalidOperationException("The chart of accounts is empty. Install the default chart (Accounting › Chart of accounts) first.");
        }

        var parent = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == "4000" && a.Type == AccountType.Revenue, ct);
        var created = new Account
        {
            Number = number,
            Name = name,
            Type = AccountType.Revenue,
            NormalBalance = NormalBalance.Credit,
            ParentId = parent?.Id,
            Description = description,
            CreatedAtUtc = utcNow,
        };
        db.Set<Account>().Add(created);
        return created;
    }

    /// <summary>A journal line in a foreign currency (null currency: an ordinary functional line). Debit when <paramref name="signedFunctional"/> is positive.</summary>
    public static JournalLine Line(JournalEntry entry, Guid accountId, decimal signedFunctional, string? memo, string? currency = null, decimal? transactionAmount = null, decimal? rate = null) => new()
    {
        JournalEntryId = entry.Id,
        LineNumber = entry.Lines.Count + 1,
        AccountId = accountId,
        Debit = signedFunctional > 0 ? signedFunctional : 0,
        Credit = signedFunctional < 0 ? -signedFunctional : 0,
        Memo = memo is { Length: > 500 } ? memo[..500] : memo,
        TransactionCurrency = currency,
        TransactionAmount = currency is null ? null : Math.Abs(transactionAmount ?? 0),
        ExchangeRate = currency is null ? null : rate,
    };
}

/// <summary>
/// A source of exchange rates (a bank feed or market data service). Register implementations as
/// <c>IExchangeRateProvider</c>; none is registered by default, so Workbench never calls out unless one is added.
/// </summary>
public interface IExchangeRateProvider
{
    string Name { get; }

    string Description { get; }

    /// <summary>Rates converting each of <paramref name="currencies"/> into <paramref name="functionalCurrency"/> on <paramref name="date"/>.</summary>
    Task<IReadOnlyDictionary<string, decimal>> GetRatesAsync(string functionalCurrency, IReadOnlyCollection<string> currencies, DateOnly date, ExchangeRateType rateType, CancellationToken cancellationToken);
}
