using Workbench.Application.Receipts;
using Workbench.Contracts.Receipts;

namespace Workbench.Tests.Receipts;

/// <summary>The PDF text layer and the folio heuristics, over the sample folios in Receipts/Samples (see its README).</summary>
public sealed class ReceiptParserTests
{
    internal static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Receipts", "Samples", name));

    private static ReceiptExtractionDto Read(string name) => ReceiptParser.Parse(ReceiptText.ReadPdfLines(Sample(name)));

    [Fact]
    public void A_folio_with_dated_rows_gives_the_stay_room_taxes_other_charges_and_total()
    {
        var folio = Read("hilton-arlington-folio.pdf");

        Assert.Equal(ReceiptDocumentKind.LodgingFolio, folio.Kind);
        Assert.Equal(ReceiptReadMethod.TextLayer, folio.Method);
        Assert.Equal("Hilton Arlington National Landing", folio.Vendor);
        Assert.Equal(("Arlington", "VA"), (folio.City, folio.State));
        Assert.Equal((new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 15), 3), (folio.CheckIn, folio.CheckOut, folio.Nights));
        Assert.Equal(239.00m, folio.NightlyRate);
        Assert.Equal(717.00m, folio.RoomTotal);
        Assert.Equal(3 * (24.50m + 14.34m), folio.LodgingTaxes);
        Assert.Equal(3 * 45.00m + 38.50m, folio.OtherCharges);
        Assert.Equal(1007.02m, folio.Total);
        Assert.Equal(717.00m + 116.52m, folio.LodgingAmount);
        Assert.Equal(new DateOnly(2026, 10, 12), folio.Date);
        Assert.Empty(folio.Notes); // the charges add up to the total

