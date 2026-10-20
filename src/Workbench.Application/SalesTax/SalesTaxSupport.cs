using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Accounting;
using Workbench.Application.ProjectAccounting;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Domain.SalesTax;

namespace Workbench.Application.SalesTax;

/// <summary>Which tax code applies to invoice and bill lines: an assignment for the line's ODC type, kind or account, else the default.</summary>
internal sealed class TaxCodeBook(IReadOnlyDictionary<(TaxCodeTarget, string), TaxCode> assignments, TaxCode? defaultSales, TaxCode? defaultPurchase)
{
    public static async Task<TaxCodeBook> LoadAsync(IWorkbenchDbContext db, SalesTaxSettings? settings, CancellationToken ct)
    {
        var codes = await db.Set<TaxCode>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var assignments = (await db.Set<TaxCodeAssignment>().AsNoTracking().ToListAsync(ct))
            .Where(a => codes.ContainsKey(a.TaxCodeId))
            .ToDictionary(a => (a.Target, a.TargetKey), a => codes[a.TaxCodeId]);
        return new TaxCodeBook(assignments, Code(codes, settings?.DefaultSalesTaxCodeId), Code(codes, settings?.DefaultPurchaseTaxCodeId));
    }

    public TaxTreatment ForInvoiceLine(InvoiceLine line) =>
        (line.OdcType is { } odc && assignments.TryGetValue((TaxCodeTarget.OdcType, odc), out var byType) ? byType
            : assignments.TryGetValue((TaxCodeTarget.InvoiceLineKind, line.Kind.ToString()), out var byKind) ? byKind
            : defaultSales)?.Treatment ?? TaxTreatment.Taxable;

    public TaxTreatment ForBillLine(ApBillLine line) =>
        (line.Kind == ApBillLineKind.Account && line.AccountId is { } account && assignments.TryGetValue((TaxCodeTarget.Account, account.ToString()), out var byAccount) ? byAccount
            : line.OdcType is { } odc && assignments.TryGetValue((TaxCodeTarget.OdcType, odc.ToString()), out var byType) ? byType
            : defaultPurchase)?.Treatment ?? TaxTreatment.Taxable;

    private static TaxCode? Code(Dictionary<Guid, TaxCode> codes, Guid? id) => id is { } x ? codes.GetValueOrDefault(x) : null;
}

/// <summary>Why a customer pays no tax on an invoice, or why a certificate on file didn't apply.</summary>
internal sealed record Exemption(string? Reason, TaxExemptionCertificate? Certificate, IReadOnlyList<string> Notes);

internal static class SalesTaxSupport
{
    public const string SalesTaxPayableNumber = "2150";
    public const string UseTaxPayableNumber = "2160";
    public const string UseTaxExpenseNumber = "6250";

    public static Task<SalesTaxSettings?> SettingsAsync(IWorkbenchDbContext db, CancellationToken ct) =>
        db.Set<SalesTaxSettings>().AsNoTracking().FirstOrDefaultAsync(ct);

    public static IReadOnlyList<string> GovernmentCategories(SalesTaxSettings? settings) =>
        (settings?.GovernmentCategories ?? new SalesTaxSettings().GovernmentCategories)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool IsGovernment(Customer customer, SalesTaxSettings? settings) =>
        customer.Category is { } c && GovernmentCategories(settings).Any(g => string.Equals(g, c.Trim(), StringComparison.OrdinalIgnoreCase));

    public static TaxJurisdictionRef Ref(TaxJurisdiction j) => new(j.Id, j.Code, j.Name, j.State, j.County, j.City);

