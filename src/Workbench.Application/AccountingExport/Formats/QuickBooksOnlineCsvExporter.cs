using Workbench.Contracts.AccountingExport;
using static Workbench.Application.AccountingExport.Formats.ExportSupport;

namespace Workbench.Application.AccountingExport.Formats;

/// <summary>
/// CSV files for QuickBooks Online's Settings › Import data (journal entries, invoices, bills, chart of accounts).
/// QuickBooks maps the file's columns to its fields on screen, so the header text is for people; what matters is the content.
/// Layout sources (Intuit help, read 2026-11):
/// journal entries https://quickbooks.intuit.com/learn-support/en-us/help-article/import-export-data-files/import-journal-entries-quickbooks-online/L4tQBwbs7_US_en_US
/// (required: journal number, date, account name, description, debits, credits; sub-accounts as Parent:Sub; Name needed on A/R and A/P lines);
/// invoices https://quickbooks.intuit.com/learn-support/en-us/help-article/import-export-data-files/import-multiple-invoices/L7E9Xrd8l_US_en_US
/// (one row per line repeating the invoice number, customer and dates; no negative amounts, discounts or credit memos; 1,000 rows and 100 invoices per import);
/// bills https://quickbooks.intuit.com/learn-support/en-us/help-articles/importing-your-bills/00/261324
/// (bill number, vendor, bill date, due date, account, line amount; line accounts only; 100 bills per import; no credits);
/// chart of accounts https://quickbooks.intuit.com/learn-support/en-us/getting-started/import-chart-of-accounts/00/263167.
/// Intuit's pages do not print the sample files' header text, and the account type spellings come from a third-party list
/// (Rightworks, tprosupport.rightworks.com article 842): both are unverified against a live import.
/// </summary>
public sealed class QuickBooksOnlineCsvExporter : IAccountingExporter
{
    private const string DateFormat = "MM/dd/yyyy";

    public AccountingFormatDto Format { get; } = new(
        AccountingFormatIds.QuickBooksOnlineCsv, "QuickBooks Online (CSV)", AccountingTarget.QuickBooks, "csv",
        [AccountingExportKind.ChartOfAccounts, AccountingExportKind.JournalEntries, AccountingExportKind.Invoices, AccountingExportKind.Bills],
        "Intuit help: Import data into QuickBooks Online (journal entries, invoices, bills, chart of accounts).",
        "Settings › Import data. Turn account numbers off in QuickBooks before importing journal entries. Invoices cannot be imported if automated sales tax is on. Credits and credit memos are skipped.");

