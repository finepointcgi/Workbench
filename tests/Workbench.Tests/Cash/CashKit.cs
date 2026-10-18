using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Cash;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;
using BillingMethod = Workbench.Domain.Billing.PaymentMethod;
using PurchasingMethod = Workbench.Domain.Purchasing.PaymentMethod;

namespace Workbench.Tests.Cash;

/// <summary>
/// The real registrations over <see cref="ProjectAccountingFixture"/> (fiscal period 2026-10 open) with the default chart
/// and an operating bank account ending 4321 linked to 1010. Helpers post GL cash the way billing and AP do (an AR receipt
/// with its payment reference, an AP check or ACH payment with its reference) and import CSV statements. Runs as finance@.
/// </summary>
internal sealed class CashKit : IAsyncDisposable
{
    /// <summary>The CSV layout the kit's statements use (signed amount column).</summary>
    public static readonly CsvColumnMapping Csv = new("Date", "Description", Amount: "Amount", CheckNumber: "Check", Reference: "Ref", DateFormat: "yyyy-MM-dd");

    private CashKit(SeededHost seeded, ProjectAccountingFixture fx)
    {
        Seeded = seeded;
        Fx = fx;
    }

    public SeededHost Seeded { get; }
    public ProjectAccountingFixture Fx { get; }
    public BankAccountDto Bank { get; private set; } = default!;
    public IReadOnlyDictionary<string, Guid> Accounts { get; private set; } = new Dictionary<string, Guid>();

    public IBankAccountService BankAccounts => Get<IBankAccountService>();
    public IBankStatementService Statements => Get<IBankStatementService>();
    public IBankReconciliationService Reconciliations => Get<IBankReconciliationService>();
    public ICashForecastService Forecast => Get<ICashForecastService>();
    public IJournalService Journal => Get<IJournalService>();
    public IWorkbenchDbContextFactory Factory => Get<IWorkbenchDbContextFactory>();

    public T Get<T>() where T : notnull => Seeded.Host.Get<T>();

    public CashKit As(string email)
    {
        Seeded.As(email);
        return this;
    }

    public Guid Account(string number) => Accounts[number];

    public static async Task<CashKit> CreateAsync()
    {
        var seeded = await SeededHost.CreateAsync();
        var fx = await ProjectAccountingFixture.SeedAsync(seeded);
        var kit = new CashKit(seeded, fx).As(Finance);
        await kit.Get<IChartOfAccountsService>().InstallDefaultChartAsync();
        kit.Accounts = (await kit.Get<IChartOfAccountsService>().ListAccountsAsync()).ToDictionary(a => a.Number, a => a.Id);
        kit.Bank = await kit.BankAccounts.CreateAsync(new SaveBankAccountRequest("Operating", "First Capital Bank", "123456784321", "054001725", kit.Account("1010")));
        return kit;
    }

    /// <summary>A customer receipt as billing posts it: debit cash, credit AR billed, source Billing with the payment's id.</summary>
    public async Task<JournalEntryDto> ReceiptAsync(DateOnly date, decimal amount, string reference)
    {
        await using var db = await Factory.CreateAsync();
        var customerId = await db.Set<Customer>().Select(c => c.Id).FirstAsync();
        var payment = new Payment { CustomerId = customerId, ReceivedOn = date, Amount = amount, Method = BillingMethod.Eft, Reference = reference, CreatedBy = Finance };
        db.Set<Payment>().Add(payment);
        await db.SaveChangesAsync();
        return await Journal.CreateAndPostAsync(new SaveJournalEntryRequest(date, $"Customer payment {reference}",
            [new JournalLineInput(Account("1010"), amount, 0), new JournalLineInput(Account("1110"), 0, amount)], JournalEntrySource.Billing, payment.Id));
    }

    /// <summary>A vendor payment as AP posts it: debit AP, credit cash, source AP with the payment's id and check/ACH reference.</summary>
    public async Task<JournalEntryDto> VendorPaymentAsync(DateOnly date, decimal amount, string reference, bool check = true)
    {
        await using var db = await Factory.CreateAsync();
        var payment = new ApPayment
        {
            RunId = Guid.NewGuid(), BillId = Guid.NewGuid(), VendorId = Guid.NewGuid(), Amount = amount, Reference = reference, PaidOn = date,
            Method = check ? PurchasingMethod.Check : PurchasingMethod.Ach, Status = ApPaymentStatus.Paid,
        };
        db.Set<ApPayment>().Add(payment);
        await db.SaveChangesAsync();
        return await Journal.CreateAndPostAsync(new SaveJournalEntryRequest(date, $"Vendor payment {reference}",
            [new JournalLineInput(Account("2010"), amount, 0), new JournalLineInput(Account("1010"), 0, amount)], JournalEntrySource.AP, payment.Id));
    }

    /// <summary>A manual cash entry (a wire received, owner's capital...): positive debits cash.</summary>
    public Task<JournalEntryDto> ManualCashAsync(DateOnly date, decimal amount, string description) =>
        Journal.CreateAndPostAsync(new SaveJournalEntryRequest(date, description, amount > 0
            ? [new JournalLineInput(Account("1010"), amount, 0), new JournalLineInput(Account("3100"), 0, amount)]
            : [new JournalLineInput(Account("6230"), -amount, 0), new JournalLineInput(Account("1010"), 0, -amount)]));

    /// <summary>Imports a CSV statement from (date, description, signed amount, check, ref) rows.</summary>
    public Task<StatementImportResultDto> ImportAsync(params (string Date, string Description, decimal Amount, string? Check, string? Ref)[] rows) =>
        ImportAsync(null, rows);

    public Task<StatementImportResultDto> ImportAsync(decimal? closingBalance, params (string Date, string Description, decimal Amount, string? Check, string? Ref)[] rows)
    {
        var text = "Date,Description,Amount,Check,Ref\n" + string.Join("\n", rows.Select(r =>
            $"{r.Date},\"{r.Description}\",{r.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)},{r.Check},{r.Ref}"));
        return Statements.ImportAsync(new ImportStatementRequest(Bank.Id, "statement.csv", text, StatementFileFormat.Csv, Csv, closingBalance));
    }

    public async Task<List<BankItemDto>> ItemsAsync() => (await Statements.ListItemsAsync(Bank.Id)).ToList();

    /// <summary>The cash line of a posted entry.</summary>
    public Guid CashLine(JournalEntryDto entry) => entry.Lines.Single(l => l.AccountId == Account("1010")).Id;

    public async Task<List<JournalEntry>> EntriesAsync(JournalSource source)
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines).Where(e => e.Source == source).ToListAsync();
    }

    public ValueTask DisposeAsync() => Seeded.DisposeAsync();

    /// <summary>The repository's samples/bank folder.</summary>
    public static string Sample(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "bank", fileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"samples/bank/{fileName} not found above {AppContext.BaseDirectory}");
    }
}

/// <summary>A clock stopped at one instant.</summary>
internal sealed class FixedClock(DateTime utc) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
