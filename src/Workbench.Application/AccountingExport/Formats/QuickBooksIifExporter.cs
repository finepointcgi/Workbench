using System.Text;
using Workbench.Contracts.AccountingExport;
using static Workbench.Application.AccountingExport.Formats.ExportSupport;

namespace Workbench.Application.AccountingExport.Formats;

/// <summary>
/// QuickBooks Desktop IIF (tab-separated, import with File › Utilities › Import › IIF Files): general journal entries, invoices,
/// bills and the account list. Intuit's overview states: ASCII tab-separated; header rows start with "!" (!TRNS, !SPL, !ENDTRNS for
/// transactions); headers and column spellings must be exact; accounts must exist before the import (a missing one is created
/// as a bank account); little error checking.
/// https://quickbooks.intuit.com/learn-support/en-us/help-article/list-management/iif-overview-import-kit-sample-files-headers/L5CZIpJne_US_en_US
/// The per-transaction field lists live in Intuit's IIF Import Kit, which was not available when this was written. From it the
/// code relies on remembered and third-party documented layouts (UNVERIFIED): TRNS amounts positive for an invoice's receivable
/// and negative for its income splits, the amounts of one transaction sum to zero, dates MM/DD/YYYY, every transaction closed by
/// ENDTRNS, !ACCNT columns NAME, ACCNTTYPE, DESC, ACCNUM (rubydoc.info/gems/iif, AccountEdge's IIF field reference).
/// </summary>
public sealed class QuickBooksIifExporter : IAccountingExporter
{
    private const string DateFormat = "MM/dd/yyyy";

    public AccountingFormatDto Format { get; } = new(
        AccountingFormatIds.QuickBooksDesktopIif, "QuickBooks Desktop (IIF)", AccountingTarget.QuickBooks, "iif",
        [AccountingExportKind.ChartOfAccounts, AccountingExportKind.JournalEntries, AccountingExportKind.Invoices, AccountingExportKind.Bills],
        "Intuit help: IIF overview, import kit, sample files and headers (QuickBooks Desktop).",
        "Import with File › Utilities › Import › IIF Files after a company file backup. Accounts and items must already exist (import the account list first). Credits and credit memos are skipped.");

