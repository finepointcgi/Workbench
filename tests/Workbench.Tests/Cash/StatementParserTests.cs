using Workbench.Application.Cash;
using Workbench.Application.Cash.Statements;
using Workbench.Contracts.Cash;
using Workbench.Domain.Cash;

namespace Workbench.Tests.Cash;

public sealed class StatementParserTests
{
    /// <summary>The six September items every sample holds, as (date, signed amount, type, check, reference).</summary>
    private static readonly (DateOnly Date, decimal Amount, BankTransactionType Type, string? Check, string Ref)[] Expected =
    [
        (new(2026, 9, 2), 12_500m, BankTransactionType.Ach, null, "B2609020001"), // an EFT credit
        (new(2026, 9, 4), -2_400m, BankTransactionType.Check, "1001", "B2609040007"),
        (new(2026, 9, 10), -5_000m, BankTransactionType.Ach, null, "B2609100003"),
        (new(2026, 9, 15), 8_000m, BankTransactionType.Wire, null, "B2609150011"),
        (new(2026, 9, 30), -25m, BankTransactionType.Fee, null, "B2609300001"),
        (new(2026, 9, 30), 3.12m, BankTransactionType.Interest, null, "B2609300002"),
    ];

    private static readonly CsvColumnMapping SampleMapping =
        new("Posting Date", "Description", Debit: "Debit", Credit: "Credit", CheckNumber: "Check #", Reference: "Bank Ref", DateFormat: "MM/dd/yyyy");

    private static void AssertSample(ParsedStatement s, bool exactTypes = true)
    {
        Assert.Equal(Expected.Length, s.Transactions.Count);
        for (var i = 0; i < Expected.Length; i++)
        {
            var (date, amount, type, check, reference) = Expected[i];
            var t = s.Transactions[i];
            Assert.Equal(date, t.Date);
            Assert.Equal(amount, t.Amount);
            Assert.Equal(check, t.CheckNumber);
            Assert.Equal(reference, t.Reference);
            if (exactTypes || type is BankTransactionType.Check or BankTransactionType.Fee or BankTransactionType.Interest)
            {
                Assert.Equal(type, t.Type);
            }
        }

        Assert.Equal(new DateOnly(2026, 9, 30), s.PeriodEnd);
    }

    [Fact]
    public void Csv_sample_reads_through_a_debit_credit_column_mapping()
    {
        var s = CsvStatementParser.Parse(CashKit.Sample("operating-2026-09.csv"), SampleMapping);

        AssertSample(s);
        Assert.Equal("DFAS COLUMBUS EFT PMT 20260902-ARMY", s.Transactions[0].Description); // quoted field
        Assert.Null(s.ClosingBalance); // CSV exports don't state it
    }

    [Fact]
    public void Csv_mapping_takes_a_signed_amount_column_column_numbers_and_inverted_signs()
    {
        const string text = "10/05/2026;Card purchase;(12.50);;\n10/06/2026;Refund;$4.00;;R1\n";
        var s = CsvStatementParser.Parse(text, new CsvColumnMapping("1", "2", Amount: "3", CheckNumber: "4", Reference: "5", HasHeader: false, Delimiter: ";"));
        Assert.Equal([-12.50m, 4.00m], s.Transactions.Select(t => t.Amount));
        Assert.Equal("R1", s.Transactions[1].Reference);

        // A bank that exports debits as positive numbers.
        var inverted = CsvStatementParser.Parse("Date,Memo,Amt\n2026-10-05,Fee,25.00\n", new CsvColumnMapping("Date", "Memo", Amount: "Amt", InvertSign: true));
        Assert.Equal(-25m, Assert.Single(inverted.Transactions).Amount);
        Assert.Equal(BankTransactionType.Fee, inverted.Transactions[0].Type);
    }

    [Fact]
    public void Csv_mapping_errors_name_the_problem()
    {
        var missing = Assert.Throws<ArgumentException>(() => CsvStatementParser.Parse("Date,Text,Amount\n", new CsvColumnMapping("Date", "Description", Amount: "Amount")));
        Assert.Contains("no \"Description\" column", missing.Message);
        var badDate = Assert.Throws<ArgumentException>(() => CsvStatementParser.Parse("Date,D,A\n31/12/2026,x,1\n", new CsvColumnMapping("Date", "D", Amount: "A", DateFormat: "MM/dd/yyyy")));
        Assert.Contains("Line 2", badDate.Message);
        Assert.Throws<ArgumentException>(() => CsvStatementParser.Parse("", new CsvColumnMapping("Date", "D")));
    }

