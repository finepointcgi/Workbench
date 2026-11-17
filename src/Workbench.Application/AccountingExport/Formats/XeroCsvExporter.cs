using Workbench.Contracts.AccountingExport;
using static Workbench.Application.AccountingExport.Formats.ExportSupport;

namespace Workbench.Application.AccountingExport.Formats;

/// <summary>
/// CSV files for Xero's import screens: manual journals, sales invoices, bills and the chart of accounts.
/// Layout sources. Xero Central's own articles could not be read when this was written (they render only in a browser), so the
/// layouts come from third-party write-ups of Xero's templates and are UNVERIFIED against the downloadable templates:
/// manual journals (*Narration, *Date, Description, *AccountCode, TaxRate, *Amount, TrackingName1/Option1...; debits positive, credits negative;
/// 300 lines per import) https://support.approvalmax.com/en/articles/414429-how-to-upload-a-manual-journal-to-approvalmax and Xero Product Ideas
/// "Manual Journals - Extend import limit"; sales invoices (27 columns, rows sharing *InvoiceNumber form one invoice, imported as drafts)
/// https://syslumelab.com/guides/xero-sales-invoice-import-csv and https://entryrocket.com/guides/xero-csv-import-guide;
/// bills (same family; template not read) https://central.xero.com/0/article/Import-bills-and-credit-notes-US;
/// chart of accounts (Type must be spelled as Xero's list; 1,000 rows) https://central.xero.com/s/article/Import-a-chart-of-accounts.
/// Dates are written as "05 Jan 2026" so they read the same in every regional setting.
/// </summary>
public sealed class XeroCsvExporter : IAccountingExporter
{
    private const string DateFormat = "dd MMM yyyy";

    public AccountingFormatDto Format { get; } = new(
        AccountingFormatIds.XeroCsv, "Xero (CSV)", AccountingTarget.Xero, "csv",
        [AccountingExportKind.ChartOfAccounts, AccountingExportKind.JournalEntries, AccountingExportKind.Invoices, AccountingExportKind.Bills],
        "Xero Central import templates (manual journals, sales invoices, bills, chart of accounts); layouts not verified against Xero's own pages.",
        "Xero holds manual journals to 300 lines per import and refuses bank and system (receivables, payables) accounts in them. Imported invoices and bills arrive as drafts. Credit memos and vendor credits are skipped.");

