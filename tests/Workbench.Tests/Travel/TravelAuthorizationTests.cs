using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.PerDiem;
using Workbench.Contracts.Travel;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Resourcing;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Travel;

/// <summary>Travel authorizations through the real registrations: the per diem estimate, routing, decisions and estimate vs actual.</summary>
public sealed class TravelAuthorizationTests
{
    private static readonly DateOnly Mon = new(2026, 10, 5);

    internal static async Task<ResourcingHost> HostAsync()
    {
        var host = await CreateAsync();
        await host.As(Finance).Get<IPerDiemRateService>().InstallSampleRatesAsync(2027);
        return host;
    }

    internal static SaveTravelAuthorizationRequest DcTrip(Guid wbs, decimal airfare = 400m, decimal rental = 0m, decimal other = 0m, decimal advance = 0m) => new(
        "DC program review", "Quarterly review with the customer", wbs,
        [new TravelDestinationDto(PerDiemTable.Conus, "US", "DC", "District of Columbia", Mon, Mon.AddDays(2))],
        airfare, rental, other, null, advance);

    /// <summary>As analyst@ (supervisor and PM: pm@): a submitted, then approved, DC trip.</summary>
    internal static async Task<TravelAuthorizationDetailDto> ApprovedTripAsync(ResourcingHost host, decimal airfare = 400m, decimal other = 0m)
    {
        var travel = host.As(Analyst).Get<ITravelAuthorizationService>();
        var trip = await travel.CreateAsync(DcTrip(host.Design, airfare, other: other));
        await travel.SubmitAsync(trip.Authorization.Id);
        return await host.As(Pm).Get<ITravelAuthorizationService>().ApproveAsync(trip.Authorization.Id, new TravelDecisionRequest());
    }

    [Fact]
    public async Task The_estimate_prices_lodging_and_mie_from_the_per_diem_tables()
    {
        await using var host = await HostAsync();
        var travel = host.As(Analyst).Get<ITravelAuthorizationService>();

        var estimate = await travel.EstimateAsync(new EstimateTravelRequest(DcTrip(host.Design).Destinations, 400m, 0m, 25m));

        // DC FY2027: $258 lodging maximum for each of 2 nights; $92 M&IE, 75% ($69) on the first and last day.
        Assert.Equal((2, 516m, 230m), (estimate.Nights, estimate.Lodging, estimate.Mie));
        Assert.Equal(400m + 516m + 230m + 25m, estimate.Total);
        Assert.Equal([258m, 258m, 0m], estimate.Days.Select(d => d.LodgingMax));
        Assert.Equal([69m, 92m, 69m], estimate.Days.Select(d => d.MieAllowance));

        // Saving stores the computed amounts; typed lodging and M&IE don't exist.
        var saved = await travel.CreateAsync(DcTrip(host.Design));
        Assert.Equal((516m, 230m, 1146m), (saved.Authorization.EstimatedLodging, saved.Authorization.EstimatedMie, saved.Authorization.EstimatedTotal));
        Assert.StartsWith("TA-", saved.Authorization.Number, StringComparison.Ordinal);
        Assert.Equal("District of Columbia, DC", saved.Authorization.Destinations);
    }