    /// <summary>
    /// Government customers (by category) are exempt unless their profile says taxable; a customer marked exempt is exempt; otherwise a
    /// current certificate for the state (or every state) exempts the sale. Expired certificates are noted, not used.
    /// </summary>
    public static Exemption ExemptionFor(Customer customer, CustomerTaxProfile? profile, IReadOnlyList<TaxExemptionCertificate> certificates,
        SalesTaxSettings? settings, string state, DateOnly date)
    {
        var status = profile?.Status ?? CustomerTaxStatus.Default;
        if (status == CustomerTaxStatus.Exempt)
        {
            return new Exemption("Customer is marked tax exempt", null, []);
        }

        if (status == CustomerTaxStatus.Default && IsGovernment(customer, settings))
        {
            return new Exemption("Government customer", null, []);
        }

        var forState = certificates.Where(c => !c.IsRevoked && (c.State is null || c.State == state)).ToList();
        if (forState.FirstOrDefault(c => c.CoversOn(date, state)) is { } valid)
        {
            return new Exemption($"Exemption certificate {valid.Number} ({valid.Reason})", valid, []);
        }

        var notes = forState.Where(c => c.ExpiresOn is { } e && e < date)
            .Select(c => $"Certificate {c.Number} expired on {c.ExpiresOn:yyyy-MM-dd}, so this sale is taxed. Get a current certificate from the customer.")
            .Concat(forState.Where(c => c.EffectiveDate > date).Select(c => $"Certificate {c.Number} isn't effective until {c.EffectiveDate:yyyy-MM-dd}."))
            .ToList();
        return new Exemption(null, null, notes);
    }

    /// <summary>
    /// The tax liability and expense accounts: the settings' choice, else 2150 Sales tax payable, 2160 Use tax payable and 6250 Use
    /// tax expense, added to the chart (in their own unit of work) when they're missing.
    /// </summary>
    public static async Task<(Account SalesPayable, Account UsePayable, Account UseExpense)> EnsureAccountsAsync(IWorkbenchDbContextFactory dbFactory, DateTime utcNow, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var settings = await SettingsAsync(db, ct);
        if (!await db.Set<Account>().AnyAsync(ct))
        {
            throw new InvalidOperationException("The chart of accounts is empty. Install the default chart (Accounting › Chart of accounts) first.");
        }

        var sales = await AccountAsync(db, settings?.SalesTaxPayableAccountId, SalesTaxPayableNumber, "Sales tax payable", AccountType.Liability, "2000",
            "Sales tax billed to customers until it's remitted", utcNow, ct);
        var usePayable = await AccountAsync(db, settings?.UseTaxPayableAccountId, UseTaxPayableNumber, "Use tax payable", AccountType.Liability, "2000",
            "Use tax accrued on taxable purchases the vendor didn't tax, until it's remitted", utcNow, ct);
        var useExpense = await AccountAsync(db, settings?.UseTaxExpenseAccountId, UseTaxExpenseNumber, "Use tax expense", AccountType.Expense, "6200",
            "Use tax on purchases (G&A)", utcNow, ct);
        await db.SaveChangesAsync(ct);
        return (sales, usePayable, useExpense);
    }

    private static async Task<Account> AccountAsync(IWorkbenchDbContext db, Guid? chosen, string number, string name, AccountType type, string parentNumber,
        string description, DateTime utcNow, CancellationToken ct)
    {
        if (chosen is { } id)
        {
            var account = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct)
                ?? throw new ArgumentException($"The {name.ToLowerInvariant()} account no longer exists; choose another under Setup › Sales tax.");
            return account.IsActive ? account : throw new ArgumentException($"{name} account {account.Number} is inactive; choose another under Setup › Sales tax.");
        }

        var existing = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == number, ct);
        if (existing is not null)
        {
            return existing.IsActive ? existing : throw new ArgumentException($"{name} account {existing.Number} is inactive; choose another under Setup › Sales tax.");
        }

        var parent = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == parentNumber, ct);
        var created = new Account
        {
            Number = number,
            Name = name,
            Type = type,
            NormalBalance = Account.DefaultNormalBalance(type),
            ParentId = parent?.Id,
            CostClass = type == AccountType.Expense ? CostClassification.GeneralAndAdministrative : CostClassification.None,
            Description = description,
            CreatedAtUtc = utcNow,
        };
        db.Set<Account>().Add(created);
        return created;
    }

    /// <summary>The number of the account sales tax lines credit (for the billing ledger, which looks accounts up by number).</summary>
    public static async Task<string> SalesTaxPayableNumberAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var settings = await SettingsAsync(db, ct);
        return settings?.SalesTaxPayableAccountId is { } id
            ? await db.Set<Account>().AsNoTracking().Where(a => a.Id == id).Select(a => a.Number).FirstOrDefaultAsync(ct) ?? SalesTaxPayableNumber
            : SalesTaxPayableNumber;
    }
}

