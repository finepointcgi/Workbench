using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Assistant;
using Workbench.Application.Collaboration;
using Workbench.Application.Receipts;
using Workbench.Contracts;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.PerDiem;
using Workbench.Contracts.Receipts;
using Workbench.Contracts.Travel;
using Workbench.Domain.Assistant;
using Workbench.Domain.Files;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Collaboration;
using Workbench.Tests.Resourcing;
using static Workbench.Tests.Receipts.ReceiptParserTests;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Receipts;

/// <summary>
/// "My receipts" through the real registrations (permission decorators included), with the sample FY2027 per diem rates:
/// reading folios into suggestions, one stored file behind several lines, the in-use delete block, and who sees what.
/// analyst@ reports to pm@; dev@ is a peer; pm2@ manages another project.
/// </summary>
public sealed class ReceiptLibraryTests
{
    private static async Task<ResourcingHost> HostAsync()
    {
        var host = await CreateAsync(services => services.Configure<AssistantOptions>(o => o.EnableFakeProvider = true));
        await host.As(Finance).Get<IPerDiemRateService>().InstallSampleRatesAsync(2027);
        return host;
    }

    private static Task<ReceiptReadingDto> UploadAsync(ResourcingHost host, string sample, Guid? reportId = null) =>
        host.Get<IReceiptLibraryService>().UploadAsync(sample, new MemoryStream(Sample(sample)), reportId);

