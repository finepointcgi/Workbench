using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Approvals;
using Workbench.Application.Notifications;
using Workbench.Application.Odcs;
using Workbench.Application.PerDiem;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.PerDiem;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Travel;
using Workbench.Domain.PerDiem;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Travel;

namespace Workbench.Application.Travel;

public static class TravelNotificationKinds
{
    public const string NeedsApproval = "travel.approval";
    public const string Decided = "travel.decided";
}

public sealed class TravelAuthorizationService(
    IWorkbenchDbContextFactory dbFactory,
    OdcWorkflow workflow,
    IProjectService projects,
    IExpenseReportService reports,
    INotificationPublisher notifications,
    IOptions<TravelOptions> options) : ITravelAuthorizationService
{
    public const string EntityType = nameof(TravelAuthorization);
    private const string Type = ApprovalDocumentTypes.TravelAuthorization;

    private decimal Tolerance => Math.Max(0, options.Value.TolerancePercent);

    public async Task<IReadOnlyList<TravelAuthorizationDto>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await workflow.FindCurrentEmployeeAsync(db, cancellationToken) is not { } me)
        {
            return [];
        }

        var rows = await Query(db).Where(t => t.EmployeeId == me.Id).ToListAsync(cancellationToken);
        return await ToDtosAsync(db, rows.OrderByDescending(t => t.TripStart).ThenByDescending(t => t.CreatedAtUtc).ToList(), cancellationToken);
    }

    public async Task<IReadOnlyList<TravelAuthorizationDto>> ListAwaitingMeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ids = (await workflow.Approvals.ActionableAsync(db, Type, workflow.Actor, cancellationToken)).Select(x => x.Instance.DocumentId).ToList();
        var rows = await Query(db).Where(t => ids.Contains(t.Id) && t.Status == TravelAuthorizationStatus.Submitted).ToListAsync(cancellationToken);
        return await ToDtosAsync(db, rows.OrderBy(t => t.SubmittedAtUtc).ToList(), cancellationToken);
    }

    public async Task<IReadOnlyList<TravelAuthorizationDto>> ListAllAsync(TravelAuthorizationQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new TravelAuthorizationQuery();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = Query(db);
        if (query.Status is { } status)
        {
            q = q.Where(t => t.Status == (TravelAuthorizationStatus)(int)status);
        }

        if (query.EmployeeId is { } employee)
        {
            q = q.Where(t => t.EmployeeId == employee);
        }

        if (query.ProjectId is { } project)
        {
            q = q.Where(t => t.ProjectId == project);
        }

        var rows = (await q.ToListAsync(cancellationToken)).OrderByDescending(t => t.TripStart).Take(Math.Clamp(query.Take, 1, 2000)).ToList();
        return await ToDtosAsync(db, rows, cancellationToken);
    }

    public async Task<TravelAuthorizationDetailDto> GetAsync(Guid authorizationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, await FindAsync(db, authorizationId, cancellationToken), cancellationToken);
    }

    public async Task<TravelEstimateDto> EstimateAsync(EstimateTravelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Money(request.EstimatedAirfare, "Airfare");
        Money(request.EstimatedRentalCar, "Rental car");
        Money(request.EstimatedOther, "Other costs");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await TravelEstimator.EstimateAsync(db, Clean(request.Destinations), request.EstimatedAirfare, request.EstimatedRentalCar, request.EstimatedOther, cancellationToken);
    }

    public async Task<TravelAuthorizationDetailDto> CreateAsync(SaveTravelAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await workflow.RequireCurrentEmployeeAsync(db, cancellationToken);
        var now = workflow.UtcNow;
        var ta = new TravelAuthorization
        {
            Number = await NextNumberAsync(db, cancellationToken),
            EmployeeId = me.Id,
            Title = "",
            Purpose = "",
            CreatedBy = workflow.Actor,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await ApplyAsync(db, ta, request, cancellationToken);
        db.Set<TravelAuthorization>().Add(ta);
        workflow.Audit.Record(db, EntityType, ta.Id, "Created", after: Snapshot(ta));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, ta, cancellationToken);
    }

    public async Task<TravelAuthorizationDetailDto> UpdateAsync(Guid authorizationId, SaveTravelAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ta = await FindOwnAsync(db, authorizationId, cancellationToken);
        EnsureEditable(ta);
        var before = Snapshot(ta);
        await ApplyAsync(db, ta, request, cancellationToken);
        ta.UpdatedAtUtc = workflow.UtcNow;
        workflow.Audit.Record(db, EntityType, ta.Id, "Updated", before, Snapshot(ta));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, ta, cancellationToken);
    }

    public async Task DeleteAsync(Guid authorizationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ta = await FindOwnAsync(db, authorizationId, cancellationToken);
        if (ta.Status is not (TravelAuthorizationStatus.Draft or TravelAuthorizationStatus.Rejected or TravelAuthorizationStatus.Cancelled))
        {
            throw new InvalidOperationException("Only draft, sent-back or cancelled trip requests can be deleted. Cancel it instead.");
        }

        workflow.Audit.Record(db, EntityType, ta.Id, "Deleted", before: Snapshot(ta));
        db.Set<TravelAuthorizationDestination>().RemoveRange(ta.Destinations);
        db.Set<TravelAuthorization>().Remove(ta);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<TravelAuthorizationDetailDto> SubmitAsync(Guid authorizationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ta = await FindOwnAsync(db, authorizationId, cancellationToken);
        EnsureEditable(ta);
        var stops = TravelEstimator.Stops(ta);
        PerDiemCalculator.Validate(stops);
        await ValidateChargeAsync(db, ta.EmployeeId, ta.WbsElementId, ta.TripStart, cancellationToken);

        // Rates may have been imported since the draft was saved: price it again.
        var estimate = await TravelEstimator.EstimateAsync(db, stops, ta.EstimatedAirfare, ta.EstimatedRentalCar, ta.EstimatedOther, cancellationToken);
        (ta.EstimatedLodging, ta.EstimatedMie) = (estimate.Lodging, estimate.Mie);
        if (ta.EstimatedTotal <= 0)
        {
            throw new ArgumentException("Estimate the trip's cost before submitting it.");
        }

        var now = workflow.UtcNow;
        var before = ta.Status;
        ta.Status = TravelAuthorizationStatus.Submitted;
        ta.SubmittedBy = workflow.Actor;
        ta.SubmittedAtUtc = now;
        ta.UpdatedAtUtc = now;
        (ta.RejectedBy, ta.RejectedAtUtc, ta.RejectionReason) = (null, null, null);
        var owner = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == ta.EmployeeId, cancellationToken);
        var route = await workflow.Approvals.StartAsync(db, Subject(ta, owner), cancellationToken);
        if (route.HasUnassigned)
        {
            throw new ArgumentException("Nobody can approve this trip yet (you have no supervisor, or you'd approve your own). Ask an administrator to set your supervisor.");
        }

        workflow.Audit.Record(db, EntityType, ta.Id, "Submitted", new { Status = before.ToString() },
            new { Status = ta.Status.ToString(), Approvers = string.Join(", ", route.NotifyEmails), Total = ta.EstimatedTotal });
        await db.SaveChangesAsync(cancellationToken);
        await notifications.PublishAsync(route.NotifyEmails, TravelNotificationKinds.NeedsApproval, $"{owner.DisplayName} requested a trip: {ta.Title}",
            $"{TravelEstimator.Summary(ta.Destinations)}, {ta.TripStart:MMM d}–{ta.TripEnd:MMM d}. Estimated {ta.EstimatedTotal:C}.", link: $"travel/{ta.Id}");
        return await DetailAsync(db, ta, cancellationToken);
    }

    public Task<TravelAuthorizationDetailDto> ApproveAsync(Guid authorizationId, TravelDecisionRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(authorizationId, approve: true, request?.Comment, cancellationToken);

    public Task<TravelAuthorizationDetailDto> RejectAsync(Guid authorizationId, TravelDecisionRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(authorizationId, approve: false, request?.Comment, cancellationToken);

    public async Task<TravelAuthorizationDetailDto> CancelAsync(Guid authorizationId, TravelDecisionRequest request, CancellationToken cancellationToken = default)
    {
        var reason = Text(request?.Comment, 1000) ?? throw new ArgumentException("Say why the trip is cancelled.");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ta = await FindOwnAsync(db, authorizationId, cancellationToken);
        if (ta.Status == TravelAuthorizationStatus.Cancelled)
        {
            throw new InvalidOperationException("The trip is already cancelled.");
        }

        if (ta.ExpenseReportId is { } reportId
            && await db.Set<ExpenseReport>().AnyAsync(r => r.Id == reportId && (r.Status == OdcStatus.Submitted || r.Status == OdcStatus.Approved), cancellationToken))
        {
            throw new InvalidOperationException("The trip's expense report is already submitted, so the trip can't be cancelled.");
        }

        var before = ta.Status;
        ta.Status = TravelAuthorizationStatus.Cancelled;
        ta.CancelledBy = workflow.Actor;
        ta.CancelledAtUtc = workflow.UtcNow;
        ta.CancellationReason = reason;
        ta.UpdatedAtUtc = workflow.UtcNow;
        await workflow.Approvals.CancelAsync(db, Type, ta.Id, workflow.Actor, reason, cancellationToken);
        workflow.Audit.Record(db, EntityType, ta.Id, "Cancelled", new { Status = before.ToString() }, new { Status = ta.Status.ToString() }, reason);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, ta, cancellationToken);
    }

    public async Task<ExpenseReportDto> StartExpenseReportAsync(Guid authorizationId, CancellationToken cancellationToken = default)
    {
        await using (var check = await dbFactory.CreateAsync(cancellationToken))
        {
            var ta = await FindOwnAsync(check, authorizationId, cancellationToken);
            EnsureApproved(ta);
            if (ta.ExpenseReportId is not null)
            {
                throw new InvalidOperationException("This trip already has an expense report.");
            }
        }

        TravelAuthorization trip;
        await using (var read = await dbFactory.CreateAsync(cancellationToken))
        {
            trip = (await TravelEstimator.FindAsync(read, authorizationId, cancellationToken, tracking: false))!;
        }

        var report = await reports.CreateReportAsync(new SaveExpenseReportRequest($"{trip.Title} ({trip.Number})", trip.TripStart, trip.TripEnd, trip.Purpose), cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var linked = await FindAsync(db, authorizationId, cancellationToken);
        linked.ExpenseReportId = report.Id;
        linked.UpdatedAtUtc = workflow.UtcNow;
        workflow.Audit.Record(db, EntityType, linked.Id, "ExpenseReportLinked", null, new { ExpenseReportId = report.Id });
        await db.SaveChangesAsync(cancellationToken);
        return report;
    }

    public async Task<TravelAuthorizationDetailDto> LinkExpenseReportAsync(Guid authorizationId, LinkExpenseReportRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ta = await FindOwnAsync(db, authorizationId, cancellationToken);
        EnsureApproved(ta);
        if (ta.ExpenseReportId is { } current
            && await db.Set<ExpenseReport>().AnyAsync(r => r.Id == current && (r.Status == OdcStatus.Submitted || r.Status == OdcStatus.Approved), cancellationToken))
        {
            throw new InvalidOperationException("The linked expense report is submitted; it stays with this trip.");
        }

        if (request?.ExpenseReportId is { } reportId)
        {
            var report = await db.Set<ExpenseReport>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Expense report", reportId);
            if (report.EmployeeId != ta.EmployeeId)
            {
                throw new ArgumentException("Link one of your own expense reports.");
            }

            if (await db.Set<TravelAuthorization>().AnyAsync(t => t.ExpenseReportId == reportId && t.Id != ta.Id, cancellationToken))
            {
                throw new ArgumentException("That expense report already belongs to another trip.");
            }
        }

        var before = ta.ExpenseReportId;
        ta.ExpenseReportId = request?.ExpenseReportId;
        ta.UpdatedAtUtc = workflow.UtcNow;
        workflow.Audit.Record(db, EntityType, ta.Id, ta.ExpenseReportId is null ? "ExpenseReportUnlinked" : "ExpenseReportLinked",
            new { ExpenseReportId = before }, new { ta.ExpenseReportId });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, ta, cancellationToken);
    }

    public async Task<TravelComparisonDto?> GetComparisonForReportAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ta = await db.Set<TravelAuthorization>().AsNoTracking().FirstOrDefaultAsync(t => t.ExpenseReportId == expenseReportId, cancellationToken);
        return ta is null ? null : await TravelComparison.BuildAsync(db, ta, expenseReportId, Tolerance, cancellationToken);
    }

    public async Task<TravelComparisonDto> SetLineCategoryAsync(Guid expenseReportId, Guid lineId, SetTravelLineCategoryRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await db.Set<ExpenseReport>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == expenseReportId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Expense report", expenseReportId);
        await ExpenseReportService.EnsureOwnerAsync(db, workflow, report, cancellationToken);
        OdcWorkflow.EnsureEditable(report.Status, "expense report");
        var ta = await db.Set<TravelAuthorization>().AsNoTracking().FirstOrDefaultAsync(t => t.ExpenseReportId == expenseReportId, cancellationToken)
            ?? throw new ArgumentException("This expense report isn't linked to a trip.");
        if (!await db.Set<OdcEntry>().AnyAsync(o => o.Id == lineId && o.ExpenseReportId == expenseReportId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Expense line", lineId);
        }

        var row = await db.Set<TravelLineCategory>().FirstOrDefaultAsync(c => c.OdcEntryId == lineId, cancellationToken);
        if (row is null)
        {
            db.Set<TravelLineCategory>().Add(new TravelLineCategory { OdcEntryId = lineId, Category = (TravelCostCategory)(int)request.Category });
        }
        else
        {
            row.Category = (TravelCostCategory)(int)request.Category;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await TravelComparison.BuildAsync(db, ta, expenseReportId, Tolerance, cancellationToken);
    }

    public async Task<TravelAuthorizationDetailDto> RecordAdvanceAsync(Guid authorizationId, RecordTravelAdvanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0)
        {
            throw new ArgumentException("The advance must be more than zero.");
        }

        Money(request.Amount, "The advance");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ta = await FindAsync(db, authorizationId, cancellationToken);
        EnsureApproved(ta);
        if (request.Amount > ta.EstimatedTotal)
        {
            throw new ArgumentException($"The advance can't be more than the trip's authorized {ta.EstimatedTotal:C}.");
        }

        var before = new { ta.AdvanceIssuedAmount, ta.AdvanceIssuedOn };
        ta.AdvanceIssuedAmount = request.Amount;
        ta.AdvanceIssuedOn = request.IssuedOn;
        ta.AdvanceIssuedBy = workflow.Actor;
        ta.AdvanceReference = Text(request.Reference, 100);
        ta.UpdatedAtUtc = workflow.UtcNow;
        workflow.Audit.Record(db, EntityType, ta.Id, "AdvanceIssued", before, new { ta.AdvanceIssuedAmount, ta.AdvanceIssuedOn, ta.AdvanceReference });
        await db.SaveChangesAsync(cancellationToken);
        var owner = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == ta.EmployeeId).Select(e => e.Email).FirstAsync(cancellationToken);
        if (!workflow.IsMe(owner))
        {
            await notifications.PublishAsync(owner, TravelNotificationKinds.Decided, $"Travel advance issued for {ta.Title}",
                $"{request.Amount:C} on {request.IssuedOn:MMM d}. It's deducted from what your expense report reimburses.", link: $"travel/{ta.Id}");
        }

        return await DetailAsync(db, ta, cancellationToken);
    }

    public Task<TravelPolicyDto> GetPolicyAsync(CancellationToken cancellationToken = default) => Task.FromResult(new TravelPolicyDto(Tolerance));

    // ---- Decisions ----

    private async Task<TravelAuthorizationDetailDto> DecideAsync(Guid id, bool approve, string? comment, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var ta = await FindAsync(db, id, ct);
        var owner = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == ta.EmployeeId, ct);
        if (workflow.IsMe(owner.Email) || workflow.IsMe(ta.SubmittedBy))
        {
            throw new WorkbenchForbiddenException("You can't approve or reject your own trip.");
        }

        if (ta.Status != TravelAuthorizationStatus.Submitted)
        {
            throw new InvalidOperationException($"This trip request is {TravelLabels.State((TravelAuthorizationState)(int)ta.Status).ToLowerInvariant()}, not waiting for approval.");
        }

        var note = Text(comment, 1000);
        if (!approve && note is null)
        {
            throw new ArgumentException("Give a reason for sending the trip request back.");
        }

        var check = await workflow.Approvals.CheckAsync(db, Type, ta.Id, workflow.Actor, _ => Task.FromResult(Subject(ta, owner)), ct);
        if (!check.CanDecide)
        {
            throw new WorkbenchForbiddenException("Nothing on this trip request is waiting on you.");
        }

        var outcome = await workflow.Approvals.DecideAsync(db, check, approve ? ApprovalDecisionKind.Approve : ApprovalDecisionKind.Reject, workflow.Actor, note, ct);
        var now = workflow.UtcNow;
        if (approve && !outcome.IsComplete)
        {
            workflow.Audit.Record(db, EntityType, ta.Id, "StepApproved", null, null, note);
            await db.SaveChangesAsync(ct);
            await notifications.PublishAsync(outcome.NotifyEmails, TravelNotificationKinds.NeedsApproval, $"{owner.DisplayName} requested a trip: {ta.Title}",
                $"Estimated {ta.EstimatedTotal:C}.", link: $"travel/{ta.Id}");
            return await DetailAsync(db, ta, ct);
        }

        if (approve)
        {
            ta.Status = TravelAuthorizationStatus.Approved;
            ta.ApprovedBy = workflow.Actor;
            ta.ApprovedAtUtc = now;
        }
        else
        {
            ta.Status = TravelAuthorizationStatus.Rejected;
            ta.RejectedBy = workflow.Actor;
            ta.RejectedAtUtc = now;
            ta.RejectionReason = note;
        }

        ta.UpdatedAtUtc = now;
        workflow.Audit.Record(db, EntityType, ta.Id, approve ? "Approved" : "Rejected", new { Status = "Submitted" }, new { Status = ta.Status.ToString() }, note);
        await db.SaveChangesAsync(ct);
        await notifications.PublishAsync(owner.Email, TravelNotificationKinds.Decided,
            approve ? $"Your trip is approved: {ta.Title}" : $"Your trip request was sent back: {ta.Title}",
            approve ? $"Authorized {ta.EstimatedTotal:C}. Start the trip's expense report from the request." : note, link: $"travel/{ta.Id}");
        return await DetailAsync(db, ta, ct);
    }

    internal static ApprovalSubject Subject(TravelAuthorization ta, Employee owner) =>
        new(Type, ta.Id, $"{owner.DisplayName}: {ta.Title}", ta.SubmittedBy ?? owner.Email, $"travel/{ta.Id}", ta.EstimatedTotal,
            $"{ta.Number} · {TravelEstimator.Summary(ta.Destinations)} · {ta.TripStart:MMM d}–{ta.TripEnd:MMM d}", owner.Id,
            [new ApprovalCharge(ta.ProjectId, ta.EstimatedTotal, owner.OrgUnitId, "Travel")]);

    // ---- Helpers ----

    private async Task ApplyAsync(IWorkbenchDbContext db, TravelAuthorization ta, SaveTravelAuthorizationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = Text(request.Title, 200) ?? throw new ArgumentException("Give the trip a title.");
        var purpose = Text(request.Purpose, 2000) ?? throw new ArgumentException("Say what the trip is for (its business purpose).");
        Money(request.EstimatedAirfare, "Airfare");
        Money(request.EstimatedRentalCar, "Rental car");
        Money(request.EstimatedOther, "Other costs");
        Money(request.AdvanceRequested, "The advance requested");
        var stops = Clean(request.Destinations);
        PerDiemCalculator.Validate(stops);
        var wbs = await ValidateChargeAsync(db, ta.EmployeeId, request.WbsElementId, stops[0].ArriveOn, ct);
        var estimate = TravelEstimator.Estimate(await PerDiemTables.LoadAsync(db, ct), stops, request.EstimatedAirfare, request.EstimatedRentalCar, request.EstimatedOther);
        if (request.AdvanceRequested > estimate.Total)
        {
            throw new ArgumentException($"The advance can't be more than the trip's estimated {estimate.Total:C}.");
        }

        ta.Title = title;
        ta.Purpose = purpose;
        ta.ProjectId = wbs.ProjectId;
        ta.WbsElementId = wbs.Id;
        ta.TripStart = stops[0].ArriveOn;
        ta.TripEnd = stops[^1].DepartOn;
        ta.EstimatedAirfare = request.EstimatedAirfare;
        ta.EstimatedRentalCar = request.EstimatedRentalCar;
        ta.EstimatedOther = request.EstimatedOther;
        ta.OtherDescription = Text(request.OtherDescription, 500);
        ta.EstimatedLodging = estimate.Lodging;
        ta.EstimatedMie = estimate.Mie;
        ta.AdvanceRequested = request.AdvanceRequested;

        db.Set<TravelAuthorizationDestination>().RemoveRange(ta.Destinations);
        ta.Destinations.Clear();
        for (var i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            var stop = new TravelAuthorizationDestination
            {
                TravelAuthorizationId = ta.Id, Order = i, Kind = (PerDiemRateKind)(int)s.Kind, Country = s.Country, State = s.State, Destination = s.Destination,
                ArriveOn = s.ArriveOn, DepartOn = s.DepartOn,
            };
            ta.Destinations.Add(stop);
            db.Set<TravelAuthorizationDestination>().Add(stop); // Explicitly added: its id is already set, so EF would take it for an existing row.
        }
    }

    /// <summary>The trip's charge code: an open, chargeable leaf the traveler may charge on the first day of the trip.</summary>
    private async Task<WbsElement> ValidateChargeAsync(IWorkbenchDbContext db, Guid employeeId, Guid wbsElementId, DateOnly on, CancellationToken ct)
    {
        var wbs = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == wbsElementId, ct)
            ?? throw new ArgumentException("Choose the project charge code the trip is for.");
        var codes = await projects.ListChargeCodesAsync(employeeId, on, ct);
        if (codes.All(c => c.WbsElementId != wbs.Id))
        {
            throw new ArgumentException($"WBS {wbs.Code} isn't one of your charge codes on {on:yyyy-MM-dd}.");
        }

        return wbs;
    }

    private static List<TravelDestinationDto> Clean(IReadOnlyList<TravelDestinationDto>? stops) =>
        (stops ?? []).Select(s => s with
        {
            Country = string.IsNullOrWhiteSpace(s.Country) ? "US" : s.Country.Trim(),
            State = (s.State ?? "").Trim().ToUpperInvariant() is { Length: 2 } st && s.Kind == PerDiemTable.Conus ? st : (s.State ?? "").Trim(),
            Destination = (s.Destination ?? "").Trim(),
        }).ToList();

    private static void Money(decimal value, string what)
    {
        if (value < 0)
        {
            throw new ArgumentException($"{what} can't be negative.");
        }

        if (decimal.Round(value, 2) != value)
        {
            throw new ArgumentException($"{what} can have at most two decimal places.");
        }
    }

    private static string? Text(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var t = value.Trim();
        return t.Length > max ? throw new ArgumentException($"Keep it under {max} characters.") : t;
    }

    private static void EnsureEditable(TravelAuthorization ta)
    {
        if (ta.Status is not (TravelAuthorizationStatus.Draft or TravelAuthorizationStatus.Rejected))
        {
            throw new InvalidOperationException(ta.Status switch
            {
                TravelAuthorizationStatus.Submitted => "The trip request is waiting for approval and can't be changed.",
                TravelAuthorizationStatus.Approved => "The trip is approved and locked. Cancel it and request a new one to change it.",
                _ => "The trip request is cancelled.",
            });
        }
    }

    private static void EnsureApproved(TravelAuthorization ta)
    {
        if (ta.Status != TravelAuthorizationStatus.Approved)
        {
            throw new InvalidOperationException("The trip isn't approved yet.");
        }
    }

    private static IQueryable<TravelAuthorization> Query(IWorkbenchDbContext db) => db.Set<TravelAuthorization>().AsNoTracking().Include(t => t.Destinations);

    private static async Task<TravelAuthorization> FindAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await TravelEstimator.FindAsync(db, id, ct) ?? throw WorkbenchNotFoundException.For("Trip request", id);

    private async Task<TravelAuthorization> FindOwnAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var ta = await FindAsync(db, id, ct);
        var me = await workflow.RequireCurrentEmployeeAsync(db, ct);
        if (ta.EmployeeId != me.Id)
        {
            throw new WorkbenchForbiddenException("Only the traveler can change their trip request.");
        }

        return ta;
    }

    private static async Task<string> NextNumberAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        return (await Customization.DocumentNumbers.NextInUnitOfWorkAsync(db, Customization.DocumentNumberTypes.TravelAuthorization, null, DateOnly.FromDateTime(DateTime.UtcNow), ct)).Number;
    }

    private static object Snapshot(TravelAuthorization t) => new
    {
        t.Number, t.EmployeeId, t.Title, t.Purpose, t.WbsElementId, t.TripStart, t.TripEnd, t.EstimatedAirfare, t.EstimatedLodging, t.EstimatedMie,
        t.EstimatedRentalCar, t.EstimatedOther, t.AdvanceRequested, Status = t.Status.ToString(), Destinations = TravelEstimator.Summary(t.Destinations),
    };

    private async Task<TravelAuthorizationDetailDto> DetailAsync(IWorkbenchDbContext db, TravelAuthorization ta, CancellationToken ct)
    {
        var dto = (await ToDtosAsync(db, [ta], ct))[0];
        var stops = TravelEstimator.Stops(ta);
        var estimate = stops.Count == 0
            ? new TravelEstimateDto(ta.EstimatedAirfare, 0, 0, ta.EstimatedRentalCar, ta.EstimatedOther, ta.EstimatedTotal, 0, [], [])
            : await TravelEstimator.EstimateAsync(db, stops, ta.EstimatedAirfare, ta.EstimatedRentalCar, ta.EstimatedOther, ct);
        var me = await workflow.FindCurrentEmployeeAsync(db, ct);
        var canDecide = ta.Status == TravelAuthorizationStatus.Submitted && !workflow.IsMe(ta.SubmittedBy) && me?.Id != ta.EmployeeId
            && await workflow.Approvals.CanDecideAsync(db, Type, ta.Id, workflow.Actor, null, ct);
        return new TravelAuthorizationDetailDto(dto, stops, estimate, me?.Id == ta.EmployeeId, canDecide);
    }

    internal static async Task<IReadOnlyList<TravelAuthorizationDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyList<TravelAuthorization> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var employeeIds = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var wbsIds = rows.Select(r => r.WbsElementId).Distinct().ToList();
        var projectIds = rows.Select(r => r.ProjectId).Distinct().ToList();
        var reportIds = rows.Where(r => r.ExpenseReportId != null).Select(r => r.ExpenseReportId!.Value).ToList();
        var names = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var projectCodes = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        var reportRows = await db.Set<ExpenseReport>().AsNoTracking().Where(r => reportIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        return rows.Select(t =>
        {
            var w = wbs.GetValueOrDefault(t.WbsElementId);
            var report = t.ExpenseReportId is { } rid ? reportRows.GetValueOrDefault(rid) : null;
            return new TravelAuthorizationDto(t.Id, t.Number, t.EmployeeId, names.GetValueOrDefault(t.EmployeeId) ?? "", t.Title, t.Purpose,
                t.ProjectId, projectCodes.GetValueOrDefault(t.ProjectId) ?? "", t.WbsElementId, w?.Code ?? "", w?.Name ?? "", t.TripStart, t.TripEnd,
                TravelEstimator.Summary(t.Destinations), (TravelAuthorizationState)(int)t.Status, t.EstimatedAirfare, t.EstimatedLodging, t.EstimatedMie,
                t.EstimatedRentalCar, t.EstimatedOther, t.OtherDescription, t.EstimatedTotal, t.AdvanceRequested, t.AdvanceIssuedAmount, t.AdvanceIssuedOn,
                t.AdvanceReference, report?.Id, report?.Title, report is null ? null : (OdcApprovalState)(int)report.Status, t.CreatedAtUtc, t.SubmittedBy,
                t.SubmittedAtUtc, t.ApprovedBy, t.ApprovedAtUtc, t.RejectionReason, t.CancellationReason);
        }).ToList();
    }
}
