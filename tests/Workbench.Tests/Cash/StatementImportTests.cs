using Workbench.Contracts.Cash;

namespace Workbench.Tests.Cash;

public sealed class StatementImportTests
{
    private static readonly CsvColumnMapping SampleMapping =
        new("Posting Date", "Description", Debit: "Debit", Credit: "Credit", CheckNumber: "Check #", Reference: "Bank Ref", DateFormat: "MM/dd/yyyy");

    [Fact]
    public async Task Overlapping_statements_skip_items_already_imported_in_any_format()
    {
        await using var kit = await CashKit.CreateAsync();
        var csv = await kit.Statements.ImportAsync(new ImportStatementRequest(kit.Bank.Id, "operating-2026-09.csv", CashKit.Sample("operating-2026-09.csv"), Mapping: SampleMapping));
        Assert.Equal((6, 0, StatementFileFormat.Csv), (csv.Import.TransactionCount, csv.Import.DuplicateCount, csv.Import.Format));

        // The same month again as OFX and BAI2 (format detected from the content): nothing new.
        var ofx = await kit.Statements.ImportAsync(new ImportStatementRequest(kit.Bank.Id, "operating-2026-09.qfx", CashKit.Sample("operating-2026-09.qfx")));
        Assert.Equal((0, 6, StatementFileFormat.Ofx, 63_078.12m), (ofx.Import.TransactionCount, ofx.Import.DuplicateCount, ofx.Import.Format, ofx.Import.ClosingBalance));
        Assert.Contains(ofx.Warnings, w => w.Contains("already imported"));
        var bai = await kit.Statements.ImportAsync(new ImportStatementRequest(kit.Bank.Id, "operating-2026-09.bai", CashKit.Sample("operating-2026-09.bai")));
        Assert.Equal((0, 6, StatementFileFormat.Bai2), (bai.Import.TransactionCount, bai.Import.DuplicateCount, bai.Import.Format));
        Assert.Equal(6, (await kit.ItemsAsync()).Count);

        // The mapping was saved on the account, so the next CSV needs none.
        Assert.Equal(SampleMapping, (await kit.BankAccounts.GetAsync(kit.Bank.Id)).CsvMapping);
        var overlap = """
            Posting Date,Description,Debit,Credit,Check #,Bank Ref
            09/30/2026,MONTHLY SERVICE FEE,25.00,,,B2609300001
            09/30/2026,INTEREST PAYMENT,,3.12,,B2609300002
            10/01/2026,CHECK 1002,700.00,,1002,B2610010004
            """;
        var next = await kit.Statements.ImportAsync(new ImportStatementRequest(kit.Bank.Id, "operating-2026-10.csv", overlap));
        Assert.Equal((1, 2), (next.Import.TransactionCount, next.Import.DuplicateCount));
        Assert.Equal(7, (await kit.ItemsAsync()).Count);
        Assert.Equal(4, (await kit.Statements.ListImportsAsync(kit.Bank.Id)).Count);
    }

    [Fact]
    public async Task Identical_items_in_one_statement_are_both_kept_and_only_the_repeat_statement_is_skipped()
    {
        await using var kit = await CashKit.CreateAsync();
        var first = await kit.ImportAsync(("2026-10-05", "WIRE FEE", -15m, null, null), ("2026-10-05", "WIRE FEE", -15m, null, null));
        Assert.Equal((2, 0), (first.Import.TransactionCount, first.Import.DuplicateCount));

        // The next statement repeats both and adds a third identical fee that day.
        var second = await kit.ImportAsync(("2026-10-05", "WIRE FEE", -15m, null, null), ("2026-10-05", "WIRE FEE", -15m, null, null), ("2026-10-05", "WIRE FEE", -15m, null, null));
        Assert.Equal((1, 2), (second.Import.TransactionCount, second.Import.DuplicateCount));
        Assert.Equal(3, (await kit.ItemsAsync()).Count);
    }

    [Fact]
    public async Task A_statement_for_another_account_or_without_a_mapping_is_refused()
    {
        await using var kit = await CashKit.CreateAsync();
        var other = await kit.BankAccounts.CreateAsync(new SaveBankAccountRequest("Payroll", "First Capital Bank", "9999", null, kit.Account("1300")));
        var wrong = await Assert.ThrowsAsync<ArgumentException>(() =>
            kit.Statements.ImportAsync(new ImportStatementRequest(other.Id, "operating.qfx", CashKit.Sample("operating-2026-09.qfx"))));
        Assert.Contains("ending 4321", wrong.Message);
        var unmapped = await Assert.ThrowsAsync<ArgumentException>(() =>
            kit.Statements.ImportAsync(new ImportStatementRequest(other.Id, "x.csv", CashKit.Sample("operating-2026-09.csv"))));
        Assert.Contains("Map the CSV columns", unmapped.Message);
        Assert.Empty(await kit.Statements.ListItemsAsync(other.Id));
    }
}