    public ExportFile Export(ExportInput input)
    {
        var warnings = new List<string>(input.Data.Warnings);
        switch (input.Kind)
        {
            case AccountingExportKind.JournalEntries:
            {
                var rows = new List<IReadOnlyList<string>>();
                foreach (var j in input.Data.Journals)
                {
                    var narration = Safe($"{j.Number}: {j.Description}");
                    foreach (var l in j.Lines)
                    {
                        var (tn, to) = Tracking(l.Class);
                        rows.Add([narration, Date(j.Date, DateFormat), Safe(l.Memo ?? ""), Safe(l.Account.Code ?? l.AccountNumber), "", Amount(l.Debit - l.Credit), tn, to]);
                    }
                }

                if (rows.Count > 300)
                {
                    warnings.Add($"Xero imports up to 300 journal lines at a time; this file has {rows.Count}. Export a shorter period.");
                }

                warnings.Add("Xero doesn't allow bank or system accounts (Accounts Receivable, Accounts Payable) in manual journals. Journals touching them need entering another way.");
                var header = new[] { "*Narration", "*Date", "Description", "*AccountCode", "TaxRate", "*Amount", "TrackingName1", "TrackingOption1" };
                return Result(input, header, rows, input.Data.Journals.Count, input.Data.Journals.Sum(j => j.Lines.Sum(l => l.Debit)), warnings, null);
            }

            case AccountingExportKind.Invoices:
            {
                var skipped = new List<Guid>();
                var rows = new List<IReadOnlyList<string>>();
                var exported = 0;
                decimal total = 0;
                foreach (var inv in input.Data.Invoices)
                {
                    if (inv.IsCreditMemo)
                    {
                        skipped.Add(inv.Id);
                        warnings.Add($"Credit memo {inv.Number} was left out: Xero's invoice import doesn't take credit notes. Import it with Xero's credit note import.");
                        continue;
                    }

                    exported++;
                    total += inv.Total;
                    rows.AddRange(inv.Lines.Select(l =>
                    {
                        var (tn, to) = Tracking(l.Class);
                        return (IReadOnlyList<string>)InvoiceRow(Safe(inv.Customer.Name), inv.Number, Safe(inv.Reference), inv.Date, inv.DueDate, Safe(l.Description), l.Quantity, l.Rate,
                            Safe(l.Account.Code ?? l.AccountNumber), TaxType(l.TaxCode), tn, to);
                    }));
                }

                return Result(input, InvoiceHeader, rows, exported, total, warnings, skipped);
            }

            case AccountingExportKind.Bills:
            {
                var skipped = new List<Guid>();
                var rows = new List<IReadOnlyList<string>>();
                var exported = 0;
                decimal total = 0;
                foreach (var bill in input.Data.Bills)
                {
                    if (bill.IsCredit || bill.Lines.Count == 0)
                    {
                        skipped.Add(bill.Id);
                        warnings.Add($"{(bill.IsCredit ? "Vendor credit" : "Bill")} {bill.Number} was left out: " + (bill.IsCredit ? "import credits with Xero's credit note import." : "it has no lines with a GL account."));
                        continue;
                    }

                    exported++;
                    total += bill.Amount;
                    rows.AddRange(bill.Lines.Select(l =>
                    {
                        var (tn, to) = Tracking(l.Class);
                        return (IReadOnlyList<string>)InvoiceRow(Safe(bill.Vendor.Name), bill.VendorInvoiceNumber, Safe(bill.Number), bill.Date, bill.DueDate, Safe(l.Description), 1m, l.Amount,
                            Safe(l.Account.Code ?? l.AccountNumber), TaxType(l.TaxCode), tn, to);
                    }));
                }

                return Result(input, InvoiceHeader, rows, exported, total, warnings, skipped);
            }

            case AccountingExportKind.ChartOfAccounts:
            {
                var skipped = new List<Guid>();
                var parents = input.Data.Accounts.Where(a => a.ParentNumber is not null).Select(a => a.ParentNumber!).ToHashSet(StringComparer.Ordinal);
                var rows = new List<IReadOnlyList<string>>();
                foreach (var a in input.Data.Accounts)
                {
                    if (parents.Contains(a.Number))
                    {
                        skipped.Add(a.Id);
                        continue;
                    }

                    var code = a.Destination.Code ?? a.Number;
                    if (code.Length > 10)
                    {
                        warnings.Add($"Account code {code} is longer than Xero's 10 characters.");
                    }

                    rows.Add([Safe(code), Safe(a.Destination.Name), XeroType(ClassOf(a.Number, a.Type)), "", Safe(a.Description), "", "", "", ""]);
                }

                if (skipped.Count > 0)
                {
                    warnings.Add($"{skipped.Count} group account(s) with sub-accounts were left out; Workbench posts only to the accounts under them.");
                }

                warnings.Add("Xero has one Accounts Receivable and one Accounts Payable control account of its own; import Workbench's 1110 and 2010 as ordinary accounts or map them to Xero's.");
                var header = new[] { "*Code", "*Name", "*Type", "*Tax Code", "Description", "Dashboard", "Expense Claims", "Enable Payments", "Balance" };
                return Result(input, header, rows, rows.Count, 0, warnings, skipped);
            }

            default:
                throw new NotSupportedException("Xero CSV can't carry " + input.Kind + ".");
        }
    }

    private static readonly string[] InvoiceHeader =
    [
        "*ContactName", "EmailAddress", "POAddressLine1", "POAddressLine2", "POAddressLine3", "POAddressLine4", "POCity", "PORegion", "POPostalCode", "POCountry",
        "*InvoiceNumber", "Reference", "*InvoiceDate", "*DueDate", "InventoryItemCode", "*Description", "*Quantity", "*UnitAmount", "Discount", "*AccountCode", "*TaxType",
        "TrackingName1", "TrackingOption1", "TrackingName2", "TrackingOption2", "Currency", "BrandingTheme",
    ];

    private static string[] InvoiceRow(string contact, string number, string reference, DateOnly date, DateOnly due, string description, decimal quantity, decimal unit, string account, string taxType, string trackingName, string trackingOption) =>
        [contact, "", "", "", "", "", "", "", "", "", Safe(number), reference, Date(date, DateFormat), Date(due, DateFormat), "", description, Number(quantity), Amount(unit), "", account, taxType, trackingName, trackingOption, "", "", "", ""];

    // A class maps to a tracking option; its destination code is the tracking category's name.
    private static (string Name, string Option) Tracking(Resolved? c) => c is { IsMapped: true, Code: { Length: > 0 } code } r ? (Safe(code), Safe(r.Name)) : ("", "");

    private static string TaxType(Resolved tax) => Safe(tax.Name);

    // Xero's account type names as the add-account screen lists them (unverified against the import template).
    private static string XeroType(AccountClass c) => c switch
    {
        AccountClass.Bank => "Bank",
        AccountClass.Receivable or AccountClass.CurrentAsset => "Current Asset",
        AccountClass.FixedAsset => "Fixed Asset",
        AccountClass.Payable or AccountClass.CurrentLiability => "Current Liability",
        AccountClass.Equity => "Equity",
        AccountClass.Income => "Revenue",
        AccountClass.OtherIncome => "Other Income",
        AccountClass.DirectCost => "Direct Costs",
        _ => "Expense",
    };

    private ExportFile Result(ExportInput input, string[] header, List<IReadOnlyList<string>> rows, int records, decimal total, List<string> warnings, List<Guid>? skipped) =>
        new(FileName(input, Format.Id, "csv"), "text/csv", Csv(header, rows), rows.Count, records, total, warnings, skipped);
}
