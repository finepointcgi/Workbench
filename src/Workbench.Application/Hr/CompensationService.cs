using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Approvals;
using Workbench.Application.Notifications;
using Workbench.Application.Payroll;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Hr;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Approvals;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;
using D = Workbench.Domain.Hr;

namespace Workbench.Application.Hr;

/// <summary>HR settings (configuration section <c>Hr</c>).</summary>
public sealed class HrOptions
{
    /// <summary>A pay change whose annualized increase is over this amount also needs Finance's approval.</summary>
    public decimal CompensationFinanceThreshold { get; set; } = 10_000m;
}

/// <summary>
/// Compensation changes. They don't keep their own rate table: the final approval appends an <see cref="EmployeePayRate"/>
/// (the effective-dated history payroll reads, <see cref="PayRates"/>) and mirrors it onto the payroll profile.
/// </summary>
public sealed class CompensationService(
    IWorkbenchDbContextFactory dbFactory,
    HrAccess hrAccess,
    AuditRecorder audit,
    INotificationPublisher notifications,
    IOptions<HrOptions> options,
    IPositionService positions,
    TimeProvider clock,
    ApprovalEngine? approvals = null,
    SecurityEventLog? securityLog = null,
    LaborRateChanges? laborRates = null) : ICompensationService
{
    public const string AuditEntityType = "CompensationChange";
    private readonly ApprovalEngine _approvals = approvals ?? new ApprovalEngine(clock);

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<CompensationHistoryDto> GetHistoryAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(employeeId);
        var profile = await db.Set<EmployeePayProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == employeeId, cancellationToken);
        var history = profile is null ? [] : await PayRates.HistoryAsync(db, profile, cancellationToken);
        var current = PayRates.InEffect(history, Today);
        var rates = history.AsEnumerable().Reverse()
            .Select(r => new PayRateDto(r.EffectiveFrom, (PayTypeKind)(int)r.PayType, r.AnnualSalary, r.HourlyRate, r.Reason, r.CreatedBy, r.CreatedAtUtc, ReferenceEquals(r, current)))
            .ToList();
        var changes = await db.Set<D.CompensationChange>().AsNoTracking().Where(c => c.EmployeeId == employeeId).ToListAsync(cancellationToken);
        var hrProfile = await db.Set<D.EmployeeHrProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == employeeId, cancellationToken);
        PositionDto? position = null;
        if (hrProfile?.PositionId is { } positionId)
        {
            position = await positions.GetAsync(positionId, cancellationToken);
        }

        return new CompensationHistoryDto(employeeId, employee.DisplayName, position, rates,
            await MapAsync(db, changes.OrderByDescending(c => c.CreatedAtUtc).ToList(), cancellationToken));
    }

    public async Task<IReadOnlyList<CompensationChangeDto>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<D.CompensationChange>().AsNoTracking().Where(c => c.Status == D.CompensationChangeStatus.Pending).ToListAsync(cancellationToken);
        return await MapAsync(db, rows.OrderByDescending(c => c.CreatedAtUtc).ToList(), cancellationToken);
    }

    public async Task<CompensationChangeDto> ProposeAsync(Guid employeeId, ProposeCompensationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(employeeId);
        if (viewer.IsSelf(employeeId))
        {
            throw new WorkbenchForbiddenException("You can't propose a change to your own pay.");
        }

        if (await db.Set<D.CompensationChange>().AnyAsync(c => c.EmployeeId == employeeId && c.Status == D.CompensationChangeStatus.Pending, cancellationToken))
        {
            throw new ArgumentException($"{employee.DisplayName} already has a pay change waiting for approval.");
        }

        var change = await BuildAsync(db, employee, request.EffectiveFrom, request.PayType, request.AnnualSalary, request.HourlyRate, request.Reason, request.Note, null, cancellationToken);
        change.Status = D.CompensationChangeStatus.Pending;
        db.Set<D.CompensationChange>().Add(change);
        audit.Record(db, AuditEntityType, change.Id, "Proposed", null, Snapshot(change));
        var route = await _approvals.StartAsync(db, Subject(change, employee), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await notifications.PublishAsync(route.NotifyEmails, HrNotificationKinds.CompensationRequested, $"Pay change for {employee.DisplayName}",
            $"{Label(change.Reason)} from {change.EffectiveFrom:MMM d, yyyy}", link: "hr/compensation");
        return (await MapAsync(db, [change], cancellationToken))[0];
    }

    public Task<CompensationChangeDto> ApproveAsync(Guid changeId, CompensationDecisionRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(changeId, approve: true, request?.Comment, cancellationToken);

    public Task<CompensationChangeDto> RejectAsync(Guid changeId, CompensationDecisionRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(changeId, approve: false, request?.Comment, cancellationToken);

    public async Task<CompensationChangeDto> CancelAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var change = await db.Set<D.CompensationChange>().FirstOrDefaultAsync(c => c.Id == changeId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Compensation change", changeId);
        if (change.Status is not (D.CompensationChangeStatus.Pending or D.CompensationChangeStatus.Draft))
        {
            throw new ArgumentException($"This change is already {change.Status.ToString().ToLowerInvariant()}.");
        }

        if (change.Status == D.CompensationChangeStatus.Pending)
        {
            await _approvals.CancelAsync(db, ApprovalDocumentTypes.Compensation, change.Id, audit.Actor, "Withdrawn by HR", cancellationToken);
        }

        change.Status = D.CompensationChangeStatus.Cancelled;
        change.DecidedBy = audit.Actor;
        change.DecidedAtUtc = UtcNow;
        audit.Record(db, AuditEntityType, change.Id, "Cancelled");
        await db.SaveChangesAsync(cancellationToken);
        return (await MapAsync(db, [change], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<MeritCycleDto>> ListCyclesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var cycles = await db.Set<D.MeritCycle>().AsNoTracking().ToListAsync(cancellationToken);
        var result = new List<MeritCycleDto>();
        foreach (var cycle in cycles.OrderByDescending(c => c.EffectiveFrom))
        {
            result.Add(await CycleDtoAsync(db, cycle, cancellationToken));
        }

        return result;
    }

    public async Task<MeritCycleDto> GetCycleAsync(Guid cycleId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var cycle = await db.Set<D.MeritCycle>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == cycleId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Merit cycle", cycleId);
        return await CycleDtoAsync(db, cycle, cancellationToken);
    }

    public async Task<MeritCycleDto> CreateCycleAsync(SaveMeritCycleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = HrText.Clean(request.Name, 200, "The name") ?? throw new ArgumentException("Give the cycle a name.");
        if (request.BudgetPercent is < 0 or > 0.5m)
        {
            throw new ArgumentException("The budget is a fraction of covered pay between 0 and 0.5 (3% = 0.03).");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var cycle = new D.MeritCycle { Name = name, EffectiveFrom = request.EffectiveFrom, BudgetPercent = request.BudgetPercent, CreatedBy = audit.Actor, CreatedAtUtc = UtcNow };
        db.Set<D.MeritCycle>().Add(cycle);
        audit.Record(db, "MeritCycle", cycle.Id, "Created", null, new { cycle.Name, cycle.EffectiveFrom, cycle.BudgetPercent });
        await db.SaveChangesAsync(cancellationToken);
        return await CycleDtoAsync(db, cycle, cancellationToken);
    }

    public async Task<MeritCycleDto> AddProposalsAsync(Guid cycleId, AddMeritProposalsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        if (request.Percent is <= 0 or > 0.5m)
        {
            throw new ArgumentException("The increase is a fraction between 0 and 0.5 (4% = 0.04).");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var cycle = await db.Set<D.MeritCycle>().FirstOrDefaultAsync(c => c.Id == cycleId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Merit cycle", cycleId);
        if (cycle.Status != D.MeritCycleStatus.Planning)
        {
            throw new ArgumentException("This cycle is already submitted.");
        }

        foreach (var employeeId in (request.EmployeeIds ?? []).Distinct())
        {
            if (viewer.IsSelf(employeeId))
            {
                throw new WorkbenchForbiddenException("You can't propose a change to your own pay.");
            }

            var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(employeeId);
            var profile = await db.Set<EmployeePayProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == employeeId, cancellationToken);
            var current = profile is null ? null : PayRates.InEffect(await PayRates.HistoryAsync(db, profile, cancellationToken), cycle.EffectiveFrom);
            if (current is null)
            {
                throw new ArgumentException($"{employee.DisplayName} has no pay rate on {cycle.EffectiveFrom:yyyy-MM-dd} to raise.");
            }

            var factor = 1 + request.Percent;
            var salary = current.PayType == PayType.Salaried ? Math.Round(current.AnnualSalary!.Value * factor, 0, MidpointRounding.AwayFromZero) : (decimal?)null;
            var hourly = current.PayType == PayType.Hourly ? Math.Round(current.HourlyRate!.Value * factor, 2, MidpointRounding.AwayFromZero) : (decimal?)null;
            db.Set<D.CompensationChange>().RemoveRange(await db.Set<D.CompensationChange>()
                .Where(c => c.CycleId == cycle.Id && c.EmployeeId == employeeId && c.Status == D.CompensationChangeStatus.Draft).ToListAsync(cancellationToken));
            var change = await BuildAsync(db, employee, cycle.EffectiveFrom, (PayTypeKind)(int)current.PayType, salary, hourly, request.Reason,
                $"{cycle.Name}: {request.Percent:P1}", cycle.Id, cancellationToken);
            change.Status = D.CompensationChangeStatus.Draft;
            db.Set<D.CompensationChange>().Add(change);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await CycleDtoAsync(db, cycle, cancellationToken);
    }

    public async Task<MeritCycleDto> SubmitCycleAsync(Guid cycleId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var cycle = await db.Set<D.MeritCycle>().FirstOrDefaultAsync(c => c.Id == cycleId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Merit cycle", cycleId);
        if (cycle.Status != D.MeritCycleStatus.Planning)
        {
            throw new ArgumentException("This cycle is already submitted.");
        }

        var drafts = await db.Set<D.CompensationChange>().Where(c => c.CycleId == cycleId && c.Status == D.CompensationChangeStatus.Draft).ToListAsync(cancellationToken);
        if (drafts.Count == 0)
        {
            throw new ArgumentException("Add proposals before submitting the cycle.");
        }

        var ids = drafts.Select(d => d.EmployeeId).ToList();
        if (await db.Set<D.CompensationChange>().AnyAsync(c => ids.Contains(c.EmployeeId) && c.Status == D.CompensationChangeStatus.Pending, cancellationToken))
        {
            throw new ArgumentException("Someone in this cycle already has a pay change waiting for approval; decide it first.");
        }

        var notify = new List<string>();
        var employees = await db.Set<Employee>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        foreach (var draft in drafts)
        {
            draft.Status = D.CompensationChangeStatus.Pending;
            draft.CreatedAtUtc = UtcNow;
            audit.Record(db, AuditEntityType, draft.Id, "Proposed", null, Snapshot(draft));
            notify.AddRange((await _approvals.StartAsync(db, Subject(draft, employees[draft.EmployeeId]), cancellationToken)).NotifyEmails);
        }

        cycle.Status = D.MeritCycleStatus.Submitted;
        audit.Record(db, "MeritCycle", cycle.Id, "Submitted", null, new { Proposals = drafts.Count });
        await db.SaveChangesAsync(cancellationToken);
        await notifications.PublishAsync(notify, HrNotificationKinds.CompensationRequested, $"{cycle.Name}: {drafts.Count} pay change(s) to approve", link: "hr/compensation");
        return await CycleDtoAsync(db, cycle, cancellationToken);
    }

    private async Task<CompensationChangeDto> DecideAsync(Guid changeId, bool approve, string? comment, CancellationToken ct)
    {
        var viewer = await hrAccess.ViewerAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var change = await db.Set<D.CompensationChange>().FirstOrDefaultAsync(c => c.Id == changeId, ct) ?? throw WorkbenchNotFoundException.For("Compensation change", changeId);
        var employee = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == change.EmployeeId, ct);
        if (viewer.IsSelf(change.EmployeeId))
        {
            throw new WorkbenchForbiddenException("You can't decide a change to your own pay.");
        }

        if (change.Status != D.CompensationChangeStatus.Pending)
        {
            throw new ArgumentException($"This change is {change.Status.ToString().ToLowerInvariant()}, not waiting for approval.");
        }

        var check = await _approvals.CheckAsync(db, ApprovalDocumentTypes.Compensation, change.Id, viewer.Email, _ => Task.FromResult(Subject(change, employee)), ct);
        if (!check.CanDecide)
        {
            throw new WorkbenchForbiddenException("Nothing on this pay change is waiting on you.");
        }

        var note = HrText.Clean(comment, 1000, "The comment");
        if (!approve && note is null)
        {
            throw new ArgumentException("Give a reason for rejecting the change.");
        }

        var outcome = await _approvals.DecideAsync(db, check, approve ? ApprovalDecisionKind.Approve : ApprovalDecisionKind.Reject, viewer.Email, note, ct);
        if (approve && !outcome.IsComplete)
        {
            audit.Record(db, AuditEntityType, change.Id, "StepApproved", null, new { Step = string.Join(", ", outcome.Decided.Select(t => t.StepName)) }, note);
            await db.SaveChangesAsync(ct);
            await notifications.PublishAsync(outcome.NotifyEmails, HrNotificationKinds.CompensationRequested, $"Pay change for {employee.DisplayName}",
                $"{Label(change.Reason)} from {change.EffectiveFrom:MMM d, yyyy}", link: "hr/compensation");
            return (await MapAsync(db, [change], ct))[0];
        }

        change.DecidedBy = viewer.Email;
        change.DecidedAtUtc = UtcNow;
        change.DecisionComment = note;
        var costChanged = false;
        if (approve)
        {
            costChanged = await ApplyAsync(db, change, ct);
        }
        else
        {
            change.Status = D.CompensationChangeStatus.Rejected;
            audit.Record(db, AuditEntityType, change.Id, "Rejected", null, null, note);
        }

        await db.SaveChangesAsync(ct);
        if (approve && securityLog is not null)
        {
            await securityLog.RecordAsync(PayrollSecurityEvents.PayRateChanged, $"employee {employee.EmployeeNumber}", detail: $"Pay rate changed from {change.EffectiveFrom:yyyy-MM-dd} (compensation change)", cancellationToken: ct);
        }

        // A back-dated change re-costs labor already posted from its effective date (adjusting entries, audited).
        if (costChanged && laborRates is not null)
        {
            await laborRates.AdjustPostedLaborAsync(change.EmployeeId, change.EffectiveFrom,
                $"Pay change effective {change.EffectiveFrom:yyyy-MM-dd} ({Label(change.Reason)}), approved by {change.DecidedBy}", ct);
        }

        await notifications.PublishAsync(change.RequestedBy, HrNotificationKinds.CompensationRequested,
            $"Pay change for {employee.DisplayName} {(approve ? "approved" : "rejected")}", approve ? $"In effect from {change.EffectiveFrom:MMM d, yyyy}" : note, link: "hr/compensation");
        return (await MapAsync(db, [change], ct))[0];
    }

    /// <summary>
    /// Appends the pay rate row (storing the derived history first for profiles that had none), mirrors it on the profile
    /// and writes the matching labor cost rate. Returns whether the cost rate changed.
    /// </summary>
    private async Task<bool> ApplyAsync(IWorkbenchDbContext db, D.CompensationChange change, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == change.EmployeeId, ct);
        var profile = await db.Set<EmployeePayProfile>().FirstOrDefaultAsync(p => p.EmployeeId == change.EmployeeId, ct);
        var initial = profile is null;
        var hr = await db.Set<D.EmployeeHrProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == change.EmployeeId, ct);
        List<EmployeePayRate> history = [];
        if (profile is null)
        {
            profile = new EmployeePayProfile { EmployeeId = change.EmployeeId, EffectiveFrom = change.EffectiveFrom, EmploymentType = hr?.EmploymentType ?? EmploymentType.FullTime };
            db.Set<EmployeePayProfile>().Add(profile);
        }
        else
        {
            history = await PayRates.HistoryAsync(db, profile, ct);
            if (!await db.Set<EmployeePayRate>().AnyAsync(r => r.EmployeeId == change.EmployeeId, ct))
            {
                db.Set<EmployeePayRate>().AddRange(history);
            }
        }

        var rate = new EmployeePayRate
        {
            EmployeeId = change.EmployeeId,
            EffectiveFrom = change.EffectiveFrom,
            PayType = change.PayType,
            AnnualSalary = change.AnnualSalary,
            HourlyRate = change.HourlyRate,
            Reason = Clip($"{Label(change.Reason)}{(change.Note is null ? "" : $": {change.Note}")}", 500),
            CreatedBy = change.DecidedBy ?? audit.Actor,
            CreatedAtUtc = UtcNow,
        };
        db.Set<EmployeePayRate>().Add(rate);
        PayRates.Mirror(profile, PayRates.Order([.. history, rate]));
        profile.UpdatedAtUtc = UtcNow;
        profile.UpdatedBy = audit.Actor;
        change.PayRateId = rate.Id;
        change.Status = D.CompensationChangeStatus.Applied;
        audit.Record(db, AuditEntityType, change.Id, "Applied", null, new { change.EffectiveFrom, PayType = change.PayType.ToString(), Employee = employee.EmployeeNumber });
        audit.Record(db, PayrollProfileService.AuditEntityType, profile.Id, "Pay rate changed", null, new { rate.EffectiveFrom, PayType = rate.PayType.ToString() }, "Compensation change");
        return await LaborRateChanges.SyncCostRateAsync(db, rate, initial, ct);
    }

    private async Task<D.CompensationChange> BuildAsync(IWorkbenchDbContext db, Employee employee, DateOnly effective, PayTypeKind payType, decimal? salary, decimal? hourly,
        CompensationReason reason, string? note, Guid? cycleId, CancellationToken ct)
    {
        if (effective < employee.HireDate)
        {
            throw new ArgumentException($"A pay change can't start before the hire date ({employee.HireDate:yyyy-MM-dd}).");
        }

        if (employee.TerminationDate is { } end && effective > end)
        {
            throw new ArgumentException($"{employee.DisplayName}'s employment ends {end:yyyy-MM-dd}, before this change would start.");
        }

        var type = (PayType)(int)payType;
        if (type == PayType.Salaried ? salary is not (> 0 and <= 10_000_000) : hourly is not (> 0 and <= 10_000))
        {
            throw new ArgumentException(type == PayType.Salaried ? "Enter an annual salary above 0 and up to 10,000,000." : "Enter an hourly rate above 0 and up to 10,000.");
        }

        var profile = await db.Set<EmployeePayProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == employee.Id, ct);
        if (profile?.PayGroupId is { } groupId)
        {
            // Payroll already paid at the old rate for posted periods: a change into them needs a correction in the provider.
            var postedThrough = await db.Set<PayrollRun>().AsNoTracking().Where(r => r.PayGroupId == groupId && r.Status == PayrollRunStatus.Posted)
                .Join(db.Set<PayPeriod>(), r => r.PayPeriodId, p => p.Id, (r, p) => (DateOnly?)p.End).MaxAsync(ct);
            if (postedThrough is { } posted && effective <= posted)
            {
                throw new ArgumentException($"Payroll is posted through {posted:yyyy-MM-dd}. Choose an effective date after it (retroactive pay is a correction in the payroll provider).");
            }
        }

        var previous = profile is null ? null : PayRates.InEffect(await PayRates.HistoryAsync(db, profile, ct), effective);
        return new D.CompensationChange
        {
            EmployeeId = employee.Id,
            CycleId = cycleId,
            EffectiveFrom = effective,
            PayType = type,
            AnnualSalary = type == PayType.Salaried ? salary : null,
            HourlyRate = type == PayType.Hourly ? hourly : null,
            PreviousAnnualized = previous is null ? null : Annualize(previous.PayType, previous.AnnualSalary, previous.HourlyRate),
            Reason = (D.CompensationReason)(int)reason,
            Note = HrText.Clean(note, 500, "The note"),
            RequestedBy = audit.Actor,
            CreatedAtUtc = UtcNow,
        };
    }

    internal static decimal? Annualize(PayType type, decimal? salary, decimal? hourly) => type == PayType.Salaried ? salary : hourly * 2080m;

    /// <summary>The workflow amount is the annualized increase, compared with the Finance threshold.</summary>
    private ApprovalSubject Subject(D.CompensationChange change, Employee employee)
    {
        var increase = Math.Max(0m, (change.Annualized ?? 0m) - (change.PreviousAnnualized ?? 0m));
        return new ApprovalSubject(ApprovalDocumentTypes.Compensation, change.Id, $"{employee.DisplayName}: pay change from {change.EffectiveFrom:MMM d, yyyy}",
            change.RequestedBy, "hr/compensation", increase, Label(change.Reason), employee.Id,
            [new ApprovalCharge(null, increase, employee.OrgUnitId, "Compensation")], options.Value.CompensationFinanceThreshold);
    }

    private async Task<MeritCycleDto> CycleDtoAsync(IWorkbenchDbContext db, D.MeritCycle cycle, CancellationToken ct)
    {
        var proposals = await db.Set<D.CompensationChange>().AsNoTracking().Where(c => c.CycleId == cycle.Id && c.Status != D.CompensationChangeStatus.Cancelled).ToListAsync(ct);
        var dtos = await MapAsync(db, proposals.OrderBy(p => p.CreatedAtUtc).ToList(), ct);
        var covered = proposals.Sum(p => p.PreviousAnnualized ?? 0m);
        var proposed = proposals.Sum(p => Math.Max(0m, (p.Annualized ?? 0m) - (p.PreviousAnnualized ?? 0m)));
        return new MeritCycleDto(cycle.Id, cycle.Name, cycle.EffectiveFrom, cycle.BudgetPercent, (MeritCycleStatus)(int)cycle.Status, covered,
            Math.Round(covered * cycle.BudgetPercent, 2), proposed, dtos);
    }

    private async Task<List<CompensationChangeDto>> MapAsync(IWorkbenchDbContext db, IReadOnlyList<D.CompensationChange> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var names = await db.Set<Employee>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);
        var pendingIds = rows.Where(r => r.Status == D.CompensationChangeStatus.Pending).Select(r => r.Id).ToList();
        var waiting = pendingIds.Count == 0 ? [] : (await db.Set<ApprovalInstance>().AsNoTracking()
                .Where(i => i.DocumentType == ApprovalDocumentTypes.Compensation && pendingIds.Contains(i.DocumentId) && i.Status == ApprovalInstanceStatus.Pending)
                .Join(db.Set<ApprovalTask>().Where(t => t.Status == ApprovalTaskStatus.Pending), i => i.Id, t => t.InstanceId, (i, t) => new { i.DocumentId, t.StepName })
                .ToListAsync(ct))
            .GroupBy(x => x.DocumentId).ToDictionary(g => g.Key, g => $"Waiting for {string.Join(", ", g.Select(x => x.StepName).Distinct())}");
        return rows.Select(c => new CompensationChangeDto(c.Id, c.EmployeeId, names.GetValueOrDefault(c.EmployeeId, ""), c.CycleId, c.EffectiveFrom, (PayTypeKind)(int)c.PayType,
            c.AnnualSalary, c.HourlyRate, c.Annualized, c.PreviousAnnualized, (CompensationReason)(int)c.Reason, c.Note, (CompensationChangeStatus)(int)c.Status,
            c.RequestedBy, c.CreatedAtUtc, c.DecidedBy, c.DecidedAtUtc, c.DecisionComment, waiting.GetValueOrDefault(c.Id))).ToList();
    }

    private static object Snapshot(D.CompensationChange c) => new { c.EmployeeId, c.EffectiveFrom, PayType = c.PayType.ToString(), Reason = c.Reason.ToString(), c.CycleId };

    internal static string Label(D.CompensationReason reason) => reason switch
    {
        D.CompensationReason.NewHire => "New hire",
        D.CompensationReason.MarketAdjustment => "Market adjustment",
        _ => reason.ToString(),
    };

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