        // The Visa payment is a credit, not a charge; parking and room service can become their own lines.
        Assert.DoesNotContain(folio.Charges, c => c.Label.Contains("Visa", StringComparison.Ordinal));
        Assert.Equal([(ReceiptChargeKind.Parking, 135.00m), (ReceiptChargeKind.Food, 38.50m)],
            folio.OtherChargeGroups.Select(g => (g.Kind, g.Amount)));
        Assert.All(new[] { ReceiptFields.Vendor, ReceiptFields.Location, ReceiptFields.CheckIn, ReceiptFields.CheckOut, ReceiptFields.Nights,
            ReceiptFields.NightlyRate, ReceiptFields.LodgingTaxes, ReceiptFields.Total }, f => Assert.True(folio.Found(f), f));
    }

    [Fact]
    public void A_folio_with_long_dates_dollar_signs_and_a_bracketed_payment()
    {
        var folio = Read("marriott-san-diego-folio.pdf");

        Assert.Equal(ReceiptDocumentKind.LodgingFolio, folio.Kind);
        Assert.Equal("San Diego Marriott Marquis & Marina", folio.Vendor);
        Assert.Equal(("San Diego", "CA"), (folio.City, folio.State));
        Assert.Equal((new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 21), 2), (folio.CheckIn, folio.CheckOut, folio.Nights));
        Assert.Equal(289.00m, folio.NightlyRate);
        Assert.Equal(578.00m, folio.RoomTotal);
        Assert.Equal(2 * (30.35m + 5.78m), folio.LodgingTaxes); // occupancy tax and the tourism district assessment
        Assert.Equal(110.00m, folio.OtherCharges);
        Assert.Equal(760.26m, folio.Total); // "Total", not the $0.00 balance
    }

    [Fact]
    public void A_folio_whose_rate_changes_keeps_each_nights_rate()
    {
        var folio = Read("hampton-huntsville-folio.pdf");

        Assert.Equal("Hampton Inn & Suites Huntsville", folio.Vendor);
        Assert.Equal(("Huntsville", "AL"), (folio.City, folio.State));
        Assert.Equal((new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 14), 2), (folio.CheckIn, folio.CheckOut, folio.Nights));
        Assert.Equal(248.00m, folio.RoomTotal);
        Assert.Equal(124.00m, folio.NightlyRate); // the average; each night is checked on its own
        Assert.Equal(27.28m, folio.LodgingTaxes);
        Assert.Equal(6.75m, folio.OtherCharges);
        Assert.Equal(282.03m, folio.Total);
        Assert.Contains(folio.Notes, n => n.Contains("rate changes", StringComparison.Ordinal));
        Assert.Equal([(new DateOnly(2026, 10, 12), 119.00m), (new DateOnly(2026, 10, 13), 129.00m)], LodgingRateChecker.NightsOf(folio));
    }

    [Fact]
    public void Any_other_receipt_gives_vendor_date_and_total()
    {
        var taxi = Read("yellow-cab-receipt.pdf");

        Assert.Equal(ReceiptDocumentKind.Receipt, taxi.Kind);
        Assert.Equal("Yellow Cab of DC", taxi.Vendor);
        Assert.Equal(new DateOnly(2026, 10, 14), taxi.Date);
        Assert.Equal(42.80m, taxi.Total);
        Assert.Null(taxi.Nights);
        Assert.Null(taxi.LodgingTaxes);
    }

    [Fact]
    public void A_scan_without_a_text_layer_gives_nothing_to_suggest()
    {
        Assert.Empty(ReceiptText.ReadPdfLines(Sample("scanned-folio.pdf")));
        Assert.Empty(ReceiptText.ReadPdfLines([1, 2, 3])); // not a PDF at all

        var none = ReceiptParser.Parse([]);
        Assert.Equal(ReceiptReadMethod.None, none.Method);
        Assert.Empty(none.FromReceipt);
        Assert.NotEmpty(none.Notes);
    }

    [Theory]
    [InlineData("10/12/2026", 2026, 10, 12)]
    [InlineData("10/12/26", 2026, 10, 12)]
    [InlineData("2026-10-12", 2026, 10, 12)]
    [InlineData("12-Oct-26", 2026, 10, 12)]
    [InlineData("Oct 12, 2026", 2026, 10, 12)]
    [InlineData("October 12 2026", 2026, 10, 12)]
    [InlineData("12 Oct 2026", 2026, 10, 12)]
    public void Common_folio_date_formats_are_read(string text, int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), ReceiptParser.ParseDate(text));

    [Fact]
    public void Amounts_need_cents_so_room_numbers_and_zip_codes_are_not_money()
    {
        Assert.Equal([1234.56m, -10.00m, -20.00m, 45.00m],
            ReceiptParser.Amounts("Total 1,234.56  (10.00)  -20.00  $45.00  Room 1412  VA 22202  6%").Select(a => a.Value));
    }

    [Fact]
    public void Text_folio_layouts_without_per_row_dates_still_give_the_stay()
    {
        var folio = ReceiptParser.Parse(
        [
            "THE WESTIN WASHINGTON",
            "1400 M Street NW, Washington, DC 20005",
            "Check-in date: 2026-10-05   Check-out date: 2026-10-07",
            "Room rate: 250.00",
            "Room charges   500.00",
            "Hotel occupancy tax   74.50",
            "Valet parking   120.00",
            "Grand Total   694.50",
            "Balance due   0.00",
        ]);

        Assert.Equal(("The Westin Washington", "Washington", "DC"), (folio.Vendor, folio.City, folio.State));
        Assert.Equal((2, 250.00m, 500.00m, 74.50m, 120.00m, 694.50m),
            (folio.Nights, folio.NightlyRate, folio.RoomTotal, folio.LodgingTaxes, folio.OtherCharges, folio.Total));
        Assert.Equal([(new DateOnly(2026, 10, 5), 250m), (new DateOnly(2026, 10, 6), 250m)], LodgingRateChecker.NightsOf(folio));
    }

    [Fact]
    public void Charges_that_do_not_add_up_to_the_total_are_pointed_out()
    {
        var folio = ReceiptParser.Parse(["Comfort Inn", "Arrival: 10/01/2026  Departure: 10/02/2026", "10/01/2026 Room 100.00", "Total 150.00"]);

        Assert.Equal(150.00m, folio.Total);
        Assert.Contains(folio.Notes, n => n.Contains("add up to", StringComparison.Ordinal));
    }
}
