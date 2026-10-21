using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Planning;
using Workbench.Domain.Accounting;
using Workbench.Domain.Planning;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Planning;

public static class PlanningNotificationKinds
{
    public const string Assigned = "budget.assigned";
    public const string Submitted = "budget.submitted";
    public const string Approved = "budget.approved";
    public const string Returned = "budget.returned";
}

/// <summary>
/// The corporate planning cycle: Finance opens a cycle for a fiscal year with an owner per org unit; owners fill in
/// their unit's lines and submit them through the "Org unit budget" approval workflow; the final approval rolls the
/// unit's lines into the cycle's corporate version (replacing that unit's lines); adopting the cycle locks the version.
/// Who may open, view and edit is checked by the permission decorator; who may decide is the workflow's.
/// </summary>
public sealed class PlanningCycleService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    AccessControl access,
    INotificationPublisher notifications,
    TimeProvider clock,
    ApprovalEngine? approvals = null) : IPlanningCycleService
{
    public const string AuditEntityType = nameof(BudgetSubmission);
    public const string CycleAuditEntityType = nameof(PlanningCycle);

    private readonly ApprovalEngine _approvals = approvals ?? new ApprovalEngine(clock);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<PlanningCycleDto>> ListCyclesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var cycles = await db.Set<PlanningCycle>().AsNoTracking().ToListAsync(cancellationToken);
        var result = new List<PlanningCycleDto>();
        foreach (var cycle in cycles.OrderByDescending(c => c.OpenedAtUtc))
        {
            result.Add(await MapCycleAsync(db, cycle, cancellationToken));
        }

        return result;
    }

    public async Task<PlanningCycleDto> GetCycleAsync(Guid cycleId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var cycle = await db.Set<PlanningCycle>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == cycleId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Planning cycle", cycleId);
        return await MapCycleAsync(db, cycle, cancellationToken);
    }

    public async Task<PlanningCycleDto> OpenCycleAsync(OpenPlanningCycleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (year, periods) = await BudgetMath.YearAsync(db, request.FiscalYear, cancellationToken);
        if (await db.Set<PlanningCycle>().AnyAsync(c => c.FiscalYearId == year.Id && c.Status == PlanningCycleStatus.Open, cancellationToken))
        {
            throw new ArgumentException($"FY{year.Year} already has an open planning cycle.");
        }

        var assignments = request.Assignments ?? [];
        if (assignments.Count == 0)
        {
            throw new ArgumentException("Assign an owner to at least one org unit.");
        }

        if (assignments.Select(a => a.OrgUnitId).Distinct().Count() != assignments.Count)
        {
            throw new ArgumentException("Each org unit can be in the cycle once.");
        }

        var name = PlanningMapping.Clean(request.Name, 200, "Name", required: true);
        var version = await GlBudgetService.CreateVersionAsync(db, new CreateBudgetVersionRequest(year.Year, BudgetVersionKind.Original,
            string.IsNullOrWhiteSpace(request.VersionName) ? $"FY{year.Year} {name}" : request.VersionName, $"Rolled up from the {name} planning cycle."), audit, UtcNow, cancellationToken);
        var cycle = new PlanningCycle
        {
            FiscalYearId = year.Id, Name = name, Instructions = string.IsNullOrWhiteSpace(request.Instructions) ? null : PlanningMapping.Clean(request.Instructions, 4000, "Instructions"),
            DueDate = request.DueDate, VersionId = version.Id, OpenedBy = audit.Actor, OpenedAtUtc = UtcNow,
        };
        db.Set<PlanningCycle>().Add(cycle);

        // Starting lines: another version's amounts for each unit (periods matched by number).
        List<GlBudgetLine> starting = [];
        Dictionary<Guid, Guid> periodMap = [];
        if (request.CopyFromVersionId is { } sourceId)
        {
            var source = await db.Set<GlBudgetVersion>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == sourceId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Budget version", sourceId);
            periodMap = await GlBudgetService.PeriodMapAsync(db, source.FiscalYearId, periods, cancellationToken);
            starting = await db.Set<GlBudgetLine>().AsNoTracking().Where(l => l.VersionId == sourceId).ToListAsync(cancellationToken);
        }

        var notify = new List<(BudgetSubmission Submission, string Email, string Org)>();
        foreach (var a in assignments)
        {
            var (org, owner) = await RequireAssignmentAsync(db, a, cancellationToken);
            var submission = new BudgetSubmission { CycleId = cycle.Id, OrgUnitId = org.Id, OwnerEmployeeId = owner.Id, UpdatedAtUtc = UtcNow };
            foreach (var line in starting.Where(l => l.OrgUnitId == org.Id && periodMap.ContainsKey(l.FiscalPeriodId)))
            {
                submission.Lines.Add(new BudgetSubmissionLine { SubmissionId = submission.Id, AccountId = line.AccountId, FiscalPeriodId = periodMap[line.FiscalPeriodId], Amount = line.Amount });
            }

            if (submission.Lines.Count > 0)
            {
                submission.Status = BudgetSubmissionStatus.Drafting;
            }

            db.Set<BudgetSubmission>().Add(submission);
            notify.Add((submission, owner.Email, $"{org.Code} {org.Name}"));
        }

        audit.Record(db, CycleAuditEntityType, cycle.Id, "Opened", after: new { cycle.Name, FiscalYear = year.Year, Version = version.Name, Units = assignments.Count, request.CopyFromVersionId });
        await db.SaveChangesAsync(cancellationToken);
        foreach (var (submission, email, org) in notify.Where(n => n.Email != audit.Actor))
        {
            await notifications.PublishAsync(email, PlanningNotificationKinds.Assigned, $"Budget for {org}: FY{year.Year}",
                $"{audit.Actor} opened {cycle.Name}{(cycle.DueDate is { } due ? $"; due {due:MMM d}" : "")}.", link: Link(submission.Id));
        }

        return await MapCycleAsync(db, cycle, cancellationToken);
    }

    public async Task<PlanningCycleDto> AssignOwnerAsync(Guid cycleId, CycleAssignmentInput assignment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var cycle = await OpenCycleAsync(db, cycleId, cancellationToken);
        var (org, owner) = await RequireAssignmentAsync(db, assignment, cancellationToken);
        var submission = await db.Set<BudgetSubmission>().FirstOrDefaultAsync(s => s.CycleId == cycleId && s.OrgUnitId == org.Id, cancellationToken);
        if (submission is null)
        {
            submission = new BudgetSubmission { CycleId = cycleId, OrgUnitId = org.Id, OwnerEmployeeId = owner.Id, UpdatedAtUtc = UtcNow };
            db.Set<BudgetSubmission>().Add(submission);
        }
        else if (submission.Status == BudgetSubmissionStatus.Approved)
        {
            throw new InvalidOperationException($"{org.Code}'s budget is already approved.");
        }
        else
        {
            submission.OwnerEmployeeId = owner.Id;
            submission.UpdatedAtUtc = UtcNow;
        }

        audit.Record(db, AuditEntityType, submission.Id, "OwnerAssigned", after: new { Org = org.Code, Owner = owner.Email });
        await db.SaveChangesAsync(cancellationToken);
        if (owner.Email != audit.Actor)
        {
            await notifications.PublishAsync(owner.Email, PlanningNotificationKinds.Assigned, $"Budget for {org.Code} {org.Name}", $"You own this unit's budget in {cycle.Name}.", link: Link(submission.Id));
        }

        return await MapCycleAsync(db, cycle, cancellationToken);
    }

    public async Task<PlanningCycleDto> AdoptAsync(Guid cycleId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var cycle = await OpenCycleAsync(db, cycleId, cancellationToken);
        var statuses = await db.Set<BudgetSubmission>().Where(s => s.CycleId == cycleId).Select(s => s.Status).ToListAsync(cancellationToken);
        if (statuses.Count == 0 || statuses.Any(s => s != BudgetSubmissionStatus.Approved))
        {
            throw new InvalidOperationException($"Every org unit's budget must be approved before the cycle is adopted ({statuses.Count(s => s == BudgetSubmissionStatus.Approved)} of {statuses.Count} are).");
        }

        var version = await db.Set<GlBudgetVersion>().FirstAsync(v => v.Id == cycle.VersionId, cancellationToken);
        version.EnsureEditable();
        GlBudgetService.Adopt(db, version, audit, UtcNow);
        cycle.Status = PlanningCycleStatus.Adopted;
        cycle.AdoptedBy = audit.Actor;
        cycle.AdoptedAtUtc = UtcNow;
        audit.Record(db, CycleAuditEntityType, cycle.Id, "Adopted", after: new { Version = version.Name });
        await db.SaveChangesAsync(cancellationToken);
        return await MapCycleAsync(db, cycle, cancellationToken);
    }

    public async Task<IReadOnlyList<BudgetSubmissionSummaryDto>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        var employeeId = await access.GetEmployeeIdAsync(cancellationToken);
        if (employeeId is null)
        {
            return [];
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var mine = await db.Set<BudgetSubmission>().AsNoTracking().Include(s => s.Lines).Where(s => s.OwnerEmployeeId == employeeId).ToListAsync(cancellationToken);
        return (await SummariesAsync(db, mine, cancellationToken)).OrderByDescending(s => s.FiscalYear).ThenBy(s => s.OrgUnitCode, StringComparer.Ordinal).ToList();
    }

    public async Task<BudgetSubmissionDto> GetSubmissionAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var submission = await db.Set<BudgetSubmission>().AsNoTracking().Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Budget submission", submissionId);
        return await MapAsync(db, submission, cancellationToken);
    }

    public async Task<BudgetSubmissionDto> SaveSubmissionCellsAsync(Guid submissionId, SaveSubmissionCellsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (submission, periods) = await EditableAsync(db, submissionId, cancellationToken);
        var cells = request.Cells ?? [];
        if (cells.Count > 5000)
        {
            throw new ArgumentException("Save at most 5,000 cells at a time.");
        }

        await SetCellsAsync(db, submission, periods, cells.Select(c => (c.AccountId, c.FiscalPeriodId, c.Amount)).ToList(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(db, submission, cancellationToken);
    }

    public async Task<BudgetSubmissionDto> SpreadSubmissionAsync(Guid submissionId, SpreadSubmissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (submission, periods) = await EditableAsync(db, submissionId, cancellationToken);
        var cycle = await db.Set<PlanningCycle>().AsNoTracking().FirstAsync(c => c.Id == submission.CycleId, cancellationToken);
        var year = await db.Set<FiscalYear>().AsNoTracking().FirstAsync(y => y.Id == cycle.FiscalYearId, cancellationToken);
        var prior = request.Method == SpreadMethod.PriorYearPattern
            ? await BudgetMath.PriorYearPatternAsync(db, year, periods.Count, request.AccountId, submission.OrgUnitId, cancellationToken)
            : null;
        var amounts = BudgetMath.Spread(request.AnnualAmount, periods.Count, request.Method, prior, request.Profile);
        await SetCellsAsync(db, submission, periods, periods.Select((p, i) => (request.AccountId, p.Id, amounts[i])).ToList(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(db, submission, cancellationToken);
    }

    public async Task<BudgetSubmissionDto> SubmitAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (submission, _) = await EditableAsync(db, submissionId, cancellationToken);
        if (submission.Lines.Count == 0)
        {
            throw new ArgumentException("Enter at least one amount before submitting.");
        }

        submission.Status = BudgetSubmissionStatus.Submitted;
        submission.Revision++;
        submission.SubmittedBy = audit.Actor;
        submission.SubmittedAtUtc = UtcNow;
        submission.DecidedBy = null;
        submission.DecidedAtUtc = null;
        submission.DecisionComment = null;
        submission.UpdatedAtUtc = UtcNow;
        var route = await _approvals.StartAsync(db, await SubjectAsync(db, submission, cancellationToken), cancellationToken);
        audit.Record(db, AuditEntityType, submission.Id, "Submitted", after: new { submission.Revision, Total = submission.Lines.Sum(l => l.Amount), Lines = submission.Lines.Count });
        await db.SaveChangesAsync(cancellationToken);
        var org = await db.Set<OrgUnit>().AsNoTracking().FirstAsync(o => o.Id == submission.OrgUnitId, cancellationToken);
        await notifications.PublishAsync(route.NotifyEmails, PlanningNotificationKinds.Submitted, $"{audit.Actor} submitted the {org.Code} budget",
            $"{submission.Lines.Sum(l => l.Amount):C0} across {submission.Lines.Select(l => l.AccountId).Distinct().Count()} account(s)", link: Link(submission.Id));
        return await MapAsync(db, submission, cancellationToken);
    }

    public async Task<BudgetSubmissionDto> RecallAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var submission = await LoadAsync(db, submissionId, cancellationToken);
        if (submission.Status != BudgetSubmissionStatus.Submitted)
        {
            throw new InvalidOperationException("Only a budget waiting for approval can be recalled.");
        }

        submission.Status = BudgetSubmissionStatus.Drafting;
        submission.UpdatedAtUtc = UtcNow;
        await _approvals.CancelAsync(db, ApprovalDocumentTypes.BudgetSubmission, submission.Id, audit.Actor, "Recalled", cancellationToken);
        audit.Record(db, AuditEntityType, submission.Id, "Recalled");
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(db, submission, cancellationToken);
    }

    public async Task<BudgetSubmissionDto> ApproveAsync(Guid submissionId, DecideSubmissionRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var submission = await LoadAsync(db, submissionId, cancellationToken);
        var check = await CheckAsync(db, submission, cancellationToken);
        var comment = string.IsNullOrWhiteSpace(request?.Comment) ? null : PlanningMapping.Clean(request.Comment, 1000, "Comment");

        // The version must accept the roll-up before anything is recorded.
        var cycle = await db.Set<PlanningCycle>().AsNoTracking().FirstAsync(c => c.Id == submission.CycleId, cancellationToken);
        (await db.Set<GlBudgetVersion>().AsNoTracking().FirstAsync(v => v.Id == cycle.VersionId, cancellationToken)).EnsureEditable();

        var outcome = await _approvals.DecideAsync(db, check, ApprovalDecisionKind.Approve, audit.Actor, comment, cancellationToken);
        var org = await db.Set<OrgUnit>().AsNoTracking().FirstAsync(o => o.Id == submission.OrgUnitId, cancellationToken);
        if (!outcome.IsComplete)
        {
            audit.Record(db, AuditEntityType, submission.Id, "StepApproved", after: new { Step = string.Join(", ", outcome.Decided.Select(t => t.StepName).Distinct()) }, reason: comment);
            await db.SaveChangesAsync(cancellationToken);
            await notifications.PublishAsync(outcome.NotifyEmails, PlanningNotificationKinds.Submitted, $"The {org.Code} budget is waiting on you", cycle.Name, link: Link(submission.Id));
            return await MapAsync(db, submission, cancellationToken);
        }

        await ApproveFinalAsync(db, submission, comment, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await NotifyOwnerAsync(db, submission, PlanningNotificationKinds.Approved, $"{audit.Actor} approved the {org.Code} budget", comment ?? $"It's rolled up into {cycle.Name}.", cancellationToken);
        return await MapAsync(db, submission, cancellationToken);
    }

    public async Task<BudgetSubmissionDto> ReturnAsync(Guid submissionId, DecideSubmissionRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var submission = await LoadAsync(db, submissionId, cancellationToken);
        var comment = string.IsNullOrWhiteSpace(request?.Comment) ? throw new ArgumentException("Say what to change when returning a budget.") : PlanningMapping.Clean(request.Comment, 1000, "Comment");
        var check = await CheckAsync(db, submission, cancellationToken);
        await _approvals.DecideAsync(db, check, ApprovalDecisionKind.Reject, audit.Actor, comment, cancellationToken);
        await _approvals.MarkReturnedAsync(db, ApprovalDocumentTypes.BudgetSubmission, submission.Id, audit.Actor, cancellationToken);
        submission.Status = BudgetSubmissionStatus.Returned;
        submission.DecidedBy = audit.Actor;
        submission.DecidedAtUtc = UtcNow;
        submission.DecisionComment = comment;
        submission.UpdatedAtUtc = UtcNow;
        audit.Record(db, AuditEntityType, submission.Id, "Returned", reason: comment);
        await db.SaveChangesAsync(cancellationToken);
        var org = await db.Set<OrgUnit>().AsNoTracking().FirstAsync(o => o.Id == submission.OrgUnitId, cancellationToken);
        await NotifyOwnerAsync(db, submission, PlanningNotificationKinds.Returned, $"{audit.Actor} returned the {org.Code} budget for changes", comment, cancellationToken);
        return await MapAsync(db, submission, cancellationToken);
    }

    // ---- Roll-up ----

    /// <summary>The final approval: the unit's lines replace its lines in the cycle's version.</summary>
    private async Task ApproveFinalAsync(IWorkbenchDbContext db, BudgetSubmission submission, string? comment, CancellationToken ct)
    {
        var cycle = await db.Set<PlanningCycle>().AsNoTracking().FirstAsync(c => c.Id == submission.CycleId, ct);
        var version = await db.Set<GlBudgetVersion>().FirstAsync(v => v.Id == cycle.VersionId, ct);
        version.EnsureEditable();
        var periods = await BudgetMath.PeriodsAsync(db, version.FiscalYearId, ct);
        var current = await db.Set<GlBudgetLine>().Where(l => l.VersionId == version.Id && l.OrgUnitId == submission.OrgUnitId).ToListAsync(ct);
        var cells = current.Select(l => (l.AccountId, l.OrgUnitId, l.FiscalPeriodId, 0m))
            .Concat(submission.Lines.Select(l => (l.AccountId, submission.OrgUnitId, l.FiscalPeriodId, l.Amount)))
            .GroupBy(c => (c.AccountId, c.OrgUnitId, c.FiscalPeriodId)).Select(g => g.Last()).ToList();
        await GlBudgetService.SetLinesAsync(db, version, periods, cells, audit.Actor, UtcNow, ct);
        submission.Status = BudgetSubmissionStatus.Approved;
        submission.DecidedBy = audit.Actor;
        submission.DecidedAtUtc = UtcNow;
        submission.DecisionComment = comment;
        submission.UpdatedAtUtc = UtcNow;
        audit.Record(db, AuditEntityType, submission.Id, "Approved", after: new { Version = version.Name, Total = submission.Lines.Sum(l => l.Amount) }, reason: comment);
        audit.Record(db, GlBudgetService.AuditEntityType, version.Id, "RolledUp", after: new { submission.OrgUnitId, Lines = submission.Lines.Count, Total = submission.Lines.Sum(l => l.Amount) });
    }

    // ---- Helpers ----

    private async Task<ApprovalCheck> CheckAsync(IWorkbenchDbContext db, BudgetSubmission submission, CancellationToken ct)
    {
        if (submission.Status != BudgetSubmissionStatus.Submitted)
        {
            throw new InvalidOperationException("This budget isn't waiting for approval.");
        }

        var check = await _approvals.CheckAsync(db, ApprovalDocumentTypes.BudgetSubmission, submission.Id, audit.Actor, c => SubjectAsync(db, submission, c), ct);
        return check.CanDecide ? check : throw new WorkbenchForbiddenException("This budget isn't waiting on you.");
    }

    internal static async Task<ApprovalSubject> SubjectAsync(IWorkbenchDbContext db, BudgetSubmission s, CancellationToken ct)
    {
        var org = await db.Set<OrgUnit>().AsNoTracking().FirstAsync(o => o.Id == s.OrgUnitId, ct);
        var cycle = await db.Set<PlanningCycle>().AsNoTracking().FirstAsync(c => c.Id == s.CycleId, ct);
        var year = await db.Set<FiscalYear>().AsNoTracking().FirstAsync(y => y.Id == cycle.FiscalYearId, ct);
        var ids = s.Lines.Select(l => l.AccountId).Distinct().ToList();
        var types = await db.Set<Account>().AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Type, ct);
        var revenue = s.Lines.Where(l => types.GetValueOrDefault(l.AccountId) == AccountType.Revenue).Sum(l => l.Amount);
        var expense = s.Lines.Where(l => types.GetValueOrDefault(l.AccountId) == AccountType.Expense).Sum(l => l.Amount);
        var submitter = s.SubmittedBy ?? "";
        var submitterEmployee = submitter.Length == 0 ? null : await EmployeeLinks.FindForUserAsync(db, submitter, ct);
        return new ApprovalSubject(ApprovalDocumentTypes.BudgetSubmission, s.Id, $"{org.Code} {org.Name}: FY{year.Year} budget", submitter, Link(s.Id),
            expense + revenue, $"{cycle.Name} · revenue {revenue:C0}, expenses {expense:C0}", submitterEmployee?.Id,
            [new ApprovalCharge(null, expense + revenue, org.Id, "Budget")]);
    }

    private static string Link(Guid submissionId) => $"planning/submissions/{submissionId}";

    private static async Task<PlanningCycle> OpenCycleAsync(IWorkbenchDbContext db, Guid cycleId, CancellationToken ct)
    {
        var cycle = await db.Set<PlanningCycle>().FirstOrDefaultAsync(c => c.Id == cycleId, ct) ?? throw WorkbenchNotFoundException.For("Planning cycle", cycleId);
        return cycle.Status == PlanningCycleStatus.Open ? cycle : throw new InvalidOperationException($"{cycle.Name} was adopted and is closed.");
    }

    private static async Task<(OrgUnit Org, Employee Owner)> RequireAssignmentAsync(IWorkbenchDbContext db, CycleAssignmentInput a, CancellationToken ct)
    {
        var org = await db.Set<OrgUnit>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == a.OrgUnitId, ct) ?? throw new ArgumentException("Choose an org unit.");
        var owner = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == a.OwnerEmployeeId, ct) ?? throw new ArgumentException($"Choose an owner for {org.Code}.");
        if (owner.TerminationDate is { } end && end < DateOnly.FromDateTime(DateTime.UtcNow))
        {
            throw new ArgumentException($"{owner.DisplayName} is no longer active; choose another owner for {org.Code}.");
        }

        return (org, owner);
    }

    private static async Task<BudgetSubmission> LoadAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<BudgetSubmission>().Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Budget submission", id);

    private static async Task<(BudgetSubmission Submission, List<FiscalPeriod> Periods)> EditableAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var submission = await LoadAsync(db, id, ct);
        var cycle = await db.Set<PlanningCycle>().AsNoTracking().FirstAsync(c => c.Id == submission.CycleId, ct);
        if (cycle.Status != PlanningCycleStatus.Open)
        {
            throw new InvalidOperationException($"{cycle.Name} was adopted; its budgets are locked.");
        }

        if (!submission.IsEditable)
        {
            throw new InvalidOperationException(submission.Status == BudgetSubmissionStatus.Submitted
                ? "This budget is waiting for approval. Recall it to make changes."
                : "This budget is approved and rolled up; it can't change.");
        }

        return (submission, await BudgetMath.PeriodsAsync(db, cycle.FiscalYearId, ct));
    }

    private async Task SetCellsAsync(IWorkbenchDbContext db, BudgetSubmission submission, IReadOnlyList<FiscalPeriod> periods,
        IReadOnlyList<(Guid AccountId, Guid PeriodId, decimal Amount)> cells, CancellationToken ct)
    {
        var budgetable = (await BudgetMath.BudgetableAccountsAsync(db, ct)).ToDictionary(a => a.Id);
        var periodIds = periods.Select(p => p.Id).ToHashSet();
        foreach (var (accountId, periodId, amount) in cells)
        {
            var account = BudgetMath.RequireBudgetable(budgetable, accountId, "A cell");
            if (!periodIds.Contains(periodId))
            {
                throw new ArgumentException("A cell's period isn't in the cycle's fiscal year.");
            }

            PlanningMapping.RequireCents(amount, account.Number);
            var line = submission.Lines.FirstOrDefault(l => l.AccountId == accountId && l.FiscalPeriodId == periodId);
            if (line is not null && amount == 0)
            {
                submission.Lines.Remove(line);
                db.Set<BudgetSubmissionLine>().Remove(line);
            }
            else if (line is not null)
            {
                line.Amount = amount;
            }
            else if (amount != 0)
            {
                line = new BudgetSubmissionLine { SubmissionId = submission.Id, AccountId = accountId, FiscalPeriodId = periodId, Amount = amount };
                submission.Lines.Add(line);
                db.Set<BudgetSubmissionLine>().Add(line);
            }
        }

        if (submission.Status == BudgetSubmissionStatus.NotStarted)
        {
            submission.Status = BudgetSubmissionStatus.Drafting;
        }

        submission.UpdatedAtUtc = UtcNow;
    }

    private async Task NotifyOwnerAsync(IWorkbenchDbContext db, BudgetSubmission submission, string kind, string title, string? body, CancellationToken ct)
    {
        var owner = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == submission.OwnerEmployeeId).Select(e => e.Email).FirstOrDefaultAsync(ct);
        await notifications.PublishAsync(new[] { owner, submission.SubmittedBy }.Where(e => e != null && e != audit.Actor), kind, title, body, link: Link(submission.Id));
    }

    private async Task<PlanningCycleDto> MapCycleAsync(IWorkbenchDbContext db, PlanningCycle cycle, CancellationToken ct)
    {
        var year = await db.Set<FiscalYear>().AsNoTracking().Where(y => y.Id == cycle.FiscalYearId).Select(y => y.Year).FirstAsync(ct);
        var version = await db.Set<GlBudgetVersion>().AsNoTracking().Where(v => v.Id == cycle.VersionId).Select(v => v.Name).FirstAsync(ct);
        var submissions = await db.Set<BudgetSubmission>().AsNoTracking().Include(s => s.Lines).Where(s => s.CycleId == cycle.Id).ToListAsync(ct);
        return new PlanningCycleDto(cycle.Id, year, cycle.Name, cycle.Instructions, cycle.DueDate, cycle.VersionId, version, cycle.Status.ToContract(),
            cycle.OpenedBy, cycle.OpenedAtUtc, cycle.AdoptedBy, cycle.AdoptedAtUtc,
            (await SummariesAsync(db, submissions, ct)).OrderBy(s => s.OrgUnitCode, StringComparer.Ordinal).ToList());
    }

    private static async Task<List<BudgetSubmissionSummaryDto>> SummariesAsync(IWorkbenchDbContext db, IReadOnlyList<BudgetSubmission> submissions, CancellationToken ct)
    {
        var cycleIds = submissions.Select(s => s.CycleId).Distinct().ToList();
        var cycles = await db.Set<PlanningCycle>().AsNoTracking().Where(c => cycleIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var years = await db.Set<FiscalYear>().AsNoTracking().ToDictionaryAsync(y => y.Id, y => y.Year, ct);
        var orgs = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Id, ct);
        var ownerIds = submissions.Select(s => s.OwnerEmployeeId).Distinct().ToList();
        var owners = await db.Set<Employee>().AsNoTracking().Where(e => ownerIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        return submissions.Select(s =>
        {
            var cycle = cycles[s.CycleId];
            var org = orgs.GetValueOrDefault(s.OrgUnitId);
            var owner = owners.GetValueOrDefault(s.OwnerEmployeeId);
            return new BudgetSubmissionSummaryDto(s.Id, s.CycleId, cycle.Name, years.GetValueOrDefault(cycle.FiscalYearId), s.OrgUnitId, org?.Code ?? "?", org?.Name ?? "",
                s.OwnerEmployeeId, owner?.DisplayName ?? "?", owner?.Email ?? "", s.Status.ToContract(), s.Lines.Sum(l => l.Amount), s.SubmittedAtUtc,
                s.DecidedBy, s.DecidedAtUtc, s.DecisionComment, cycle.DueDate);
        }).ToList();
    }

    private async Task<BudgetSubmissionDto> MapAsync(IWorkbenchDbContext db, BudgetSubmission s, CancellationToken ct)
    {
        var summary = (await SummariesAsync(db, [s], ct))[0];
        var cycle = await db.Set<PlanningCycle>().AsNoTracking().FirstAsync(c => c.Id == s.CycleId, ct);
        var year = await db.Set<FiscalYear>().AsNoTracking().FirstAsync(y => y.Id == cycle.FiscalYearId, ct);
        var periods = await BudgetMath.PeriodsAsync(db, cycle.FiscalYearId, ct);
        var accounts = await BudgetMath.BudgetableAccountsAsync(db, ct);
        var all = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        var index = periods.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var rows = s.Lines.GroupBy(l => l.AccountId).Select(g =>
        {
            var amounts = new decimal[periods.Count];
            foreach (var l in g)
            {
                if (index.TryGetValue(l.FiscalPeriodId, out var i))
                {
                    amounts[i] += l.Amount;
                }
            }

            var a = all[g.Key];
            return new SubmissionRowDto(a.Id, a.Number, a.Name, a.Type.ToContract(), amounts);
        }).OrderBy(r => r.AccountNumber, StringComparer.Ordinal).ToList();

        // Prior-year actuals per account for this org unit, as a guide.
        var prior = new Dictionary<Guid, decimal>();
        var priorYear = await db.Set<FiscalYear>().AsNoTracking().FirstOrDefaultAsync(y => y.Year == year.Year - 1, ct);
        if (priorYear is not null)
        {
            var priorIds = (await BudgetMath.PeriodsAsync(db, priorYear.Id, ct)).Select(p => p.Id).ToList();
            var lines = await (
                    from l in db.Set<JournalLine>().AsNoTracking()
                    join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                    where e.Status != JournalStatus.Draft && priorIds.Contains(e.FiscalPeriodId) && l.OrgUnitId == s.OrgUnitId
                    select new { l.AccountId, l.Debit, l.Credit })
                .ToListAsync(ct);
            foreach (var g in lines.GroupBy(l => l.AccountId).Where(g => all.ContainsKey(g.Key)))
            {
                var value = all[g.Key].Natural(g.Sum(l => l.Debit - l.Credit));
                if (value != 0 && all[g.Key].Type is AccountType.Revenue or AccountType.Expense)
                {
                    prior[g.Key] = value;
                }
            }
        }

        var employeeId = await access.GetEmployeeIdAsync(ct);
                var mayChange = s.OwnerEmployeeId == employeeId || await access.HasAsync(Permission.BudgetsManage, ct);
        var canEdit = s.IsEditable && cycle.Status == PlanningCycleStatus.Open && mayChange;
        var canDecide = s.Status == BudgetSubmissionStatus.Submitted
            && await _approvals.CanDecideAsync(db, ApprovalDocumentTypes.BudgetSubmission, s.Id, audit.Actor, c => SubjectAsync(db, s, c), ct);
        return new BudgetSubmissionDto(summary, cycle.Instructions, periods.Select(GlBudgetService.Period).ToList(), rows,
            accounts.Select(a => new AccountOptionDto(a.Id, a.Number, a.Name, a.Type.ToContract())).ToList(), prior, canEdit, canDecide,
            s.Status == BudgetSubmissionStatus.Submitted && s.OwnerEmployeeId == employeeId);
    }
}

/// <summary>Lets the approvals inbox decide org unit budgets through the planning service (so its checks and the roll-up run).</summary>
public sealed class BudgetSubmissionApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.BudgetSubmission;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken cancellationToken) =>
        services.GetRequiredService<IPlanningCycleService>().ApproveAsync(documentId, new DecideSubmissionRequest(comment), cancellationToken);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken cancellationToken) =>
        services.GetRequiredService<IPlanningCycleService>().ReturnAsync(documentId, new DecideSubmissionRequest(reason), cancellationToken);
}
