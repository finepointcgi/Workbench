using Microsoft.EntityFrameworkCore;
using Workbench.Application.SalesTax;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.SalesTax;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.SalesTax;
using Workbench.Tests.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.SalesTax;

/// <summary>
/// Sales and use tax over the real registrations (<see cref="PurchasingKit"/>: period 2026-10 open, default chart, contract C1
/// for customer ARMY with no category). Fairfax, VA is 6% (state 4.3%, county 1%, NoVA district 0.7%) from Oct 1 and 7% from
/// Nov 1; Montgomery, MD is 6% state only. Codes: TX taxable, NT exempt, RS resale. Milestones and materials are taxable, labor
/// isn't; sales default to TX, purchases to NT; the home jurisdiction is Fairfax.
/// </summary>
public sealed class SalesTaxTests
{
    private static readonly DateOnly Oct1 = new(2026, 10, 1);
    private static readonly DateOnly Oct31 = new(2026, 10, 31);

    private sealed record Setup(PurchasingKit Kit, Guid Fairfax, Guid Montgomery, Guid Taxable, Guid Exempt, Guid CustomerId) : IAsyncDisposable
    {
        public ISalesTaxService Tax => Kit.Get<ISalesTaxService>();

        public ValueTask DisposeAsync() => Kit.DisposeAsync();
    }

    private static async Task<Setup> SetupAsync()
    {
        var kit = await PurchasingKit.CreateAsync();
        kit.As(Finance);
        var tax = kit.Get<ISalesTaxService>();
        var fairfax = await tax.SaveJurisdictionAsync(null, new SaveTaxJurisdictionRequest("VA-FAIRFAX", "Fairfax County, VA", "va", "Fairfax"));
        await tax.AddRateAsync(fairfax.Id, new AddTaxRateRequest(Oct1, 0.043m, 0.01m, 0m, 0.007m));
        await tax.AddRateAsync(fairfax.Id, new AddTaxRateRequest(new DateOnly(2026, 11, 1), 0.053m, 0.01m, 0m, 0.007m));
        var montgomery = await tax.SaveJurisdictionAsync(null, new SaveTaxJurisdictionRequest("MD-MONTGOMERY", "Montgomery County, MD", "MD", "Montgomery"));
        await tax.AddRateAsync(montgomery.Id, new AddTaxRateRequest(Oct1, 0.06m));
        var tx = await tax.SaveTaxCodeAsync(null, new SaveTaxCodeRequest("TX", "Taxable goods", TaxTreatmentKind.Taxable));
        var nt = await tax.SaveTaxCodeAsync(null, new SaveTaxCodeRequest("NT", "Services (not taxed)", TaxTreatmentKind.Exempt));
        await tax.SaveTaxCodeAsync(null, new SaveTaxCodeRequest("RS", "Bought for resale", TaxTreatmentKind.Resale));
        await tax.SaveAssignmentAsync(new SaveTaxCodeAssignmentRequest(TaxCodeTargetKind.InvoiceLineKind, "Labor", nt.Id));
        await tax.SaveAssignmentAsync(new SaveTaxCodeAssignmentRequest(TaxCodeTargetKind.InvoiceLineKind, "Milestone", tx.Id));
        await tax.SaveAssignmentAsync(new SaveTaxCodeAssignmentRequest(TaxCodeTargetKind.OdcType, "Materials", tx.Id));
        await tax.SaveSettingsAsync(new SaveSalesTaxSettingsRequest(null, null, null, fairfax.Id, tx.Id, nt.Id));
        Guid customerId;
        await using (var db = await kit.Factory.CreateAsync())
        {
            customerId = (await db.Set<Contract>().FirstAsync(c => c.Id == kit.Fx.Contract1)).CustomerId;
        }

        await tax.SaveCustomerAsync(customerId, new SaveCustomerTaxRequest(CustomerTaxState.Default, fairfax.Id));
        return new Setup(kit, fairfax.Id, montgomery.Id, tx.Id, nt.Id, customerId);
    }

    private static int _sequence;