    [Fact]
    public void Ofx_sample_reads_transactions_balance_period_and_account()
    {
        var s = OfxStatementParser.Parse(CashKit.Sample("operating-2026-09.qfx"));

        AssertSample(s);
        Assert.Equal("123456784321", s.AccountNumber);
        Assert.Equal(63_078.12m, s.ClosingBalance);
        Assert.Equal(new DateOnly(2026, 9, 1), s.PeriodStart);
        Assert.Equal("ACME FEDERAL ACH PMT ACH-000123", s.Transactions[2].Description); // NAME + MEMO
    }

    [Fact]
    public void Ofx_2_xml_with_closing_tags_reads_the_same()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <?OFX OFXHEADER="200" VERSION="220"?>
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD</CURDEF>
            <BANKACCTFROM><BANKID>054001725</BANKID><ACCTID>9994321</ACCTID><ACCTTYPE>CHECKING</ACCTTYPE></BANKACCTFROM>
            <BANKTRANLIST><DTSTART>20261001</DTSTART><DTEND>20261031</DTEND>
            <STMTTRN><TRNTYPE>CHECK</TRNTYPE><DTPOSTED>20261006</DTPOSTED><TRNAMT>-2400.00</TRNAMT><FITID>X1</FITID><CHECKNUM>1001</CHECKNUM><NAME>CHECK</NAME></STMTTRN>
            <STMTTRN><TRNTYPE>INT</TRNTYPE><DTPOSTED>20261031</DTPOSTED><TRNAMT>1.05</TRNAMT><FITID>X2</FITID><NAME>INTEREST</NAME></STMTTRN>
            </BANKTRANLIST><LEDGERBAL><BALAMT>997.05</BALAMT><DTASOF>20261031</DTASOF></LEDGERBAL></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """;
        var s = OfxStatementParser.Parse(xml);
        Assert.Equal(2, s.Transactions.Count);
        Assert.Equal("9994321", s.AccountNumber);
        Assert.Equal(997.05m, s.ClosingBalance);
        Assert.Equal(("1001", BankTransactionType.Check), (s.Transactions[0].CheckNumber, s.Transactions[0].Type));
        Assert.Equal(BankTransactionType.Interest, s.Transactions[1].Type);
    }

    [Fact]
    public void Bai2_sample_reads_summary_balances_detail_records_and_continuations()
    {
        var s = Bai2StatementParser.Parse(CashKit.Sample("operating-2026-09.bai"));

        AssertSample(s);
        Assert.Equal("123456784321", s.AccountNumber);
        Assert.Equal(50_000m, s.OpeningBalance);
        Assert.Equal(63_078.12m, s.ClosingBalance);
        Assert.Equal("MONTHLY SERVICE FEE", s.Transactions[4].Description); // 88 continuation of the text
        Assert.Equal(s.OpeningBalance + s.Transactions.Sum(t => t.Amount), s.ClosingBalance);
    }

    [Fact]
    public void Bai2_with_several_accounts_picks_the_one_ending_in_the_last_four()
    {
        const string file = """
            01,BANK,CO,261101,0600,1,,,2/
            02,CO,BANK,1,261031,,USD,2/
            03,11112222,USD,015,100000,,/
            16,301,100000,Z,R1,,DEPOSIT/
            49,200000,3/
            03,99994321,USD,015,-5000,,/
            16,698,5000,Z,R2,,FEE/
            49,-10000,3/
            98,190000,2,8/
            99,190000,1,10/
            """;
        var s = Bai2StatementParser.Parse(file, "4321");
        var t = Assert.Single(s.Transactions);
        Assert.Equal((-50m, BankTransactionType.Fee, new DateOnly(2026, 10, 31)), (t.Amount, t.Type, t.Date));
        Assert.Equal(-50m, s.ClosingBalance);
        Assert.Throws<ArgumentException>(() => Bai2StatementParser.Parse(file, "0000"));
    }

    [Fact]
    public void The_same_item_has_the_same_fingerprint_in_every_format()
    {
        var csv = CsvStatementParser.Parse(CashKit.Sample("operating-2026-09.csv"), SampleMapping).Transactions.Select(BankStatementService.Fingerprint);
        var ofx = OfxStatementParser.Parse(CashKit.Sample("operating-2026-09.qfx")).Transactions.Select(BankStatementService.Fingerprint);
        var bai = Bai2StatementParser.Parse(CashKit.Sample("operating-2026-09.bai")).Transactions.Select(BankStatementService.Fingerprint);
        Assert.Equal(csv, ofx);
        Assert.Equal(csv, bai);
        Assert.Equal(StatementFormat.Ofx, BankStatementService.Detect(CashKit.Sample("operating-2026-09.qfx"), "x.txt"));
        Assert.Equal(StatementFormat.Bai2, BankStatementService.Detect(CashKit.Sample("operating-2026-09.bai"), null));
        Assert.Equal(StatementFormat.Csv, BankStatementService.Detect("Date,Amount\n", "export.csv"));
    }
}
