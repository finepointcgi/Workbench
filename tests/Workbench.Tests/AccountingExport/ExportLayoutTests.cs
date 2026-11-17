using System.Runtime.CompilerServices;
using System.Text;
using ClosedXML.Excel;
using Workbench.Application.AccountingExport;
using Workbench.Application.AccountingExport.Formats;
using Workbench.Contracts.AccountingExport;

namespace Workbench.Tests.AccountingExport;

/// <summary>
/// The column layouts of every format, held in golden files (<c>Golden/*.txt</c>). A layout change must change its golden file in the
/// same commit; regenerate with <c>UPDATE_GOLDEN=1 dotnet test --filter ExportLayoutTests</c> and read the diff. The layouts are what
/// the vendors' documentation (and, where it was unreadable, third-party write-ups) say; see the exporters' comments.
/// </summary>
public sealed class ExportLayoutTests
{
    private static IAccountingExporter Exporter(string id) => id switch
    {
        AccountingFormatIds.QuickBooksOnlineCsv => new QuickBooksOnlineCsvExporter(),
        AccountingFormatIds.QuickBooksDesktopIif => new QuickBooksIifExporter(),
        AccountingFormatIds.XeroCsv => new XeroCsvExporter(),
        _ => new GenericCsvExporter(),
    };

    public static TheoryData<string, AccountingExportKind> Cases()
    {
        var data = new TheoryData<string, AccountingExportKind>();
        foreach (var id in new[] { AccountingFormatIds.QuickBooksOnlineCsv, AccountingFormatIds.QuickBooksDesktopIif, AccountingFormatIds.XeroCsv, AccountingFormatIds.GenericCsv })
        {
            foreach (var kind in Exporter(id).Format.Kinds)
            {
                data.Add(id, kind);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Format_matches_its_golden_file(string formatId, AccountingExportKind kind)
    {
        var file = Exporter(formatId).Export(ExportFixtures.Input(kind));
        var text = Encoding.UTF8.GetString(file.Content).TrimStart('﻿').Replace("\r\n", "\n");
        var name = $"{formatId}.{kind}.txt";
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(SourcePath(name), text);
        }

        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AccountingExport", "Golden", name)).Replace("\r\n", "\n");
        Assert.Equal(golden, text);
    }

    [Fact]
    public void Credit_memos_and_vendor_credits_are_skipped_by_package_formats_and_kept_by_generic()
    {
        foreach (var id in new[] { AccountingFormatIds.QuickBooksOnlineCsv, AccountingFormatIds.QuickBooksDesktopIif, AccountingFormatIds.XeroCsv })
        {
            var invoices = Exporter(id).Export(ExportFixtures.Input(AccountingExportKind.Invoices));
            Assert.Equal(1, invoices.RecordCount);
            Assert.Single(invoices.Skipped);
            Assert.Contains(invoices.Warnings, w => w.Contains("CM-1002"));
            var bills = Exporter(id).Export(ExportFixtures.Input(AccountingExportKind.Bills));
            Assert.Equal(1, bills.RecordCount);
            Assert.Single(bills.Skipped);
        }

        var generic = Exporter(AccountingFormatIds.GenericCsv).Export(ExportFixtures.Input(AccountingExportKind.Invoices));
        Assert.Equal(2, generic.RecordCount);
        Assert.Empty(generic.Skipped);
    }

    [Fact]
    public void Text_that_looks_like_a_formula_is_neutralized_but_amounts_keep_their_minus_sign()
    {
        var journal = new ExportDataset
        {
            Kind = AccountingExportKind.JournalEntries,
            Journals = [new(Guid.NewGuid(), "JE-1", new DateOnly(2026, 1, 1), "=HYPERLINK(\"http://x\")", "Manual",
                [new(1, "1010", new Resolved("Cash", null, true), 5m, 0, "+cmd", null), new(2, "4020", new Resolved("Sales", null, true), 0, 5m, "@sum", null)])],
        };
        var text = Encoding.UTF8.GetString(new GenericCsvExporter().Export(new ExportInput(AccountingExportKind.JournalEntries, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), journal, DateTime.UtcNow)).Content);
        Assert.Contains("'=HYPERLINK", text, StringComparison.Ordinal);
        Assert.Contains("'+cmd", text, StringComparison.Ordinal);
        Assert.Contains("'@sum", text, StringComparison.Ordinal);
        var inv = new GenericCsvExporter().Export(ExportFixtures.Input(AccountingExportKind.Invoices));
        Assert.Contains(",-250.00,", Encoding.UTF8.GetString(inv.Content), StringComparison.Ordinal);
    }

    [Fact]
    public void Journal_files_balance_and_the_control_total_is_the_total_of_debits()
    {
        var journals = ExportFixtures.Dataset(AccountingExportKind.JournalEntries).Journals;
        Assert.All(journals, j => Assert.Equal(j.Lines.Sum(l => l.Debit), j.Lines.Sum(l => l.Credit)));
        foreach (var id in new[] { AccountingFormatIds.QuickBooksOnlineCsv, AccountingFormatIds.QuickBooksDesktopIif, AccountingFormatIds.XeroCsv, AccountingFormatIds.GenericCsv })
        {
            Assert.Equal(1750.50m, Exporter(id).Export(ExportFixtures.Input(AccountingExportKind.JournalEntries)).ControlTotal);
        }
    }

    [Fact]
    public void Iif_transactions_net_to_zero_and_end_with_ENDTRNS()
    {
        foreach (var kind in new[] { AccountingExportKind.JournalEntries, AccountingExportKind.Invoices, AccountingExportKind.Bills })
        {
            var text = Encoding.ASCII.GetString(new QuickBooksIifExporter().Export(ExportFixtures.Input(kind)).Content);
            var sum = 0m;
            var open = false;
            foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split('\t');
                if (f[0] is "TRNS" or "SPL")
                {
                    open = true;
                    sum += decimal.Parse(f[7], System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (f[0] == "ENDTRNS")
                {
                    Assert.True(open);
                    Assert.Equal(0m, sum);
                    sum = 0;
                    open = false;
                }
            }

            Assert.False(open);
        }
    }

    [Fact]
    public void Xlsx_holds_the_generic_table()
    {
        var file = new GenericXlsxExporter().Export(ExportFixtures.Input(AccountingExportKind.Bills));
        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        var sheet = wb.Worksheet("Export");
        Assert.Equal("Bill", sheet.Cell(1, 1).GetString());
        Assert.Equal("AP-0001", sheet.Cell(2, 1).GetString());
        Assert.Equal(file.RowCount + 1, sheet.LastRowUsed()!.RowNumber());
        Assert.EndsWith(".xlsx", file.FileName, StringComparison.Ordinal);
    }

    private static string SourcePath(string name, [CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Golden", name);
}