    /// <summary>A draft invoice on C1 with a $1,000.00 milestone and $500.00 of labor.</summary>
    private static async Task<Guid> DraftInvoiceAsync(Setup s, DateOnly? date = null)
    {
        var day = date ?? PurchasingKit.Day;
        var invoice = new Invoice
        {
            ContractId = s.Kit.Fx.Contract1, CustomerId = s.CustomerId, Sequence = Interlocked.Increment(ref _sequence), Number = $"C1-{Guid.NewGuid().ToString()[..4]}", Type = InvoiceType.FixedPriceMilestone,
            PeriodFrom = Oct1, PeriodTo = Oct31, InvoiceDate = day, DueDate = day.AddDays(30), CreatedBy = Finance, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        invoice.Lines.Add(new InvoiceLine { InvoiceId = invoice.Id, LineNumber = 1, Kind = InvoiceLineKind.Milestone, Description = "Delivery 1", Quantity = 1, Rate = 1_000m, Amount = 1_000m, IsGenerated = false });
        invoice.Lines.Add(new InvoiceLine { InvoiceId = invoice.Id, LineNumber = 2, Kind = InvoiceLineKind.Labor, Description = "Installation labor", Quantity = 5, Rate = 100m, Amount = 500m, IsGenerated = false });
        invoice.Recalculate();
        await using var db = await s.Kit.Factory.CreateAsync();
        db.Set<Invoice>().Add(invoice);
        await db.SaveChangesAsync();
        return invoice.Id;
    }

    /// <summary>The tax accounts are added to the chart when first used, after the kit cached its account ids.</summary>
    private static async Task<Guid> AccountIdAsync(PurchasingKit kit, string number)
    {
        await using var db = await kit.Factory.CreateAsync();
        return await db.Set<Account>().Where(a => a.Number == number).Select(a => a.Id).SingleAsync();
    }

    private static async Task<decimal> BalanceAsync(PurchasingKit kit, string number)
    {
        var id = await AccountIdAsync(kit, number);
        return (await kit.EntriesAsync()).SelectMany(e => e.Lines).Where(l => l.AccountId == id).Sum(l => l.Debit - l.Credit);
    }

    private static async Task SetCategoryAsync(Setup s, string? category)
    {
        await using var db = await s.Kit.Factory.CreateAsync();
        (await db.Set<Customer>().FirstAsync(c => c.Id == s.CustomerId)).Category = category;
        await db.SaveChangesAsync();
    }

    // ---- Arithmetic ----

    [Fact]
    public void Tax_rounds_half_away_from_zero_and_the_levels_add_up_to_the_total()
    {
        var rate = new TaxRate { StateRate = 0.043m, CountyRate = 0.01m, CityRate = 0m, SpecialRate = 0.007m, Source = "Test", CreatedBy = "t" };
        var where = new TaxJurisdictionRef(Guid.NewGuid(), "VA-FAIRFAX", "Fairfax", "VA", "Fairfax", null);
        var r = SalesTaxMath.Calculate(new TaxCalculationRequest(where, Oct1,
            [new TaxLine("a", 10.25m, TaxTreatment.Taxable), new TaxLine("b", 3m, TaxTreatment.Exempt), new TaxLine("c", 1m, TaxTreatment.Resale)], IsPurchase: false), rate, "Rate tables");

        Assert.Equal(11.25m, r.TaxableAmount); // resale items are taxed when we sell them
        Assert.Equal(3m, r.ExemptAmount);
        Assert.Equal(0.68m, r.Tax); // 11.25 × 6% = 0.675 → 0.68
        Assert.Equal(0.48m, r.StateTax); // 0.48375
        Assert.Equal(0.11m, r.CountyTax); // 0.1125
        Assert.Equal(0.09m, r.SpecialTax); // what's left
        Assert.Equal(r.Tax, r.StateTax + r.CountyTax + r.CityTax + r.SpecialTax);

        var purchase = SalesTaxMath.Calculate(new TaxCalculationRequest(where, Oct1, [new TaxLine("c", 1m, TaxTreatment.Resale)], IsPurchase: true), rate, "Rate tables");
        Assert.Equal(0m, purchase.Tax); // bought for resale: no use tax
        Assert.Equal(-0.62m, SalesTaxMath.Round(-0.615m));
    }

    // ---- Invoices ----

    [Fact]
    public async Task A_commercial_invoice_is_taxed_at_the_ship_to_rate_and_the_tax_posts_to_the_liability()
    {
        await using var s = await SetupAsync();
        var id = await DraftInvoiceAsync(s);

        var approved = await s.Kit.Get<IInvoiceService>().ApproveAsync(id, new ApproveInvoiceRequest());

        var taxLine = Assert.Single(approved.Lines, l => l.Kind == InvoiceLineType.SalesTax);
        Assert.Equal(60m, taxLine.Amount); // 6% of the $1,000 milestone; labor isn't taxed
        Assert.Equal(1_560m, approved.Summary.Total);
        var tax = await s.Tax.GetInvoiceTaxAsync(id);
        Assert.Equal(1_000m, tax.TaxableAmount);
        Assert.Equal(500m, tax.ExemptAmount);
        Assert.Equal(0.06m, tax.Rate);
        Assert.Equal("Fairfax County, VA", tax.Jurisdiction);

        var entry = (await s.Kit.EntriesAsync(JournalSource.Billing)).Single(e => e.SourceId == id);
        var payable = await AccountIdAsync(s.Kit, SalesTaxSupport.SalesTaxPayableNumber);
        Assert.Equal(60m, entry.Lines.Where(l => l.AccountId == payable).Sum(l => l.Credit));
        Assert.Equal(1_560m, entry.Lines.Where(l => l.AccountId == s.Kit.Account("1110")).Sum(l => l.Debit));
        Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Tax.CalculateInvoiceTaxAsync(id)); // fixed once approved
    }

