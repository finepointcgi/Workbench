using Microsoft.EntityFrameworkCore;
using Workbench.Application.Approvals;
using Workbench.Application.Notifications;
using Workbench.Application.Payroll;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Hr;
using Workbench.Domain.Accounting;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;
using D = Workbench.Domain.Hr;

namespace Workbench.Application.Hr;

/// <summary>Benefit plans, enrollment windows, dependents, elections and life events (rules: <see cref="BenefitRules"/>).</summary>
public sealed class BenefitService(
    IWorkbenchDbContextFactory dbFactory,
    HrAccess hrAccess,
    AuditRecorder audit,
    INotificationPublisher notifications,
    TimeProvider clock) : IBenefitService
{
    public const string PlanAudit = "BenefitPlan";
    public const string ElectionAudit = "BenefitElection";

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<BenefitPlanDto>> ListPlansAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var plans = await db.Set<D.BenefitPlan>().AsNoTracking().Include(p => p.Tiers).Where(p => includeInactive || p.IsActive).ToListAsync(cancellationToken);
        return await MapPlansAsync(db, plans, cancellationToken);
    }

    public async Task<BenefitPlanDto> SavePlanAsync(Guid? planId, SaveBenefitPlanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var code = HrText.Code(request.Code, "The plan");
        var name = HrText.Clean(request.Name, 200, "The name") ?? throw new ArgumentException("Give the plan a name.");
        var deduction = HrText.Clean(request.DeductionCode, 32, "The deduction code")?.ToUpperInvariant() ?? throw new ArgumentException("Give the plan the payroll provider's deduction code.");
        if (request.EndsOn < request.StartsOn)
        {
            throw new ArgumentException("The plan year must end after it starts.");
        }

        if (await db.Set<D.BenefitPlan>().AnyAsync(p => p.Code == code && p.Id != planId, cancellationToken))
        {
            throw new ArgumentException($"Plan code {code} is already used.");
        }

        var type = (D.BenefitPlanType)(int)request.Type;
        var contribution = BenefitLabels.IsContribution(request.Type);
        var tiers = request.Tiers ?? [];
        if (contribution && tiers.Count > 0)
        {
            throw new ArgumentException("401(k), FSA and HSA plans take contributions, not coverage tiers.");
        }

        if (!contribution && tiers.Count == 0)
        {
            throw new ArgumentException("Add at least one coverage tier with its monthly costs.");
        }

        if (tiers.GroupBy(t => t.Coverage).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Each coverage level can appear once.");
        }

        if (tiers.Any(t => t.EmployeeMonthlyCost is < 0 or > 100_000 || t.EmployerMonthlyCost is < 0 or > 100_000)
            || request.EmployerMonthlyContribution is < 0 or > 100_000 || request.AnnualElectionLimit is < 0 or > 1_000_000)
        {
            throw new ArgumentException("Costs and limits can't be negative (or implausibly large).");
        }

        if (request.EmployerMatchRate is < 0 or > 5 || request.EmployerMatchCapPercent is < 0 or > 1)
        {
            throw new ArgumentException("The match rate is a fraction (1 = dollar for dollar) and the cap a fraction of pay (0.04 = 4%).");
        }

        if (request.Type == BenefitPlanType.Retirement401k && request.AnnualElectionLimit is > 100)
        {
            throw new ArgumentException("For a 401(k), the election limit is a percent of pay (at most 100).");
        }

        await CheckAccountAsync(db, request.ExpenseAccountId, cancellationToken);
        var plan = planId is { } id
            ? await db.Set<D.BenefitPlan>().Include(p => p.Tiers).FirstOrDefaultAsync(p => p.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Benefit plan", id)
            : null;
        if (plan is not null && plan.Type != type && await db.Set<D.BenefitElection>().AnyAsync(e => e.PlanId == plan.Id, cancellationToken))
        {
            throw new ArgumentException("People have elected this plan, so its type can't change. Add a new plan instead.");
        }

        var isNew = plan is null;
        plan ??= db.Set<D.BenefitPlan>().Add(new D.BenefitPlan { Code = code, Name = name, DeductionCode = deduction }).Entity;
        plan.Code = code;
        plan.Name = name;
        plan.Type = type;
        plan.Carrier = HrText.Clean(request.Carrier, 200, "The carrier");
        plan.StartsOn = request.StartsOn;
        plan.EndsOn = request.EndsOn;
        plan.DeductionCode = deduction;
        plan.IsPreTax = request.IsPreTax;
        plan.ExpenseAccountId = request.ExpenseAccountId;
        plan.IsHighDeductible = request.Type == BenefitPlanType.Medical && request.IsHighDeductible;
        plan.FullTimeOnly = request.FullTimeOnly;
        plan.AnnualElectionLimit = contribution ? request.AnnualElectionLimit : null;
        plan.EmployerMatchRate = request.Type == BenefitPlanType.Retirement401k ? request.EmployerMatchRate : null;
        plan.EmployerMatchCapPercent = request.Type == BenefitPlanType.Retirement401k ? request.EmployerMatchCapPercent : null;
        plan.EmployerMonthlyContribution = request.Type is BenefitPlanType.Hsa or BenefitPlanType.Fsa ? request.EmployerMonthlyContribution : null;
        plan.Description = HrText.Clean(request.Description, 1000, "The description");
        plan.IsActive = request.IsActive;
        plan.UpdatedAtUtc = UtcNow;
        plan.UpdatedBy = audit.Actor;

        // Tiers are matched by coverage level so elections keep pointing at theirs; a tier in use can't be removed.
        var keep = tiers.Select(t => (D.CoverageLevel)(int)t.Coverage).ToHashSet();
        foreach (var gone in plan.Tiers.Where(t => !keep.Contains(t.Coverage)).ToList())
        {
            if (await db.Set<D.BenefitElection>().AnyAsync(e => e.TierId == gone.Id, cancellationToken))
            {
                throw new ArgumentException($"People have elected {BenefitLabels.Label((CoverageLevel)(int)gone.Coverage)} coverage, so that tier stays.");
            }

            plan.Tiers.Remove(gone);
            db.Set<D.BenefitTier>().Remove(gone);
        }

        foreach (var t in tiers)
        {
            var level = (D.CoverageLevel)(int)t.Coverage;
            var tier = plan.Tiers.FirstOrDefault(x => x.Coverage == level);
            if (tier is null)
            {
                tier = new D.BenefitTier { PlanId = plan.Id, Coverage = level };
                plan.Tiers.Add(tier);
                db.Set<D.BenefitTier>().Add(tier);
            }

            tier.EmployeeMonthlyCost = t.EmployeeMonthlyCost;
            tier.EmployerMonthlyCost = t.EmployerMonthlyCost;
        }

        audit.Record(db, PlanAudit, plan.Id, isNew ? "Created" : "Updated", null,
            new { plan.Code, plan.Name, Type = plan.Type.ToString(), plan.DeductionCode, plan.ExpenseAccountId, plan.IsActive, Tiers = plan.Tiers.Count });
        await db.SaveChangesAsync(cancellationToken);
        return (await MapPlansAsync(db, [plan], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<BenefitAccountOptionDto>> ListExpenseAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await db.Set<Account>().AsNoTracking().Where(a => a.Type == AccountType.Expense && a.IsActive).ToListAsync(cancellationToken))
            .Select(a => new BenefitAccountOptionDto(a.Id, a.Number, a.Name, a.CostClass == CostClassification.Fringe))
            .OrderByDescending(a => a.IsFringe).ThenBy(a => a.Number, StringComparer.Ordinal).ToList();
    }

    public async Task<BenefitPlanDto> SetPlanAccountAsync(Guid planId, Guid? expenseAccountId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var plan = await db.Set<D.BenefitPlan>().Include(p => p.Tiers).FirstOrDefaultAsync(p => p.Id == planId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Benefit plan", planId);
        await CheckAccountAsync(db, expenseAccountId, cancellationToken);
        var before = plan.ExpenseAccountId;
        plan.ExpenseAccountId = expenseAccountId;
        plan.UpdatedAtUtc = UtcNow;
        plan.UpdatedBy = audit.Actor;
        audit.Record(db, PlanAudit, plan.Id, "GL account changed", new { ExpenseAccountId = before }, new { plan.ExpenseAccountId });
        await db.SaveChangesAsync(cancellationToken);
        return (await MapPlansAsync(db, [plan], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<OpenEnrollmentWindowDto>> ListWindowsAsync(CancellationToken cancellationToken = default)
    {
        await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var today = Today;
        return (await db.Set<D.OpenEnrollmentWindow>().AsNoTracking().ToListAsync(cancellationToken))
            .OrderByDescending(w => w.OpensOn).Select(w => ToDto(w, today)).ToList();
    }

    public async Task<OpenEnrollmentWindowDto> SaveWindowAsync(Guid? windowId, SaveOpenEnrollmentWindowRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = HrText.Clean(request.Name, 200, "The name") ?? throw new ArgumentException("Give the window a name.");
        if (request.ClosesOn < request.OpensOn)
        {
            throw new ArgumentException("The window must close after it opens.");
        }

        if (request.CoverageStartsOn < request.OpensOn)
        {
            throw new ArgumentException("Coverage from open enrollment can't start before the window opens.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<D.OpenEnrollmentWindow>().AnyAsync(w => w.Id != windowId && w.OpensOn <= request.ClosesOn && w.ClosesOn >= request.OpensOn, cancellationToken))
        {
            throw new ArgumentException("Another open enrollment window overlaps these dates.");
        }

        var window = windowId is { } id
            ? await db.Set<D.OpenEnrollmentWindow>().FirstOrDefaultAsync(w => w.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Enrollment window", id)
            : db.Set<D.OpenEnrollmentWindow>().Add(new D.OpenEnrollmentWindow { Name = name, CreatedBy = audit.Actor, CreatedAtUtc = UtcNow }).Entity;
        window.Name = name;
        window.OpensOn = request.OpensOn;
        window.ClosesOn = request.ClosesOn;
        window.CoverageStartsOn = request.CoverageStartsOn;
        audit.Record(db, "OpenEnrollmentWindow", window.Id, windowId is null ? "Created" : "Updated", null, new { window.Name, window.OpensOn, window.ClosesOn, window.CoverageStartsOn });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(window, Today);
    }

    public async Task<EmployeeBenefitsDto> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        if (viewer.EmployeeId is not { } me)
        {
            throw new WorkbenchNotFoundException("There's no employee record for your sign-in. Ask HR to link one.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await BenefitsAsync(db, me, cancellationToken);
    }

    public async Task<EmployeeBenefitsDto> GetForEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await BenefitsAsync(db, employeeId, cancellationToken);
    }

    public async Task<DependentDto> SaveDependentAsync(Guid employeeId, Guid? dependentId, SaveDependentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        _ = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(employeeId);
        var first = HrText.Clean(request.FirstName, 100, "The first name") ?? throw new ArgumentException("Enter the dependent's first name.");
        var last = HrText.Clean(request.LastName, 100, "The last name") ?? throw new ArgumentException("Enter the dependent's last name.");
        if (request.BirthDate > Today)
        {
            throw new ArgumentException("The birth date can't be in the future.");
        }

        var dependent = dependentId is { } id
            ? await db.Set<D.BenefitDependent>().FirstOrDefaultAsync(d => d.Id == id && d.EmployeeId == employeeId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Dependent", id)
            : db.Set<D.BenefitDependent>().Add(new D.BenefitDependent { EmployeeId = employeeId, FirstName = first, LastName = last }).Entity;
        if (dependentId is not null && (!request.IsActive || (D.DependentRelationship)(int)request.Relationship != dependent.Relationship))
        {
            var today = Today;
            var covered = await db.Set<D.BenefitElection>().AsNoTracking().Where(e => e.EmployeeId == employeeId && !e.IsWaived && (e.CoverageEnd == null || e.CoverageEnd >= today))
                .AnyAsync(e => e.Dependents.Any(d => d.DependentId == dependent.Id), cancellationToken);
            if (covered)
            {
                throw new ArgumentException($"{dependent.FirstName} is covered by a current election. Change the elections first (a life event lets you).");
            }
        }

        dependent.FirstName = first;
        dependent.LastName = last;
        dependent.Relationship = (D.DependentRelationship)(int)request.Relationship;
        dependent.BirthDate = request.BirthDate;
        dependent.IsActive = request.IsActive;
        dependent.UpdatedAtUtc = UtcNow;
        audit.Record(db, "BenefitDependent", dependent.Id, dependentId is null ? "Added" : "Updated", null, new { Relationship = dependent.Relationship.ToString(), dependent.IsActive });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(dependent);
    }

    public async Task<ElectionDto> ElectAsync(Guid employeeId, ElectBenefitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(employeeId);
        var plan = await db.Set<D.BenefitPlan>().AsNoTracking().Include(p => p.Tiers).FirstOrDefaultAsync(p => p.Id == request.PlanId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Benefit plan", request.PlanId);
        if (!plan.IsActive)
        {
            throw new ArgumentException($"{plan.Name} isn't offered.");
        }

        var today = Today;
        var start = await CoverageStartAsync(db, employee, plan, request.Source, request.LifeEventId, viewer, cancellationToken);
        if (start < plan.StartsOn || start > plan.EndsOn)
        {
            throw new ArgumentException($"{plan.Name} covers {plan.StartsOn:MMM d, yyyy} to {plan.EndsOn:MMM d, yyyy}; this election would start {start:MMM d, yyyy}.");
        }

        var election = new D.BenefitElection
        {
            EmployeeId = employeeId,
            PlanId = plan.Id,
            PlanType = plan.Type,
            IsWaived = request.Waive,
            CoverageStart = start,
            Source = (D.ElectionSource)(int)request.Source,
            WindowId = request.Source == ElectionSource.OpenEnrollment ? (await OpenWindowAsync(db, today, cancellationToken))?.Id : null,
            LifeEventId = request.Source == ElectionSource.LifeEvent ? request.LifeEventId : null,
            ElectedBy = viewer.Email,
            ElectedAtUtc = UtcNow,
        };

        if (!request.Waive)
        {
            if (plan.FullTimeOnly && await EmploymentTypeAsync(db, employeeId, cancellationToken) != EmploymentType.FullTime)
            {
                throw new ArgumentException($"{plan.Name} is for full-time employees.");
            }

            if (plan.IsContribution)
            {
                await CheckContributionAsync(db, employeeId, plan, request.ContributionAmount, start, cancellationToken);
                election.ContributionAmount = request.ContributionAmount;
            }
            else
            {
                var level = request.Coverage ?? throw new ArgumentException("Choose a coverage level.");
                var tier = plan.Tiers.FirstOrDefault(t => t.Coverage == (D.CoverageLevel)(int)level)
                    ?? throw new ArgumentException($"{plan.Name} doesn't offer {BenefitLabels.Label(level)} coverage.");
                var ids = (request.DependentIds ?? []).Distinct().ToList();
                var dependents = await db.Set<D.BenefitDependent>().AsNoTracking().Where(d => ids.Contains(d.Id) && d.EmployeeId == employeeId && d.IsActive).ToListAsync(cancellationToken);
                if (dependents.Count != ids.Count)
                {
                    throw new ArgumentException("Cover only your own active dependents.");
                }

                BenefitRules.CheckCoverage((D.CoverageLevel)(int)level, dependents, start);
                election.Coverage = (D.CoverageLevel)(int)level;
                election.TierId = tier.Id;
                election.Dependents.AddRange(dependents.Select(d => new D.BenefitElectionDependent { ElectionId = election.Id, DependentId = d.Id }));
            }
        }

        // The new election replaces this plan type from its start: earlier ones end the day before; later (not yet started) ones go.
        var sameType = await db.Set<D.BenefitElection>().Where(e => e.EmployeeId == employeeId && e.PlanType == plan.Type && (e.CoverageEnd == null || e.CoverageEnd >= start))
            .ToListAsync(cancellationToken);
        foreach (var old in sameType)
        {
            if (old.CoverageStart >= start)
            {
                db.Set<D.BenefitElection>().Remove(old);
            }
            else
            {
                old.CoverageEnd = start.AddDays(-1);
            }
        }

        db.Set<D.BenefitElection>().Add(election);
        audit.Record(db, ElectionAudit, election.Id, request.Waive ? "Waived" : "Elected", null,
            new { Employee = employee.EmployeeNumber, Plan = plan.Code, Coverage = election.Coverage?.ToString(), election.CoverageStart, Source = election.Source.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        var pay = await AnnualPayAsync(db, employeeId, today, cancellationToken);
        return ToDto(election, plan, plan.Tiers.FirstOrDefault(t => t.Id == election.TierId), pay);
    }

    public async Task<LifeEventDto> ReportLifeEventAsync(Guid employeeId, ReportLifeEventRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(employeeId);
        var today = Today;
        if (request.EventDate > today.AddDays(BenefitRules.EnrollmentWindowDays))
        {
            throw new ArgumentException("Report a life event once it's within 30 days.");
        }

        if (request.EventDate.AddDays(BenefitRules.EnrollmentWindowDays) < today)
        {
            throw new ArgumentException("Life events must be reported within 30 days of the event. Talk to HR about anything older.");
        }

        var byHr = viewer.IsHr && !viewer.IsSelf(employeeId);
        var row = new D.LifeEvent
        {
            EmployeeId = employeeId,
            Kind = (D.LifeEventKind)(int)request.Kind,
            EventDate = request.EventDate,
            WindowEndsOn = request.EventDate.AddDays(BenefitRules.EnrollmentWindowDays),
            Note = HrText.Clean(request.Note, 1000, "The note"),
            ReportedBy = viewer.Email,
            ReportedAtUtc = UtcNow,
            Status = byHr ? D.LifeEventStatus.Approved : D.LifeEventStatus.Pending,
            DecidedBy = byHr ? viewer.Email : null,
            DecidedAtUtc = byHr ? UtcNow : null,
        };
        db.Set<D.LifeEvent>().Add(row);
        audit.Record(db, "LifeEvent", row.Id, "Reported", null, new { Kind = row.Kind.ToString(), row.EventDate, Status = row.Status.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        if (!byHr)
        {
            var hr = (await ApprovalEngine.RoleMembersAsync(db, ApprovalOrgRole.Hr, cancellationToken)).Where(e => e != viewer.Email);
            await notifications.PublishAsync(hr, HrNotificationKinds.LifeEvent, $"{employee.DisplayName} reported a life event",
                $"{BenefitLabels.Label((LifeEventKind)(int)row.Kind)} on {row.EventDate:MMM d}", link: "hr/benefits?tab=life-events");
        }

        return ToDto(row, employee.DisplayName);
    }

    public async Task<IReadOnlyList<LifeEventDto>> ListPendingLifeEventsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<D.LifeEvent>().AsNoTracking().Where(e => e.Status == D.LifeEventStatus.Pending).ToListAsync(cancellationToken);
        var names = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.DisplayName, cancellationToken);
        return rows.OrderBy(r => r.ReportedAtUtc).Select(r => ToDto(r, names.GetValueOrDefault(r.EmployeeId, ""))).ToList();
    }

    public async Task<LifeEventDto> DecideLifeEventAsync(Guid lifeEventId, DecideLifeEventRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<D.LifeEvent>().FirstOrDefaultAsync(e => e.Id == lifeEventId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Life event", lifeEventId);
        if (viewer.IsSelf(row.EmployeeId))
        {
            throw new WorkbenchForbiddenException("You can't verify your own life event.");
        }

        if (row.Status != D.LifeEventStatus.Pending)
        {
            throw new ArgumentException($"This life event is already {row.Status.ToString().ToLowerInvariant()}.");
        }

        row.Status = request.Approve ? D.LifeEventStatus.Approved : D.LifeEventStatus.Rejected;
        row.DecidedBy = viewer.Email;
        row.DecidedAtUtc = UtcNow;
        audit.Record(db, "LifeEvent", row.Id, request.Approve ? "Approved" : "Rejected", null, null, HrText.Clean(request.Comment, 1000, "The comment"));
        await db.SaveChangesAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == row.EmployeeId, cancellationToken);
        await notifications.PublishAsync(employee.Email, HrNotificationKinds.LifeEvent,
            request.Approve ? $"Your life event is verified: change your benefits by {row.WindowEndsOn:MMM d}" : "HR couldn't verify your life event",
            request.Comment, link: "hr/benefits/me");
        return ToDto(row, employee.DisplayName);
    }

    private async Task<DateOnly> CoverageStartAsync(IWorkbenchDbContext db, Employee employee, D.BenefitPlan plan, ElectionSource source, Guid? lifeEventId,
        HrAccess.Viewer viewer, CancellationToken ct)
    {
        var today = Today;
        switch (source)
        {
            case ElectionSource.AnyTime:
                if (plan.Type != D.BenefitPlanType.Retirement401k)
                {
                    throw new ArgumentException("Only 401(k) contributions change any time; other benefits change at open enrollment, as a new hire or after a life event.");
                }

                return BenefitRules.FirstOfNextMonth(today);
            case ElectionSource.Administrative:
                if (!viewer.IsHr)
                {
                    throw new WorkbenchForbiddenException("Only HR can make administrative benefit changes.");
                }

                return BenefitRules.FirstOfNextMonth(today);
            case ElectionSource.OpenEnrollment:
                var window = await OpenWindowAsync(db, today, ct) ?? throw new ArgumentException("Open enrollment isn't open.");
                return window.CoverageStartsOn;
            case ElectionSource.NewHire:
                if (today > employee.HireDate.AddDays(BenefitRules.EnrollmentWindowDays))
                {
                    throw new ArgumentException($"New hire enrollment ended {employee.HireDate.AddDays(BenefitRules.EnrollmentWindowDays):MMM d, yyyy} (30 days after the hire date).");
                }

                return BenefitRules.NewHireCoverageStart(employee.HireDate);
            default:
                var lifeEvent = lifeEventId is { } id ? await db.Set<D.LifeEvent>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id && e.EmployeeId == employee.Id, ct) : null;
                if (lifeEvent is null || lifeEvent.Status != D.LifeEventStatus.Approved)
                {
                    throw new ArgumentException("Choose a life event HR has verified.");
                }

                if (today > lifeEvent.WindowEndsOn)
                {
                    throw new ArgumentException($"The window to change benefits for this life event ended {lifeEvent.WindowEndsOn:MMM d, yyyy}.");
                }

                return BenefitRules.LifeEventCoverageStart(lifeEvent.Kind, lifeEvent.EventDate);
        }
    }

    private static async Task CheckContributionAsync(IWorkbenchDbContext db, Guid employeeId, D.BenefitPlan plan, decimal? amount, DateOnly start, CancellationToken ct)
    {
        if (amount is not > 0)
        {
            throw new ArgumentException(plan.Type == D.BenefitPlanType.Retirement401k ? "Enter the percent of pay to contribute." : "Enter the amount to contribute for the year.");
        }

        if (plan.AnnualElectionLimit is { } limit && amount > limit)
        {
            throw new ArgumentException(plan.Type == D.BenefitPlanType.Retirement401k
                ? $"{plan.Name} takes at most {limit:0.##}% of pay."
                : $"{plan.Name} takes at most {limit:C0} a year.");
        }

        if (plan.Type is D.BenefitPlanType.Hsa or D.BenefitPlanType.Fsa)
        {
            var inForce = await db.Set<D.BenefitElection>().AsNoTracking()
                .Where(e => e.EmployeeId == employeeId && !e.IsWaived && e.CoverageStart <= start && (e.CoverageEnd == null || e.CoverageEnd >= start))
                .Join(db.Set<D.BenefitPlan>(), e => e.PlanId, p => p.Id, (e, p) => new { p.Type, p.IsHighDeductible })
                .ToListAsync(ct);
            if (plan.Type == D.BenefitPlanType.Hsa)
            {
                if (!inForce.Any(p => p.Type == D.BenefitPlanType.Medical && p.IsHighDeductible))
                {
                    throw new ArgumentException("An HSA needs a high-deductible medical plan. Elect one first.");
                }

                if (inForce.Any(p => p.Type == D.BenefitPlanType.Fsa))
                {
                    throw new ArgumentException("You can't contribute to an HSA and a health FSA at the same time. Waive the FSA first.");
                }
            }
            else if (inForce.Any(p => p.Type == D.BenefitPlanType.Hsa))
            {
                throw new ArgumentException("You can't contribute to a health FSA and an HSA at the same time. Waive the HSA first.");
            }
        }
    }

    private async Task<EmployeeBenefitsDto> BenefitsAsync(IWorkbenchDbContext db, Guid employeeId, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, ct) ?? throw HrAccess.EmployeeNotFound(employeeId);
        var today = Today;
        var plans = await db.Set<D.BenefitPlan>().AsNoTracking().Include(p => p.Tiers).ToListAsync(ct);
        var planById = plans.ToDictionary(p => p.Id);
        var elections = await db.Set<D.BenefitElection>().AsNoTracking().Include(e => e.Dependents)
            .Where(e => e.EmployeeId == employeeId && (e.CoverageEnd == null || e.CoverageEnd >= today)).ToListAsync(ct);
        var pay = await AnnualPayAsync(db, employeeId, today, ct);
        var dependents = await db.Set<D.BenefitDependent>().AsNoTracking().Where(d => d.EmployeeId == employeeId).OrderBy(d => d.BirthDate).ToListAsync(ct);
        var events = await db.Set<D.LifeEvent>().AsNoTracking().Where(e => e.EmployeeId == employeeId).OrderByDescending(e => e.EventDate).ToListAsync(ct);
        var window = await OpenWindowAsync(db, today, ct);
        var opportunities = new List<EnrollmentOpportunityDto>();
        if (window is not null)
        {
            opportunities.Add(new(ElectionSource.OpenEnrollment, window.Name, window.CoverageStartsOn, window.ClosesOn, window.Id, null));
        }

        if (today <= employee.HireDate.AddDays(BenefitRules.EnrollmentWindowDays))
        {
            opportunities.Add(new(ElectionSource.NewHire, "New hire enrollment", BenefitRules.NewHireCoverageStart(employee.HireDate),
                employee.HireDate.AddDays(BenefitRules.EnrollmentWindowDays), null, null));
        }

        foreach (var e in events.Where(e => e.Status == D.LifeEventStatus.Approved && today <= e.WindowEndsOn))
        {
            opportunities.Add(new(ElectionSource.LifeEvent, $"{BenefitLabels.Label((LifeEventKind)(int)e.Kind)} on {e.EventDate:MMM d}",
                BenefitRules.LifeEventCoverageStart(e.Kind, e.EventDate), e.WindowEndsOn, null, e.Id));
        }

        var employmentType = await EmploymentTypeAsync(db, employeeId, ct);
        var available = plans.Where(p => p.IsActive && (!p.FullTimeOnly || employmentType == EmploymentType.FullTime)).ToList();
        return new EmployeeBenefitsDto(
            employeeId, employee.DisplayName,
            elections.OrderBy(e => e.PlanType).ThenBy(e => e.CoverageStart)
                .Select(e => ToDto(e, planById[e.PlanId], planById[e.PlanId].Tiers.FirstOrDefault(t => t.Id == e.TierId), pay)).ToList(),
            dependents.Select(ToDto).ToList(),
            events.Select(e => ToDto(e, employee.DisplayName)).ToList(),
            opportunities,
            await MapPlansAsync(db, available, ct),
            window is null ? null : ToDto(window, today),
            window is null ? [] : elections.Where(e => e.WindowId == window.Id).Select(e => (BenefitPlanType)(int)e.PlanType).Distinct().ToList());
    }

    internal static async Task<D.OpenEnrollmentWindow?> OpenWindowAsync(IWorkbenchDbContext db, DateOnly today, CancellationToken ct) =>
        await db.Set<D.OpenEnrollmentWindow>().AsNoTracking().Where(w => w.OpensOn <= today && w.ClosesOn >= today).OrderBy(w => w.OpensOn).FirstOrDefaultAsync(ct);

    internal static async Task<EmploymentType> EmploymentTypeAsync(IWorkbenchDbContext db, Guid employeeId, CancellationToken ct) =>
        await db.Set<D.EmployeeHrProfile>().AsNoTracking().Where(p => p.EmployeeId == employeeId).Select(p => (EmploymentType?)p.EmploymentType).FirstOrDefaultAsync(ct)
        ?? await db.Set<EmployeePayProfile>().AsNoTracking().Where(p => p.EmployeeId == employeeId).Select(p => (EmploymentType?)p.EmploymentType).FirstOrDefaultAsync(ct)
        ?? EmploymentType.FullTime;

    /// <summary>Annual pay in effect on <paramref name="on"/> (for 401(k) costs), or null without a rate.</summary>
    internal static async Task<decimal?> AnnualPayAsync(IWorkbenchDbContext db, Guid employeeId, DateOnly on, CancellationToken ct)
    {
        var profile = await db.Set<EmployeePayProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == employeeId, ct);
        var rate = profile is null ? null : PayRates.InEffect(await PayRates.HistoryAsync(db, profile, ct), on);
        return rate is null ? null : CompensationService.Annualize(rate.PayType, rate.AnnualSalary, rate.HourlyRate);
    }

    private static async Task CheckAccountAsync(IWorkbenchDbContext db, Guid? accountId, CancellationToken ct)
    {
        if (accountId is not { } id)
        {
            return;
        }

        var account = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new ArgumentException("That GL account doesn't exist.");
        if (account.Type != AccountType.Expense || !account.IsActive)
        {
            throw new ArgumentException($"Benefit costs post to an active expense account; {account.Number} {account.Name} isn't one.");
        }
    }

    private async Task<List<BenefitPlanDto>> MapPlansAsync(IWorkbenchDbContext db, IReadOnlyList<D.BenefitPlan> plans, CancellationToken ct)
    {
        var accountIds = plans.Where(p => p.ExpenseAccountId != null).Select(p => p.ExpenseAccountId!.Value).ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var today = Today;
        var ids = plans.Select(p => p.Id).ToList();
        var enrolled = (await db.Set<D.BenefitElection>().AsNoTracking()
                .Where(e => ids.Contains(e.PlanId) && !e.IsWaived && e.CoverageStart <= today && (e.CoverageEnd == null || e.CoverageEnd >= today))
                .Select(e => e.PlanId).ToListAsync(ct))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        return plans.OrderBy(p => p.Type).ThenBy(p => p.Code, StringComparer.Ordinal).Select(p =>
        {
            var account = p.ExpenseAccountId is { } a ? accounts.GetValueOrDefault(a) : null;
            return new BenefitPlanDto(p.Id, p.Code, p.Name, (BenefitPlanType)(int)p.Type, p.Carrier, p.StartsOn, p.EndsOn, p.DeductionCode, p.IsPreTax,
                p.ExpenseAccountId, account?.Number, account?.Name, account?.CostClass == CostClassification.Fringe, p.IsHighDeductible, p.FullTimeOnly,
                p.AnnualElectionLimit, p.EmployerMatchRate, p.EmployerMatchCapPercent, p.EmployerMonthlyContribution, p.Description, p.IsActive,
                p.Tiers.OrderBy(t => t.Coverage).Select(t => new BenefitTierDto(t.Id, (CoverageLevel)(int)t.Coverage, t.EmployeeMonthlyCost, t.EmployerMonthlyCost)).ToList(),
                enrolled.GetValueOrDefault(p.Id));
        }).ToList();
    }

    internal static ElectionDto ToDto(D.BenefitElection e, D.BenefitPlan plan, D.BenefitTier? tier, decimal? annualPay)
    {
        var (employee, employer) = BenefitRules.MonthlyCost(plan, tier, e, annualPay);
        return new ElectionDto(e.Id, plan.Id, plan.Code, plan.Name, (BenefitPlanType)(int)e.PlanType, e.Coverage is { } c ? (CoverageLevel)(int)c : null,
            e.ContributionAmount, e.IsWaived, e.CoverageStart, e.CoverageEnd, (ElectionSource)(int)e.Source, employee, employer,
            e.Dependents.Select(d => d.DependentId).ToList(), e.ElectedBy, e.ElectedAtUtc);
    }

    private static DependentDto ToDto(D.BenefitDependent d) =>
        new(d.Id, d.FirstName, d.LastName, (DependentRelationship)(int)d.Relationship, d.BirthDate, d.IsActive);

    private static OpenEnrollmentWindowDto ToDto(D.OpenEnrollmentWindow w, DateOnly today) =>
        new(w.Id, w.Name, w.OpensOn, w.ClosesOn, w.CoverageStartsOn, w.OpensOn <= today && w.ClosesOn >= today);

    private static LifeEventDto ToDto(D.LifeEvent e, string employeeName) =>
        new(e.Id, e.EmployeeId, employeeName, (LifeEventKind)(int)e.Kind, e.EventDate, e.WindowEndsOn, e.Note, (LifeEventStatus)(int)e.Status, e.ReportedBy, e.ReportedAtUtc, e.DecidedBy);
}
