using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Companies;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.SalesTax;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Domain.SalesTax;

namespace Workbench.Application.SalesTax;

/// <summary>
/// Sales and use tax setup, document tax and the return. Setup changes are audited; rates are append-only (a correction is a
/// new rate). Invoice tax goes on the invoice as a sales tax line (credited to the sales tax liability when it posts); bill tax
/// records what the vendor charged and accrues use tax on taxable purchases the vendor didn't tax.
/// </summary>
public sealed class SalesTaxService(
    IWorkbenchDbContextFactory dbFactory,
    ISalesTaxEngine engine,
    InvoiceTaxApplier invoiceTax,
    UseTaxAccrual useTax,
    AuditRecorder audit,
    TimeProvider clock,
    IEnumerable<ISalesTaxEngine>? engines = null,
    CompanyScope? companies = null,
    Security.SecurityEventLog? securityLog = null) : ISalesTaxService
{
    private const string SetupEntity = "SalesTaxSetup";

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    // ---- Jurisdictions and rates ----

    public async Task<IReadOnlyList<TaxJurisdictionDto>> ListJurisdictionsAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var list = await db.Set<TaxJurisdiction>().AsNoTracking().Where(j => includeInactive || j.IsActive).ToListAsync(cancellationToken);
        var ids = list.Select(j => j.Id).ToList();
        var rates = (await db.Set<TaxRate>().AsNoTracking().Where(r => ids.Contains(r.JurisdictionId)).ToListAsync(cancellationToken)).ToLookup(r => r.JurisdictionId);
        return list.OrderBy(j => j.State, StringComparer.Ordinal).ThenBy(j => j.Name, StringComparer.Ordinal).Select(j => ToDto(j, rates[j.Id])).ToList();
    }

    public async Task<TaxJurisdictionDto> SaveJurisdictionAsync(Guid? jurisdictionId, SaveTaxJurisdictionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Required(request.Code, "A code", 64).ToUpperInvariant();
        var name = Required(request.Name, "A name", 200);
        var state = State(request.State) ?? throw new ArgumentException("Choose the state (two letters, for example VA).");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<TaxJurisdiction>().AnyAsync(j => j.Code == code && j.Id != jurisdictionId, cancellationToken))
        {
            throw new ArgumentException($"There's already a jurisdiction with code {code}.");
        }

        var j = jurisdictionId is { } id
            ? await db.Set<TaxJurisdiction>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Tax jurisdiction", id)
            : null;
        var before = j is null ? null : new { j.Code, j.Name, j.State, j.County, j.City, j.IsActive };
        if (j is null)
        {
            j = new TaxJurisdiction { Code = code, Name = name, State = state, CreatedAtUtc = UtcNow };
            db.Set<TaxJurisdiction>().Add(j);
        }

        j.Code = code;
        j.Name = name;
        j.State = state;
        j.County = Optional(request.County, 100);
        j.City = Optional(request.City, 100);
        j.IsActive = request.IsActive;
        audit.Record(db, nameof(TaxJurisdiction), j.Id, before is null ? "Created" : "Updated", before, new { j.Code, j.Name, j.State, j.County, j.City, j.IsActive });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(j, await db.Set<TaxRate>().AsNoTracking().Where(r => r.JurisdictionId == j.Id).ToListAsync(cancellationToken));
    }

    public async Task<TaxJurisdictionDto> AddRateAsync(Guid jurisdictionId, AddTaxRateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var j = await db.Set<TaxJurisdiction>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == jurisdictionId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Tax jurisdiction", jurisdictionId);
        var rate = NewRate(j.Id, request.EffectiveDate, request.StateRate, request.CountyRate, request.CityRate, request.SpecialRate, "Manual");
        db.Set<TaxRate>().Add(rate);
        audit.Record(db, nameof(TaxRate), rate.Id, "Added", after: new { j.Code, rate.EffectiveDate, rate.StateRate, rate.CountyRate, rate.CityRate, rate.SpecialRate });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(j, await db.Set<TaxRate>().AsNoTracking().Where(r => r.JurisdictionId == j.Id).ToListAsync(cancellationToken));
    }

    public async Task<TaxRateImportResultDto> ImportRatesAsync(ImportTaxRatesRequest request, CancellationToken cancellationToken = default)
    {
        var rows = RateTableCsv.Parse(request?.Csv ?? "");
        var errors = rows.Where(r => r.Error is not null).Select(r => $"Line {r.Line}: {r.Error}").ToList();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var jurisdictions = await db.Set<TaxJurisdiction>().ToDictionaryAsync(j => j.Code, cancellationToken);
        var existing = (await db.Set<TaxRate>().AsNoTracking().Select(r => new { r.JurisdictionId, r.EffectiveDate }).ToListAsync(cancellationToken))
            .Select(r => (r.JurisdictionId, r.EffectiveDate)).ToHashSet();
        int created = 0, added = 0, skipped = 0;
        foreach (var row in rows.Where(r => r.Error is null))
        {
            try
            {
                ValidateRates(row.StateRate, row.CountyRate, row.CityRate, row.SpecialRate);
                if (!jurisdictions.TryGetValue(row.Code, out var j))
                {
                    j = new TaxJurisdiction { Code = row.Code, Name = row.Name ?? row.Code, State = State(row.State) ?? throw new ArgumentException("needs a two-letter state"), County = row.County, City = row.City, CreatedAtUtc = UtcNow };
                    db.Set<TaxJurisdiction>().Add(j);
                    jurisdictions[j.Code] = j;
                    created++;
                }

                if (!existing.Add((j.Id, row.EffectiveDate)))
                {
                    skipped++;
                    continue;
                }

                db.Set<TaxRate>().Add(NewRate(j.Id, row.EffectiveDate, row.StateRate, row.CountyRate, row.CityRate, row.SpecialRate, "CSV import"));
                added++;
            }
            catch (ArgumentException ex)
            {
                errors.Add($"Line {row.Line}: {ex.Message}");
            }
        }

        audit.Record(db, SetupEntity, Guid.Empty, "RatesImported", after: new { JurisdictionsCreated = created, RatesAdded = added, RatesSkipped = skipped, Errors = errors.Count });
        await db.SaveChangesAsync(cancellationToken);
        return new TaxRateImportResultDto(created, added, skipped, errors);
    }

    public async Task<TaxQuoteDto> QuoteAsync(TaxQuoteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var j = await db.Set<TaxJurisdiction>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.JurisdictionId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Tax jurisdiction", request.JurisdictionId);
        var r = await engine.CalculateAsync(new TaxCalculationRequest(SalesTaxSupport.Ref(j), request.Date, [new TaxLine("quote", request.TaxableAmount, TaxTreatment.Taxable)], false), cancellationToken);
        return new TaxQuoteDto(j.Id, j.Name, request.Date, r.TaxableAmount, r.Rate, r.Tax, r.StateTax, r.CountyTax, r.CityTax, r.SpecialTax, r.Engine);
    }

    // ---- Tax codes ----

    public async Task<IReadOnlyList<TaxCodeDto>> ListTaxCodesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await db.Set<TaxCode>().AsNoTracking().ToListAsync(cancellationToken)).OrderBy(c => c.Code, StringComparer.Ordinal).Select(ToDto).ToList();
    }

    public async Task<TaxCodeDto> SaveTaxCodeAsync(Guid? taxCodeId, SaveTaxCodeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Required(request.Code, "A code", 32).ToUpperInvariant();
        var name = Required(request.Name, "A name", 200);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<TaxCode>().AnyAsync(c => c.Code == code && c.Id != taxCodeId, cancellationToken))
        {
            throw new ArgumentException($"There's already a tax code {code}.");
        }

        var c = taxCodeId is { } id
            ? await db.Set<TaxCode>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Tax code", id)
            : null;
        var before = c is null ? null : new { c.Code, c.Name, Treatment = c.Treatment.ToString(), c.IsActive };
        if (c is null)
        {
            c = new TaxCode { Code = code, Name = name, UpdatedBy = audit.Actor };
            db.Set<TaxCode>().Add(c);
        }

        c.Code = code;
        c.Name = name;
        c.Treatment = (TaxTreatment)(int)request.Treatment;
        c.IsActive = request.IsActive;
        c.UpdatedBy = audit.Actor;
        c.UpdatedAtUtc = UtcNow;
        audit.Record(db, nameof(TaxCode), c.Id, before is null ? "Created" : "Updated", before, new { c.Code, c.Name, Treatment = c.Treatment.ToString(), c.IsActive });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(c);
    }

    public async Task<IReadOnlyList<TaxCodeAssignmentDto>> ListAssignmentsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await AssignmentsAsync(db, cancellationToken);
    }

    public async Task<IReadOnlyList<TaxCodeAssignmentDto>> SaveAssignmentAsync(SaveTaxCodeAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = (TaxCodeTarget)(int)request.Target;
        var key = Required(request.TargetKey, "What the code applies to", 64);
        key = target switch
        {
            TaxCodeTarget.InvoiceLineKind => Enum.TryParse<InvoiceLineKind>(key, true, out var k) && k != InvoiceLineKind.SalesTax ? k.ToString() : throw new ArgumentException($"\"{key}\" isn't an invoice line kind."),
            TaxCodeTarget.OdcType => Enum.TryParse<OdcType>(key, true, out var t) ? t.ToString() : throw new ArgumentException($"\"{key}\" isn't an ODC type."),
            _ => Guid.TryParse(key, out var g) ? g.ToString() : throw new ArgumentException("Choose the account or part."),
        };
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (target == TaxCodeTarget.Account && !await db.Set<Account>().AnyAsync(a => a.Id == Guid.Parse(key), cancellationToken))
        {
            throw new ArgumentException("That account doesn't exist.");
        }

        if (request.TaxCodeId is { } codeId && !await db.Set<TaxCode>().AnyAsync(c => c.Id == codeId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Tax code", codeId);
        }

        var existing = await db.Set<TaxCodeAssignment>().FirstOrDefaultAsync(a => a.Target == target && a.TargetKey == key, cancellationToken);
        var before = existing?.TaxCodeId;
        if (request.TaxCodeId is null)
        {
            if (existing is not null)
            {
                db.Set<TaxCodeAssignment>().Remove(existing);
            }
        }
        else if (existing is null)
        {
            db.Set<TaxCodeAssignment>().Add(new TaxCodeAssignment { Target = target, TargetKey = key, TaxCodeId = request.TaxCodeId.Value, UpdatedBy = audit.Actor, UpdatedAtUtc = UtcNow });
        }
        else
        {
            existing.TaxCodeId = request.TaxCodeId.Value;
            existing.UpdatedBy = audit.Actor;
            existing.UpdatedAtUtc = UtcNow;
        }

        audit.Record(db, nameof(TaxCodeAssignment), existing?.Id ?? Guid.Empty, "Assigned", new { Target = target.ToString(), key, TaxCodeId = before }, new { Target = target.ToString(), key, request.TaxCodeId });
        await db.SaveChangesAsync(cancellationToken);
        return await AssignmentsAsync(db, cancellationToken);
    }

    // ---- Customers ----

    public async Task<IReadOnlyList<CustomerTaxDto>> ListCustomersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var customers = await db.Set<Customer>().AsNoTracking().ToListAsync(cancellationToken);
        return await CustomerDtosAsync(db, customers, cancellationToken);
    }

    public async Task<CustomerTaxDto> SaveCustomerAsync(Guid customerId, SaveCustomerTaxRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var customer = await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Customer", customerId);
        if (request.ShipToJurisdictionId is { } jid && !await db.Set<TaxJurisdiction>().AnyAsync(j => j.Id == jid, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Tax jurisdiction", jid);
        }

        var profile = await db.Set<CustomerTaxProfile>().FirstOrDefaultAsync(p => p.CustomerId == customerId, cancellationToken);
        var before = profile is null ? null : new { Status = profile.Status.ToString(), profile.ShipToJurisdictionId };
        if (profile is null)
        {
            profile = new CustomerTaxProfile { CustomerId = customerId, UpdatedBy = audit.Actor };
            db.Set<CustomerTaxProfile>().Add(profile);
        }

        profile.Status = (CustomerTaxStatus)(int)request.Status;
        profile.ShipToJurisdictionId = request.ShipToJurisdictionId;
        profile.UpdatedBy = audit.Actor;
        profile.UpdatedAtUtc = UtcNow;
        audit.Record(db, nameof(CustomerTaxProfile), customerId, before is null ? "Created" : "Updated", before, new { Status = profile.Status.ToString(), profile.ShipToJurisdictionId });
        await db.SaveChangesAsync(cancellationToken);
        return (await CustomerDtosAsync(db, [customer], cancellationToken))[0];
    }

    public async Task<CustomerTaxDto> SaveCertificateAsync(Guid customerId, Guid? certificateId, SaveTaxExemptionCertificateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var number = Required(request.Number, "The certificate number", 64);
        var reason = Required(request.Reason, "The reason", 100);
        if (request.ExpiresOn is { } expires && expires < request.EffectiveDate)
        {
            throw new ArgumentException("A certificate can't expire before it takes effect.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var customer = await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Customer", customerId);
        var cert = certificateId is { } id
            ? await db.Set<TaxExemptionCertificate>().FirstOrDefaultAsync(c => c.Id == id && c.CustomerId == customerId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Exemption certificate", id)
            : null;
        var before = cert is null ? null : new { cert.Number, cert.State, cert.Reason, cert.EffectiveDate, cert.ExpiresOn, cert.IsRevoked };
        if (cert is null)
        {
            cert = new TaxExemptionCertificate { CustomerId = customerId, Number = number, Reason = reason, UpdatedBy = audit.Actor };
            db.Set<TaxExemptionCertificate>().Add(cert);
        }

        cert.Number = number;
        cert.State = State(request.State);
        cert.Reason = reason;
        cert.EffectiveDate = request.EffectiveDate;
        cert.ExpiresOn = request.ExpiresOn;
        cert.IsRevoked = request.IsRevoked;
        cert.UpdatedBy = audit.Actor;
        cert.UpdatedAtUtc = UtcNow;
        audit.Record(db, nameof(TaxExemptionCertificate), cert.Id, before is null ? "Created" : "Updated", before,
            new { cert.Number, cert.State, cert.Reason, cert.EffectiveDate, cert.ExpiresOn, cert.IsRevoked });
        await db.SaveChangesAsync(cancellationToken);
        return (await CustomerDtosAsync(db, [customer], cancellationToken))[0];
    }

    // ---- Settings ----

    public async Task<SalesTaxSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await SettingsDtoAsync(db, cancellationToken);
    }

    public async Task<SalesTaxSettingsDto> SaveSettingsAsync(SaveSalesTaxSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureAccountAsync(db, request.SalesTaxPayableAccountId, AccountType.Liability, "The sales tax payable account", cancellationToken);
        await EnsureAccountAsync(db, request.UseTaxPayableAccountId, AccountType.Liability, "The use tax payable account", cancellationToken);
        await EnsureAccountAsync(db, request.UseTaxExpenseAccountId, AccountType.Expense, "The use tax expense account", cancellationToken);
        if (request.HomeJurisdictionId is { } home && !await db.Set<TaxJurisdiction>().AnyAsync(j => j.Id == home, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Tax jurisdiction", home);
        }

        foreach (var code in new[] { request.DefaultSalesTaxCodeId, request.DefaultPurchaseTaxCodeId }.OfType<Guid>())
        {
            if (!await db.Set<TaxCode>().AnyAsync(c => c.Id == code, cancellationToken))
            {
                throw WorkbenchNotFoundException.For("Tax code", code);
            }
        }

        var engineName = string.IsNullOrWhiteSpace(request.Engine) ? engine.Name : request.Engine.Trim();
        if (!EngineNames().Contains(engineName))
        {
            throw new ArgumentException($"\"{engineName}\" isn't an available tax engine.");
        }

        var settings = await db.Set<SalesTaxSettings>().FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new SalesTaxSettings();
            db.Set<SalesTaxSettings>().Add(settings);
        }

        var before = Snapshot(settings);
        settings.SalesTaxPayableAccountId = request.SalesTaxPayableAccountId;
        settings.UseTaxPayableAccountId = request.UseTaxPayableAccountId;
        settings.UseTaxExpenseAccountId = request.UseTaxExpenseAccountId;
        settings.HomeJurisdictionId = request.HomeJurisdictionId;
        settings.DefaultSalesTaxCodeId = request.DefaultSalesTaxCodeId;
        settings.DefaultPurchaseTaxCodeId = request.DefaultPurchaseTaxCodeId;
        if (request.GovernmentCategories is { } categories)
        {
            var joined = string.Join(",", categories.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
            settings.GovernmentCategories = joined.Length <= 500 ? joined : throw new ArgumentException("The government categories are too long.");
        }

        settings.Engine = engineName;
        settings.UpdatedBy = audit.Actor;
        settings.UpdatedAtUtc = UtcNow;
        audit.Record(db, SetupEntity, settings.Id, "SettingsChanged", before, Snapshot(settings));
        await db.SaveChangesAsync(cancellationToken);
        return await SettingsDtoAsync(db, cancellationToken);
    }

    public async Task<IReadOnlyList<TaxAccountOptionDto>> ListAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => a.IsActive && (a.Type == AccountType.Liability || a.Type == AccountType.Expense)).ToListAsync(cancellationToken);
        var parents = accounts.Where(a => a.ParentId is not null).Select(a => a.ParentId!.Value).ToHashSet();
        return accounts.Where(a => !parents.Contains(a.Id)).OrderBy(a => a.Number, StringComparer.Ordinal)
            .Select(a => new TaxAccountOptionDto(a.Id, a.Number, a.Name, a.Type.ToString())).ToList();
    }

    // ---- Invoices ----

    public async Task<InvoiceTaxDto> GetInvoiceTaxAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await db.Set<Invoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Invoice", invoiceId);
        return await InvoiceDtoAsync(db, invoice, cancellationToken);
    }

    public async Task<InvoiceTaxDto> CalculateInvoiceTaxAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await invoiceTax.EnsureAccountsAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await DraftAsync(db, invoiceId, cancellationToken);
        if (await invoiceTax.ApplyAsync(db, invoice, cancellationToken) is null)
        {
            // Not set up yet: record an empty calculation so the panel can say what's missing.
            db.Set<InvoiceTax>().Add(new InvoiceTax { InvoiceId = invoice.Id, Engine = engine.Name, CalculatedBy = audit.Actor, CalculatedAtUtc = UtcNow, ExemptAmount = invoice.Total,
                ExemptionReason = "No ship-to jurisdiction: set one for the customer (Setup › Sales tax) or on the invoice." });
        }

        invoice.UpdatedAtUtc = UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await InvoiceDtoAsync(db, invoice, cancellationToken);
    }

    public async Task<InvoiceTaxDto> SetInvoiceTaxAsync(Guid invoiceId, SetInvoiceTaxRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await invoiceTax.EnsureAccountsAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await DraftAsync(db, invoiceId, cancellationToken);
        if (request.JurisdictionOverrideId is { } jid && !await db.Set<TaxJurisdiction>().AnyAsync(j => j.Id == jid, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Tax jurisdiction", jid);
        }

        var record = await db.Set<InvoiceTax>().FirstOrDefaultAsync(t => t.InvoiceId == invoice.Id, cancellationToken);
        if (record is null)
        {
            record = new InvoiceTax { InvoiceId = invoice.Id, Engine = engine.Name, CalculatedBy = audit.Actor, CalculatedAtUtc = UtcNow };
            db.Set<InvoiceTax>().Add(record);
        }

        record.JurisdictionOverrideId = request.JurisdictionOverrideId;
        await invoiceTax.ApplyAsync(db, invoice, cancellationToken);
        invoice.UpdatedAtUtc = UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await InvoiceDtoAsync(db, invoice, cancellationToken);
    }

    // ---- Bills ----

    public async Task<BillTaxDto> GetBillTaxAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await BillDtoAsync(db, billId, cancellationToken);
    }

    public async Task<BillTaxDto> SetBillTaxAsync(Guid billId, SetBillTaxRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.VendorChargedTax < 0 || decimal.Round(request.VendorChargedTax, 2) != request.VendorChargedTax)
        {
            throw new ArgumentException("The tax the vendor charged is an amount of zero or more, in cents.");
        }

        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var bill = await db.Set<ApBill>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == billId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Bill", billId);
            if (bill.Status is ApBillStatus.Void or ApBillStatus.Rejected)
            {
                throw new InvalidOperationException($"Bill {bill.Number} is {bill.Status.ToString().ToLowerInvariant()}.");
            }

            if (request.VendorChargedTax > bill.Amount)
            {
                throw new ArgumentException("The vendor's tax is part of the bill, so it can't be more than the bill.");
            }

            if (request.JurisdictionId is { } jid && !await db.Set<TaxJurisdiction>().AnyAsync(j => j.Id == jid, cancellationToken))
            {
                throw WorkbenchNotFoundException.For("Tax jurisdiction", jid);
            }

            if (request.TaxCodeOverrideId is { } cid && !await db.Set<TaxCode>().AnyAsync(c => c.Id == cid, cancellationToken))
            {
                throw WorkbenchNotFoundException.For("Tax code", cid);
            }

            var record = await db.Set<ApBillTax>().FirstOrDefaultAsync(t => t.BillId == billId, cancellationToken);
            var before = record is null ? null : new { record.JurisdictionId, record.VendorChargedTax, record.TaxCodeOverrideId };
            if (record is null)
            {
                record = new ApBillTax { BillId = billId, UpdatedBy = audit.Actor };
                db.Set<ApBillTax>().Add(record);
            }

            record.JurisdictionId = request.JurisdictionId;
            record.VendorChargedTax = request.VendorChargedTax;
            record.TaxCodeOverrideId = request.TaxCodeOverrideId;
            record.UpdatedBy = audit.Actor;
            record.UpdatedAtUtc = UtcNow;
            audit.Record(db, nameof(ApBillTax), billId, before is null ? "Created" : "Updated", before, new { record.JurisdictionId, record.VendorChargedTax, record.TaxCodeOverrideId });
            await db.SaveChangesAsync(cancellationToken);
        }

        // Evaluates the tax and, on a posted bill, accrues (or re-accrues) use tax.
        await useTax.AccrueAsync(billId, cancellationToken);
        await using var read = await dbFactory.CreateAsync(cancellationToken);
        return await BillDtoAsync(read, billId, cancellationToken);
    }

    // ---- Return ----

    public async Task<SalesTaxReturnDto> GetReturnAsync(DateOnly from, DateOnly to, string? state = null, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            throw new ArgumentException("The period ends before it starts.");
        }

        var stateFilter = State(state);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var visible = companies is null ? null : await companies.VisibleCompanyIdsAsync(cancellationToken);
        var settings = await SalesTaxSupport.SettingsAsync(db, cancellationToken);
        var billed = new[] { InvoiceStatus.Approved, InvoiceStatus.Sent, InvoiceStatus.PartiallyPaid, InvoiceStatus.Paid };
        var sales = await db.Set<InvoiceTax>().AsNoTracking()
            .Join(db.Set<Invoice>().AsNoTracking(), t => t.InvoiceId, i => i.Id, (t, i) => new { Tax = t, i.InvoiceDate, i.Status, i.Type, i.CompanyId })
            .Where(x => x.InvoiceDate >= from && x.InvoiceDate <= to && billed.Contains(x.Status) && x.Type != InvoiceType.CreditMemo && x.Tax.JurisdictionId != null)
            .ToListAsync(cancellationToken);
        var purchases = await db.Set<ApBillTax>().AsNoTracking()
            .Join(db.Set<ApBill>().AsNoTracking(), t => t.BillId, b => b.Id, (t, b) => new { Tax = t, b.InvoiceDate, b.Status, b.CompanyId })
            .Where(x => x.InvoiceDate >= from && x.InvoiceDate <= to && x.Status == ApBillStatus.Posted)
            .ToListAsync(cancellationToken);
        if (visible is not null)
        {
            sales = sales.Where(s => visible.Contains(s.CompanyId)).ToList();
            purchases = purchases.Where(p => visible.Contains(p.CompanyId)).ToList();
        }

        var jurisdictions = await db.Set<TaxJurisdiction>().AsNoTracking().ToDictionaryAsync(j => j.Id, cancellationToken);
        var rows = new Dictionary<Guid, SalesTaxReturnRowDto>();
        SalesTaxReturnRowDto Row(Guid id)
        {
            var j = jurisdictions[id];
            return rows.TryGetValue(id, out var r) ? r : rows[id] = new SalesTaxReturnRowDto(j.Id, j.Code, j.Name, j.State, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        foreach (var s in sales.Where(s => jurisdictions.ContainsKey(s.Tax.JurisdictionId!.Value)))
        {
            var t = s.Tax;
            var r = Row(t.JurisdictionId!.Value);
            rows[r.JurisdictionId] = r with
            {
                GrossSales = r.GrossSales + t.TaxableAmount + t.ExemptAmount, ExemptSales = r.ExemptSales + t.ExemptAmount, TaxableSales = r.TaxableSales + t.TaxableAmount,
                SalesTax = r.SalesTax + t.TaxAmount, StateTax = r.StateTax + t.StateTax, CountyTax = r.CountyTax + t.CountyTax, CityTax = r.CityTax + t.CityTax,
                SpecialTax = r.SpecialTax + t.SpecialTax, Invoices = r.Invoices + 1,
            };
        }

        foreach (var p in purchases)
        {
            var id = p.Tax.JurisdictionId ?? settings?.HomeJurisdictionId;
            if (id is not { } jid || !jurisdictions.ContainsKey(jid) || (p.Tax.UseTaxAmount == 0 && p.Tax.VendorChargedTax == 0))
            {
                continue;
            }

            var r = Row(jid);
            rows[jid] = r with
            {
                TaxablePurchases = r.TaxablePurchases + (p.Tax.UseTaxAmount > 0 ? p.Tax.TaxableAmount : 0), UseTax = r.UseTax + p.Tax.UseTaxAmount,
                VendorChargedTax = r.VendorChargedTax + p.Tax.VendorChargedTax, Bills = r.Bills + 1,
            };
        }

        return new SalesTaxReturnDto(from, to, stateFilter, rows.Values.Where(r => stateFilter is null || r.State == stateFilter)
            .OrderBy(r => r.State, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).ToList());
    }

    public async Task<SalesTaxExportFile> ExportReturnCsvAsync(DateOnly from, DateOnly to, string? state = null, CancellationToken cancellationToken = default)
    {
        var report = await GetReturnAsync(from, to, state, cancellationToken);
        var rows = new List<IEnumerable<object?>>
        {
            new object?[] { "Jurisdiction", "Name", "State", "Gross sales", "Exempt sales", "Taxable sales", "Sales tax", "State tax", "County tax", "City tax", "Special tax", "Taxable purchases", "Use tax", "Vendor-charged tax", "Total due", "Invoices", "Bills" },
        };
        rows.AddRange(report.Rows.Select(r => new object?[]
        {
            r.Code, r.Name, r.State, r.GrossSales, r.ExemptSales, r.TaxableSales, r.SalesTax, r.StateTax, r.CountyTax, r.CityTax, r.SpecialTax, r.TaxablePurchases, r.UseTax, r.VendorChargedTax, r.TotalDue, r.Invoices, r.Bills,
        }));
        rows.Add(new object?[] { "Total", "", "", report.Rows.Sum(r => r.GrossSales), report.Rows.Sum(r => r.ExemptSales), report.Rows.Sum(r => r.TaxableSales), report.TotalSalesTax, report.Rows.Sum(r => r.StateTax),
            report.Rows.Sum(r => r.CountyTax), report.Rows.Sum(r => r.CityTax), report.Rows.Sum(r => r.SpecialTax), report.Rows.Sum(r => r.TaxablePurchases), report.TotalUseTax, report.Rows.Sum(r => r.VendorChargedTax), report.TotalDue, report.Rows.Sum(r => r.Invoices), report.Rows.Sum(r => r.Bills) });
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(Contracts.Security.SecurityEventTypes.ExportFinancialCsv, "sales and use tax return", Contracts.Security.SecurityOutcomes.Success,
                $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}{(report.State is null ? "" : " " + report.State)}", cancellationToken: cancellationToken);
        }

        return new SalesTaxExportFile($"sales-use-tax-{from:yyyyMMdd}-{to:yyyyMMdd}{(report.State is null ? "" : "-" + report.State)}.csv", "text/csv", Purchasing.PurchasingSupport.Csv(rows));
    }

    // ---- Helpers ----

    private IReadOnlyList<string> EngineNames() =>
        (engines ?? [engine]).Select(e => e.Name).Append(engine.Name).Distinct(StringComparer.Ordinal).ToList();

    private async Task<SalesTaxSettingsDto> SettingsDtoAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var s = await SalesTaxSupport.SettingsAsync(db, ct);
        var ids = new[] { s?.SalesTaxPayableAccountId, s?.UseTaxPayableAccountId, s?.UseTaxExpenseAccountId }.OfType<Guid>().ToList();
        var defaults = new[] { SalesTaxSupport.SalesTaxPayableNumber, SalesTaxSupport.UseTaxPayableNumber, SalesTaxSupport.UseTaxExpenseNumber };
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => ids.Contains(a.Id) || defaults.Contains(a.Number)).ToListAsync(ct);
        string? Label(Guid? id, string number) =>
            (id is { } x ? accounts.FirstOrDefault(a => a.Id == x) : accounts.FirstOrDefault(a => a.Number == number)) is { } a ? $"{a.Number} {a.Name}" : $"{number} (added when first used)";
        return new SalesTaxSettingsDto(
            s?.SalesTaxPayableAccountId, Label(s?.SalesTaxPayableAccountId, SalesTaxSupport.SalesTaxPayableNumber),
            s?.UseTaxPayableAccountId, Label(s?.UseTaxPayableAccountId, SalesTaxSupport.UseTaxPayableNumber),
            s?.UseTaxExpenseAccountId, Label(s?.UseTaxExpenseAccountId, SalesTaxSupport.UseTaxExpenseNumber),
            s?.HomeJurisdictionId, s?.DefaultSalesTaxCodeId, s?.DefaultPurchaseTaxCodeId, SalesTaxSupport.GovernmentCategories(s), s?.Engine ?? engine.Name, EngineNames());
    }

    private static object Snapshot(SalesTaxSettings s) => new
    {
        s.SalesTaxPayableAccountId, s.UseTaxPayableAccountId, s.UseTaxExpenseAccountId, s.HomeJurisdictionId, s.DefaultSalesTaxCodeId, s.DefaultPurchaseTaxCodeId, s.GovernmentCategories, s.Engine,
    };

    private static async Task EnsureAccountAsync(IWorkbenchDbContext db, Guid? id, AccountType type, string what, CancellationToken ct)
    {
        if (id is { } x)
        {
            var account = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == x, ct) ?? throw WorkbenchNotFoundException.For("Account", x);
            if (account.Type != type || !account.IsActive)
            {
                throw new ArgumentException($"{what} must be an active {type.ToString().ToLowerInvariant()} account.");
            }
        }
    }

    private async Task<IReadOnlyList<TaxCodeAssignmentDto>> AssignmentsAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var list = await db.Set<TaxCodeAssignment>().AsNoTracking().ToListAsync(ct);
        var codes = await db.Set<TaxCode>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var accountIds = list.Where(a => a.Target == TaxCodeTarget.Account && Guid.TryParse(a.TargetKey, out _)).Select(a => Guid.Parse(a.TargetKey)).ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id.ToString(), a => $"{a.Number} {a.Name}", ct);
        return list.OrderBy(a => a.Target).ThenBy(a => a.TargetKey, StringComparer.Ordinal).Select(a => new TaxCodeAssignmentDto(a.Id, (TaxCodeTargetKind)(int)a.Target, a.TargetKey,
            a.Target switch
            {
                TaxCodeTarget.InvoiceLineKind => $"Invoice line: {a.TargetKey}",
                TaxCodeTarget.OdcType => $"ODC type: {a.TargetKey}",
                TaxCodeTarget.Account => $"Account {accounts.GetValueOrDefault(a.TargetKey) ?? a.TargetKey}",
                _ => $"Part {a.TargetKey}",
            }, a.TaxCodeId, codes.GetValueOrDefault(a.TaxCodeId)?.Code ?? "")).ToList();
    }

    private async Task<IReadOnlyList<CustomerTaxDto>> CustomerDtosAsync(IWorkbenchDbContext db, IReadOnlyList<Customer> customers, CancellationToken ct)
    {
        var ids = customers.Select(c => c.Id).ToList();
        var settings = await SalesTaxSupport.SettingsAsync(db, ct);
        var profiles = await db.Set<CustomerTaxProfile>().AsNoTracking().Where(p => ids.Contains(p.CustomerId)).ToDictionaryAsync(p => p.CustomerId, ct);
        var certs = (await db.Set<TaxExemptionCertificate>().AsNoTracking().Where(c => ids.Contains(c.CustomerId)).ToListAsync(ct)).ToLookup(c => c.CustomerId);
        var jurisdictions = await db.Set<TaxJurisdiction>().AsNoTracking().ToDictionaryAsync(j => j.Id, j => j.Name, ct);
        return customers.OrderBy(c => c.Code, StringComparer.Ordinal).Select(c =>
        {
            var p = profiles.GetValueOrDefault(c.Id);
            var government = SalesTaxSupport.IsGovernment(c, settings);
            var status = p?.Status ?? CustomerTaxStatus.Default;
            return new CustomerTaxDto(c.Id, c.Code, c.Name, c.Category, government, (CustomerTaxState)(int)status,
                status == CustomerTaxStatus.Exempt || (status == CustomerTaxStatus.Default && government),
                p?.ShipToJurisdictionId, p?.ShipToJurisdictionId is { } j ? jurisdictions.GetValueOrDefault(j) : null,
                certs[c.Id].OrderByDescending(x => x.EffectiveDate).Select(ToDto).ToList());
        }).ToList();
    }

    private TaxExemptionCertificateDto ToDto(TaxExemptionCertificate c) =>
        new(c.Id, c.CustomerId, c.Number, c.State, c.Reason, c.EffectiveDate, c.ExpiresOn, c.IsRevoked,
            c.ExpiresOn is { } e && e < Today, c.ExpiresOn is { } s && s >= Today && s <= Today.AddDays(30));

    private TaxJurisdictionDto ToDto(TaxJurisdiction j, IEnumerable<TaxRate> rates)
    {
        var list = rates.OrderByDescending(r => r.EffectiveDate).ThenByDescending(r => r.CreatedAtUtc).ToList();
        var current = list.FirstOrDefault(r => r.EffectiveDate <= Today);
        return new TaxJurisdictionDto(j.Id, j.Code, j.Name, j.State, j.County, j.City, j.IsActive, current?.CombinedRate,
            list.Select(r => new TaxRateDto(r.Id, r.EffectiveDate, r.StateRate, r.CountyRate, r.CityRate, r.SpecialRate, r.CombinedRate, r.Source, r.CreatedBy)).ToList());
    }

    private static TaxCodeDto ToDto(TaxCode c) => new(c.Id, c.Code, c.Name, (TaxTreatmentKind)(int)c.Treatment, c.IsActive);

    private static void ValidateRates(params decimal[] rates)
    {
        if (rates.Any(r => r < 0 || r >= 0.5m || decimal.Round(r, 6) != r))
        {
            throw new ArgumentException("Rates are fractions from 0 to 0.5 with up to six decimals (0.053 is 5.3%).");
        }
    }

    private TaxRate NewRate(Guid jurisdictionId, DateOnly date, decimal state, decimal county, decimal city, decimal special, string source)
    {
        ValidateRates(state, county, city, special);
        return new TaxRate
        {
            JurisdictionId = jurisdictionId, EffectiveDate = date, StateRate = state, CountyRate = county, CityRate = city, SpecialRate = special,
            Source = source, CreatedBy = audit.Actor, CreatedAtUtc = UtcNow,
        };
    }

    private static async Task<Invoice> DraftAsync(IWorkbenchDbContext db, Guid invoiceId, CancellationToken ct)
    {
        var invoice = await db.Set<Invoice>().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw WorkbenchNotFoundException.For("Invoice", invoiceId);
        if (invoice.Status != InvoiceStatus.Draft)
        {
            throw new InvalidOperationException($"Invoice {invoice.Number} is {invoice.Status.ToString().ToLowerInvariant()}; tax is fixed once an invoice is approved.");
        }

        return invoice.Type == InvoiceType.CreditMemo
            ? throw new InvalidOperationException("A credit memo keeps the tax of the lines it credits.")
            : invoice;
    }

    private async Task<InvoiceTaxDto> InvoiceDtoAsync(IWorkbenchDbContext db, Invoice invoice, CancellationToken ct)
    {
        var t = await db.Set<InvoiceTax>().AsNoTracking().FirstOrDefaultAsync(x => x.InvoiceId == invoice.Id, ct);
        var jurisdictionId = t?.JurisdictionId ?? t?.JurisdictionOverrideId
            ?? await db.Set<CustomerTaxProfile>().AsNoTracking().Where(p => p.CustomerId == invoice.CustomerId).Select(p => p.ShipToJurisdictionId).FirstOrDefaultAsync(ct);
        var jurisdiction = jurisdictionId is { } jid ? await db.Set<TaxJurisdiction>().AsNoTracking().FirstOrDefaultAsync(j => j.Id == jid, ct) : null;
        var cert = t?.CertificateId is { } cid ? await db.Set<TaxExemptionCertificate>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        var notes = new List<string>();
        if (t is null && invoice.Status == InvoiceStatus.Draft)
        {
            notes.Add("Tax hasn't been worked out yet; it's calculated when the invoice is approved, or now with Calculate tax.");
        }

        if (invoice.Status == InvoiceStatus.Draft && t is not null && t.CalculatedAtUtc < invoice.UpdatedAtUtc.AddSeconds(-1))
        {
            notes.Add("The invoice changed after tax was calculated; approval recalculates it.");
        }

        return new InvoiceTaxDto(invoice.Id, invoice.Number, invoice.Status == InvoiceStatus.Draft, t is not null, jurisdiction?.Id, jurisdiction?.Name, t?.JurisdictionOverrideId,
            t?.TaxableAmount ?? 0, t?.ExemptAmount ?? 0, t?.Rate ?? 0, t?.TaxAmount ?? 0, t?.ExemptionReason, cert?.Number, notes, t?.Engine, t?.CalculatedAtUtc);
    }

    private async Task<BillTaxDto> BillDtoAsync(IWorkbenchDbContext db, Guid billId, CancellationToken ct)
    {
        var bill = await db.Set<ApBill>().AsNoTracking().Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == billId, ct) ?? throw WorkbenchNotFoundException.For("Bill", billId);
        var record = await db.Set<ApBillTax>().AsNoTracking().FirstOrDefaultAsync(t => t.BillId == billId, ct);
        var settings = await SalesTaxSupport.SettingsAsync(db, ct);
        var evaluation = await useTax.EvaluateAsync(db, bill, record, settings, ct);
        var notes = evaluation.Notes.ToList();
        var expected = evaluation.Result?.Tax ?? 0;
        if (bill.Status == ApBillStatus.Posted && record is { VendorChargedTax: 0 } && expected > 0 && record.UseTaxJournalEntryId is null)
        {
            notes.Add("Use tax is due on this bill but hasn't posted (check the fiscal period and the tax accounts), then save again.");
        }

        if (bill.Status != ApBillStatus.Posted && (record?.VendorChargedTax ?? 0) == 0 && expected > 0)
        {
            notes.Add($"The vendor didn't charge tax, so {expected:C} use tax will accrue when the bill posts.");
        }

        return new BillTaxDto(bill.Id, bill.Number, bill.Status == ApBillStatus.Posted, evaluation.Jurisdiction?.Id, evaluation.Jurisdiction?.Name, record?.VendorChargedTax ?? 0,
            record?.TaxCodeOverrideId, evaluation.Result?.TaxableAmount ?? 0, evaluation.Result?.Rate ?? 0, expected, record?.UseTaxAmount ?? 0, record?.UseTaxJournalEntryId, notes);
    }

    private static string Required(string? value, string what, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException($"{what} is required.");
        }

        return text.Length <= max ? text : throw new ArgumentException($"{what} can be at most {max} characters.");
    }

    private static string? Optional(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length <= max ? text : throw new ArgumentException($"That's longer than {max} characters.");
    }

    private static string? State(string? value)
    {
        var text = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length == 2 && text.All(char.IsAsciiLetterUpper) ? text : throw new ArgumentException($"\"{value}\" isn't a two-letter state code.");
    }
}

