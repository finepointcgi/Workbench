using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Billing;
using Workbench.Contracts.AccountingExport;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.AccountingExport;

/// <summary>A record an export could include: its id and number, for the "already exported" check.</summary>
public sealed record ExportCandidate(Guid Id, string Number);

/// <summary>
/// Finds the records an export covers and turns them into neutral documents with every name resolved through the target's mappings.
/// Reads only; the service decides which candidates to include.
/// </summary>
internal static class ExportDataLoader
{
    private static readonly InvoiceStatus[] ExportableInvoices = [InvoiceStatus.Approved, InvoiceStatus.Sent, InvoiceStatus.PartiallyPaid, InvoiceStatus.Paid];
    private static readonly JournalSource[] SubledgerSources = [JournalSource.Billing, JournalSource.AP];

    public static async Task<List<ExportCandidate>> CandidatesAsync(
        IWorkbenchDbContext db, AccountingFileRequest r, IReadOnlySet<Guid>? visibleCompanies, CancellationToken ct)
    {
        var companies = visibleCompanies?.ToList();
        switch (r.Kind)
        {
            case AccountingExportKind.ChartOfAccounts:
            {
                var q = db.Set<Account>().AsNoTracking().AsQueryable();
                if (r.CompanyId is { } c)
                {
                    q = q.Where(a => a.CompanyId == null || a.CompanyId == c);
                }

                return (await q.Select(a => new { a.Id, a.Number }).ToListAsync(ct)).Select(a => new ExportCandidate(a.Id, a.Number)).ToList();
            }

            case AccountingExportKind.JournalEntries:
            {
                var q = db.Set<JournalEntry>().AsNoTracking().Where(e => e.Status != JournalStatus.Draft && e.Date >= r.From && e.Date <= r.To);
                if (r.CompanyId is { } c)
                {
                    q = q.Where(e => e.CompanyId == c);
                }
                else if (companies is not null)
                {
                    q = q.Where(e => companies.Contains(e.CompanyId));
                }

                if (!r.IncludeSubledgerJournals)
                {
                    // Invoices, payments and bills carry their own entries; exporting both would count them twice in the destination.
                    var excluded = SubledgerSources;
                    var excludedIds = db.Set<JournalEntry>().Where(x => excluded.Contains(x.Source)).Select(x => (Guid?)x.Id);
                    q = q.Where(e => !excluded.Contains(e.Source) && (e.ReversalOfId == null || !excludedIds.Contains(e.ReversalOfId)));
                }

                return (await q.Select(e => new { e.Id, e.Number }).ToListAsync(ct)).Select(e => new ExportCandidate(e.Id, e.Number)).ToList();
            }

            case AccountingExportKind.Invoices:
            {
                var q = db.Set<Invoice>().AsNoTracking().Where(i => ExportableInvoices.Contains(i.Status) && i.InvoiceDate >= r.From && i.InvoiceDate <= r.To);
                if (r.CompanyId is { } c)
                {
                    q = q.Where(i => i.CompanyId == c);
                }
                else if (companies is not null)
                {
                    q = q.Where(i => companies.Contains(i.CompanyId));
                }

                return (await q.Select(i => new { i.Id, i.Number }).ToListAsync(ct)).Select(i => new ExportCandidate(i.Id, i.Number)).ToList();
            }

            case AccountingExportKind.Payments:
            {
                var q = db.Set<Payment>().AsNoTracking().Where(p => !p.IsVoid && p.ReceivedOn >= r.From && p.ReceivedOn <= r.To);
                if (r.CompanyId is { } c)
                {
                    q = q.Where(p => p.CompanyId == c);
                }
                else if (companies is not null)
                {
                    q = q.Where(p => companies.Contains(p.CompanyId));
                }

                return (await q.Select(p => new { p.Id, p.Reference, p.ReceivedOn }).ToListAsync(ct))
                    .Select(p => new ExportCandidate(p.Id, p.Reference ?? p.ReceivedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).ToList();
            }

            default:
            {
                var q = db.Set<ApBill>().AsNoTracking().Where(b => b.Status == ApBillStatus.Posted && b.InvoiceDate >= r.From && b.InvoiceDate <= r.To);
                if (r.CompanyId is { } c)
                {
                    q = q.Where(b => b.CompanyId == c);
                }
                else if (companies is not null)
                {
                    q = q.Where(b => companies.Contains(b.CompanyId));
                }

                return (await q.Select(b => new { b.Id, b.Number }).ToListAsync(ct)).Select(b => new ExportCandidate(b.Id, b.Number)).ToList();
            }
        }
    }

    /// <summary>Documents that were exported earlier and voided since the target's last run of this kind: the destination still holds them.</summary>
    public static async Task<List<string>> VoidedSinceAsync(IWorkbenchDbContext db, AccountingExportKind kind, IReadOnlyCollection<Guid> exportedIds, DateTime? since, CancellationToken ct)
    {
        var warnings = new List<string>();
        if (exportedIds.Count == 0 || since is null)
        {
            return warnings;
        }

        var ids = exportedIds.ToList();
        List<string> numbers = kind switch
        {
            AccountingExportKind.Invoices => (await db.Set<Invoice>().AsNoTracking()
                .Where(i => ids.Contains(i.Id) && i.Status == InvoiceStatus.Void && i.VoidedAtUtc > since).Select(i => i.Number).ToListAsync(ct)),
            AccountingExportKind.Bills => (await db.Set<ApBill>().AsNoTracking()
                .Where(b => ids.Contains(b.Id) && b.Status == ApBillStatus.Void && b.VoidedAtUtc > since).Select(b => b.Number).ToListAsync(ct)),
            AccountingExportKind.Payments => (await db.Set<Payment>().AsNoTracking()
                .Where(p => ids.Contains(p.Id) && p.IsVoid && p.VoidedAtUtc > since).Select(p => p.Reference ?? "payment").ToListAsync(ct)),
            _ => [],
        };
        if (numbers.Count > 0)
        {
            warnings.Add($"{numbers.Count} document(s) exported earlier were voided in Workbench since the last export and are still in your accounting software: "
                + string.Join(", ", numbers.Order(StringComparer.Ordinal).Take(15)) + (numbers.Count > 15 ? ", ..." : "") + ". Void or delete them there too.");
        }

        return warnings;
    }

    public static async Task<ExportDataset> BuildAsync(
        IWorkbenchDbContext db, AccountingExportKind kind, IReadOnlyCollection<Guid> idSet, MappingResolver map, CancellationToken ct)
    {
        var ids = idSet.ToList();
        return kind switch
        {
            AccountingExportKind.ChartOfAccounts => await AccountsAsync(db, ids, map, ct),
            AccountingExportKind.JournalEntries => await JournalsAsync(db, ids, map, ct),
            AccountingExportKind.Invoices => await InvoicesAsync(db, ids, map, ct),
            AccountingExportKind.Payments => await PaymentsAsync(db, ids, map, ct),
            _ => await BillsAsync(db, ids, map, ct),
        };
    }

    private static Resolved AccountOf(MappingResolver map, Account a) => map.Resolve(AccountingMapKind.Account, a.Number, a.Name, a.Number);

    private static Resolved? ClassOf(MappingResolver map, Guid? projectId, IReadOnlyDictionary<Guid, Project> projects) =>
        projectId is { } id && projects.TryGetValue(id, out var p) ? map.Resolve(AccountingMapKind.Class, p.Code, p.Name, p.Code) : null;

    private static async Task<Dictionary<Guid, Project>> ProjectsAsync(IWorkbenchDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return await db.Set<Project>().AsNoTracking().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
    }

    private static async Task<ExportDataset> AccountsAsync(IWorkbenchDbContext db, List<Guid> ids, MappingResolver map, CancellationToken ct)
    {
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        var numbers = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Number, ct);
        return new ExportDataset
        {
            Kind = AccountingExportKind.ChartOfAccounts,
            Accounts = accounts.OrderBy(a => a.Number, StringComparer.Ordinal)
                .Select(a => new AccountDoc(a.Id, a.Number, a.Name, a.Type, a.ParentId is { } p ? numbers.GetValueOrDefault(p) : null,
                    map.Resolve(AccountingMapKind.Account, a.Number, a.Name, a.Number), a.Description, a.IsActive)).ToList(),
        };
    }

