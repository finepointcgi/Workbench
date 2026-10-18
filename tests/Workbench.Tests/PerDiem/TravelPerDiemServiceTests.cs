using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.PerDiem;
using Workbench.Domain.PerDiem;
using Workbench.Tests.Resourcing;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.PerDiem;

/// <summary>Per diem on an expense report through the real services: save, compute, generate lines, submit rules, access.</summary>
public sealed class TravelPerDiemServiceTests
{
    private static readonly DateOnly Mon = new(2026, 10, 5);

    private static async Task<(ResourcingHost Host, Guid ReportId)> TripAsync()
    {
        var host = await CreateAsync();
        await host.As(Finance).Get<IPerDiemRateService>().InstallSampleRatesAsync(2027);
        var report = await host.As(Analyst).Get<IExpenseReportService>().CreateReportAsync(new SaveExpenseReportRequest("DC review", Mon, Mon.AddDays(2)));
        return (host, report.Id);
    }

    private static SaveTravelPerDiemRequest DcRequest(Guid wbs, string? justification = null) => new(
        wbs, true,
        [new TravelDestinationDto(PerDiemTable.Conus, "US", "DC", "District of Columbia", Mon, Mon.AddDays(2))],
        [
            new TravelDayInput(Mon, 300m, 40m, Justification: justification),
            new TravelDayInput(Mon.AddDays(1), 250m, 33m, LunchProvided: true),
        ]);

    [Fact]
    public async Task Saving_computes_the_per_diem_from_the_rate_table()
    {
        var (host, reportId) = await TripAsync();
        await using var _ = host;

        var dto = await host.Get<ITravelPerDiemService>().SaveAsync(reportId, DcRequest(host.Design));

        Assert.Equal([69m, 66m, 69m], dto.Days.Select(d => d.MieAllowable));
        Assert.Equal(PerDiemAmountStatus.RequiresJustification, dto.Days[0].LodgingStatus);
        Assert.Equal((508m, 204m, 42m), (dto.Totals.LodgingAllowable, dto.Totals.MieAllowable, dto.Totals.Unallowable));
        Assert.Equal((1, true, "1001.01.01"), (dto.PendingJustifications, dto.LinesOutOfDate, dto.WbsCode));
        Assert.Contains("FY2027", dto.Days[0].RateDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_generates_lines_and_submit_needs_a_justification_and_current_lines()
    {
        var (host, reportId) = await TripAsync();
        await using var _ = host;
        var perDiem = host.Get<ITravelPerDiemService>();
        var reports = host.Get<IExpenseReportService>();
        await perDiem.SaveAsync(reportId, DcRequest(host.Design));

        await perDiem.ApplyToReportAsync(reportId);
        var lines = (await reports.GetReportAsync(reportId)).Lines;
        Assert.Equal(4, lines.Count); // lodging, taxes, M&IE, unallowable overage
        Assert.Equal(508m, lines.Single(l => l.Description.StartsWith("Lodging ·", StringComparison.Ordinal)).Amount);
        Assert.Equal(204m, lines.Single(l => l.Description.StartsWith("M&IE", StringComparison.Ordinal)).Amount);
        var overage = lines.Single(l => l.IsUnallowable);
        Assert.Equal(42m, overage.Amount);
        Assert.Contains("justification", overage.UnallowableReason, StringComparison.Ordinal);

        var pending = await Assert.ThrowsAsync<ArgumentException>(() => reports.SubmitAsync(reportId));
        Assert.Contains("justification", pending.Message, StringComparison.Ordinal);

        // With a justification the overage becomes allowable; until the lines are regenerated, submit is refused.
        await perDiem.SaveAsync(reportId, DcRequest(host.Design, "Only hotel with rooms near the customer site"));
        var stale = await Assert.ThrowsAsync<ArgumentException>(() => reports.SubmitAsync(reportId));
        Assert.Contains("Update expense lines", stale.Message, StringComparison.Ordinal);

        var applied = await perDiem.ApplyToReportAsync(reportId);
        Assert.False(applied.LinesOutOfDate);
        lines = (await reports.GetReportAsync(reportId)).Lines;
        Assert.Equal(4, lines.Count); // regenerated, not added on top
        Assert.DoesNotContain(lines, l => l.IsUnallowable);
        Assert.Equal(42m, lines.Single(l => l.Description.Contains("justified", StringComparison.Ordinal)).Amount);

        // M&IE, taxes and justified overage need no receipt of their own; the lodging line does.
        var receipt = await Assert.ThrowsAsync<ArgumentException>(() => reports.SubmitAsync(reportId));
        Assert.Contains("Lodging ·", receipt.Message, StringComparison.Ordinal);
        var lodging = lines.Single(l => l.Description.StartsWith("Lodging ·", StringComparison.Ordinal));
        await reports.AttachReceiptAsync(reportId, lodging.Id, "folio.pdf", new MemoryStream("%PDF-1.4"u8.ToArray()));
        var submitted = await reports.SubmitAsync(reportId);
        Assert.Equal(OdcApprovalState.Submitted, submitted.Status);

        // Submitted reports are locked.
        await Assert.ThrowsAsync<InvalidOperationException>(() => perDiem.SaveAsync(reportId, DcRequest(host.Design)));
    }

    [Fact]
    public async Task Removing_the_per_diem_removes_its_lines_only()
    {
        var (host, reportId) = await TripAsync();
        await using var _ = host;
        var perDiem = host.Get<ITravelPerDiemService>();
        var reports = host.Get<IExpenseReportService>();
        await reports.AddLineAsync(reportId, new SaveExpenseLineRequest(host.Design, OdcCostType.Travel, Mon, 45m, "Taxi"));
        await perDiem.SaveAsync(reportId, DcRequest(host.Design));
        await perDiem.ApplyToReportAsync(reportId);

        await perDiem.RemoveAsync(reportId);

        Assert.Equal("Taxi", Assert.Single((await reports.GetReportAsync(reportId)).Lines).Description);
        Assert.False((await perDiem.GetAsync(reportId)).Exists);
        await using var db = await host.Db.CreateAsync();
        Assert.False(await db.Set<PerDiemLine>().AnyAsync());
    }

    [Fact]
    public async Task The_traveler_edits_and_their_supervisor_and_finance_read()
    {
        var (host, reportId) = await TripAsync();
        await using var _ = host;
        await host.Get<ITravelPerDiemService>().SaveAsync(reportId, DcRequest(host.Design));

        Assert.True((await host.As(Pm).Get<ITravelPerDiemService>().GetAsync(reportId)).Exists);
        Assert.True((await host.As(Finance).Get<ITravelPerDiemService>().GetAsync(reportId)).Exists);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(Pm2).Get<ITravelPerDiemService>().GetAsync(reportId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(Pm).Get<ITravelPerDiemService>().SaveAsync(reportId, DcRequest(host.Design)));
    }

    [Fact]
    public async Task The_charge_code_must_be_the_travelers()
    {
        var (host, reportId) = await TripAsync();
        await using var _ = host;

        await Assert.ThrowsAsync<ArgumentException>(() => host.Get<ITravelPerDiemService>().SaveAsync(reportId, DcRequest(host.Wbs2)));
    }
}