/// <summary>Parses rate table CSV (see <see cref="ImportTaxRatesRequest"/>).</summary>
internal static class RateTableCsv
{
    internal sealed record Row(int Line, string Code, string? Name, string? State, string? County, string? City, DateOnly EffectiveDate,
        decimal StateRate, decimal CountyRate, decimal CityRate, decimal SpecialRate, string? Error);

    public static IReadOnlyList<Row> Parse(string csv)
    {
        var lines = csv.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select((text, i) => (Text: text, Line: i + 1)).Where(l => l.Text.Trim().Length > 0).ToList();
        if (lines.Count < 2)
        {
            throw new ArgumentException("The file needs a header row and at least one rate.");
        }

        if (lines.Count > 20_001)
        {
            throw new ArgumentException("Import at most 20,000 rates at a time.");
        }

        var header = Split(lines[0].Text).Select(h => h.Trim().Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        int code = Col("code", "jurisdiction", "jurisdictioncode"), name = Col("name"), state = Col("state"), county = Col("county"), city = Col("city"),
            date = Col("effectivedate", "effective", "date"), sr = Col("staterate"), cr = Col("countyrate"), ci = Col("cityrate"), sp = Col("specialrate", "districtrate");
        if (code < 0 || date < 0 || sr < 0)
        {
            throw new ArgumentException("The header needs at least Code, EffectiveDate and StateRate columns.");
        }

        var rows = new List<Row>();
        foreach (var (text, line) in lines.Skip(1))
        {
            var f = Split(text);
            string? At(int i) => i >= 0 && i < f.Count && f[i].Trim().Length > 0 ? f[i].Trim() : null;
            try
            {
                var c = At(code)?.ToUpperInvariant() ?? throw new FormatException("the code is missing");
                if (c.Length > 64)
                {
                    throw new FormatException("the code is longer than 64 characters");
                }

                var d = DateOnly.TryParseExact(At(date), ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed : throw new FormatException("the effective date isn't a date (use yyyy-MM-dd)");
                rows.Add(new Row(line, c, At(name), At(state), At(county), At(city), d, Rate(At(sr)), Rate(At(cr)), Rate(At(ci)), Rate(At(sp)), null));
            }
            catch (FormatException ex)
            {
                rows.Add(new Row(line, "", null, null, null, null, default, 0, 0, 0, 0, ex.Message));
            }
        }

        return rows;
    }

    /// <summary>"5.3%" or "5.3" (one or more: a percent) or "0.053" (a fraction).</summary>
    private static decimal Rate(string? text)
    {
        if (text is null)
        {
            return 0;
        }

        var percent = text.EndsWith('%');
        if (!decimal.TryParse(text.TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new FormatException($"\"{text}\" isn't a rate");
        }

        return percent || value >= 1 ? decimal.Round(value / 100m, 6) : decimal.Round(value, 6);
    }

    private static List<string> Split(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