/// <summary>
/// Puts sales tax on a draft invoice in the caller's unit of work: replaces its sales tax line with one for the tax on the
/// taxable lines at the ship-to jurisdiction's rate (none when the customer is exempt) and records the calculation. Approval
/// runs it again, so an approved invoice always carries the tax in effect on its date. Credit memos keep the lines they have.
/// </summary>
public sealed class InvoiceTaxApplier(IWorkbenchDbContextFactory dbFactory, ISalesTaxEngine engine, AuditRecorder audit, TimeProvider clock)
{
    public async Task<InvoiceTax?> ApplyAsync(IWorkbenchDbContext db, Invoice invoice, CancellationToken ct)
    {
        if (invoice.Type == InvoiceType.CreditMemo || invoice.Status != InvoiceStatus.Draft)
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var settings = await SalesTaxSupport.SettingsAsync(db, ct);
        var record = db.Set<InvoiceTax>().Local.FirstOrDefault(t => t.InvoiceId == invoice.Id)
            ?? await db.Set<InvoiceTax>().FirstOrDefaultAsync(t => t.InvoiceId == invoice.Id, ct);
        var profile = await db.Set<CustomerTaxProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.CustomerId == invoice.CustomerId, ct);
        var jurisdictionId = record?.JurisdictionOverrideId ?? profile?.ShipToJurisdictionId;
        if (settings is null && record is null && jurisdictionId is null)
        {
            // Sales tax isn't set up and nobody asked for it on this invoice.
            return null;
        }

        var customer = await db.Set<Customer>().AsNoTracking().FirstAsync(c => c.Id == invoice.CustomerId, ct);
        var jurisdiction = jurisdictionId is { } jid ? await db.Set<TaxJurisdiction>().AsNoTracking().FirstOrDefaultAsync(j => j.Id == jid, ct) : null;
        var book = await TaxCodeBook.LoadAsync(db, settings, ct);
        var lines = invoice.Lines.Where(l => l.Kind != InvoiceLineKind.SalesTax)
            .Select(l => new TaxLine(l.Id.ToString(), l.Amount, book.ForInvoiceLine(l))).ToList();

        TaxCalculationResult result;
        string? reason = null;
        TaxExemptionCertificate? certificate = null;
        if (jurisdiction is null)
        {
            result = new TaxCalculationResult(0, lines.Sum(l => l.Amount), 0, 0, 0, 0, 0, engine.Name,
                "No ship-to jurisdiction: set one for the customer (Setup › Sales tax) or on the invoice.");
        }
        else
        {
            var certificates = await db.Set<TaxExemptionCertificate>().AsNoTracking().Where(c => c.CustomerId == customer.Id).ToListAsync(ct);
            var exemption = SalesTaxSupport.ExemptionFor(customer, profile, certificates, settings, jurisdiction.State, invoice.InvoiceDate);
            (reason, certificate) = (exemption.Reason, exemption.Certificate);
            var request = new TaxCalculationRequest(SalesTaxSupport.Ref(jurisdiction), invoice.InvoiceDate,
                reason is null ? lines : lines.Select(l => l with { Treatment = TaxTreatment.Exempt }).ToList(), IsPurchase: false);
            result = await engine.CalculateAsync(request, ct);
            if (exemption.Notes.Count > 0)
            {
                result = result with { Note = string.Join(" ", exemption.Notes.Append(result.Note ?? "")).Trim() };
            }
        }

        foreach (var old in invoice.Lines.Where(l => l.Kind == InvoiceLineKind.SalesTax).ToList())
        {
            invoice.Lines.Remove(old);
            db.Set<InvoiceLine>().Remove(old);
        }

        if (result.Tax != 0)
        {
            var line = new InvoiceLine
            {
                InvoiceId = invoice.Id,
                LineNumber = invoice.Lines.Count == 0 ? 1 : invoice.Lines.Max(l => l.LineNumber) + 1,
                Kind = InvoiceLineKind.SalesTax,
                Description = $"Sales tax: {jurisdiction!.Name} {result.Rate * 100m:0.###}%",
                Quantity = 1,
                Rate = result.Tax,
                Amount = result.Tax,
                IsGenerated = false,
            };
            invoice.Lines.Add(line);
            db.Set<InvoiceLine>().Add(line);
        }

        invoice.Recalculate();
        if (record is null)
        {
            record = new InvoiceTax { InvoiceId = invoice.Id, Engine = engine.Name, CalculatedBy = audit.Actor };
            db.Set<InvoiceTax>().Add(record);
        }