    private static async Task<ExpenseReportDto> ReportAsync(ResourcingHost host, string title = "DC program review") =>
        await host.Get<IExpenseReportService>().CreateReportAsync(new SaveExpenseReportRequest(title, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 15)));

    private static SaveExpenseLineRequest Line(ResourcingHost host, decimal amount, string description, Guid? receipt, DateOnly? on = null) =>
        new(host.Design, OdcCostType.Travel, on ?? new DateOnly(2026, 10, 12), amount, description, "Hilton Arlington", ReceiptFileId: receipt);

    [Fact]
    public async Task Uploading_a_folio_reads_it_and_checks_the_room_rate_against_the_gsa_lodging_maximum()
    {
        await using var host = await HostAsync();
        host.As(Analyst);

        // Arlington, VA is under the Washington DC rate ($258 in October): within the maximum.
        var hilton = await UploadAsync(host, "hilton-arlington-folio.pdf");
        Assert.Equal((ReceiptDocumentKind.LodgingFolio, 3, 239m, 1007.02m), (hilton.Extraction.Kind, hilton.Extraction.Nights, hilton.Extraction.NightlyRate, hilton.Extraction.Total));
        Assert.Equal((ReceiptDocumentKind.LodgingFolio, "Hilton Arlington National Landing", 1007.02m), (hilton.Receipt.Kind, hilton.Receipt.Vendor, hilton.Receipt.Total));
        var check = Assert.IsType<LodgingRateCheckDto>(hilton.LodgingCheck);
        Assert.Equal([258m, 258m, 258m], check.Nights.Select(n => n.LodgingMax));
        Assert.False(check.IsOver);
        Assert.Contains("District of Columbia", check.Location, StringComparison.Ordinal);

        // San Diego is $205 in October; $289 a night is $84 over on both nights.
        var marriott = await UploadAsync(host, "marriott-san-diego-folio.pdf");
        Assert.Equal((2, 168m), (marriott.LodgingCheck!.NightsOver, marriott.LodgingCheck.AmountOver));
        Assert.Contains("over the GSA lodging maximum", marriott.LodgingCheck.Notes[0], StringComparison.Ordinal);

        // Huntsville is $121: the $119 night is fine, the $129 night is $8 over.
        var hampton = await UploadAsync(host, "hampton-huntsville-folio.pdf");
        Assert.Equal([false, true], hampton.LodgingCheck!.Nights.Select(n => n.IsOver));
        Assert.Equal(8m, hampton.LodgingCheck.AmountOver);

        // Nothing is saved on a line by reading: the files just wait in the library.
        Assert.All(await host.Get<IReceiptLibraryService>().ListMineAsync(), r => Assert.False(r.IsInUse));
        await using var db = await host.Db.CreateAsync();
        Assert.Equal(0, await db.Set<OdcEntry>().CountAsync(o => o.ReceiptAttachmentId != null));
    }

    [Fact]
    public async Task The_trip_destination_on_the_report_decides_the_rate_before_the_folio_city()
    {
        await using var host = await HostAsync();
        host.As(Analyst);
        var report = await ReportAsync(host);
        await host.Get<ITravelPerDiemService>().SaveAsync(report.Id, new SaveTravelPerDiemRequest(host.Design, true,
            [new TravelDestinationDto(PerDiemTable.Conus, "US", "AL", "Huntsville", new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 15))], []));

        var reading = await UploadAsync(host, "hilton-arlington-folio.pdf", report.Id);

        Assert.Equal([121m, 121m, 121m], reading.LodgingCheck!.Nights.Select(n => n.LodgingMax));
        Assert.Contains("from the trip", reading.LodgingCheck.Location, StringComparison.Ordinal);

        // Edited values are checked again without the receipt.
        var edited = await host.Get<IReceiptLibraryService>().CheckLodgingAsync(new LodgingRateCheckRequest(new DateOnly(2026, 10, 12), 2, 115m, "Arlington", "VA"));
        Assert.Equal((2, false, 258m), (edited.Nights.Count, edited.IsOver, edited.LodgingMax));
    }

    [Fact]
    public async Task One_upload_backs_the_line_it_filled_and_a_second_line_from_the_same_folio()
    {
        await using var host = await HostAsync();
        host.As(Analyst);
        var reports = host.Get<IExpenseReportService>();
        var report = await ReportAsync(host);
        var folio = await UploadAsync(host, "hilton-arlington-folio.pdf", report.Id);

        var lodging = await reports.AddLineAsync(report.Id, Line(host, folio.Extraction.LodgingAmount!.Value, "Lodging, Arlington, 3 nights", folio.Receipt.Id));
        var parking = await reports.AddLineAsync(report.Id, Line(host, 135m, "Hotel parking", folio.Receipt.Id));

        Assert.Equal((folio.Receipt.Id, folio.Receipt.Id), (lodging.ReceiptFileId, parking.ReceiptFileId));
        Assert.Equal("hilton-arlington-folio.pdf", lodging.ReceiptFileName);
        Assert.Single(host.Get<IBlobStore>() is MemoryBlobStore blobs ? blobs.Blobs : throw new InvalidOperationException());
        await using (var db = await host.Db.CreateAsync())
        {
            Assert.Equal(1, await db.Set<StoredFile>().CountAsync());
            Assert.Equal(2, await db.Set<AuditEvent>().CountAsync(a => a.Action == "ReceiptAttached"));
        }

        var library = await host.Get<IReceiptLibraryService>().GetAsync(folio.Receipt.Id);
        Assert.Equal(["Hotel parking", "Lodging, Arlington, 3 nights"], library.Uses.Select(u => u.Description).Order());
        Assert.All(library.Uses, u => Assert.Equal("DC program review", u.ExpenseReportTitle));

        // Both lines meet the receipt rule with the one file, and the report goes for approval.
        Assert.Equal(OdcApprovalState.Submitted, (await reports.SubmitAsync(report.Id)).Status);
        await using var content = await host.Get<IOdcService>().OpenReceiptAsync(parking.Id);
        Assert.Equal("hilton-arlington-folio.pdf", content.FileName);
    }

    [Fact]
    public async Task A_receipt_uploaded_onto_a_line_joins_the_library_and_can_back_another_line()
    {
        await using var host = await HostAsync();
        host.As(Analyst);
        var reports = host.Get<IExpenseReportService>();
        var report = await ReportAsync(host);
        var first = await reports.AddLineAsync(report.Id, Line(host, 42.80m, "Taxi from DCA", null));
        await reports.AttachReceiptAsync(report.Id, first.Id, "yellow-cab-receipt.pdf", new MemoryStream(Sample("yellow-cab-receipt.pdf")));

        var mine = Assert.Single(await host.Get<IReceiptLibraryService>().ListMineAsync());
        Assert.Equal(("Yellow Cab of DC", 42.80m, 1), (mine.Vendor, mine.Total, mine.Uses.Count));

        var second = await reports.AddLineAsync(report.Id, Line(host, 10m, "Taxi tip", mine.Id));
        Assert.Equal(mine.Id, second.ReceiptFileId);
    }

    [Fact]
    public async Task A_file_in_use_cannot_be_deleted_and_a_file_that_was_attached_is_kept_when_removed()
    {
        await using var host = await HostAsync();
        host.As(Analyst);
        var library = host.Get<IReceiptLibraryService>();
        var reports = host.Get<IExpenseReportService>();
        var report = await ReportAsync(host);
        var folio = await UploadAsync(host, "hilton-arlington-folio.pdf");
        var line = await reports.AddLineAsync(report.Id, Line(host, 833.52m, "Lodging", folio.Receipt.Id));

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => library.DeleteAsync(folio.Receipt.Id));
        Assert.Contains("attached to 1 line", blocked.Message, StringComparison.Ordinal);
        Assert.Contains("Lodging", blocked.Message, StringComparison.Ordinal);

        // Once the line is gone it can leave the library, but the file stays behind for the audit trail.
        await reports.RemoveLineAsync(report.Id, line.Id);
        Assert.Contains(await library.ListMineAsync(new ReceiptQuery(UnattachedOnly: true)), r => r.Id == folio.Receipt.Id);
        await library.DeleteAsync(folio.Receipt.Id);
        Assert.DoesNotContain(await library.ListMineAsync(), r => r.Id == folio.Receipt.Id);
        await using (var db = await host.Db.CreateAsync())
        {
            Assert.True(await db.Set<StoredFile>().AnyAsync(f => f.Id == folio.Receipt.Id));
            Assert.NotNull((await db.Set<ReceiptDocument>().SingleAsync(d => d.FileId == folio.Receipt.Id)).RemovedAtUtc);
        }

        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => reports.AddLineAsync(report.Id, Line(host, 50m, "Again", folio.Receipt.Id)));

        // A file that never backed anything is deleted outright, bytes and all.
        var taxi = await UploadAsync(host, "yellow-cab-receipt.pdf");
        await library.DeleteAsync(taxi.Receipt.Id);
        await using var after = await host.Db.CreateAsync();
        Assert.False(await after.Set<StoredFile>().AnyAsync(f => f.Id == taxi.Receipt.Id));
        Assert.Single(((MemoryBlobStore)host.Get<IBlobStore>()).Blobs); // only the folio's bytes are left
    }

    [Fact]
    public async Task People_see_only_their_own_library_and_others_see_a_file_only_through_a_line_they_can_see()
    {
        await using var host = await HostAsync();
        host.As(Analyst);
        var report = await ReportAsync(host);
        var folio = await UploadAsync(host, "hilton-arlington-folio.pdf");
        var unattached = await UploadAsync(host, "yellow-cab-receipt.pdf");
        await host.Get<IExpenseReportService>().AddLineAsync(report.Id, Line(host, 833.52m, "Lodging", folio.Receipt.Id));
        await host.Get<IExpenseReportService>().SubmitAsync(report.Id);

        // A peer: nothing in their library, can't open, read or use the analyst's files.
        host.As(Dev);
        var peer = host.Get<IReceiptLibraryService>();
        Assert.Empty(await peer.ListMineAsync());
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => peer.GetAsync(folio.Receipt.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => peer.OpenAsync(folio.Receipt.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => peer.ReadAsync(unattached.Receipt.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => peer.DeleteAsync(unattached.Receipt.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => peer.UpdateAsync(unattached.Receipt.Id, new UpdateReceiptRequest(ReceiptDocumentKind.Other)));
        var devReport = await ReportAsync(host, "Dev trip");
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() =>
            host.Get<IExpenseReportService>().AddLineAsync(devReport.Id, Line(host, 20m, "Borrowed receipt", unattached.Receipt.Id)));

        // The approver (the analyst's supervisor) and Finance open the file through the line; not the unattached one.
        foreach (var reviewer in new[] { Pm, Finance })
        {
            host.As(reviewer);
            var library = host.Get<IReceiptLibraryService>();
            await using (var content = await library.OpenAsync(folio.Receipt.Id))
            {
                Assert.Equal("hilton-arlington-folio.pdf", content.FileName);
            }

            Assert.Single((await library.GetAsync(folio.Receipt.Id)).Uses);
            await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => library.OpenAsync(unattached.Receipt.Id));
            Assert.Empty(await library.ListMineAsync());
        }

        // Someone with no tie to the report or its project sees nothing.
        host.As(Pm2);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IReceiptLibraryService>().OpenAsync(folio.Receipt.Id));
    }

    [Fact]
    public async Task The_library_lists_unattached_files_first_and_searches_by_name_date_and_amount()
    {
        await using var host = await HostAsync();
        host.As(Analyst);
        var library = host.Get<IReceiptLibraryService>();
        var report = await ReportAsync(host);
        var hilton = await UploadAsync(host, "hilton-arlington-folio.pdf");
        var marriott = await UploadAsync(host, "marriott-san-diego-folio.pdf");
        var taxi = await UploadAsync(host, "yellow-cab-receipt.pdf");
        await host.Get<IExpenseReportService>().AddLineAsync(report.Id, Line(host, 833.52m, "Lodging", hilton.Receipt.Id));

        var all = await library.ListMineAsync();
        Assert.Equal(hilton.Receipt.Id, all[^1].Id); // the one in use goes last
        Assert.Equal(new[] { marriott.Receipt.Id, taxi.Receipt.Id }.Order(), all.Take(2).Select(r => r.Id).Order()); // unattached first

        Assert.Equal([marriott.Receipt.Id], (await library.ListMineAsync(new ReceiptQuery("marriott"))).Select(r => r.Id));
        Assert.Equal([taxi.Receipt.Id], (await library.ListMineAsync(new ReceiptQuery("yellow cab"))).Select(r => r.Id));
        Assert.Equal([taxi.Receipt.Id], (await library.ListMineAsync(new ReceiptQuery("42.80"))).Select(r => r.Id));
        Assert.Equal([marriott.Receipt.Id], (await library.ListMineAsync(new ReceiptQuery(MinAmount: 500m, MaxAmount: 900m))).Select(r => r.Id));
        Assert.Equal([marriott.Receipt.Id], (await library.ListMineAsync(new ReceiptQuery(From: new DateOnly(2026, 10, 19)))).Select(r => r.Id));
        Assert.Equal(new[] { marriott.Receipt.Id, taxi.Receipt.Id }.Order(), (await library.ListMineAsync(new ReceiptQuery(UnattachedOnly: true))).Select(r => r.Id).Order());

        // The owner can correct what a file is; choosing it again reuses what was read (no second reading).
        var fixedUp = await library.UpdateAsync(taxi.Receipt.Id, new UpdateReceiptRequest(ReceiptDocumentKind.Receipt, "DC Yellow Cab", new DateOnly(2026, 10, 14), 42.80m));
        Assert.Equal("DC Yellow Cab", fixedUp.Vendor);
        var again = await library.ReadAsync(marriott.Receipt.Id);
        Assert.Equal((760.26m, 2), (again.Extraction.Total, again.LodgingCheck!.NightsOver));
    }

    [Fact]
    public async Task An_approved_receipt_cant_be_swapped_and_a_swap_after_reopening_is_in_the_audit_trail()
    {
        await using var host = await HostAsync();
        host.As(Analyst);
        var reports = host.Get<IExpenseReportService>();
        var report = await ReportAsync(host);
        var hilton = await UploadAsync(host, "hilton-arlington-folio.pdf");
        var other = await UploadAsync(host, "hampton-huntsville-folio.pdf");
        var line = await reports.AddLineAsync(report.Id, Line(host, 833.52m, "Lodging", hilton.Receipt.Id));
        await reports.SubmitAsync(report.Id);
        await host.As(Pm).Get<IOdcApprovalService>().ApproveReportAsync(report.Id);

        host.As(Analyst);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reports.UpdateLineAsync(report.Id, line.Id, Line(host, 833.52m, "Lodging", other.Receipt.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Get<IReceiptLibraryService>().DeleteAsync(hilton.Receipt.Id));

        await host.As(Pm).Get<IOdcApprovalService>().ReopenReportAsync(report.Id, "Wrong folio");
        host.As(Analyst);
        await reports.UpdateLineAsync(report.Id, line.Id, Line(host, 833.52m, "Lodging", other.Receipt.Id));

        await using var db = await host.Db.CreateAsync();
        var swap = await db.Set<AuditEvent>().Where(a => a.EntityId == line.Id && a.Action == "ReceiptAttached").OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, swap.Count);
        Assert.Contains(hilton.Receipt.Id.ToString(), swap[1].BeforeJson, StringComparison.Ordinal);
        Assert.Contains(other.Receipt.Id.ToString(), swap[1].AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_assistant_reads_only_when_an_admin_turned_it_on()
    {
        await using var host = await HostAsync();
        host.As(Analyst);

        // Off (the default): a photo has no text layer, so nothing is suggested and nothing is sent anywhere.
        var photo = await UploadAsync(host, "parking-receipt-photo.png");
        Assert.Equal(ReceiptReadMethod.None, photo.Extraction.Method);
        Assert.Empty(photo.Extraction.FromReceipt);
        await using (var db = await host.Db.CreateAsync())
        {
            Assert.Equal(0, await db.Set<AssistantInteraction>().CountAsync());
        }

        // A complete folio needs no help even when the assistant is on.
        host.As(Admin);
        await host.Get<IAssistantAdminService>().SaveSettingsAsync(new SaveAssistantSettingsRequest(true, AssistantOptions.FakeConnection, null, AcceptNotice: true));
        host.As(Analyst);
        await UploadAsync(host, "hilton-arlington-folio.pdf");
        await using (var db = await host.Db.CreateAsync())
        {
            Assert.Equal(0, await db.Set<AssistantInteraction>().CountAsync());
        }

        // On: the photo goes to the (fake) provider, audited as a receipt reading.
        await UploadAsync(host, "parking-receipt-photo.png");
        await using var after = await host.Db.CreateAsync();
        var interaction = await after.Set<AssistantInteraction>().SingleAsync();
        Assert.Equal((AssistantInteractionKind.ReceiptReading, AssistantOutcome.Answered, Analyst), (interaction.Kind, interaction.Outcome, interaction.UserEmail));
        var call = await after.Set<AssistantToolCall>().SingleAsync(c => c.InteractionId == interaction.Id);
        Assert.Contains("parking-receipt-photo.png", call.RecordsRead, StringComparison.Ordinal);
    }

    [Fact]
    public void The_assistants_answer_only_fills_what_the_text_layer_missed()
    {
        var parsed = ReceiptParser.Parse(["Joe's Garage", "Parking 3 days"]);
        Assert.Null(parsed.Total);

        var merged = ReceiptReader.Merge(parsed,
            """{"kind":"receipt","vendor":"Someone else","date":"2026-10-14","total":36.00,"city":null,"state":null,"checkIn":null,"checkOut":null,"nights":null,"nightlyRate":null,"roomTotal":null,"lodgingTaxes":null,"otherCharges":null}""");

        Assert.Equal(("Joe's Garage", 36.00m, new DateOnly(2026, 10, 14)), (merged.Vendor, merged.Total, merged.Date));
        Assert.Equal(ReceiptReadMethod.TextLayer, merged.ReadBy[ReceiptFields.Vendor]);
        Assert.Equal(ReceiptReadMethod.Assistant, merged.ReadBy[ReceiptFields.Total]);
        Assert.Equal(ReceiptReadMethod.Assistant, merged.Method);
    }

    [Fact]
    public async Task A_card_charge_can_take_a_receipt_from_the_library()
    {
        await using var host = await HostAsync();
        var cards = host.As(Finance).Get<ICardProgramService>();
        var program = await cards.CreateProgramAsync(new SaveCardProgramRequest("Citi", "Citibank"));
        await cards.CreateCardAsync(new SaveCorporateCardRequest(program.Id, host.AnalystId, "4417"));
        await cards.ImportStatementAsync(program.Id, "statement.csv", new MemoryStream(Encoding.UTF8.GetBytes("""
            Transaction Date,Posted Date,Card Number,Description,Category,Amount
            10/14/2026,10/15/2026,XXXX-4417,YELLOW CAB DC,4121,42.80
            """)));

        host.As(Analyst);
        var report = await ReportAsync(host);
        var taxi = await UploadAsync(host, "yellow-cab-receipt.pdf");
        var charge = Assert.Single(await host.Get<ICardChargeService>().ListMyChargesAsync());
        var line = await host.Get<ICardChargeService>().AttachToReportAsync(charge.Id, new AttachCardChargeRequest(report.Id, host.Design, ReceiptFileId: taxi.Receipt.Id));

        Assert.Equal(taxi.Receipt.Id, line.ReceiptFileId);
        Assert.Single((await host.Get<IReceiptLibraryService>().GetAsync(taxi.Receipt.Id)).Uses);
    }
}
