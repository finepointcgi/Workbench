using ClosedXML.Excel;
using Workbench.Contracts.AccountingExport;
using static Workbench.Application.AccountingExport.Formats.ExportSupport;

namespace Workbench.Application.AccountingExport.Formats;

/// <summary>
/// Plain tables of the same data for any other accounting package, a spreadsheet or an auditor: every kind, Workbench numbers next to
/// the mapped names, credits and credit memos included, nothing skipped. Dates are ISO (yyyy-MM-dd); money has two decimals.
/// </summary>
internal static class GenericTables
{
    public sealed record Table(string[] Header, List<IReadOnlyList<string>> Rows, int Records, decimal Total);

    public static Table Build(ExportInput input)
    {
        var rows = new List<IReadOnlyList<string>>();
        const string D = "yyyy-MM-dd";
        switch (input.Kind)
        {
            case AccountingExportKind.JournalEntries:
                foreach (var j in input.Data.Journals)
                {
                    rows.AddRange(j.Lines.Select(l => (IReadOnlyList<string>)[j.Number, Date(j.Date, D), j.Source, Safe(j.Description), l.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        l.AccountNumber, Safe(l.Account.Name), Safe(l.Account.Code), l.Debit == 0 ? "" : Amount(l.Debit), l.Credit == 0 ? "" : Amount(l.Credit), Safe(l.Memo), Safe(l.Class?.Name)]));
                }

                return new(["Entry", "Date", "Source", "Description", "Line", "Account number", "Account", "Account code", "Debit", "Credit", "Memo", "Class"], rows,
                    input.Data.Journals.Count, input.Data.Journals.Sum(j => j.Lines.Sum(l => l.Debit)));

            case AccountingExportKind.Invoices:
                foreach (var i in input.Data.Invoices)
                {
                    rows.AddRange(i.Lines.Select(l => (IReadOnlyList<string>)[i.Number, i.IsCreditMemo ? "Credit memo" : "Invoice", Date(i.Date, D), Date(i.DueDate, D), i.TermsDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Safe(i.Customer.Name), Safe(i.Reference), l.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), l.Kind, Safe(l.Description), Safe(l.Item.Name), l.AccountNumber, Safe(l.Account.Name),
                        Number(l.Quantity), Amount(l.Rate), Amount(l.Amount), Safe(l.Class?.Name), Amount(i.Total)]));
                }

                return new(["Invoice", "Type", "Date", "Due date", "Terms (days)", "Customer", "Contract", "Line", "Line type", "Description", "Item", "Account number", "Account", "Quantity", "Rate", "Amount", "Class", "Invoice total"],
                    rows, input.Data.Invoices.Count, input.Data.Invoices.Sum(i => i.Total));

            case AccountingExportKind.Payments:
                foreach (var p in input.Data.Payments)
                {
                    rows.Add([Date(p.Date, D), Safe(p.Customer.Name), Amount(p.Amount), p.Method, Safe(p.Reference), p.DepositAccountNumber, Safe(p.DepositAccount.Name), Safe(p.ReceivableAccount.Name),
                        string.Join("; ", p.Applications.Select(a => $"{a.InvoiceNumber}: {Amount(a.Amount)}")), Safe(p.Notes)]);
                }

                return new(["Date", "Customer", "Amount", "Method", "Reference", "Deposit account number", "Deposit account", "Receivable account", "Applied to", "Notes"],
                    rows, input.Data.Payments.Count, input.Data.Payments.Sum(p => p.Amount));

            case AccountingExportKind.Bills:
                foreach (var b in input.Data.Bills)
                {
                    rows.AddRange(b.Lines.Select(l => (IReadOnlyList<string>)[b.Number, b.IsCredit ? "Vendor credit" : "Bill", Safe(b.Vendor.Name), Safe(b.VendorInvoiceNumber), Date(b.Date, D), Date(b.DueDate, D),
                        l.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), l.AccountNumber, Safe(l.Account.Name), Safe(l.Description), Amount(l.Amount), Safe(l.Class?.Name), Amount(b.Amount)]));
                }

                return new(["Bill", "Type", "Vendor", "Vendor invoice", "Date", "Due date", "Line", "Account number", "Account", "Description", "Amount", "Class", "Bill total"],
                    rows, input.Data.Bills.Count, input.Data.Bills.Sum(b => b.IsCredit ? -b.Amount : b.Amount));

            default:
                foreach (var a in input.Data.Accounts)
                {
                    rows.Add([a.Number, Safe(a.Name), a.Type.ToString(), a.ParentNumber ?? "", Safe(a.Destination.Name), Safe(a.Destination.Code), Safe(a.Description), a.IsActive ? "Yes" : "No"]);
                }

                return new(["Number", "Name", "Type", "Parent number", "Name in destination", "Code in destination", "Description", "Active"], rows, input.Data.Accounts.Count, 0);
        }
    }
}

public sealed class GenericCsvExporter : IAccountingExporter
{
    private static readonly AccountingExportKind[] All = Enum.GetValues<AccountingExportKind>();

    public AccountingFormatDto Format { get; } = new(
        AccountingFormatIds.GenericCsv, "Generic CSV", AccountingTarget.Generic, "csv", All,
        "Workbench's own layout (no vendor template).", "One row per line, Workbench numbers beside the mapped names. Nothing is skipped.");

    public ExportFile Export(ExportInput input)
    {
        var t = GenericTables.Build(input);
        return new ExportFile(FileName(input, Format.Id, "csv"), "text/csv", Csv(t.Header, t.Rows, bom: true), t.Rows.Count, t.Records, t.Total, input.Data.Warnings.ToList());
    }
}

public sealed class GenericXlsxExporter : IAccountingExporter
{
    private static readonly AccountingExportKind[] All = Enum.GetValues<AccountingExportKind>();

    public AccountingFormatDto Format { get; } = new(
        AccountingFormatIds.GenericXlsx, "Generic Excel", AccountingTarget.Generic, "xlsx", All,
        "Workbench's own layout (no vendor template).", "The same table as the generic CSV, as an Excel workbook.");

    public ExportFile Export(ExportInput input)
    {
        var t = GenericTables.Build(input);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Export");
        for (var c = 0; c < t.Header.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = t.Header[c];
            sheet.Cell(1, c + 1).Style.Font.Bold = true;
        }

        for (var r = 0; r < t.Rows.Count; r++)
        {
            for (var c = 0; c < t.Header.Length; c++)
            {
                // Text only: a figure kept as text keeps its exact digits and never turns into a formula.
                sheet.Cell(r + 2, c + 1).SetValue(t.Rows[r][c]);
            }
        }

        sheet.SheetView.FreezeRows(1);
        // A fixed timestamp keeps the bytes (and so the checksum) the same for the same data.
        workbook.Properties.Created = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        workbook.Properties.Modified = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return new ExportFile(FileName(input, Format.Id, "xlsx"), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ms.ToArray(), t.Rows.Count, t.Records, t.Total, input.Data.Warnings.ToList());
    }
}