    [Fact]
    public async Task Rates_are_effective_dated_and_an_invoice_can_override_the_ship_to()
    {
        await using var s = await SetupAsync();
        var october = await s.Tax.CalculateInvoiceTaxAsync(await DraftInvoiceAsync(s));
        Assert.Equal(60m, october.TaxAmount);

        var quote = await s.Tax.QuoteAsync(new TaxQuoteRequest(s.Fairfax, new DateOnly(2026, 11, 2), 1_000m));
        Assert.Equal(0.07m, quote.Rate);
        Assert.Equal(70m, quote.Tax);

        var id = await DraftInvoiceAsync(s);
        var maryland = await s.Tax.SetInvoiceTaxAsync(id, new SetInvoiceTaxRequest(s.Montgomery));
        Assert.Equal("Montgomery County, MD", maryland.Jurisdiction);
        Assert.Equal(60m, maryland.TaxAmount);

        // Recalculating replaces the tax line rather than adding another.
        await s.Tax.CalculateInvoiceTaxAsync(id);
        var invoice = await s.Kit.Get<IInvoiceService>().GetAsync(id);
        Assert.Single(invoice.Lines, l => l.Kind == InvoiceLineType.SalesTax);
        Assert.Equal(1_560m, invoice.Summary.Total);
    }

    [Fact]
    public async Task Government_customers_are_exempt_by_default_unless_marked_taxable()
    {
        await using var s = await SetupAsync();
        await SetCategoryAsync(s, "DoD");

        var exempt = await s.Tax.CalculateInvoiceTaxAsync(await DraftInvoiceAsync(s));
        Assert.Equal(0m, exempt.TaxAmount);
        Assert.Equal(1_500m, exempt.ExemptAmount);
        Assert.Equal("Government customer", exempt.ExemptionReason);
        Assert.True((await s.Tax.ListCustomersAsync()).Single(c => c.CustomerId == s.CustomerId).IsExemptByDefault);

        await s.Tax.SaveCustomerAsync(s.CustomerId, new SaveCustomerTaxRequest(CustomerTaxState.Taxable, s.Fairfax));
        Assert.Equal(60m, (await s.Tax.CalculateInvoiceTaxAsync(await DraftInvoiceAsync(s))).TaxAmount);
    }

