using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Currencies;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.Currencies;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Currencies;

/// <summary>Currencies, exchange rates (by hand, CSV or a registered provider), the functional currency and party defaults.</summary>
public sealed class CurrencyService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    TimeProvider clock,
    IEnumerable<IExchangeRateProvider> providers) : ICurrencyService
{
    private const int MaxImportRows = 5000;

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<CurrencyDto>> ListCurrenciesAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var functional = await CurrencySupport.FunctionalAsync(db, cancellationToken);
        var currencies = await db.Set<Currency>().AsNoTracking().ToListAsync(cancellationToken);
        if (currencies.All(c => c.Code != functional))
        {
            // The functional currency is always listed, even before anyone set currencies up.
            currencies.Add(new Currency { Code = functional, Name = functional == "USD" ? "US dollar" : functional, UpdatedBy = "" });
        }

        return currencies.Where(c => includeInactive || c.IsActive || c.Code == functional)
            .OrderBy(c => c.Code == functional ? 0 : 1).ThenBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => ToDto(c, functional)).ToList();
    }

    public async Task<CurrencyDto> SaveCurrencyAsync(SaveCurrencyRequest request, CancellationToken cancellationToken = default)
    {
        var code = CurrencySupport.NormalizeCode(request.Code) ?? throw new ArgumentException("Enter the currency's three-letter code.");
        var name = string.IsNullOrWhiteSpace(request.Name) ? throw new ArgumentException("Enter the currency's name.") : request.Name.Trim();
        if (name.Length > 100)
        {
            throw new ArgumentException("The name can be at most 100 characters.");
        }

        if (request.Decimals is < 0 or > 4)
        {
            throw new ArgumentException("Decimals is 0 to 4 (2 for most currencies).");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var functional = await CurrencySupport.FunctionalAsync(db, cancellationToken);
        if (code == functional && !request.IsActive)
        {
            throw new ArgumentException($"{code} is the functional currency, so it stays active.");
        }

        var currency = await db.Set<Currency>().FirstOrDefaultAsync(c => c.Code == code, cancellationToken);
        var before = currency is null ? null : new { currency.Name, currency.Decimals, currency.IsActive };
        if (currency is null)
        {
            currency = new Currency { Code = code, Name = name, UpdatedBy = audit.Actor };
            db.Set<Currency>().Add(currency);
        }
        else if (currency.Decimals != request.Decimals && await IsUsedAsync(db, code, cancellationToken))
        {
            throw new ArgumentException($"{code} is already on documents, so its decimals can't change.");
        }

        currency.Name = name;
        currency.Decimals = request.Decimals;
        currency.IsActive = request.IsActive;
        currency.UpdatedBy = audit.Actor;
        currency.UpdatedAtUtc = UtcNow;
        audit.Record(db, CurrencySupport.AuditEntityCurrency, currency.Id, before is null ? "Created" : "Updated", before,
            new { currency.Code, currency.Name, currency.Decimals, currency.IsActive });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(currency, functional);
    }

    public async Task<IReadOnlyList<ExchangeRateDto>> ListRatesAsync(ExchangeRateQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<ExchangeRate>().AsNoTracking().AsQueryable();
        if (CurrencySupport.NormalizeCode(query.Currency) is { } code)
        {
            q = q.Where(r => r.FromCurrency == code || r.ToCurrency == code);
        }

        if (query.RateType is { } type)
        {
            var t = type.ToDomain();
            q = q.Where(r => r.RateType == t);
        }

        if (query.From is { } from)
        {
            q = q.Where(r => r.EffectiveDate >= from);
        }

        if (query.To is { } to)
        {
            q = q.Where(r => r.EffectiveDate <= to);
        }

        return (await q.ToListAsync(cancellationToken))
            .OrderByDescending(r => r.EffectiveDate).ThenBy(r => r.FromCurrency, StringComparer.Ordinal).ThenBy(r => r.RateType)
            .Take(Math.Clamp(query.Take, 1, 5000)).Select(ToDto).ToList();
    }

    public async Task<ExchangeRateDto> SaveRateAsync(SaveExchangeRateRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await ValidateAsync(db, request.FromCurrency, request.ToCurrency, request.Rate, cancellationToken);
        var (rate, _) = Upsert(db, await ExistingAsync(db, cancellationToken), row.From, row.To, request.RateType.ToDomain(), request.EffectiveDate, row.Rate, "Manual");
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(rate);
    }

    public async Task<RateLookupDto> GetRateAsync(string from, string to, DateOnly date, RateKind rateType = RateKind.Spot, CancellationToken cancellationToken = default)
    {
        var f = CurrencySupport.NormalizeCode(from) ?? throw new ArgumentException("Choose the currency to convert from.");
        var t = CurrencySupport.NormalizeCode(to) ?? throw new ArgumentException("Choose the currency to convert to.");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var quote = await CurrencySupport.FindRateAsync(db, f, t, date, rateType.ToDomain(), cancellationToken)
            ?? throw new WorkbenchNotFoundException($"There's no {f} to {t} rate on or before {date:yyyy-MM-dd}.");
        return new RateLookupDto(f, t, date, quote.Rate, quote.RateType.ToContract(), quote.EffectiveDate, quote.IsFallback);
    }

    public async Task<RateImportResultDto> ImportRatesAsync(ImportExchangeRatesRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Csv))
        {
            throw new ArgumentException("The file is empty.");
        }

        var rows = ParseCsv(request.Csv, out var errors);
        if (rows.Count > MaxImportRows)
        {
            throw new ArgumentException($"Import at most {MaxImportRows} rates at a time.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var existing = await ExistingAsync(db, cancellationToken);
        var currencies = (await db.Set<Currency>().AsNoTracking().Select(c => c.Code).ToListAsync(cancellationToken)).ToHashSet();
        currencies.Add(await CurrencySupport.FunctionalAsync(db, cancellationToken));
        int added = 0, replaced = 0, unchanged = 0;
        var seen = new HashSet<(string, string, ExchangeRateType, DateOnly)>();
        foreach (var (lineNumber, from, to, type, date, value) in rows)
        {
            if (!currencies.Contains(from) || !currencies.Contains(to))
            {
                errors.Add($"Line {lineNumber}: {(!currencies.Contains(from) ? from : to)} isn't set up; add the currency first.");
                continue;
            }

            if (from == to)
            {
                errors.Add($"Line {lineNumber}: the two currencies are the same.");
                continue;
            }

            if (!seen.Add((from, to, type, date)))
            {
                errors.Add($"Line {lineNumber}: {from} to {to} {type} on {date:yyyy-MM-dd} is in the file twice; the first one is used.");
                continue;
            }

            var (_, outcome) = Upsert(db, existing, from, to, type, date, CurrencyMath.RoundRate(value), "CSV import");
            _ = outcome switch { UpsertOutcome.Added => added++, UpsertOutcome.Replaced => replaced++, _ => unchanged++ };
        }

        if (!request.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return new RateImportResultDto(added, replaced, unchanged, errors, request.DryRun);
    }

    public Task<IReadOnlyList<RateProviderDto>> ListRateProvidersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RateProviderDto>>(providers.Select(p => new RateProviderDto(p.Name, p.Description)).OrderBy(p => p.Name).ToList());

    public async Task<RateImportResultDto> FetchRatesAsync(FetchExchangeRatesRequest request, CancellationToken cancellationToken = default)
    {
        var provider = providers.FirstOrDefault(p => string.Equals(p.Name, request.Provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(providers.Any() ? $"There's no rate provider called \"{request.Provider}\"." : "No rate provider is set up; enter rates by hand or import a CSV.");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var functional = await CurrencySupport.FunctionalAsync(db, cancellationToken);
        var codes = await db.Set<Currency>().AsNoTracking().Where(c => c.IsActive && c.Code != functional).Select(c => c.Code).ToListAsync(cancellationToken);
        if (codes.Count == 0)
        {
            return new RateImportResultDto(0, 0, 0, ["There are no active foreign currencies to fetch rates for."], false);
        }

        var type = request.RateType.ToDomain();
        var quotes = await provider.GetRatesAsync(functional, codes, request.Date, type, cancellationToken);
        var existing = await ExistingAsync(db, cancellationToken);
        var errors = new List<string>();
        int added = 0, replaced = 0, unchanged = 0;
        foreach (var code in codes.Order(StringComparer.Ordinal))
        {
            if (!quotes.TryGetValue(code, out var value) || value <= 0)
            {
                errors.Add($"{provider.Name} had no {code} rate for {request.Date:yyyy-MM-dd}.");
                continue;
            }

            var (_, outcome) = Upsert(db, existing, code, functional, type, request.Date, CurrencyMath.RoundRate(value), provider.Name);
            _ = outcome switch { UpsertOutcome.Added => added++, UpsertOutcome.Replaced => replaced++, _ => unchanged++ };
        }

        await db.SaveChangesAsync(cancellationToken);
        return new RateImportResultDto(added, replaced, unchanged, errors, false);
    }

    public async Task<CurrencySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await SettingsDtoAsync(db, cancellationToken);
    }

    public async Task<CurrencySettingsDto> SaveSettingsAsync(SaveCurrencySettingsRequest request, CancellationToken cancellationToken = default)
    {
        var functional = CurrencySupport.NormalizeCode(request.FunctionalCurrency) ?? throw new ArgumentException("Choose the functional currency.");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var settings = await db.Set<CurrencySettings>().OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        var before = settings is null ? null : new { settings.FunctionalCurrency, settings.RealizedGainLossAccountId, settings.UnrealizedGainLossAccountId };
        if (settings is null)
        {
            settings = new CurrencySettings();
            db.Set<CurrencySettings>().Add(settings);
        }

        if (functional != settings.FunctionalCurrency)
        {
            if (await AnyForeignActivityAsync(db, cancellationToken))
            {
                throw new ArgumentException("Documents have already posted in other currencies, so the functional currency can't change.");
            }

            if (await db.Set<Currency>().AnyAsync(c => c.Code == functional && !c.IsActive, cancellationToken))
            {
                throw new ArgumentException($"{functional} is inactive; activate it first.");
            }
        }

        foreach (var id in new[] { request.RealizedGainLossAccountId, request.UnrealizedGainLossAccountId }.OfType<Guid>())
        {
            var account = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Account", id);
            if (!account.IsActive || await db.Set<Account>().AnyAsync(a => a.ParentId == id, cancellationToken))
            {
                throw new ArgumentException($"{account.Number} {account.Name} must be an active detail account.");
            }

            if (account.Type is not (AccountType.Revenue or AccountType.Expense))
            {
                throw new ArgumentException($"{account.Number} {account.Name} must be an income or expense account.");
            }
        }

        settings.FunctionalCurrency = functional;
        settings.RealizedGainLossAccountId = request.RealizedGainLossAccountId;
        settings.UnrealizedGainLossAccountId = request.UnrealizedGainLossAccountId;
        settings.UpdatedBy = audit.Actor;
        settings.UpdatedAtUtc = UtcNow;
        audit.Record(db, CurrencySupport.AuditEntitySettings, settings.Id, before is null ? "Created" : "Updated", before,
            new { settings.FunctionalCurrency, settings.RealizedGainLossAccountId, settings.UnrealizedGainLossAccountId });
        await db.SaveChangesAsync(cancellationToken);
        return await SettingsDtoAsync(db, cancellationToken);
    }

    public async Task<IReadOnlyList<PartyCurrencyDto>> ListPartyCurrenciesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => v.Status != VendorStatus.Inactive)
            .Select(v => new PartyCurrencyDto(CurrencyPartyKind.Vendor, v.Id, v.Code, v.Name, v.CurrencyCode)).ToListAsync(cancellationToken);
        var profiles = await db.Set<CustomerBillingProfile>().AsNoTracking().ToDictionaryAsync(p => p.CustomerId, p => p.CurrencyCode, cancellationToken);
        var customers = (await db.Set<Customer>().AsNoTracking().ToListAsync(cancellationToken))
            .Select(c => new PartyCurrencyDto(CurrencyPartyKind.Customer, c.Id, c.Code, c.Name, profiles.GetValueOrDefault(c.Id)));
        return customers.Concat(vendors).OrderBy(p => p.Kind).ThenBy(p => p.Code, StringComparer.Ordinal).ToList();
    }

    public async Task<PartyCurrencyDto> SetPartyCurrencyAsync(SetPartyCurrencyRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var code = await ForeignCodeAsync(db, request.CurrencyCode, cancellationToken);
        PartyCurrencyDto result;
        string? before;
        if (request.Kind == CurrencyPartyKind.Vendor)
        {
            var vendor = await db.Set<Vendor>().FirstOrDefaultAsync(v => v.Id == request.Id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Vendor", request.Id);
            before = vendor.CurrencyCode;
            vendor.CurrencyCode = code;
            result = new PartyCurrencyDto(CurrencyPartyKind.Vendor, vendor.Id, vendor.Code, vendor.Name, code);
        }
        else
        {
            var customer = await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.Id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Customer", request.Id);
            var profile = await db.Set<CustomerBillingProfile>().FirstOrDefaultAsync(p => p.CustomerId == customer.Id, cancellationToken);
            if (profile is null)
            {
                profile = new CustomerBillingProfile { CustomerId = customer.Id, UpdatedAtUtc = UtcNow };
                db.Set<CustomerBillingProfile>().Add(profile);
            }

            before = profile.CurrencyCode;
            profile.CurrencyCode = code;
            profile.UpdatedBy = audit.Actor;
            profile.UpdatedAtUtc = UtcNow;
            result = new PartyCurrencyDto(CurrencyPartyKind.Customer, customer.Id, customer.Code, customer.Name, code);
        }

        audit.Record(db, request.Kind.ToString(), request.Id, "CurrencyChanged", new { Currency = before }, new { Currency = code });
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task SetInvoiceCurrencyAsync(Guid invoiceId, SetInvoiceCurrencyRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await db.Set<Invoice>().FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Invoice", invoiceId);
        if (invoice.Status != InvoiceStatus.Draft)
        {
            throw new InvalidOperationException($"Invoice {invoice.Number} is {invoice.Status}; only a draft's currency can change.");
        }

        if (invoice.Type == InvoiceType.CreditMemo)
        {
            throw new ArgumentException("A credit memo is in the currency of the invoice it credits.");
        }

        var code = await ForeignCodeAsync(db, request.CurrencyCode, cancellationToken);
        var before = invoice.CurrencyCode;
        invoice.CurrencyCode = code;
        invoice.ExchangeRate = 1m;
        invoice.UpdatedAtUtc = UtcNow;
        audit.Record(db, "Invoice", invoice.Id, "CurrencyChanged", new { Currency = before }, new { Currency = code });
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- Helpers ----

    private enum UpsertOutcome { Added, Replaced, Unchanged }

    private static async Task<Dictionary<(string, string, ExchangeRateType, DateOnly), ExchangeRate>> ExistingAsync(IWorkbenchDbContext db, CancellationToken ct) =>
        (await db.Set<ExchangeRate>().ToListAsync(ct)).ToDictionary(r => (r.FromCurrency, r.ToCurrency, r.RateType, r.EffectiveDate));

    private (ExchangeRate Rate, UpsertOutcome Outcome) Upsert(
        IWorkbenchDbContext db, Dictionary<(string, string, ExchangeRateType, DateOnly), ExchangeRate> existing,
        string from, string to, ExchangeRateType type, DateOnly date, decimal value, string source)
    {
        if (existing.TryGetValue((from, to, type, date), out var row))
        {
            if (row.Rate == value)
            {
                return (row, UpsertOutcome.Unchanged);
            }

            var before = row.Rate;
            row.Rate = value;
            row.Source = source;
            row.CreatedBy = audit.Actor;
            row.CreatedAtUtc = UtcNow;
            audit.Record(db, CurrencySupport.AuditEntityRate, row.Id, "Replaced", new { Rate = before }, new { row.FromCurrency, row.ToCurrency, Type = type.ToString(), row.EffectiveDate, row.Rate, Source = source });
            return (row, UpsertOutcome.Replaced);
        }

        row = new ExchangeRate { FromCurrency = from, ToCurrency = to, RateType = type, EffectiveDate = date, Rate = value, Source = source, CreatedBy = audit.Actor, CreatedAtUtc = UtcNow };
        db.Set<ExchangeRate>().Add(row);
        existing[(from, to, type, date)] = row;
        audit.Record(db, CurrencySupport.AuditEntityRate, row.Id, "Created", after: new { row.FromCurrency, row.ToCurrency, Type = type.ToString(), row.EffectiveDate, row.Rate, Source = source });
        return (row, UpsertOutcome.Added);
    }

    private static async Task<(string From, string To, decimal Rate)> ValidateAsync(IWorkbenchDbContext db, string from, string to, decimal rate, CancellationToken ct)
    {
        var f = CurrencySupport.NormalizeCode(from) ?? throw new ArgumentException("Choose the currency to convert from.");
        var t = CurrencySupport.NormalizeCode(to) ?? throw new ArgumentException("Choose the currency to convert to.");
        if (f == t)
        {
            throw new ArgumentException("Choose two different currencies.");
        }

        if (rate <= 0)
        {
            throw new ArgumentException("The rate must be positive.");
        }

        var functional = await CurrencySupport.FunctionalAsync(db, ct);
        foreach (var code in new[] { f, t }.Where(c => c != functional))
        {
            if (!await db.Set<Currency>().AnyAsync(c => c.Code == code, ct))
            {
                throw new ArgumentException($"{code} isn't set up; add the currency first.");
            }
        }

        return (f, t, CurrencyMath.RoundRate(rate));
    }

    /// <summary>An active foreign currency code, or null for blank or the functional currency.</summary>
    private static async Task<string?> ForeignCodeAsync(IWorkbenchDbContext db, string? code, CancellationToken ct)
    {
        var normalized = CurrencySupport.NormalizeCode(code);
        if (normalized is null || normalized == await CurrencySupport.FunctionalAsync(db, ct))
        {
            return null;
        }

        return await db.Set<Currency>().AnyAsync(c => c.Code == normalized && c.IsActive, ct)
            ? normalized
            : throw new ArgumentException($"{normalized} isn't an active currency; add it under Setup › Currencies and rates.");
    }

    private static List<(int Line, string From, string To, ExchangeRateType Type, DateOnly Date, decimal Rate)> ParseCsv(string csv, out List<string> errors)
    {
        errors = [];
        var rows = new List<(int, string, string, ExchangeRateType, DateOnly, decimal)>();
        var lines = csv.Replace("\r\n", "\n").Split('\n');
        var header = lines[0].Split(',').Select(h => h.Trim().Trim('"').ToLowerInvariant().Replace(" ", "")).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        var (fromCol, toCol, typeCol, dateCol, rateCol) = (Col("from", "fromcurrency"), Col("to", "tocurrency"), Col("type", "ratetype"), Col("effectivedate", "date"), Col("rate"));
        if (fromCol < 0 || toCol < 0 || dateCol < 0 || rateCol < 0)
        {
            throw new ArgumentException("The first row must name the columns: From, To, Type, EffectiveDate, Rate.");
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var n = i + 1;
            var cells = lines[i].Split(',').Select(c => c.Trim().Trim('"')).ToList();
            string Cell(int col) => col >= 0 && col < cells.Count ? cells[col] : "";
            try
            {
                var from = CurrencySupport.NormalizeCode(Cell(fromCol)) ?? throw new ArgumentException("the From currency is missing");
                var to = CurrencySupport.NormalizeCode(Cell(toCol)) ?? throw new ArgumentException("the To currency is missing");
                var typeText = Cell(typeCol).Replace("-", "").Replace(" ", "");
                var type = typeText.Length == 0 ? ExchangeRateType.Spot
                    : Enum.TryParse<ExchangeRateType>(typeText, ignoreCase: true, out var t) && Enum.IsDefined(t) ? t
                    : throw new ArgumentException($"the type \"{Cell(typeCol)}\" isn't Spot, Average or MonthEnd");
                var date = DateOnly.TryParseExact(Cell(dateCol), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
                    : throw new ArgumentException($"the date \"{Cell(dateCol)}\" isn't yyyy-MM-dd");
                var rate = decimal.TryParse(Cell(rateCol), NumberStyles.Number, CultureInfo.InvariantCulture, out var r) && r > 0 ? r
                    : throw new ArgumentException($"the rate \"{Cell(rateCol)}\" isn't a positive number");
                rows.Add((n, from, to, type, date, rate));
            }
            catch (ArgumentException ex)
            {
                errors.Add($"Line {n}: {ex.Message.TrimEnd('.')}.");
            }
        }

        return rows;
    }

    private static async Task<bool> IsUsedAsync(IWorkbenchDbContext db, string code, CancellationToken ct) =>
        await db.Set<JournalLine>().AnyAsync(l => l.TransactionCurrency == code, ct)
        || await db.Set<ApBill>().AnyAsync(b => b.CurrencyCode == code, ct)
        || await db.Set<Invoice>().AnyAsync(i => i.CurrencyCode == code, ct)
        || await db.Set<OdcEntry>().AnyAsync(o => o.CurrencyCode == code, ct);

    private static async Task<bool> AnyForeignActivityAsync(IWorkbenchDbContext db, CancellationToken ct) =>
        await db.Set<JournalLine>().AnyAsync(l => l.TransactionCurrency != null, ct)
        || await db.Set<ApBill>().AnyAsync(b => b.CurrencyCode != null, ct)
        || await db.Set<Invoice>().AnyAsync(i => i.CurrencyCode != null, ct)
        || await db.Set<OdcEntry>().AnyAsync(o => o.CurrencyCode != null, ct);

    private static async Task<CurrencySettingsDto> SettingsDtoAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var settings = await CurrencySupport.SettingsAsync(db, ct);
        var ids = new[] { settings?.RealizedGainLossAccountId, settings?.UnrealizedGainLossAccountId }.OfType<Guid>().ToList();
        var numbers = new[] { CurrencySupport.RealizedAccountNumber, CurrencySupport.UnrealizedAccountNumber };
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => ids.Contains(a.Id) || numbers.Contains(a.Number)).ToListAsync(ct);
        string? Label(Guid? id, string number) =>
            (id is { } i ? accounts.FirstOrDefault(a => a.Id == i) : accounts.FirstOrDefault(a => a.Number == number)) is { } a ? $"{a.Number} {a.Name}" : $"{number} (added when first needed)";
        return new CurrencySettingsDto(
            settings?.FunctionalCurrency ?? CurrencySupport.DefaultFunctional,
            settings?.RealizedGainLossAccountId,
            Label(settings?.RealizedGainLossAccountId, CurrencySupport.RealizedAccountNumber),
            settings?.UnrealizedGainLossAccountId,
            Label(settings?.UnrealizedGainLossAccountId, CurrencySupport.UnrealizedAccountNumber),
            !await AnyForeignActivityAsync(db, ct));
    }

    private static CurrencyDto ToDto(Currency c, string functional) => new(c.Code, c.Name, c.Decimals, c.IsActive || c.Code == functional, c.Code == functional);

    private static ExchangeRateDto ToDto(ExchangeRate r) =>
        new(r.Id, r.FromCurrency, r.ToCurrency, r.RateType.ToContract(), r.EffectiveDate, r.Rate, r.Source, r.CreatedBy, r.CreatedAtUtc);
}