        var before = new { record.TaxAmount, record.Rate, record.JurisdictionId };
        record.JurisdictionId = jurisdiction?.Id;
        record.TaxableAmount = result.TaxableAmount;
        record.ExemptAmount = result.ExemptAmount;
        record.Rate = result.Rate;
        record.StateTax = result.StateTax;
        record.CountyTax = result.CountyTax;
        record.CityTax = result.CityTax;
        record.SpecialTax = result.SpecialTax;
        record.TaxAmount = result.Tax;
        record.ExemptionReason = Clip(reason ?? result.Note, 200);
        record.CertificateId = certificate?.Id;
        record.Engine = result.Engine;
        record.CalculatedBy = audit.Actor;
        record.CalculatedAtUtc = now;
        audit.Record(db, nameof(InvoiceTax), invoice.Id, "Calculated", before, new { record.TaxAmount, record.Rate, record.JurisdictionId, record.ExemptionReason });
        return record;
    }

    /// <summary>Makes sure the sales tax liability account exists before a posting looks it up (its own unit of work).</summary>
    public async Task EnsureAccountsAsync(CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            if (await SalesTaxSupport.SettingsAsync(db, ct) is null && !await db.Set<TaxJurisdiction>().AnyAsync(ct))
            {
                return; // Sales tax isn't set up.
            }
        }

        await SalesTaxSupport.EnsureAccountsAsync(dbFactory, clock.GetUtcNow().UtcDateTime, ct);
    }

    private static string? Clip(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];
}

