using System.Globalization;
using System.Text;
using Workbench.Contracts.AccountingExport;
using Workbench.Domain.Accounting;

namespace Workbench.Application.AccountingExport.Formats;

/// <summary>Shared writing helpers for the export formats: CSV quoting, number and date text, file names, account type guesses.</summary>
public static class ExportSupport
{
    public static string Amount(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Number(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    public static string Date(DateOnly d, string format) => d.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>
    /// A text cell. A leading =, +, - or @ (or a control character) would be run as a formula by a spreadsheet, so it gets a
    /// leading apostrophe; numbers go through <see cref="Amount"/> and are never neutralized.
    /// </summary>
    public static string Safe(string? value)
    {
        var v = (value ?? "").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
        return v.Length > 0 && "=+-@".Contains(v[0]) ? "'" + v : v;
    }

    public static string CsvCell(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    public static byte[] Csv(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows, bool bom = false)
    {
        var sb = new StringBuilder();
        sb.AppendJoin(',', header.Select(CsvCell)).Append("\r\n");
        foreach (var row in rows)
        {
            sb.AppendJoin(',', row.Select(CsvCell)).Append("\r\n");
        }

        return new UTF8Encoding(bom).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public static string FileName(ExportInput input, string formatId, string extension)
    {
        var slug = input.Kind switch
        {
            AccountingExportKind.ChartOfAccounts => "chart-of-accounts",
            AccountingExportKind.JournalEntries => "journal-entries",
            AccountingExportKind.Invoices => "invoices",
            AccountingExportKind.Payments => "payments",
            _ => "bills",
        };
        return input.Kind == AccountingExportKind.ChartOfAccounts
            ? $"workbench-{slug}-{formatId}.{extension}"
            : $"workbench-{slug}-{input.From:yyyyMMdd}-{input.To:yyyyMMdd}-{formatId}.{extension}";
    }

    public static string Terms(int days) => days <= 0 ? "Due on receipt" : $"Net {days}";

    /// <summary>The package-neutral class of an account, from its number (the default chart's ranges) and Workbench type.</summary>
    public static AccountClass ClassOf(string number, AccountType type) => type switch
    {
        AccountType.Asset when number.StartsWith("10", StringComparison.Ordinal) => AccountClass.Bank,
        AccountType.Asset when number is "1110" or "1120" => AccountClass.Receivable,
        AccountType.Asset when number.StartsWith("15", StringComparison.Ordinal) => AccountClass.FixedAsset,
        AccountType.Asset => AccountClass.CurrentAsset,
        AccountType.Liability when number == "2010" => AccountClass.Payable,
        AccountType.Liability => AccountClass.CurrentLiability,
        AccountType.Equity => AccountClass.Equity,
        AccountType.Revenue when number.StartsWith("49", StringComparison.Ordinal) => AccountClass.OtherIncome,
        AccountType.Revenue => AccountClass.Income,
        _ when number.StartsWith("50", StringComparison.Ordinal) || number.StartsWith("51", StringComparison.Ordinal) => AccountClass.DirectCost,
        _ => AccountClass.Expense,
    };

    public static bool Clamp(string value, int max) => value.Length <= max;
}

public enum AccountClass
{
    Bank,
    Receivable,
    CurrentAsset,
    FixedAsset,
    Payable,
    CurrentLiability,
    Equity,
    Income,
    OtherIncome,
    DirectCost,
    Expense,
}
