using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Workbench.Application.Receipts;
using Workbench.Contracts.Currencies;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Receipts;
using Workbench.Tests.Accessibility;
using Workbench.Tests.Navigation;
using Workbench.Ui.Odcs;
using Workbench.Ui.Receipts;
using static Workbench.Tests.Receipts.ReceiptParserTests;

namespace Workbench.Tests.Receipts;

/// <summary>The expense line dialog's receipt suggestions and the My receipts page, over stub services.</summary>
public sealed class ReceiptComponentTests
{
    private static readonly Guid Employee = Guid.CreateVersion7();
    private static readonly Guid Wbs = Guid.CreateVersion7();
    private static readonly Guid FolioId = Guid.CreateVersion7();

    private static ReceiptDto Folio(params ReceiptUseDto[] uses) => new(FolioId, "hilton-arlington-folio.pdf", "application/pdf", 1937, new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc),
        "analyst@example.com", ReceiptDocumentKind.LodgingFolio, "Hilton Arlington National Landing", "Arlington, VA", new DateOnly(2026, 10, 12), 1007.02m, ReceiptReadMethod.TextLayer, uses);

    private static BunitContext Context(IReceiptLibraryService library)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(library);
        ctx.Services.AddSingleton(Stub<ICurrencyService>.Create(("ListCurrenciesAsync", _ => Task.FromResult<IReadOnlyList<CurrencyDto>>([]))));
        ctx.Services.AddSingleton(Stub<IProjectService>.Create(("ListChargeCodesAsync", _ => Task.FromResult<IReadOnlyList<ChargeCodeDto>>(
            [new ChargeCodeDto(Wbs, "1001.01.01", "Design", Guid.CreateVersion7(), "1001", "Modernization", ChargeCodeKind.Direct, Guid.CreateVersion7())]))));
        return ctx;
    }

    private static IReceiptLibraryService Library(ReceiptReadingDto reading, List<string>? calls = null) => Stub<IReceiptLibraryService>.Create(
        ("ListMineAsync", _ => Task.FromResult<IReadOnlyList<ReceiptDto>>([reading.Receipt])),
        ("ReadAsync", args =>
        {
            calls?.Add($"read {args![0]} for {args[1]}");
            return Task.FromResult(reading);
        }),
        ("CheckLodgingAsync", _ => Task.FromResult(reading.LodgingCheck!)),
        ("GetContentUrl", args => $"api/v1/receipts/{args![0]}/content"));

    private static ReceiptReadingDto HiltonReading(bool over = false)
    {
        var extraction = ReceiptParser.Parse(ReceiptText.ReadPdfLines(Sample("hilton-arlington-folio.pdf")));
        var max = over ? 200m : 258m;
        var check = new LodgingRateCheckDto("District of Columbia, DC", "District of Columbia, DC", false,
            [.. LodgingRateChecker.NightsOf(extraction).Select(n => new LodgingNightCheckDto(n.Date, n.Rate, max))], false,
            over ? ["3 of 3 nights are over the GSA lodging maximum, by $117.00 in all. Explain why on the line."] : []);
        return new ReceiptReadingDto(Folio(), extraction, check);
    }

    private static AngleSharp.Dom.IElement Input<T>(IRenderedComponent<T> rendered, string id) where T : Microsoft.AspNetCore.Components.IComponent =>
        rendered.Find($"input[data-testid={id}], [data-testid={id}] input");

    private static string? Value<T>(IRenderedComponent<T> rendered, string id) where T : Microsoft.AspNetCore.Components.IComponent =>
        Input(rendered, id).GetAttribute("value");

    [Fact]
    public async Task Choosing_a_folio_fills_a_new_line_with_marked_suggestions_and_offers_its_parking_as_another_line()
    {
        var calls = new List<string>();
        var reportId = Guid.CreateVersion7();
        await using var ctx = Context(Library(HiltonReading(), calls));
        var provider = ctx.Render<MudDialogProvider>();
        var dialogs = ctx.Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<ExpenseLineDialog>
        {
            { d => d.EmployeeId, Employee },
            { d => d.ReportId, reportId },
            { d => d.DefaultDate, new DateOnly(2026, 10, 12) },
        };
        var reference = await provider.InvokeAsync(() => dialogs.ShowAsync<ExpenseLineDialog>("Add expense line", parameters));

        provider.Find("[data-testid=receipt-choose]").Click();
        provider.WaitForAssertion(() => Assert.Contains("hilton-arlington-folio.pdf", provider.Find("[data-testid=receipt-picker-list]").TextContent));
        provider.Find("[data-testid=receipt-picker-list] .mud-list-item").Click();

        provider.WaitForAssertion(() =>
        {
            Assert.Equal("833.52", Value(provider, "line-amount"));
            Assert.Equal("Lodging, Arlington, VA, Oct 12–15 (3 nights)", Value(provider, "line-description"));
            Assert.Equal("Hilton Arlington National Landing", Value(provider, "line-vendor"));
        });
        Assert.Equal([$"read {FolioId} for {reportId}"], calls);
        Assert.Contains("From the receipt", Input(provider, "line-amount").Closest(".mud-input-control")!.TextContent);
        Assert.Contains("Within the GSA lodging maximum of $258.00 a night", provider.Find("[data-testid=lodging-check]").TextContent);
        Assert.Contains("hilton-arlington-folio.pdf", provider.Find("[data-testid=receipt-chosen]").TextContent);
        Assert.Contains("Folio total $1,007.02", provider.Find("[data-testid=stay-folio-total]").TextContent);
        Assert.Contains("(meals are usually covered by your M&IE per diem)", provider.Find("[data-testid=lodging-stay]").TextContent);
        A11yAssert.Check(provider);

        // Nothing is saved until the person saves; parking from the folio becomes a second line on the same receipt.
        Input(provider, "other-Parking").Change(true);
        provider.WaitForAssertion(() => Assert.Contains("and 1 more", provider.Find("[data-testid=line-save]").TextContent));
        provider.Find("[data-testid=line-save]").Click();
        var result = Assert.IsType<ExpenseLineDialogResult>((await reference.Result)!.Data);
        Assert.Equal((Wbs, OdcCostType.Travel, new DateOnly(2026, 10, 12), 833.52m, FolioId), (result.Line.WbsElementId, result.Line.Type, result.Line.IncurredOn, result.Line.Amount, result.Line.ReceiptFileId));
        var parking = Assert.Single(result.ExtraLines);
        Assert.Equal((135.00m, "Parking, Arlington stay", null, FolioId), (parking.Amount, parking.Description, parking.Vendor, parking.ReceiptFileId));
    }

    [Fact]
    public async Task An_existing_line_asks_before_taking_the_receipts_values_and_shows_a_rate_over_the_maximum()
    {
        await using var ctx = Context(Library(HiltonReading(over: true)));
        var provider = ctx.Render<MudDialogProvider>();
        var existing = new OdcEntryDto(Guid.CreateVersion7(), Guid.CreateVersion7(), "1001", Wbs, "1001.01.01", "Design", OdcCostType.Travel, new DateOnly(2026, 10, 12),
            700m, "Hotel", null, null, Employee, Guid.CreateVersion7(), false, null, null, null, OdcApprovalState.Draft, "analyst@example.com", DateTime.UtcNow, null, null, null, null);
        var parameters = new DialogParameters<ExpenseLineDialog> { { d => d.EmployeeId, Employee }, { d => d.Existing, existing } };
        await provider.InvokeAsync(() => ctx.Services.GetRequiredService<IDialogService>().ShowAsync<ExpenseLineDialog>("Edit expense line", parameters));

        provider.Find("[data-testid=receipt-choose]").Click();
        provider.WaitForAssertion(() => provider.Find("[data-testid=receipt-picker-list] .mud-list-item").Click());
        provider.WaitForAssertion(() => Assert.Contains("lodging $833.52", provider.Find("[data-testid=line-receipt]").TextContent));
        Assert.Equal("700.00", Value(provider, "line-amount")); // unchanged until asked

        provider.Find("[data-testid=receipt-apply]").Click();
        provider.WaitForAssertion(() => Assert.Equal("833.52", Value(provider, "line-amount")));
        Assert.Contains("over the GSA lodging maximum", provider.Find("[data-testid=lodging-check]").TextContent);
    }

    [Fact]
    public async Task My_receipts_lists_files_with_where_they_are_used_and_blocks_deleting_one_in_use()
    {
        var inUse = Folio(new ReceiptUseDto(Guid.CreateVersion7(), Guid.CreateVersion7(), "DC program review", new DateOnly(2026, 10, 12), "Lodging", 833.52m, OdcApprovalState.Draft));
        var loose = new ReceiptDto(Guid.CreateVersion7(), "yellow-cab-receipt.pdf", "application/pdf", 1321, new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc),
            "analyst@example.com", ReceiptDocumentKind.Receipt, "Yellow Cab of DC", null, new DateOnly(2026, 10, 14), 42.80m, ReceiptReadMethod.TextLayer, []);
        var queries = new List<ReceiptQuery>();
        await using var ctx = Context(Stub<IReceiptLibraryService>.Create(
            ("ListMineAsync", args =>
            {
                queries.Add((ReceiptQuery)args![0]!);
                return Task.FromResult<IReadOnlyList<ReceiptDto>>([loose, inUse]);
            }),
            ("GetContentUrl", args => $"api/v1/receipts/{args![0]}/content")));
        ctx.Render<MudPopoverProvider>();

        var page = ctx.Render<MyReceiptsPage>();

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-testid=receipts-table] tbody tr").Count));
        var rows = page.FindAll("[data-testid=receipts-table] tbody tr");
        Assert.Contains("Not attached", rows[0].TextContent);
        Assert.Contains("DC program review", rows[1].TextContent);
        Assert.Contains("$1,007.02", rows[1].TextContent);
        Assert.False(rows[0].QuerySelector("button[aria-label='Delete yellow-cab-receipt.pdf']")!.HasAttribute("disabled"));
        Assert.True(rows[1].QuerySelector("button[aria-label='Delete hilton-arlington-folio.pdf']")!.HasAttribute("disabled"));
        Assert.Equal("api/v1/receipts/" + FolioId + "/content", rows[1].QuerySelector("a[download]")!.GetAttribute("href"));
        // MudTable can't render a <caption>; its aria-label (on the wrapper) names the scroll region instead. Everything else must pass.
        Assert.Empty(A11yAssert.Find(page.Nodes).Where(p => !p.StartsWith("Data table without a caption", StringComparison.Ordinal)));

        Input(page, "receipts-search").Input("hilton");
        page.WaitForAssertion(() => Assert.Contains(queries, q => q.Search == "hilton"));
    }
}
