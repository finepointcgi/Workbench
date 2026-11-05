using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Billing;
using Workbench.Application.Collaboration;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.Currencies;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for multicurrency: USD functional, EUR and GBP (plus CAD, and JPY inactive), spot rates at the
/// start of the month and today, last month's month-end and average rates; Berliner Technik GmbH (invoices in EUR) with an
/// approved, posted €8,000 engineering bill left open; and the UK Ministry of Defence (billed in GBP) on a small firm fixed
/// price contract with an approved £12,000 invoice. Entered through the real services, so they post like real work.
/// Runs once, after the purchasing and billing seeds.
/// </summary>
internal static class CurrencySeed
{
    public const string EuroVendorCode = "BTG";
    public const string PoundCustomerCode = "UKMOD";

    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<Currency>().AnyAsync(cancellationToken) || !await db.Set<Account>().AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Start <= today && p.End >= today && p.Status == PeriodStatus.Open, cancellationToken);
        var engineering = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == "6230", cancellationToken);
        var ops = await db.Set<OrgUnit>().AsNoTracking().FirstOrDefaultAsync(o => o.Code == "OPS", cancellationToken);
        if (period is null || engineering is null || ops is null)
        {
            return;
        }

        const string actor = "pm@example.com";
        db.Add(new CurrencySettings { FunctionalCurrency = "USD", UpdatedBy = actor, UpdatedAtUtc = now });
        db.AddRange(
            new Currency { Code = "USD", Name = "US dollar", UpdatedBy = actor, UpdatedAtUtc = now },
            new Currency { Code = "EUR", Name = "Euro", UpdatedBy = actor, UpdatedAtUtc = now },
            new Currency { Code = "GBP", Name = "Pound sterling", UpdatedBy = actor, UpdatedAtUtc = now },
            new Currency { Code = "CAD", Name = "Canadian dollar", UpdatedBy = actor, UpdatedAtUtc = now },
            new Currency { Code = "JPY", Name = "Japanese yen", Decimals = 0, IsActive = false, UpdatedBy = actor, UpdatedAtUtc = now });

        var start = period.Start;
        var lastMonthEnd = start.AddDays(-1);
        ExchangeRate Rate(string from, ExchangeRateType type, DateOnly date, decimal rate) =>
            new() { FromCurrency = from, ToCurrency = "USD", RateType = type, EffectiveDate = date, Rate = rate, Source = "Manual", CreatedBy = actor, CreatedAtUtc = now };
        var rates = new List<ExchangeRate>
        {
            Rate("EUR", ExchangeRateType.Spot, start.AddMonths(-1), 1.0780m),
            Rate("EUR", ExchangeRateType.Average, lastMonthEnd, 1.0795m),
            Rate("EUR", ExchangeRateType.MonthEnd, lastMonthEnd, 1.0810m),
            Rate("EUR", ExchangeRateType.Spot, start, 1.0850m),
            Rate("GBP", ExchangeRateType.Spot, start.AddMonths(-1), 1.2580m),
            Rate("GBP", ExchangeRateType.Average, lastMonthEnd, 1.2620m),
            Rate("GBP", ExchangeRateType.MonthEnd, lastMonthEnd, 1.2600m),
            Rate("GBP", ExchangeRateType.Spot, start, 1.2650m),
            Rate("CAD", ExchangeRateType.Spot, start, 0.7310m),
        };
        if (today > start)
        {
            rates.Add(Rate("EUR", ExchangeRateType.Spot, today, 1.0920m));
            rates.Add(Rate("GBP", ExchangeRateType.Spot, today, 1.2710m));
        }

        db.AddRange(rates);

        var vendor = new Vendor
        {
            Code = EuroVendorCode,
            Name = "Berliner Technik GmbH",
            PaymentTermsDays = 30,
            RemitToAddress = "Berliner Technik GmbH\nFriedrichstraße 120\n10117 Berlin, Germany",
            ContactEmail = "rechnungen@berliner-technik.example",
            CurrencyCode = "EUR",
            CreatedAtUtc = now,
        };
        var customer = new Customer { Code = PoundCustomerCode, Name = "UK Ministry of Defence (DE&S)", Category = "Foreign government" };
        db.AddRange(vendor, customer);
        db.Add(new CustomerBillingProfile { CustomerId = customer.Id, PaymentTermsDays = 30, CurrencyCode = "GBP", BillingAddress = "DE&S Abbey Wood\nBristol BS34 8JH\nUnited Kingdom", UpdatedBy = actor, UpdatedAtUtc = now });
        var contract = new Contract
        {
            CustomerId = customer.Id,
            ContractNumber = "DESS-2026-0417",
            Name = "Interoperability engineering support (UK)",
            Type = ContractType.FirmFixedPrice,
            PeriodStart = start.AddMonths(-1),
            PeriodEnd = start.AddMonths(11).AddDays(-1),
            CeilingValue = 182_160m,
            FundedValue = 60_720m,
        };
        db.Add(contract);

        // £12,000.00 at 1.2650: billing is worked out in dollars and issued in pounds.
        var invoice = new Invoice
        {
            ContractId = contract.Id,
            CustomerId = customer.Id,
            Sequence = 1,
            Number = "DESS-0001",
            Type = InvoiceType.FixedPriceMilestone,
            PeriodFrom = start,
            PeriodTo = period.End,
            InvoiceDate = start,
            DueDate = start.AddDays(30),
            CurrencyCode = "GBP",
            Notes = "Milestone 1: interface control document accepted (seeded).",
            CreatedBy = actor,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        invoice.Lines.Add(new InvoiceLine
        {
            InvoiceId = invoice.Id,
            LineNumber = 1,
            Kind = InvoiceLineKind.Milestone,
            Description = "Milestone 1: interface control document",
            Quantity = 1,
            Rate = 15_180m,
            Amount = 15_180m,
            IsGenerated = false,
        });
        invoice.Recalculate();
        db.Add(invoice);
        await db.SaveChangesAsync(cancellationToken);

        var user = new SeedUser { Email = actor };
        var clock = TimeProvider.System;
        var factory = services.GetRequiredService<IWorkbenchDbContextFactory>();
        var audit = new AuditRecorder(user, clock);
        var ledger = new GeneralLedger(audit, clock);
        var engine = new PostingEngine(factory, ledger, NullLogger<PostingEngine>.Instance);
        var access = new AccessControl(factory, user);
        var bills = new ApBillService(factory, audit, ledger, engine, access, new NoNotifications(), Options.Create(new PurchasingOptions()), clock, NullLogger<ApBillService>.Instance);
        var billing = new BillingLedger(ledger);
        var generator = new InvoiceGenerator(new BillingCostSource([]));
        var invoices = new InvoiceService(factory, generator, billing, audit, clock, new InvoiceDocuments(Options.Create(new BillingOptions())), new UnbilledAccruals(generator, billing, audit));

        // €8,000.00 at 1.0850 = $8,680.00, an overhead engineering review routed to Finance; approved and posted, left open.
        var bill = await bills.CreateAsync(new SaveBillRequest(vendor.Id, "BT-2026-1187", start,
            [new SaveBillLineRequest(BillLineKind.Account, "Antenna design review, Berlin lab", 8_000m, AccountId: engineering.Id, OrgUnitId: ops.Id)],
            Description: "Engineering review (invoiced in euros)"), cancellationToken);
        await bills.SubmitAsync(bill.Bill.Id, new SubmitBillRequest(), cancellationToken);
        user.Email = "dev@workbench.local";
        await bills.ApproveAsync(bill.Bill.Id, cancellationToken);

        user.Email = actor;
        await invoices.ApproveAsync(invoice.Id, new ApproveInvoiceRequest(), cancellationToken);
        await invoices.MarkSentAsync(invoice.Id, cancellationToken);
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email { get; set; } = "";
    }

    private sealed class NoNotifications : INotificationPublisher
    {
        public Task<bool> PublishAsync(string recipientEmail, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> PublishAsync(IEnumerable<string?> recipientEmails, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