    [Fact]
    public async Task A_trip_needs_a_purpose_contiguous_stops_and_one_of_the_travelers_charge_codes()
    {
        await using var host = await HostAsync();
        var travel = host.As(Analyst).Get<ITravelAuthorizationService>();

        await Assert.ThrowsAsync<ArgumentException>(() => travel.CreateAsync(DcTrip(host.Design) with { Purpose = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => travel.CreateAsync(DcTrip(host.Wbs2))); // not assigned to 2002
        var gap = DcTrip(host.Design) with
        {
            Destinations =
            [
                new TravelDestinationDto(PerDiemTable.Conus, "US", "DC", "District of Columbia", Mon, Mon.AddDays(1)),
                new TravelDestinationDto(PerDiemTable.Conus, "US", "AL", "Huntsville", Mon.AddDays(2), Mon.AddDays(3)),
            ],
        };
        await Assert.ThrowsAsync<ArgumentException>(() => travel.CreateAsync(gap));
        await Assert.ThrowsAsync<ArgumentException>(() => travel.CreateAsync(DcTrip(host.Design, airfare: -1m)));
    }

    [Fact]
    public async Task Submitting_routes_to_the_supervisor_and_the_charged_projects_manager()
    {
        await using var host = await HostAsync();
        await using (var db = await host.Db.CreateAsync())
        {
            db.Set<ChargeAssignment>().Add(new ChargeAssignment { WbsElementId = host.Wbs2, EmployeeId = host.DevId });
            await db.SaveChangesAsync();
        }

        var trip = await host.As(Dev).Get<ITravelAuthorizationService>().CreateAsync(DcTrip(host.Wbs2));
        var id = trip.Authorization.Id;
        await host.Get<ITravelAuthorizationService>().SubmitAsync(id);

        // Both approvers (dev's supervisor pm@ and 2002's manager pm2@) see it in their inbox; nobody else can open it.
        Assert.Contains(await host.As(Pm).Get<IApprovalInboxService>().ListMineAsync(), i => i.DocumentId == id && i.DocumentType == ApprovalDocumentTypes.TravelAuthorization);
        Assert.Contains(await host.As(Pm2).Get<ITravelAuthorizationService>().ListAwaitingMeAsync(), t => t.Id == id);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.As(Analyst).Get<ITravelAuthorizationService>().GetAsync(id));
        Assert.Equal(TravelAuthorizationState.Submitted, (await host.As(Finance).Get<ITravelAuthorizationService>().GetAsync(id)).Authorization.Status);

        // The traveler can't change it now, nor approve it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.As(Dev).Get<ITravelAuthorizationService>().UpdateAsync(id, DcTrip(host.Wbs2)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(Dev).Get<ITravelAuthorizationService>().ApproveAsync(id, new TravelDecisionRequest()));

        // The supervisor's approval alone isn't enough; the PM's completes it (through the approval inbox).
        var afterPm = await host.As(Pm).Get<ITravelAuthorizationService>().ApproveAsync(id, new TravelDecisionRequest("OK"));
        Assert.Equal(TravelAuthorizationState.Submitted, afterPm.Authorization.Status);
        await host.As(Pm2).Get<IApprovalInboxService>().DecideAsync(ApprovalDocumentTypes.TravelAuthorization, id, new ApprovalDecisionRequest(ApprovalDecisionKind.Approve));
        var done = await host.As(Dev).Get<ITravelAuthorizationService>().GetAsync(id);
        Assert.Equal((TravelAuthorizationState.Approved, Pm2), (done.Authorization.Status, done.Authorization.ApprovedBy));
    }

    [Fact]
    public async Task Rejecting_needs_a_reason_and_sends_it_back_for_changes()
    {
        await using var host = await HostAsync();
        var travel = host.As(Analyst).Get<ITravelAuthorizationService>();
        var trip = await travel.CreateAsync(DcTrip(host.Design));
        await travel.SubmitAsync(trip.Authorization.Id);

        var pm = host.As(Pm).Get<ITravelAuthorizationService>();
        await Assert.ThrowsAsync<ArgumentException>(() => pm.RejectAsync(trip.Authorization.Id, new TravelDecisionRequest()));
        var rejected = await pm.RejectAsync(trip.Authorization.Id, new TravelDecisionRequest("Fly economy"));
        Assert.Equal((TravelAuthorizationState.Rejected, "Fly economy"), (rejected.Authorization.Status, rejected.Authorization.RejectionReason));

        var fixedTrip = await host.As(Analyst).Get<ITravelAuthorizationService>().UpdateAsync(trip.Authorization.Id, DcTrip(host.Design, airfare: 300m));
        Assert.True(fixedTrip.Authorization.IsEditable);
        var resubmitted = await host.Get<ITravelAuthorizationService>().SubmitAsync(trip.Authorization.Id);
        Assert.Equal(TravelAuthorizationState.Submitted, resubmitted.Authorization.Status);
    }

    [Fact]
    public async Task The_approved_trip_is_the_budget_and_lines_over_it_are_flagged()
    {
        await using var host = await HostAsync();
        var trip = await ApprovedTripAsync(host, airfare: 300m);
        var travel = host.As(Analyst).Get<ITravelAuthorizationService>();
        var reports = host.Get<IExpenseReportService>();

        var report = await travel.StartExpenseReportAsync(trip.Authorization.Id);
        Assert.Equal((Mon, Mon.AddDays(2)), (report.TripStart, report.TripEnd));
        Assert.Equal(report.Id, (await travel.GetAsync(trip.Authorization.Id)).Authorization.ExpenseReportId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => travel.StartExpenseReportAsync(trip.Authorization.Id));

        // Airfare within the 10% tolerance: over, but not flagged.
        var airfare = await reports.AddLineAsync(report.Id, new SaveExpenseLineRequest(host.Design, OdcCostType.Travel, Mon, 320m, "Airfare DCA round trip", "Delta"));
        await reports.AddLineAsync(report.Id, new SaveExpenseLineRequest(host.Design, OdcCostType.Travel, Mon, 250m, "Hotel", "Marriott"));
        var comparison = (await travel.GetComparisonForReportAsync(report.Id))!;
        var air = comparison.Categories.Single(c => c.Category == TravelCategory.Airfare);
        Assert.Equal((300m, 320m, true, false), (air.Authorized, air.Actual, air.IsOverAuthorized, air.IsOverTolerance));
        Assert.Empty(comparison.Flags);
        Assert.Equal(TravelCategory.Lodging, comparison.Lines.Single(l => l.Description == "Hotel").Category);

        // Over the tolerance, and a category that wasn't authorized at all: their lines are flagged.
        await reports.UpdateLineAsync(report.Id, airfare.Id, new SaveExpenseLineRequest(host.Design, OdcCostType.Travel, Mon, 340m, "Airfare DCA round trip", "Delta"));
        var rental = await reports.AddLineAsync(report.Id, new SaveExpenseLineRequest(host.Design, OdcCostType.Travel, Mon.AddDays(2), 120m, "Hertz rental car", "Hertz"));
        comparison = (await travel.GetComparisonForReportAsync(report.Id))!;
        Assert.True(comparison.Categories.Single(c => c.Category == TravelCategory.Airfare).IsOverTolerance);
        Assert.True(comparison.Lines.Single(l => l.OdcEntryId == airfare.Id).IsFlagged);
        Assert.Contains("wasn't authorized", comparison.Lines.Single(l => l.OdcEntryId == rental.Id).Flag, StringComparison.Ordinal);
        Assert.False(comparison.Lines.Single(l => l.Description == "Hotel").IsFlagged);
        Assert.Equal(2, comparison.Flags.Count);

        // The traveler can recategorize a line; Finance sees the same comparison.
        comparison = await travel.SetLineCategoryAsync(report.Id, rental.Id, new SetTravelLineCategoryRequest(TravelCategory.Other));
        Assert.True(comparison.Lines.Single(l => l.OdcEntryId == rental.Id).CategoryChosen);
        Assert.NotNull(await host.As(Finance).Get<ITravelAuthorizationService>().GetComparisonForReportAsync(report.Id));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => host.As(Dev).Get<ITravelAuthorizationService>().GetComparisonForReportAsync(report.Id));
    }