/// <summary>
/// Use tax on posted AP bills: when the vendor charged no tax on a taxable purchase, accrue the tax at the rate where the
/// purchase is used (debit use tax expense, credit use tax payable; GL source AP, source id = the bill's tax record). Re-running
/// after a change reverses the previous accrual first. Without a jurisdiction (the bill's or the home one) nothing accrues.
/// </summary>
public sealed class UseTaxAccrual(
    IWorkbenchDbContextFactory dbFactory, ISalesTaxEngine engine, GeneralLedger ledger, AuditRecorder audit, TimeProvider clock, ILogger<UseTaxAccrual> logger)
{
    /// <summary>Evaluates (and posts, for a posted bill) the bill's tax. Returns the tax record, or null when sales tax isn't set up.</summary>
    public async Task<ApBillTax?> AccrueAsync(Guid billId, CancellationToken ct)
    {
        await using (var check = await dbFactory.CreateAsync(ct))
        {
            if (await SalesTaxSupport.SettingsAsync(check, ct) is null && !await check.Set<ApBillTax>().AnyAsync(t => t.BillId == billId, ct))
            {
                return null;
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var accounts = await SalesTaxSupport.EnsureAccountsAsync(dbFactory, now, ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var settings = await SalesTaxSupport.SettingsAsync(db, ct);
        var bill = await db.Set<ApBill>().AsNoTracking().Include(b => b.Lines).FirstAsync(b => b.Id == billId, ct);
        var record = await db.Set<ApBillTax>().FirstOrDefaultAsync(t => t.BillId == billId, ct);
        if (record is null)
        {
            record = new ApBillTax { BillId = billId, UpdatedBy = audit.Actor, UpdatedAtUtc = now };
            db.Set<ApBillTax>().Add(record);
        }

        var evaluation = await EvaluateAsync(db, bill, record, settings, ct);
        record.TaxableAmount = evaluation.Result?.TaxableAmount ?? 0;
        record.Rate = evaluation.Result?.Rate ?? 0;
        var useTax = record.VendorChargedTax == 0 ? evaluation.Result?.Tax ?? 0 : 0;
        if (bill.Status != ApBillStatus.Posted)
        {
            record.UseTaxAmount = useTax;
            await db.SaveChangesAsync(ct);
            return record;
        }

        if (record.UseTaxJournalEntryId is { } previous)
        {
            if (record.UseTaxAmount == useTax)
            {
                await db.SaveChangesAsync(ct);
                return record;
            }

            var old = await db.Set<JournalEntry>().Include(e => e.Lines).FirstAsync(e => e.Id == previous, ct);
            if (old.Status == JournalStatus.Posted)
            {
                await ledger.ReverseAsync(db, old, await OpenDateAsync(db, bill.InvoiceDate, now, ct), "Use tax recalculated", ct);
            }

            record.UseTaxJournalEntryId = null;
        }

        record.UseTaxAmount = useTax;
        if (useTax > 0)
        {
            var date = await OpenDateAsync(db, bill.InvoiceDate, now, ct);
            var entry = new JournalEntry
            {
                CompanyId = bill.CompanyId,
                Date = date,
                Source = JournalSource.AP,
                SourceId = record.Id,
                SourceRevision = await db.Set<JournalEntry>().CountAsync(e => e.Source == JournalSource.AP && e.SourceId == record.Id, ct),
                Description = $"Use tax on bill {bill.Number} ({bill.InvoiceNumber}): {evaluation.Jurisdiction!.Name} {record.Rate * 100m:0.###}%",
                CreatedBy = ledger.Actor,
                CreatedAtUtc = ledger.UtcNow,
            };
            var org = bill.Lines.FirstOrDefault(l => l.OrgUnitId is not null)?.OrgUnitId;
            entry.Lines.Add(new JournalLine { JournalEntryId = entry.Id, LineNumber = 1, AccountId = accounts.UseExpense.Id, OrgUnitId = org, Debit = useTax, Memo = $"Use tax {bill.Number}" });
            entry.Lines.Add(new JournalLine { JournalEntryId = entry.Id, LineNumber = 2, AccountId = accounts.UsePayable.Id, Credit = useTax, Memo = evaluation.Jurisdiction.Code });
            db.Set<JournalEntry>().Add(entry);
            await ledger.PostAsync(db, entry, ct);
            record.UseTaxJournalEntryId = entry.Id;
        }

        record.UpdatedAtUtc = now;
        audit.Record(db, nameof(ApBillTax), bill.Id, "UseTaxAccrued", after: new { record.TaxableAmount, record.Rate, record.UseTaxAmount, record.VendorChargedTax });
        await db.SaveChangesAsync(ct);
        return record;
    }

    /// <summary>For the bill posting path: never fails the posting; a problem is logged and shows on the bill's tax panel.</summary>
    public async Task TryAccrueAsync(Guid billId, CancellationToken ct)
    {
        try
        {
            await AccrueAsync(billId, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DbUpdateException)
        {
            logger.LogWarning(ex, "Use tax on AP bill {BillId} wasn't accrued", billId);
        }
    }

    internal sealed record Evaluation(TaxJurisdiction? Jurisdiction, TaxCalculationResult? Result, IReadOnlyList<string> Notes);

    internal async Task<Evaluation> EvaluateAsync(IWorkbenchDbContext db, ApBill bill, ApBillTax? record, SalesTaxSettings? settings, CancellationToken ct)
    {
        var jurisdictionId = record?.JurisdictionId ?? settings?.HomeJurisdictionId;
        var jurisdiction = jurisdictionId is { } id ? await db.Set<TaxJurisdiction>().AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct) : null;
        if (jurisdiction is null)
        {
            return new Evaluation(null, null, ["No jurisdiction: choose where the purchase is used, or set the home jurisdiction under Setup › Sales tax."]);
        }

        var book = await TaxCodeBook.LoadAsync(db, settings, ct);
        var overrideCode = record?.TaxCodeOverrideId is { } codeId ? await db.Set<TaxCode>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == codeId, ct) : null;
        var lines = bill.Lines.Where(l => l.Status != ApLineStatus.Rejected)
            .Select(l => new TaxLine(l.Id.ToString(), l.Amount, overrideCode?.Treatment ?? book.ForBillLine(l))).ToList();
        var result = await engine.CalculateAsync(new TaxCalculationRequest(SalesTaxSupport.Ref(jurisdiction), bill.InvoiceDate, lines, IsPurchase: true), ct);
        var notes = new List<string>();
        if (result.Note is { } note)
        {
            notes.Add(note);
        }

        if (record is { VendorChargedTax: > 0 } && record.VendorChargedTax < result.Tax)
        {
            notes.Add($"The vendor charged {record.VendorChargedTax:C} but {result.Tax:C} was expected at {jurisdiction.Name}'s rate. Check the invoice; no use tax accrues when the vendor charged tax.");
        }

        return new Evaluation(jurisdiction, result, notes);
    }

    private static async Task<DateOnly> OpenDateAsync(IWorkbenchDbContext db, DateOnly date, DateTime now, CancellationToken ct)
    {
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Start <= date && p.End >= date, ct);
        return period is { Status: PeriodStatus.Open } ? date : DateOnly.FromDateTime(now);
    }
}
