using Workbench.Application.BankFiles;
using Workbench.Contracts.BankFiles;
using Workbench.Tests.Cash;

namespace Workbench.Tests.BankFiles;

/// <summary>The NACHA and positive pay writers against fixed inputs (no database).</summary>
public sealed class BankFileFormatTests
{
    private static readonly NachaOrigin Origin = new("054001725", "First Capital Bank", "1123456789", "Workbench Federal LLC", "1123456789", "Workbench Fed", "Vendor Pay", "05400172");

    private static readonly NachaBatch Batch = new(new DateOnly(2026, 10, 20),
    [
        new NachaCredit("021000021", "123456789", false, 1_500m, "ACME", "Acme Federal", NachaWriter.Remittance([("INV-1001", 1_500m)], "PR-000042")),
        new NachaCredit("011000015", "987654321", true, 250.75m, "PRIMA", "Primavera Reseller LLC", null),
    ], DescriptiveDate: new DateOnly(2026, 10, 20));

    private static readonly DateTime Created = new(2026, 10, 19, 14, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void A_ccd_file_matches_the_known_good_sample()
    {
        // samples/bank/nacha-ccd-sample.ach was laid out field by field from the NACHA record formats (see its README entry).
        var file = NachaWriter.Write(Origin, Batch, Created, 'A', PaymentFileService.TraceStart("PR-000042"));

        Assert.Equal(CashKit.Sample("nacha-ccd-sample.ach"), file.Content);
        Assert.Equal(["054001720042001", "054001720042002"], file.TraceNumbers);
    }

    [Fact]
    public void Control_totals_count_entries_and_addenda_hash_the_receiving_banks_and_block_by_ten()
    {
        var file = NachaWriter.Write(Origin, Batch, Created, 'A', 42_001);
        var records = file.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.All(records, r => Assert.Equal(94, r.Length));
        Assert.Equal(0, records.Length % 10);
        Assert.Equal(3, file.EntryAddendaCount); // two entries, one addenda
        Assert.Equal(2_100_002L + 1_100_001L, file.EntryHash); // 02100002 + 01100001
        Assert.Equal(1_750.75m, file.TotalCredit);
        Assert.Equal(0m, file.TotalDebit);
        Assert.Equal(1, file.BlockCount);

        var batchControl = records.Single(r => r[0] == '8');
        Assert.Equal("220", batchControl[1..4]);
        Assert.Equal("000003", batchControl[4..10]);
        Assert.Equal("0003200003", batchControl[10..20]);
        Assert.Equal("000000175075", batchControl[32..44]);
        var fileControl = records.Single(r => r[0] == '9' && r[1] != '9');
        Assert.Equal("000001", fileControl[1..7]); // batches
        Assert.Equal("000001", fileControl[7..13]); // blocks
        Assert.Equal("00000003", fileControl[13..21]);
    }

    [Fact]
    public void A_balanced_file_adds_an_offsetting_debit_and_mixed_service_class()
    {
        var file = NachaWriter.Write(Origin, Batch, Created, 'B', 1, new NachaOffset("054001725", "123456784321", false));
        var records = file.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(file.TotalCredit, file.TotalDebit);
        Assert.Equal(2_100_002L + 1_100_001L + 5_400_172L, file.EntryHash);
        Assert.Equal("200", records.Single(r => r[0] == '5')[1..4]);
        var offset = records.Where(r => r[0] == '6').Last();
        Assert.Equal("27", offset[1..3]);
        Assert.Equal("0000175075", offset[29..39]);
        Assert.Equal(2, file.TraceNumbers.Count); // the offset isn't a vendor payment
    }

    [Fact]
    public void Text_is_upper_case_ascii_and_bad_numbers_are_refused()
    {
        Assert.Equal("CAFE MULLER & SOHNE", NachaWriter.Clean("Café Müller & Söhne"));
        var badRouting = Batch with { Credits = [new NachaCredit("02100002", "1", false, 1m, "X", "X", null)] };
        Assert.Throws<ArgumentException>(() => NachaWriter.Write(Origin, badRouting, Created, 'A', 1));
        var zero = Batch with { Credits = [new NachaCredit("021000021", "1", false, 0m, "X", "X", null)] };
        Assert.Throws<ArgumentException>(() => NachaWriter.Write(Origin, zero, Created, 'A', 1));
        Assert.Throws<ArgumentException>(() => NachaWriter.Write(Origin, Batch, Created, 'a', 1));
    }

    [Fact]
    public void Remittance_addenda_lists_invoices_or_summarizes_when_they_do_not_fit()
    {
        Assert.Equal("RMR*IV*A-1**10.00\\RMR*IV*B-2**5.50\\", NachaWriter.Remittance([("a-1", 10m), ("B-2", 5.5m)], "PR-1"));
        var many = Enumerable.Range(1, 6).Select(i => ($"INVOICE-{i:0000}", 100m)).ToList();
        Assert.Equal("PR-1 PAYS 6 INVOICES; REMITTANCE ADVICE SENT BY EMAIL", NachaWriter.Remittance(many, "PR-1"));
    }

    [Fact]
    public void File_id_modifiers_and_trace_numbers_follow_nacha()
    {
        Assert.Equal('A', PaymentFileService.FileIdModifier(0));
        Assert.Equal('Z', PaymentFileService.FileIdModifier(25));
        Assert.Equal('0', PaymentFileService.FileIdModifier(26));
        Assert.Throws<InvalidOperationException>(() => PaymentFileService.FileIdModifier(36));
        Assert.Equal(42_001, PaymentFileService.TraceStart("PR-000042"));
        Assert.NotEqual(PaymentFileService.TraceStart("PR-000042"), PaymentFileService.TraceStart("PR-000043"));
    }

    private static readonly PositivePayItem[] Checks =
    [
        new("1001", new DateOnly(2026, 10, 20), 1_234.5m, "Acme Federal, Inc.", false),
        new("1002", new DateOnly(2026, 10, 20), 99m, "Bolt Consulting", true),
    ];

    [Fact]
    public void Positive_pay_standard_csv_has_a_header_and_marks_voids()
    {
        var csv = PositivePayWriter.Write(PositivePayWriter.Presets[0], "123456784321", "054001725", Checks);

        Assert.Equal(
            "Account,Check number,Issue date,Amount,Payee,Void\r\n" +
            "123456784321,1001,10/20/2026,1234.50,\"Acme Federal, Inc.\",\r\n" +
            "123456784321,1002,10/20/2026,99.00,Bolt Consulting,V\r\n", csv);
    }

    [Fact]
    public void Positive_pay_fixed_width_pads_every_field_to_its_width()
    {
        var text = PositivePayWriter.Write(PositivePayWriter.Presets[1], "123456784321", null, Checks);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.All(lines, l => Assert.Equal(80, l.Length));
        // Account keeps its rightmost ten digits; check and cents are zero-filled; date MMddyyyy; issue code; payee.
        Assert.Equal("3456784321" + "0000001001" + "0000123450" + "10202026" + "I" + "Acme Federal, Inc.".PadRight(41), lines[0]);
        Assert.Equal('V', lines[1][38]);

        var arp = PositivePayWriter.Write(PositivePayWriter.Presets[2], "123456784321", null, Checks).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("000123456784321" + "0000001001" + "000000123450" + "261020" + "O", arp[0]);
    }

    [Fact]
    public void A_layout_needs_the_check_number_amount_and_fixed_widths()
    {
        Assert.Throws<ArgumentException>(() => PositivePayWriter.Validate([new(PositivePayField.CheckNumber, 10)], PositivePayFileFormat.FixedWidth));
        Assert.Throws<ArgumentException>(() => PositivePayWriter.Validate([new(PositivePayField.CheckNumber), new(PositivePayField.Amount)], PositivePayFileFormat.FixedWidth));
        PositivePayWriter.Validate([new(PositivePayField.CheckNumber), new(PositivePayField.Amount)], PositivePayFileFormat.Csv);
    }
}