    private static async Task<ExportDataset> JournalsAsync(IWorkbenchDbContext db, List<Guid> ids, MappingResolver map, CancellationToken ct)
    {
        var entries = await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines).Where(e => ids.Contains(e.Id)).ToListAsync(ct);
        var accountIds = entries.SelectMany(e => e.Lines).Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var projects = await ProjectsAsync(db, entries.SelectMany(e => e.Lines).Select(l => l.ProjectId), ct);
        var set = new ExportDataset
        {
            Kind = AccountingExportKind.JournalEntries,
            Journals = entries.OrderBy(e => e.Date).ThenBy(e => e.Sequence).ThenBy(e => e.Number, StringComparer.Ordinal)
                .Select(e => new JournalDoc(e.Id, e.Number, e.Date, e.Description, e.Source.ToString(),
                    e.Lines.OrderBy(l => l.LineNumber).Select(l => new JournalLineRow(l.LineNumber, accounts[l.AccountId].Number, AccountOf(map, accounts[l.AccountId]),
                        l.Debit, l.Credit, l.Memo, ClassOf(map, l.ProjectId, projects))).ToList())).ToList(),
        };
        var foreign = entries.Count(e => e.Lines.Any(l => l.TransactionCurrency != null));
        if (foreign > 0)
        {
            set.Warnings.Add($"{foreign} journal entr{(foreign == 1 ? "y has" : "ies have")} foreign-currency lines. They are exported in the functional currency.");
        }

