using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.BankFiles;
using Workbench.Application.Billing;
using Workbench.Application.Collaboration;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Application.SalesTax;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.Cash;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Domain.SalesTax;
using PaymentMethod = Workbench.Domain.Purchasing.PaymentMethod;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for bank payment files and sales tax. ACH settings on the seeded operating account; Nettle Systems
/// (NETS) with approved ACH details (asked for by pm@, approved by dev@), a posted bill for network switches with use tax accrued
/// (the vendor didn't charge tax), and an approved ACH payment run ready for its NACHA file. Sales tax: Fairfax VA, Montgomery MD
/// and Washington DC at 6%, codes TX/NT/RS, and Northern Virginia Data Centers (commercial, ship-to Fairfax) with an approved
/// invoice taxed at 6%. Government customers stay exempt. Runs once, after the cash and currency seeds.
/// </summary>
internal static class BankFilesTaxSeed
{
    public const string AchVendorCode = "NETS";
    public const string TaxCustomerCode = "NVDC";
    private const string Actor = "pm@example.com";
    private const string Approver = "dev@workbench.local";

    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<TaxJurisdiction>().AnyAsync(cancellationToken) || await db.Set<Vendor>().AnyAsync(v => v.Code == AchVendorCode, cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Start <= today && p.End >= today && p.Status == PeriodStatus.Open, cancellationToken);
        var it = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == "6230", cancellationToken);
        var ops = await db.Set<OrgUnit>().AsNoTracking().FirstOrDefaultAsync(o => o.Code == "OPS", cancellationToken);
        var bank = await db.Set<BankAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Name == "Operating account", cancellationToken);
        if (period is null || it is null || ops is null || bank is null)
        {
            return;
        }

        // ---- Sales tax setup ----
        TaxJurisdiction Jurisdiction(string code, string name, string state, string? county, string? city) => new() { Code = code, Name = name, State = state, County = county, City = city, CreatedAtUtc = now };
        var fairfax = Jurisdiction("VA-FAIRFAX", "Fairfax County, VA", "VA", "Fairfax", null);
        var montgomery = Jurisdiction("MD-MONTGOMERY", "Montgomery County, MD", "MD", "Montgomery", null);
        var dc = Jurisdiction("DC-WASHINGTON", "Washington, DC", "DC", null, "Washington");
        db.AddRange(fairfax, montgomery, dc);
        var effective = new DateOnly(today.Year - 1, 1, 1);
        TaxRate Rate(TaxJurisdiction j, decimal state, decimal county = 0, decimal special = 0) =>
            new() { JurisdictionId = j.Id, EffectiveDate = effective, StateRate = state, CountyRate = county, SpecialRate = special, Source = "Manual", CreatedBy = Actor, CreatedAtUtc = now };
        db.AddRange(Rate(fairfax, 0.043m, 0.01m, 0.007m), Rate(montgomery, 0.06m), Rate(dc, 0.06m));
        TaxCode Code(string code, string name, TaxTreatment treatment) => new() { Code = code, Name = name, Treatment = treatment, UpdatedBy = Actor, UpdatedAtUtc = now };
        var taxable = Code("TX", "Taxable goods", TaxTreatment.Taxable);
        var services_ = Code("NT", "Services (not taxed)", TaxTreatment.Exempt);
        db.AddRange(taxable, services_, Code("RS", "Bought for resale", TaxTreatment.Resale));
        TaxCodeAssignment Assign(TaxCodeTarget target, string key, TaxCode code) => new() { Target = target, TargetKey = key, TaxCodeId = code.Id, UpdatedBy = Actor, UpdatedAtUtc = now };
        db.AddRange(
            Assign(TaxCodeTarget.InvoiceLineKind, nameof(InvoiceLineKind.Labor), services_),
            Assign(TaxCodeTarget.InvoiceLineKind, nameof(InvoiceLineKind.Milestone), taxable),
            Assign(TaxCodeTarget.OdcType, nameof(OdcType.Materials), taxable),
            Assign(TaxCodeTarget.OdcType, nameof(OdcType.Equipment), taxable));
        db.Add(new SalesTaxSettings { HomeJurisdictionId = fairfax.Id, DefaultSalesTaxCodeId = taxable.Id, DefaultPurchaseTaxCodeId = services_.Id, UpdatedBy = Actor, UpdatedAtUtc = now });

        var customer = new Customer { Code = TaxCustomerCode, Name = "Northern Virginia Data Centers LLC", Category = "Commercial" };
        db.Add(customer);
        db.Add(new CustomerTaxProfile { CustomerId = customer.Id, ShipToJurisdictionId = fairfax.Id, UpdatedBy = Actor, UpdatedAtUtc = now });
        db.Add(new CustomerBillingProfile { CustomerId = customer.Id, PaymentTermsDays = 30, BillingAddress = "Northern Virginia Data Centers LLC\n21000 Atlantic Blvd\nDulles, VA 20166", UpdatedBy = Actor, UpdatedAtUtc = now });
        var contract = new Contract
        {
            CustomerId = customer.Id,
            ContractNumber = "NVDC-2026-01",
            Name = "Rack and cabling build-out (commercial)",
            Type = ContractType.FirmFixedPrice,
            IsPrime = true,
            PeriodStart = period.Start.AddMonths(-1),
            PeriodEnd = period.Start.AddMonths(11).AddDays(-1),
            CeilingValue = 120_000m,
            FundedValue = 120_000m,
        };
        db.Add(contract);
        var invoice = new Invoice
        {
            ContractId = contract.Id,
            CustomerId = customer.Id,
            Sequence = 1,
            Number = "NVDC-0001",
            Type = InvoiceType.FixedPriceMilestone,
            PeriodFrom = period.Start,
            PeriodTo = period.End,
            InvoiceDate = period.Start,
            DueDate = period.Start.AddDays(30),
            Notes = "Delivery 1: racks and cabling for hall B (seeded, taxable).",
            CreatedBy = Actor,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        invoice.Lines.Add(new InvoiceLine { InvoiceId = invoice.Id, LineNumber = 1, Kind = InvoiceLineKind.Milestone, Description = "Delivery 1: 12 racks with cabling", Quantity = 1, Rate = 18_400m, Amount = 18_400m, IsGenerated = false });
        invoice.Lines.Add(new InvoiceLine { InvoiceId = invoice.Id, LineNumber = 2, Kind = InvoiceLineKind.Labor, Description = "Installation labor", Quantity = 40, Rate = 95m, Amount = 3_800m, IsGenerated = false });
        invoice.Recalculate();
        db.Add(invoice);

        // ---- Bank files ----
        db.Add(new BankAchSettings
        {
            BankAccountId = bank.Id,
            ImmediateDestination = bank.RoutingNumber ?? "054001725",
            ImmediateDestinationName = "FIRST CAPITAL BANK",
            ImmediateOrigin = "1521234567",
            ImmediateOriginName = "WORKBENCH FEDERAL LLC",
            CompanyIdentification = "1521234567",
            CompanyName = "WORKBENCH FED",
            EntryDescription = "VENDOR PAY",
            OriginatingDfi = (bank.RoutingNumber ?? "054001725")[..8],
            PositivePayLayoutId = PositivePayWriter.StandardCsvId,
            UpdatedBy = Actor,
            UpdatedAtUtc = now,
        });

        var vendor = new Vendor
        {
            Code = AchVendorCode,
            Name = "Nettle Systems Inc",
            PaymentTermsDays = 15,
            BusinessSize = "Small",
            RemitToAddress = "Nettle Systems Inc\n4100 Lafayette Center Dr\nChantilly, VA 20151",
            ContactName = "Dana Okafor",
            ContactEmail = "ar@nettlesystems.example",
            CreatedAtUtc = now,
        };
        db.Add(vendor);
        var protector = new VendorBankProtector(services.GetRequiredService<IDataProtectionProvider>());
        var change = new VendorBankChange
        {
            VendorId = vendor.Id,
            Status = VendorBankChangeStatus.Approved,
            AccountHolderName = "NETTLE SYSTEMS INC",
            ProtectedRoutingNumber = protector.ProtectRouting("021000021"),
            ProtectedAccountNumber = protector.ProtectAccount("770012345678"),
            RoutingLast4 = "0021",
            AccountLast4 = "5678",
            RemittanceEmail = "remit@nettlesystems.example",
            Reason = "Bank letter with the W-9; confirmed by phone with Dana Okafor at the number on the 2025 contract (seeded)",
            RequestedBy = Actor,
            RequestedAtUtc = now.AddDays(-7),
            DecidedBy = Approver,
            DecidedAtUtc = now.AddDays(-6),
        };
        db.Add(change);
        db.Add(new VendorBankAccount
        {
            VendorId = vendor.Id,
            AccountHolderName = change.AccountHolderName,
            ProtectedRoutingNumber = change.ProtectedRoutingNumber,
            ProtectedAccountNumber = change.ProtectedAccountNumber,
            RoutingLast4 = change.RoutingLast4,
            AccountLast4 = change.AccountLast4,
            RemittanceEmail = change.RemittanceEmail,
            ChangeId = change.Id,
            ApprovedBy = Approver,
            ApprovedAtUtc = now.AddDays(-6),
        });
        await db.SaveChangesAsync(cancellationToken);

        // Through the real services, so they post like real work.
        var user = new SeedUser { Email = Actor };
        var clock = TimeProvider.System;
        var factory = services.GetRequiredService<IWorkbenchDbContextFactory>();
        var audit = new AuditRecorder(user, clock);
        var ledger = new GeneralLedger(audit, clock);
        var engine = new PostingEngine(factory, ledger, NullLogger<PostingEngine>.Instance);
        var access = new AccessControl(factory, user);
        var taxEngine = new TableSalesTaxEngine(factory);
        var useTax = new UseTaxAccrual(factory, taxEngine, ledger, audit, clock, NullLogger<UseTaxAccrual>.Instance);
        var bills = new ApBillService(factory, audit, ledger, engine, access, new NoNotifications(), Options.Create(new PurchasingOptions()), clock, NullLogger<ApBillService>.Instance, useTax: useTax);
        var billing = new BillingLedger(ledger);
        var generator = new InvoiceGenerator(new BillingCostSource([]));
        var invoices = new InvoiceService(factory, generator, billing, audit, clock, new InvoiceDocuments(Options.Create(new BillingOptions())), new UnbilledAccruals(generator, billing, audit),
            salesTax: new InvoiceTaxApplier(factory, taxEngine, audit, clock));

        // $18,400 of racks is taxable at 6% ($1,104.00); the installation labor isn't.
        await invoices.ApproveAsync(invoice.Id, new ApproveInvoiceRequest(), cancellationToken);

        // Network switches for the office: the vendor didn't charge tax, so 6% use tax accrues at Fairfax.
        var bill = await bills.CreateAsync(new SaveBillRequest(vendor.Id, "NS-30418", period.Start,
            [new SaveBillLineRequest(BillLineKind.Account, "Core network switches (2)", 6_480m, AccountId: it.Id, OrgUnitId: ops.Id)],
            Description: "Office network refresh"), cancellationToken);
        await using (var tax = await factory.CreateAsync(cancellationToken))
        {
            tax.Set<ApBillTax>().Add(new ApBillTax { BillId = bill.Bill.Id, TaxCodeOverrideId = taxable.Id, UpdatedBy = Actor, UpdatedAtUtc = now });
            await tax.SaveChangesAsync(cancellationToken);
        }

        await bills.SubmitAsync(bill.Bill.Id, new SubmitBillRequest(), cancellationToken);
        user.Email = Approver;
        await bills.ApproveAsync(bill.Bill.Id, cancellationToken);

        // An ACH run for the bill, approved by someone other than its creator and ready for its NACHA file.
        await using var write = await factory.CreateAsync(cancellationToken);
        var posted = await write.Set<ApBill>().AsNoTracking().FirstAsync(b => b.Id == bill.Bill.Id, cancellationToken);
        if (posted.Status != ApBillStatus.Posted)
        {
            return;
        }

        var numbers = await write.Set<ApPaymentRun>().Select(r => r.Number).ToListAsync(cancellationToken);
        var run = new ApPaymentRun
        {
            Number = PurchasingSupportNumbers.Next(numbers, "PR"),
            PaymentDate = today,
            Method = PaymentMethod.Ach,
            Status = PaymentRunStatus.Approved,
            CreatedBy = Actor,
            CreatedAtUtc = now,
            ApprovedBy = Approver,
            ApprovedAtUtc = now,
        };
        write.Set<ApPaymentRun>().Add(run);
        write.Set<ApPayment>().Add(new ApPayment { RunId = run.Id, BillId = posted.Id, VendorId = vendor.Id, Amount = posted.Balance, Method = PaymentMethod.Ach });
        await write.SaveChangesAsync(cancellationToken);
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

    /// <summary>Run numbers like "PR-000004", the next after the highest.</summary>
    private static class PurchasingSupportNumbers
    {
        public static string Next(IEnumerable<string> existing, string prefix)
        {
            var max = existing.Select(n => n.StartsWith(prefix + "-", StringComparison.Ordinal) && int.TryParse(n[(prefix.Length + 1)..], out var x) ? x : 0).DefaultIfEmpty(0).Max();
            return $"{prefix}-{max + 1:000000}";
        }
    }
}