    [Fact]
    public async Task An_advance_is_deducted_from_what_the_report_reimburses()
    {
        await using var host = await HostAsync();
        var trip = await ApprovedTripAsync(host);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            host.As(Analyst).Get<ITravelAuthorizationService>().RecordAdvanceAsync(trip.Authorization.Id, new RecordTravelAdvanceRequest(200m, Mon)));
        await host.As(Finance).Get<ITravelAuthorizationService>().RecordAdvanceAsync(trip.Authorization.Id, new RecordTravelAdvanceRequest(200m, Mon, "CHK 1001"));

        var travel = host.As(Analyst).Get<ITravelAuthorizationService>();
        var report = await travel.StartExpenseReportAsync(trip.Authorization.Id);
        await host.Get<IExpenseReportService>().AddLineAsync(report.Id, new SaveExpenseLineRequest(host.Design, OdcCostType.Travel, Mon, 350m, "Airfare", "Delta"));
        var comparison = (await travel.GetComparisonForReportAsync(report.Id))!;
        Assert.Equal((350m, 200m, 150m), (comparison.Reimbursable, comparison.AdvanceIssued, comparison.DueToEmployee));
    }

    [Fact]
    public async Task A_cancelled_trip_leaves_the_inbox_and_deleting_its_report_unlinks_it()
    {
        await using var host = await HostAsync();
        var travel = host.As(Analyst).Get<ITravelAuthorizationService>();
        var trip = await travel.CreateAsync(DcTrip(host.Design));
        await travel.SubmitAsync(trip.Authorization.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => travel.CancelAsync(trip.Authorization.Id, new TravelDecisionRequest()));
        await travel.CancelAsync(trip.Authorization.Id, new TravelDecisionRequest("Meeting moved online"));
        Assert.DoesNotContain(await host.As(Pm).Get<IApprovalInboxService>().ListMineAsync(), i => i.DocumentId == trip.Authorization.Id);

        var approved = await ApprovedTripAsync(host);
        travel = host.As(Analyst).Get<ITravelAuthorizationService>();
        var report = await travel.StartExpenseReportAsync(approved.Authorization.Id);
        await host.Get<IExpenseReportService>().DeleteReportAsync(report.Id);
        Assert.Null((await travel.GetAsync(approved.Authorization.Id)).Authorization.ExpenseReportId);
        await using var db = await host.Db.CreateAsync();
        Assert.False(await db.Set<ExpenseReport>().AnyAsync(r => r.Id == report.Id));
    }
}