    [Fact]
    public async Task A_current_exemption_certificate_exempts_the_sale_and_an_expired_one_does_not()
    {
        await using var s = await SetupAsync();
        await s.Tax.SaveCertificateAsync(s.CustomerId, null, new SaveTaxExemptionCertificateRequest("RS-2025-01", "VA", "Resale", new DateOnly(2025, 1, 1), new DateOnly(2026, 9, 30)));

        var expired = await s.Tax.CalculateInvoiceTaxAsync(await DraftInvoiceAsync(s));
        Assert.Equal(60m, expired.TaxAmount);
        Assert.Contains("expired on 2026-09-30", expired.ExemptionReason, StringComparison.Ordinal);
        var cert = Assert.Single((await s.Tax.ListCustomersAsync()).Single(c => c.CustomerId == s.CustomerId).Certificates);
        Assert.True(cert.IsExpired);

        await s.Tax.SaveCertificateAsync(s.CustomerId, null, new SaveTaxExemptionCertificateRequest("MD-77", "MD", "Resale", Oct1, null));
        Assert.Equal(60m, (await s.Tax.CalculateInvoiceTaxAsync(await DraftInvoiceAsync(s))).TaxAmount); // another state's certificate doesn't count

        await s.Tax.SaveCertificateAsync(s.CustomerId, cert.Id, new SaveTaxExemptionCertificateRequest("RS-2025-01", "VA", "Resale", new DateOnly(2025, 1, 1), new DateOnly(2027, 9, 30)));
        var renewed = await s.Tax.CalculateInvoiceTaxAsync(await DraftInvoiceAsync(s));
        Assert.Equal(0m, renewed.TaxAmount);
        Assert.Equal("RS-2025-01", renewed.CertificateNumber);
        await Assert.ThrowsAsync<ArgumentException>(() => s.Tax.SaveCertificateAsync(s.CustomerId, null,
            new SaveTaxExemptionCertificateRequest("X", "VA", "Resale", Oct1, Oct1.AddDays(-1))));
    }

    // ---- Use tax ----