        var interco = entries.Count(e => e.Lines.Any(l => l.IntercompanyCompanyId != null));
        if (interco > 0)
        {
            set.Warnings.Add($"{interco} journal entr{(interco == 1 ? "y has" : "ies have")} due-to/due-from (intercompany) lines. Check the destination books treat them as you expect.");
        }

        return set;
    }

    private static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static async Task<ExportDataset> InvoicesAsync(IWorkbenchDbContext db, List<Guid> ids, MappingResolver map, CancellationToken ct)
    {
        var invoices = await db.Set<Invoice>().AsNoTracking().Include(i => i.Lines).Where(i => ids.Contains(i.Id)).ToListAsync(ct);
        var customerIds = invoices.Select(i => i.CustomerId).Distinct().ToList();
        var customers = await db.Set<Customer>().AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var contractIds = invoices.Select(i => i.ContractId).Distinct().ToList();
        var contracts = await db.Set<Contract>().AsNoTracking().Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => contractIds.Contains(c.ContractId)).ToDictionaryAsync(c => c.Id, ct);
        var projects = await ProjectsAsync(db, invoices.SelectMany(i => i.Lines).Select(l => l.ProjectId), ct);
        var salesTaxNumber = invoices.Any(i => i.Lines.Any(l => l.Kind == InvoiceLineKind.SalesTax))
            ? await SalesTax.SalesTaxSupport.SalesTaxPayableNumberAsync(db, ct)
            : null;
        var neededNumbers = new HashSet<string> { BillingLedger.ArBilled, BillingLedger.ArUnbilled, BillingLedger.ContractFinancing, "4010", "4020", "4030", "4040" };
        if (salesTaxNumber is not null)
        {
            neededNumbers.Add(salesTaxNumber);
        }

        var accountsByNumber = (await db.Set<Account>().AsNoTracking().Where(a => neededNumbers.Contains(a.Number)).ToListAsync(ct)).ToDictionary(a => a.Number);
        Resolved AccountByNumber(string number) =>
            accountsByNumber.TryGetValue(number, out var a) ? AccountOf(map, a) : map.Resolve(AccountingMapKind.Account, number, number, number);
        var noTax = map.Resolve(AccountingMapKind.TaxCode, AccountingMapKeys.NoTax, "No tax", null);

        var docs = new List<InvoiceDoc>();
        var set = new ExportDataset { Kind = AccountingExportKind.Invoices };
        foreach (var inv in invoices.OrderBy(i => i.InvoiceDate).ThenBy(i => i.Sequence).ThenBy(i => i.Number, StringComparer.Ordinal))
        {
            var customer = customers[inv.CustomerId];
            var contract = contracts[inv.ContractId];
            var lines = new List<InvoiceLineRow>();
            foreach (var l in inv.Lines.OrderBy(l => l.LineNumber))
            {
                // The same account the ledger credited for this line (BillingLedger.PostInvoiceAsync).
                var number = l.Kind is InvoiceLineKind.FeeWithhold or InvoiceLineKind.WithholdRelease ? BillingLedger.ArUnbilled
                    : BillingLedger.IsFinancing(l.Kind) ? BillingLedger.ContractFinancing
                    : l.Kind == InvoiceLineKind.SalesTax ? salesTaxNumber!
                    : BillingLedger.RevenueAccount(l.ClinId is { } cid && clins.TryGetValue(cid, out var clin) ? clin.Pricing : null, contract.Type);
                var useQtyRate = l.Quantity > 0 && l.Rate != 0 && decimal.Round(l.Quantity * l.Rate, 2) == l.Amount;
                var kindName = l.Kind.ToString();
                lines.Add(new InvoiceLineRow(l.LineNumber, kindName, map.Resolve(AccountingMapKind.Item, kindName, KindLabel(l.Kind), null), number, AccountByNumber(number),
                    l.Description, useQtyRate ? l.Quantity : 1m, useQtyRate ? l.Rate : l.Amount, l.Amount, ClassOf(map, l.ProjectId, projects), noTax));
            }

            docs.Add(new InvoiceDoc(inv.Id, inv.Number, map.Resolve(AccountingMapKind.Customer, customer.Code, customer.Name, customer.Code), inv.InvoiceDate, inv.DueDate,
                inv.PaymentTermsDays, contract.ContractNumber, inv.Notes, inv.Total, inv.Type == InvoiceType.CreditMemo, BillingLedger.ArBilled, AccountByNumber(BillingLedger.ArBilled), lines));
            if (decimal.Round(lines.Sum(l => l.Amount), 2) != decimal.Round(inv.Total, 2))
            {
                set.Warnings.Add($"Invoice {inv.Number}: the lines add up to {Money(lines.Sum(l => l.Amount))} but the invoice total is {Money(inv.Total)}.");
            }
        }

        var foreign = invoices.Count(i => i.CurrencyCode != null);
        if (foreign > 0)
        {
            set.Warnings.Add($"{foreign} invoice(s) are in a foreign currency. They are exported in the functional currency (what Workbench booked).");
        }

        var result = new ExportDataset { Kind = set.Kind, Invoices = docs };
        result.Warnings.AddRange(set.Warnings);
        return result;
    }

    private static string KindLabel(InvoiceLineKind kind) => kind switch
    {
        InvoiceLineKind.Odc => "Other direct costs",
        InvoiceLineKind.MaterialHandling => "Material handling",
        InvoiceLineKind.FeeWithhold => "Fee withheld",
        InvoiceLineKind.WithholdRelease => "Fee withhold released",
        InvoiceLineKind.ProgressPayment => "Progress payment",
        InvoiceLineKind.PerformanceBasedPayment => "Performance-based payment",
        InvoiceLineKind.SalesTax => "Sales tax",
        _ => kind.ToString(),
    };

    public static IReadOnlyList<(string Key, string Name)> InvoiceItemKeys { get; } =
        Enum.GetValues<InvoiceLineKind>().Select(k => (k.ToString(), KindLabel(k))).ToList();

    private static async Task<ExportDataset> PaymentsAsync(IWorkbenchDbContext db, List<Guid> ids, MappingResolver map, CancellationToken ct)
    {
        var payments = await db.Set<Payment>().AsNoTracking().Include(p => p.Applications).Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        var customerIds = payments.Select(p => p.CustomerId).Distinct().ToList();
        var customers = await db.Set<Customer>().AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var invoiceIds = payments.SelectMany(p => p.Applications).Select(a => a.InvoiceId).Distinct().ToList();
        var invoiceNumbers = await db.Set<Invoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number, ct);
        var cash = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == BillingLedger.Cash, ct);
        var ar = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == BillingLedger.ArBilled, ct);
        Resolved Of(Account? a, string number) => a is null ? map.Resolve(AccountingMapKind.Account, number, number, number) : AccountOf(map, a);
        var set = new ExportDataset
        {
            Kind = AccountingExportKind.Payments,
            Payments = payments.OrderBy(p => p.ReceivedOn).ThenBy(p => p.CreatedAtUtc).Select(p =>
            {
                var c = customers[p.CustomerId];
                return new PaymentDoc(p.Id, map.Resolve(AccountingMapKind.Customer, c.Code, c.Name, c.Code), p.ReceivedOn, p.Amount, p.Method.ToString(), p.Reference, p.Notes,
                    BillingLedger.Cash, Of(cash, BillingLedger.Cash), Of(ar, BillingLedger.ArBilled),
                    p.Applications.Select(a => new PaymentApplicationRow(invoiceNumbers.GetValueOrDefault(a.InvoiceId, "?"), a.Amount)).ToList());
            }).ToList(),
        };
        var foreign = payments.Count(p => p.CurrencyCode != null);
        if (foreign > 0)
        {
            set.Warnings.Add($"{foreign} payment(s) are in a foreign currency. They are exported in the functional currency, deposited to cash account {BillingLedger.Cash}.");
        }

        return set;
    }

    private static async Task<ExportDataset> BillsAsync(IWorkbenchDbContext db, List<Guid> ids, MappingResolver map, CancellationToken ct)
    {
        var bills = await db.Set<ApBill>().AsNoTracking().Include(b => b.Lines).Where(b => ids.Contains(b.Id)).ToListAsync(ct);
        var vendorIds = bills.Select(b => b.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var lineAccountIds = bills.SelectMany(b => b.Lines).Where(l => l.AccountId != null).Select(l => l.AccountId!.Value).Distinct().ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => lineAccountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var projects = await ProjectsAsync(db, bills.SelectMany(b => b.Lines).Select(l => l.ProjectId), ct);
        Account? payables = null;
        try
        {
            payables = await Purchasing.PurchasingSupport.PayablesAccountAsync(db, ct);
        }
        catch (ArgumentException)
        {
            // No payables account yet: the bills still export, with the account named by its usual number.
        }

        var odcRules = await db.Set<PostingRule>().AsNoTracking().Where(r => r.Kind == PostingRuleKind.Odc).ToListAsync(ct);
        var ruleAccountIds = odcRules.Select(r => r.AccountId).Distinct().ToList();
        var ruleAccounts = await db.Set<Account>().AsNoTracking().Where(a => ruleAccountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var noTax = map.Resolve(AccountingMapKind.TaxCode, AccountingMapKeys.NoTax, "No tax", null);
        var set = new ExportDataset { Kind = AccountingExportKind.Bills };
        var docs = new List<BillDoc>();
        foreach (var b in bills.OrderBy(b => b.InvoiceDate).ThenBy(b => b.Number, StringComparer.Ordinal))
        {
            var vendor = vendors[b.VendorId];
            var lines = new List<BillLineRow>();
            var number = 0;
            foreach (var l in b.Lines.Where(l => l.Kind != ApBillLineKind.Retainage).OrderBy(l => l.LineNumber))
            {
                Account? account = l.AccountId is { } aid ? accounts.GetValueOrDefault(aid) : null;
                if (account is null && l.OdcType is { } type)
                {
                    var rule = odcRules.FirstOrDefault(r => r.Key == type.ToString()) ?? odcRules.FirstOrDefault(r => r.Key == "");
                    account = rule is null ? null : ruleAccounts.GetValueOrDefault(rule.AccountId);
                }

                if (account is null)
                {
                    set.Warnings.Add($"Bill {b.Number} line {l.LineNumber} has no GL account (a project cost line with no posting rule); it was skipped.");
                    continue;
                }

                lines.Add(new BillLineRow(++number, account.Number, AccountOf(map, account), l.Description, l.Amount, ClassOf(map, l.ProjectId, projects), noTax));
            }

            if (b.RetainageAmount > 0)
            {
                set.Warnings.Add($"Bill {b.Number} holds retainage of {Money(b.RetainageAmount)}. It is exported at the full invoice amount; track the retainage in your accounting software.");
            }

            var payablesNumber = payables?.Number ?? "2010";
            docs.Add(new BillDoc(b.Id, b.Number, map.Resolve(AccountingMapKind.Vendor, vendor.Code, vendor.Name, vendor.Code), b.InvoiceNumber, b.InvoiceDate, b.DueDate, (b.DueDate.DayNumber - b.InvoiceDate.DayNumber),
                b.Description, lines.Sum(l => l.Amount), b.IsCredit, payablesNumber,
                payables is null ? map.Resolve(AccountingMapKind.Account, payablesNumber, "Accounts payable", payablesNumber) : AccountOf(map, payables), lines));
        }

        var foreign = bills.Count(b => b.CurrencyCode != null);
        if (foreign > 0)
        {
            set.Warnings.Add($"{foreign} bill(s) are in a foreign currency. They are exported in the functional currency.");
        }

        var result = new ExportDataset { Kind = set.Kind, Bills = docs };
        result.Warnings.AddRange(set.Warnings);
        return result;
    }
}