    public ExportFile Export(ExportInput input)
    {
        var warnings = new List<string>(input.Data.Warnings);
        var sb = new StringBuilder();
        var skipped = new List<Guid>();
        int records;
        int rows;
        decimal total;
        switch (input.Kind)
        {
            case AccountingExportKind.ChartOfAccounts:
            {
                sb.Append("!ACCNT\tNAME\tACCNTTYPE\tDESC\tACCNUM\r\n");
                var parents = input.Data.Accounts.Where(a => a.ParentNumber is not null).Select(a => a.ParentNumber!).ToHashSet(StringComparer.Ordinal);
                foreach (var a in input.Data.Accounts)
                {
                    if (parents.Contains(a.Number))
                    {
                        skipped.Add(a.Id);
                        continue;
                    }

                    sb.Append("ACCNT\t").Append(Tab(a.Destination.Name)).Append('\t').Append(IifType(ClassOf(a.Number, a.Type))).Append('\t').Append(Tab(a.Description)).Append('\t').Append(Tab(a.Number)).Append("\r\n");
                    NameLength(warnings, a.Destination.Name, 31, "account");
                }

                records = rows = input.Data.Accounts.Count - skipped.Count;
                total = 0;
                if (skipped.Count > 0)
                {
                    warnings.Add($"{skipped.Count} group account(s) with sub-accounts were left out; Workbench posts only to the accounts under them.");
                }

                break;
            }

            case AccountingExportKind.JournalEntries:
            {
                Header(sb);
                foreach (var j in input.Data.Journals)
                {
                    var first = true;
                    foreach (var l in j.Lines)
                    {
                        sb.Append(first ? "TRNS" : "SPL").Append("\t\tGENERAL JOURNAL\t").Append(Date(j.Date, DateFormat)).Append('\t').Append(Tab(l.Account.Name)).Append("\t\t")
                            .Append(Tab(l.Class?.Name)).Append('\t').Append(Amount(l.Debit - l.Credit)).Append('\t').Append(Tab(j.Number)).Append('\t').Append(Tab(l.Memo ?? j.Description)).Append("\t\t\t\t\r\n");
                        NameLength(warnings, l.Account.Name, 31, "account");
                        first = false;
                    }

                    sb.Append("ENDTRNS\r\n");
                }

                records = input.Data.Journals.Count;
                rows = input.Data.Journals.Sum(j => j.Lines.Count);
                total = input.Data.Journals.Sum(j => j.Lines.Sum(l => l.Debit));
                break;
            }

            case AccountingExportKind.Invoices:
            {
                var docs = new List<InvoiceDoc>();
                foreach (var inv in input.Data.Invoices)
                {
                    if (inv.IsCreditMemo)
                    {
                        skipped.Add(inv.Id);
                        warnings.Add($"Credit memo {inv.Number} was left out of the IIF file. Enter it in QuickBooks.");
                    }
                    else
                    {
                        docs.Add(inv);
                    }
                }

                sb.Append("!CUST\tNAME\r\n");
                foreach (var name in docs.Select(d => d.Customer.Name).Distinct(StringComparer.Ordinal))
                {
                    sb.Append("CUST\t").Append(Tab(name)).Append("\r\n");
                    NameLength(warnings, name, 41, "customer");
                }

                Header(sb);
                foreach (var inv in docs)
                {
                    sb.Append("TRNS\t\tINVOICE\t").Append(Date(inv.Date, DateFormat)).Append('\t').Append(Tab(inv.ReceivableAccount.Name)).Append('\t').Append(Tab(inv.Customer.Name)).Append("\t\t")
                        .Append(Amount(inv.Total)).Append('\t').Append(Tab(inv.Number)).Append('\t').Append(Tab(inv.Reference)).Append('\t').Append(Tab(Terms(inv.TermsDays))).Append('\t').Append(Date(inv.DueDate, DateFormat)).Append("\t\t\r\n");
                    foreach (var l in inv.Lines)
                    {
                        sb.Append("SPL\t\tINVOICE\t").Append(Date(inv.Date, DateFormat)).Append('\t').Append(Tab(l.Account.Name)).Append('\t').Append(Tab(inv.Customer.Name)).Append('\t')
                            .Append(Tab(l.Class?.Name)).Append('\t').Append(Amount(-l.Amount)).Append('\t').Append(Tab(inv.Number)).Append('\t').Append(Tab(l.Description)).Append('\t')
                            .Append(Tab(l.Item.Name)).Append('\t').Append(Number(-l.Quantity)).Append('\t').Append(Amount(l.Rate)).Append("\t\r\n");
                    }

                    sb.Append("ENDTRNS\r\n");
                }

                records = docs.Count;
                rows = docs.Sum(d => d.Lines.Count);
                total = docs.Sum(d => d.Total);
                warnings.Add("Each invoice line is posted to the account the line's Workbench revenue account maps to; QuickBooks may instead use the account set on the item named in the line.");
                break;
            }

            case AccountingExportKind.Bills:
            {
                var docs = new List<BillDoc>();
                foreach (var bill in input.Data.Bills)
                {
                    if (bill.IsCredit || bill.Lines.Count == 0)
                    {
                        skipped.Add(bill.Id);
                        warnings.Add($"{(bill.IsCredit ? "Vendor credit" : "Bill")} {bill.Number} was left out of the IIF file.");
                    }
                    else
                    {
                        docs.Add(bill);
                    }
                }

                sb.Append("!VEND\tNAME\r\n");
                foreach (var name in docs.Select(d => d.Vendor.Name).Distinct(StringComparer.Ordinal))
                {
                    sb.Append("VEND\t").Append(Tab(name)).Append("\r\n");
                    NameLength(warnings, name, 41, "vendor");
                }

                Header(sb);
                foreach (var bill in docs)
                {
                    sb.Append("TRNS\t\tBILL\t").Append(Date(bill.Date, DateFormat)).Append('\t').Append(Tab(bill.PayablesAccount.Name)).Append('\t').Append(Tab(bill.Vendor.Name)).Append("\t\t")
                        .Append(Amount(-bill.Amount)).Append('\t').Append(Tab(bill.VendorInvoiceNumber)).Append('\t').Append(Tab(bill.Description ?? bill.Number)).Append("\t\t").Append(Date(bill.DueDate, DateFormat)).Append("\t\t\r\n");
                    foreach (var l in bill.Lines)
                    {
                        sb.Append("SPL\t\tBILL\t").Append(Date(bill.Date, DateFormat)).Append('\t').Append(Tab(l.Account.Name)).Append('\t').Append(Tab(bill.Vendor.Name)).Append('\t')
                            .Append(Tab(l.Class?.Name)).Append('\t').Append(Amount(l.Amount)).Append('\t').Append(Tab(bill.VendorInvoiceNumber)).Append('\t').Append(Tab(l.Description)).Append("\t\t\t\t\r\n");
                    }

                    sb.Append("ENDTRNS\r\n");
                }

                records = docs.Count;
                rows = docs.Sum(d => d.Lines.Count);
                total = docs.Sum(d => d.Amount);
                break;
            }

            default:
                throw new NotSupportedException("QuickBooks IIF can't carry " + input.Kind + ".");
        }

        return new ExportFile(FileName(input, Format.Id, "iif"), "text/plain", Encoding.ASCII.GetBytes(Ascii(sb.ToString())), rows, records, total, warnings.Distinct().ToList(), skipped);
    }

    private static void Header(StringBuilder sb)
    {
        sb.Append("!TRNS\tTRNSID\tTRNSTYPE\tDATE\tACCNT\tNAME\tCLASS\tAMOUNT\tDOCNUM\tMEMO\tTERMS\tDUEDATE\tQNTY\tPRICE\r\n");
        sb.Append("!SPL\tSPLID\tTRNSTYPE\tDATE\tACCNT\tNAME\tCLASS\tAMOUNT\tDOCNUM\tMEMO\tINVITEM\tQNTY\tPRICE\tTAXABLE\r\n");
        sb.Append("!ENDTRNS\r\n");
    }

    // IIF is tab-separated with no quoting: tabs and line breaks inside a value become spaces.
    private static string Tab(string? value) => (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    // IIF is an ASCII format; anything else becomes '?' rather than corrupting the import.
    private static string Ascii(string text) => string.Create(text.Length, text, (span, t) =>
    {
        for (var i = 0; i < t.Length; i++)
        {
            span[i] = t[i] < 128 ? t[i] : '?';
        }
    });

    private static void NameLength(List<string> warnings, string name, int max, string what)
    {
        if (name.Length > max)
        {
            warnings.Add($"The {what} name \"{name}\" is longer than QuickBooks Desktop's {max} characters.");
        }
    }

    private static string IifType(AccountClass c) => c switch
    {
        AccountClass.Bank => "BANK",
        AccountClass.Receivable => "AR",
        AccountClass.CurrentAsset => "OCASSET",
        AccountClass.FixedAsset => "FIXASSET",
        AccountClass.Payable => "AP",
        AccountClass.CurrentLiability => "OCLIAB",
        AccountClass.Equity => "EQUITY",
        AccountClass.Income => "INC",
        AccountClass.OtherIncome => "EXINC",
        AccountClass.DirectCost => "COGS",
        _ => "EXP",
    };
}