    [Fact]
    public async Task Use_tax_accrues_when_the_vendor_charged_none_on_a_taxable_purchase()
    {
        await using var s = await SetupAsync();
        var kit = s.Kit;

        // Materials are taxable; the vendor didn't charge tax, so 6% use tax accrues at the home jurisdiction.
        var materials = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "M-1", PurchasingKit.Day, [PurchasingKit.ProjectLine(kit.Fx.Wbs1, 1_000m)]));
        var accrued = await s.Tax.GetBillTaxAsync(materials.Bill.Id);
        Assert.Equal(60m, accrued.UseTaxAmount);
        Assert.Equal(1_000m, accrued.TaxableAmount);
        Assert.NotNull(accrued.UseTaxJournalEntryId);
        Assert.Equal(60m, await BalanceAsync(kit, SalesTaxSupport.UseTaxExpenseNumber));
        Assert.Equal(-60m, await BalanceAsync(kit, SalesTaxSupport.UseTaxPayableNumber));

        // Services (the default purchase code) aren't taxed.
        var services = await kit.PostedBillAsync(new SaveBillRequest(kit.Bolt.Id, "S-1", PurchasingKit.Day, [kit.OverheadLine(2_000m)]));
        Assert.Equal(0m, (await s.Tax.GetBillTaxAsync(services.Bill.Id)).UseTaxAmount);

        // The vendor did charge tax after all: the accrual is reversed.
        var corrected = await s.Tax.SetBillTaxAsync(materials.Bill.Id, new SetBillTaxRequest(null, 60m));
        Assert.Equal(0m, corrected.UseTaxAmount);
        Assert.Null(corrected.UseTaxJournalEntryId);
        Assert.Equal(0m, await BalanceAsync(kit, SalesTaxSupport.UseTaxPayableNumber));
    }

    [Fact]
    public async Task Vendor_charged_tax_recorded_before_posting_means_no_use_tax()
    {
        await using var s = await SetupAsync();
        var kit = s.Kit;
        kit.As(Finance);
        var bill = await kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, "M-2", PurchasingKit.Day, [PurchasingKit.ProjectLine(kit.Fx.Wbs1, 1_060m)]));
        var pending = await s.Tax.SetBillTaxAsync(bill.Bill.Id, new SetBillTaxRequest(s.Montgomery, 60m));
        Assert.Equal("Montgomery County, MD", pending.Jurisdiction);
        Assert.Equal(0m, pending.UseTaxAmount);

        await kit.Bills.SubmitAsync(bill.Bill.Id, new SubmitBillRequest());
        kit.As(Owner);
        await kit.Bills.ApproveAsync(bill.Bill.Id);
        kit.As(Finance);
        Assert.Equal(BillState.Posted, (await kit.Bills.GetAsync(bill.Bill.Id)).Bill.Status);
        Assert.Equal(0m, (await s.Tax.GetBillTaxAsync(bill.Bill.Id)).UseTaxAmount);
        Assert.Equal(0m, await BalanceAsync(kit, SalesTaxSupport.UseTaxPayableNumber));
    }

    // ---- Rates import and the return ----

    [Fact]
    public async Task Rate_tables_import_from_csv()
    {
        await using var s = await SetupAsync();
        var result = await s.Tax.ImportRatesAsync(new ImportTaxRatesRequest(
            "Code,Name,State,County,City,EffectiveDate,StateRate,CountyRate,CityRate,SpecialRate\n" +
            "DC-DC,\"Washington, DC\",DC,,Washington,2026-10-01,6%,,,\n" +
            "VA-FAIRFAX,Fairfax County,VA,Fairfax,,2026-10-01,0.043,0.01,0,0.007\n" +
            "VA-ARL,Arlington County,VA,Arlington,,2026-10-01,4.3%,1%,,0.7%\n" +
            "BAD,Bad,VA,,,someday,1,,,\n"));

        Assert.Equal(2, result.JurisdictionsCreated);
        Assert.Equal(2, result.RatesAdded);
        Assert.Equal(1, result.RatesSkipped); // Fairfax already has an Oct 1 rate
        Assert.Contains(result.Errors, e => e.StartsWith("Line 5", StringComparison.Ordinal));
        var list = await s.Tax.ListJurisdictionsAsync();
        Assert.Equal(0.06m, list.Single(j => j.Code == "DC-DC").Rates.Single().CombinedRate);
        Assert.Equal("Washington, DC", list.Single(j => j.Code == "DC-DC").Name);
        Assert.Equal(0.06m, list.Single(j => j.Code == "VA-ARL").Rates.Single().CombinedRate);
    }

    [Fact]
    public async Task The_return_adds_up_sales_and_use_tax_by_jurisdiction()
    {
        await using var s = await SetupAsync();
        await s.Kit.Get<IInvoiceService>().ApproveAsync(await DraftInvoiceAsync(s), new ApproveInvoiceRequest());
        await s.Tax.CalculateInvoiceTaxAsync(await DraftInvoiceAsync(s)); // a draft doesn't count
        await s.Kit.PostedBillAsync(new SaveBillRequest(s.Kit.Prima.Id, "M-1", PurchasingKit.Day, [PurchasingKit.ProjectLine(s.Kit.Fx.Wbs1, 500m)]));

        var report = await s.Tax.GetReturnAsync(Oct1, Oct31);

        var va = Assert.Single(report.Rows);
        Assert.Equal("VA-FAIRFAX", va.Code);
        Assert.Equal(1_500m, va.GrossSales);
        Assert.Equal(1_000m, va.TaxableSales);
        Assert.Equal(60m, va.SalesTax);
        Assert.Equal(43m, va.StateTax);
        Assert.Equal(10m, va.CountyTax);
        Assert.Equal(7m, va.SpecialTax);
        Assert.Equal(500m, va.TaxablePurchases);
        Assert.Equal(30m, va.UseTax);
        Assert.Equal(90m, report.TotalDue);
        Assert.Empty((await s.Tax.GetReturnAsync(Oct1, Oct31, "MD")).Rows);

        var csv = await s.Tax.ExportReturnCsvAsync(Oct1, Oct31);
        Assert.Contains("VA-FAIRFAX,Fairfax County, VA,VA,1500.00,500.00,1000.00,60.00", csv.Content.Replace("\"", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_finance_manages_sales_tax()
    {
        await using var s = await SetupAsync();
        s.Kit.As(Viewer);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => s.Tax.ListJurisdictionsAsync());
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => s.Tax.GetReturnAsync(Oct1, Oct31));
        s.Kit.As(Owner); // a project manager, not Finance
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => s.Tax.SaveSettingsAsync(new SaveSalesTaxSettingsRequest(null, null, null, null, null, null)));
    }
}