    public ExportFile Export(ExportInput input)
    {
        var warnings = new List<string>(input.Data.Warnings);
        switch (input.Kind)
        {
            case AccountingExportKind.JournalEntries:
            {
                var rows = input.Data.Journals.SelectMany(j => j.Lines.Select(l => (IReadOnlyList<string>)
                    [j.Number, Date(j.Date, DateFormat), Safe(l.Account.Name), l.Debit == 0 ? "" : Amount(l.Debit), l.Credit == 0 ? "" : Amount(l.Credit),
                     Safe(l.Memo ?? j.Description), "", Safe(l.Class?.Name)])).ToList();
                var header = new[] { "Journal No.", "Journal Date", "Account Name", "Debits", "Credits", "Description", "Name", "Class" };
                warnings.Add("Lines posted to Accounts Receivable or Accounts Payable need a customer or vendor in QuickBooks's Name column; Workbench journal lines don't carry one.");
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
                    if (inv.IsCreditMemo || inv.Lines.Any(l => l.Amount < 0))
                    {
                        skipped.Add(inv.Id);
                        warnings.Add($"Invoice {inv.Number} was left out: QuickBooks Online's invoice import takes no credit memos or negative lines. Enter it in QuickBooks.");
                        continue;
                    }

                    exported++;
                    total += inv.Total;
                    rows.AddRange(inv.Lines.Select(l => (IReadOnlyList<string>)
                        [inv.Number, Safe(inv.Customer.Name), Date(inv.Date, DateFormat), Date(inv.DueDate, DateFormat), Terms(inv.TermsDays), Safe(inv.Reference),
                         Safe(l.Item.Name), Safe(l.Description), Number(l.Quantity), Amount(l.Rate), Amount(l.Amount), l.TaxCode.IsMapped ? Safe(l.TaxCode.Name) : "", Safe(l.Class?.Name)]));
                }

                if (rows.Count > 1000 || exported > 100)
                {
                    warnings.Add($"QuickBooks Online imports up to 1,000 rows and 100 invoices at a time; this file has {rows.Count} rows and {exported} invoices. Export a shorter period.");
                }

                var header = new[] { "Invoice No.", "Customer", "Invoice Date", "Due Date", "Terms", "Memo", "Item(Product/Service)", "Item Description", "Item Quantity", "Item Rate", "Item Amount", "Item Tax Code", "Class" };
                return Result(input, header, rows, exported, total, warnings, skipped);
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
                        warnings.Add($"{(bill.IsCredit ? "Vendor credit" : "Bill")} {bill.Number} was left out: " + (bill.IsCredit ? "QuickBooks Online's bill import takes no credits." : "it has no lines with a GL account."));
                        continue;
                    }

                    exported++;
                    total += bill.Amount;
                    rows.AddRange(bill.Lines.Select(l => (IReadOnlyList<string>)
                        [Safe(bill.VendorInvoiceNumber), Safe(bill.Vendor.Name), Date(bill.Date, DateFormat), Date(bill.DueDate, DateFormat), Terms(bill.TermsDays),
                         Safe($"{bill.Number} {bill.Description}".Trim()), Safe(l.Account.Name), Safe(l.Description), Amount(l.Amount), l.TaxCode.IsMapped ? Safe(l.TaxCode.Name) : "", Safe(l.Class?.Name)]));
                }

                if (exported > 100)
                {
                    warnings.Add($"QuickBooks Online recommends at most 100 bills per import; this file has {exported}. Export a shorter period.");
                }

                var header = new[] { "Bill No.", "Vendor", "Bill Date", "Due Date", "Terms", "Memo", "Account", "Line Description", "Line Amount", "Line Tax Code", "Class" };
                return Result(input, header, rows, exported, total, warnings, skipped);
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

                    rows.Add([Safe(a.Destination.Name), QbType(ClassOf(a.Number, a.Type)), "", a.Number, Safe(a.Description)]);
                }

                if (skipped.Count > 0)
                {
                    warnings.Add($"{skipped.Count} group account(s) with sub-accounts were left out; Workbench posts only to the accounts under them.");
                }

                warnings.Add("Detail Type is left blank; QuickBooks picks a default. Check each account's type after the import.");
                var header = new[] { "Account Name", "Type", "Detail Type", "Number", "Description" };
                return Result(input, header, rows, rows.Count, 0, warnings, skipped);
            }

            default:
                throw new NotSupportedException("QuickBooks Online CSV can't carry " + input.Kind + ".");
        }
    }

    // Spellings of the importer's accepted Type values (Rightworks article 842).
    private static string QbType(AccountClass c) => c switch
    {
        AccountClass.Bank => "Bank",
        AccountClass.Receivable => "AccountsReceivable",
        AccountClass.CurrentAsset => "OtherCurrentAsset",
        AccountClass.FixedAsset => "FixedAsset",
        AccountClass.Payable => "AccountsPayable",
        AccountClass.CurrentLiability => "OtherCurrentLiability",
        AccountClass.Equity => "Equity",
        AccountClass.Income => "Income",
        AccountClass.OtherIncome => "OtherIncome",
        AccountClass.DirectCost => "CostofGoodsSold",
        _ => "Expense",
    };

    private ExportFile Result(ExportInput input, string[] header, List<IReadOnlyList<string>> rows, int records, decimal total, List<string> warnings, List<Guid>? skipped) =>
        new(FileName(input, Format.Id, "csv"), "text/csv", Csv(header, rows), rows.Count, records, total, warnings, skipped);
}
