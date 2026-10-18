using Microsoft.EntityFrameworkCore;
using Workbench.Application.Billing;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Evm;
using Workbench.Domain.Evm;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Scheduling;

namespace Workbench.Application.Evm;

public sealed class EvmService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, BillingCostSource costs, TimeProvider clock) : IEvmService
{
    private const int MaxText = 4000;

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<EvmBaselineDto?> GetBaselineAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EvmEngine.RequireProjectAsync(db, projectId, cancellationToken);
        return await EvmBaselines.ToDtoAsync(db, projectId, Today, [], cancellationToken);
    }

    public async Task<EvmBaselineDto> CreateBaselineAsync(Guid projectId, CreateEvmBaselineRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ControlAccountLevel is < 1 or > 10)
        {
            throw new ArgumentException("The control account level must be between 1 and 10.");
        }

        if (request.ManagementReserve < 0 || request.UndistributedBudget < 0)
        {
            throw new ArgumentException("Management reserve and undistributed budget can't be negative.");
        }

        if (request.DefaultTechnique is EvTechnique.Milestones or EvTechnique.Apportioned || !Enum.IsDefined(request.DefaultTechnique))
        {
            throw new ArgumentException("The default technique must be 0/100, 50/50, percent complete or level of effort; set milestones and apportioned work packages afterwards.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await EvmEngine.RequireProjectAsync(db, projectId, cancellationToken);
        var existing = await db.Set<EvmBaseline>().FirstOrDefaultAsync(b => b.ProjectId == projectId, cancellationToken);
        if (existing is not null)
        {
            if (await db.Set<EvmStatusPeriod>().AnyAsync(s => s.ProjectId == projectId, cancellationToken)
                || await db.Set<EvmChangeRequest>().AnyAsync(c => c.ProjectId == projectId, cancellationToken))
            {
                throw new ArgumentException("The PMB has a closed status period or change requests; change it through a baseline change request.");
            }

            var old = await db.Set<EvmWorkPackage>().Include(w => w.Milestones).Where(w => w.BaselineId == existing.Id).ToListAsync(cancellationToken);
            db.Set<EvmWorkPackage>().RemoveRange(old);
            db.Set<EvmBaseline>().Remove(existing);
            audit.Record(db, nameof(EvmBaseline), existing.Id, "Replaced", before: new { existing.BudgetVersionId, WorkPackages = old.Count });
            await db.SaveChangesAsync(cancellationToken);
        }

        var version = request.BudgetVersionId is { } versionId
            ? await db.Set<BudgetVersion>().AsNoTracking().Include(v => v.Lines).FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Budget version", versionId)
            : await db.Set<BudgetVersion>().AsNoTracking().Include(v => v.Lines).FirstOrDefaultAsync(v => v.ProjectId == projectId && v.Kind == BudgetKind.Baseline, cancellationToken)
                ?? throw new ArgumentException("This project has no Baseline budget. Create and lock one under Budgets first.");
        if (version.ProjectId != projectId)
        {
            throw new ArgumentException("The budget version belongs to another project.");
        }

        if (!version.IsLocked)
        {
            throw new ArgumentException($"Lock \"{version.Name}\" before building the PMB from it.");
        }

        Baseline? schedule = null;
        if (request.ScheduleBaselineId is { } scheduleId)
        {
            schedule = await db.Set<Baseline>().AsNoTracking().Include(b => b.Entries).FirstOrDefaultAsync(b => b.Id == scheduleId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Schedule baseline", scheduleId);
            if (schedule.SheetId != project.SheetId)
            {
                throw new ArgumentException("The schedule baseline isn't from this project's plan sheet.");
            }
        }
        else if (project.SheetId is { } sheetId)
        {
            schedule = await db.Set<Baseline>().AsNoTracking().Include(b => b.Entries)
                .Where(b => b.SheetId == sheetId).OrderByDescending(b => b.CapturedAtUtc).FirstOrDefaultAsync(cancellationToken);
        }

        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToDictionaryAsync(w => w.Id, cancellationToken);
        var items = project.SheetId is { } sid
            ? await db.Items.AsNoTracking().Where(i => i.SheetId == sid).ToDictionaryAsync(i => i.Id, cancellationToken)
            : [];
        var entries = schedule?.Entries.GroupBy(e => e.ItemId).ToDictionary(g => g.Key, g => g.First()) ?? [];
        var periods = await db.Set<FiscalPeriod>().AsNoTracking().ToListAsync(cancellationToken);

        var now = clock.GetUtcNow().UtcDateTime;
        var baseline = new EvmBaseline
        {
            ProjectId = projectId,
            BudgetVersionId = version.Id,
            ScheduleBaselineId = schedule?.Id,
            ControlAccountLevel = request.ControlAccountLevel,
            ManagementReserve = Financials.Money.Round(request.ManagementReserve),
            UndistributedBudget = Financials.Money.Round(request.UndistributedBudget),
            CreatedAtUtc = now,
            CreatedBy = audit.Actor,
            UpdatedAtUtc = now,
        };

        var fallbacks = new List<string>();
        foreach (var group in version.Lines.GroupBy(l => l.WbsElementId).OrderBy(g => wbs.GetValueOrDefault(g.Key)?.Code, StringComparer.Ordinal))
        {
            var amount = group.Sum(l => l.Amount);
            if (amount == 0 || !wbs.TryGetValue(group.Key, out var element))
            {
                continue;
            }

            if (!EvmEngine.IsLeaf(wbs, element.Id))
            {
                // Budget above the work packages hasn't been distributed to work yet.
                baseline.UndistributedBudget += amount;
                fallbacks.Add($"{element.Code} isn't a WBS leaf; its {amount:N2} went to undistributed budget.");
                continue;
            }

            var (start, finish, how) = PlannedDates(element, entries, items, group.ToList(), periods, project);
            if (how is not null)
            {
                fallbacks.Add($"{element.Code}: {how}");
            }

            db.Set<EvmWorkPackage>().Add(new EvmWorkPackage
            {
                BaselineId = baseline.Id,
                WbsElementId = element.Id,
                ControlAccountWbsId = EvmEngine.ControlAccountOf(wbs, element.Id, request.ControlAccountLevel),
                Technique = (EarnedValueTechnique)(int)request.DefaultTechnique,
                Budget = amount,
                PlannedStart = start,
                PlannedFinish = finish,
            });
        }

        db.Set<EvmBaseline>().Add(baseline);
        audit.Record(db, nameof(EvmBaseline), baseline.Id, "Created", after: new
        {
            BudgetVersion = version.Name,
            Schedule = schedule?.Name,
            baseline.ControlAccountLevel,
            baseline.ManagementReserve,
            baseline.UndistributedBudget,
        });
        await db.SaveChangesAsync(cancellationToken);
        return (await EvmBaselines.ToDtoAsync(db, projectId, Today, fallbacks, cancellationToken))!;
    }

    /// <summary>
    /// A work package's dates: its task in the schedule baseline, else the task's current dates, else the budget's own phasing,
    /// else the project's period of performance. The third value explains a fallback.
    /// </summary>
    private (DateOnly Start, DateOnly Finish, string? How) PlannedDates(
        WbsElement element,
        IReadOnlyDictionary<Guid, BaselineEntry> entries,
        IReadOnlyDictionary<Guid, Domain.Item> items,
        IReadOnlyList<BudgetLine> lines,
        IReadOnlyList<FiscalPeriod> periods,
        Project project)
    {
        if (element.ItemId is { } itemId)
        {
            if (entries.TryGetValue(itemId, out var entry) && entry.Start is { } s && entry.Finish is { } f)
            {
                return Ordered(s, f, null);
            }

            if (items.TryGetValue(itemId, out var item) && item.Start is { } s2 && item.Finish is { } f2)
            {
                return Ordered(s2, f2, entries.Count == 0 ? null : "not in the schedule baseline; used the task's current dates.");
            }
        }

        var phased = lines.Where(l => l.PeriodStart is not null).Select(l => l.PeriodStart!.Value).ToList();
        if (phased.Count > 0)
        {
            var last = phased.Max();
            var end = periods.FirstOrDefault(p => p.Start == last)?.End ?? last.AddMonths(1).AddDays(-1);
            return Ordered(phased.Min(), end, "no plan task dates; phased like its budget.");
        }

        if (project.Start is { } ps && project.Finish is { } pf)
        {
            return Ordered(ps, pf, "no plan task dates or phasing; spread over the project's period of performance.");
        }

        return Ordered(Today, Today.AddYears(1).AddDays(-1), "no dates anywhere; spread over the next year.");
    }

    private static (DateOnly, DateOnly, string?) Ordered(DateOnly a, DateOnly b, string? how) => a <= b ? (a, b, how) : (b, a, how);

    public async Task<EvmBaselineDto> UpdateWorkPackageAsync(Guid workPackageId, UpdateWorkPackageRequest request, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.Technique))
        {
            throw new ArgumentException("Unknown earned value technique.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wp = await db.Set<EvmWorkPackage>().Include(w => w.Milestones).FirstOrDefaultAsync(w => w.Id == workPackageId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Work package", workPackageId);
        var baseline = await db.Set<EvmBaseline>().FirstAsync(b => b.Id == wp.BaselineId, cancellationToken);
        if (await EvmBaselines.HasClosedPeriodsAsync(db, baseline.ProjectId, cancellationToken))
        {
            throw new ArgumentException("A status period is closed, so techniques and milestone weights change through a baseline change request.");
        }

        var before = EvmBaselines.Describe(wp);
        var technique = (EarnedValueTechnique)(int)request.Technique;
        if (technique == EarnedValueTechnique.Apportioned)
        {
            var target = request.ApportionedToId is { } t
                ? await db.Set<EvmWorkPackage>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == t && w.BaselineId == wp.BaselineId, cancellationToken)
                : null;
            if (target is null || target.Id == wp.Id)
            {
                throw new ArgumentException("Choose the discrete work package this one is apportioned to.");
            }

            if (target.Technique is EarnedValueTechnique.Apportioned or EarnedValueTechnique.LevelOfEffort)
            {
                throw new ArgumentException("Apportion to a discrete work package (not level of effort or another apportioned one).");
            }

            wp.ApportionedToId = target.Id;
        }
        else
        {
            if (await db.Set<EvmWorkPackage>().AnyAsync(w => w.ApportionedToId == wp.Id, cancellationToken)
                && technique is EarnedValueTechnique.LevelOfEffort)
            {
                throw new ArgumentException("Other work packages are apportioned to this one, so it must stay discrete.");
            }

            wp.ApportionedToId = null;
        }

        wp.Technique = technique;
        if (request.Milestones is { } milestones)
        {
            ReplaceMilestones(db, wp, milestones);
        }
        else if (technique == EarnedValueTechnique.Milestones && wp.Milestones.Count == 0)
        {
            var project = await EvmEngine.RequireProjectAsync(db, baseline.ProjectId, cancellationToken);
            var plan = await EvmEngine.LoadPlanAsync(db, project, cancellationToken);
            var element = await db.Set<WbsElement>().AsNoTracking().FirstAsync(w => w.Id == wp.WbsElementId, cancellationToken);
            foreach (var m in EvmBaselines.MilestonesFromPlan(wp, element, plan))
            {
                wp.Milestones.Add(m);
                db.Set<EvmMilestone>().Add(m);
            }
        }

        if (technique == EarnedValueTechnique.Milestones && wp.Milestones.Count == 0)
        {
            throw new ArgumentException("Add at least one milestone.");
        }

        baseline.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, nameof(EvmWorkPackage), wp.Id, "TechniqueChanged", before, EvmBaselines.Describe(wp));
        await db.SaveChangesAsync(cancellationToken);
        return (await EvmBaselines.ToDtoAsync(db, baseline.ProjectId, Today, [], cancellationToken))!;
    }

    private static void ReplaceMilestones(IWorkbenchDbContext db, EvmWorkPackage wp, IReadOnlyList<SaveMilestoneRequest> milestones)
    {
        if (milestones.Count == 0)
        {
            throw new ArgumentException("Add at least one milestone.");
        }

        if (milestones.Any(m => m.Weight <= 0) || milestones.Sum(m => m.Weight) != 100m)
        {
            throw new ArgumentException("Milestone weights must be positive and add up to 100.");
        }

        var completed = wp.Milestones.Where(m => m.CompletedOn is not null).ToDictionary(m => m.Name, m => m.CompletedOn, StringComparer.OrdinalIgnoreCase);
        db.Set<EvmMilestone>().RemoveRange(wp.Milestones);
        wp.Milestones.Clear();
        var order = 0;
        foreach (var m in milestones)
        {
            var name = m.Name.Required("Milestone name");
            if (name.Length > 200)
            {
                throw new ArgumentException("Milestone names are limited to 200 characters.");
            }

            var milestone = new EvmMilestone
            {
                WorkPackageId = wp.Id,
                Name = name,
                Weight = m.Weight,
                Order = order++,
                ItemId = m.ItemId,
                PlannedDate = m.PlannedDate,
                CompletedOn = completed.GetValueOrDefault(name),
            };
            wp.Milestones.Add(milestone);
            db.Set<EvmMilestone>().Add(milestone);
        }
    }

    public async Task<EvmBaselineDto> SetThresholdsAsync(Guid projectId, SetEvmThresholdsRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ThresholdPercent is <= 0 or > 100 || request.ThresholdAmount < 0)
        {
            throw new ArgumentException("The variance threshold must be between 0 and 100% and the dollar threshold can't be negative.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var baseline = await RequireBaselineAsync(db, projectId, cancellationToken);
        var before = new { baseline.ThresholdPercent, baseline.ThresholdAmount };
        baseline.ThresholdPercent = request.ThresholdPercent;
        baseline.ThresholdAmount = Financials.Money.Round(request.ThresholdAmount);
        baseline.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, nameof(EvmBaseline), baseline.Id, "ThresholdsChanged", before, new { baseline.ThresholdPercent, baseline.ThresholdAmount });
        await db.SaveChangesAsync(cancellationToken);
        return (await EvmBaselines.ToDtoAsync(db, projectId, Today, [], cancellationToken))!;
    }

    public async Task<EvmBaselineDto> SetProgressAsync(Guid workPackageId, SetWorkPackageProgressRequest request, CancellationToken cancellationToken = default)
    {
        if (request.PercentComplete is < 0 or > 100)
        {
            throw new ArgumentException("% complete must be between 0 and 100.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wp = await db.Set<EvmWorkPackage>().FirstOrDefaultAsync(w => w.Id == workPackageId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Work package", workPackageId);
        if (wp.Technique is EarnedValueTechnique.LevelOfEffort or EarnedValueTechnique.Apportioned or EarnedValueTechnique.Milestones)
        {
            throw new ArgumentException("This work package doesn't take a % complete (it's level of effort, apportioned or earned by milestones).");
        }

        var before = wp.PercentCompleteOverride;
        wp.PercentCompleteOverride = request.PercentComplete is { } p ? Math.Round(p, 2) : null;
        wp.ProgressUpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        wp.ProgressUpdatedBy = audit.Actor;
        audit.Record(db, nameof(EvmWorkPackage), wp.Id, "ProgressEntered", new { PercentComplete = before }, new { PercentComplete = wp.PercentCompleteOverride });
        await db.SaveChangesAsync(cancellationToken);
        var projectId = await db.Set<EvmBaseline>().Where(b => b.Id == wp.BaselineId).Select(b => b.ProjectId).FirstAsync(cancellationToken);
        return (await EvmBaselines.ToDtoAsync(db, projectId, Today, [], cancellationToken))!;
    }

    public async Task<EvmBaselineDto> SetMilestoneCompletionAsync(Guid milestoneId, SetMilestoneCompletionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.CompletedOn is { } on && on > Today)
        {
            throw new ArgumentException("A milestone can't be completed in the future.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var milestone = await db.Set<EvmMilestone>().FirstOrDefaultAsync(m => m.Id == milestoneId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Milestone", milestoneId);
        var before = milestone.CompletedOn;
        milestone.CompletedOn = request.CompletedOn;
        audit.Record(db, nameof(EvmMilestone), milestone.Id, "CompletionChanged", new { CompletedOn = before }, new { milestone.CompletedOn });
        await db.SaveChangesAsync(cancellationToken);
        var projectId = await EvmLookup.ProjectOfMilestoneAsync(db, milestoneId, cancellationToken);
        return (await EvmBaselines.ToDtoAsync(db, projectId!.Value, Today, [], cancellationToken))!;
    }

    public async Task<IReadOnlyList<EvmPeriodRefDto>> ListPeriodsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var p = await EvmEngine.LoadAsync(db, projectId, Today, cancellationToken);
        return p is null ? [] : EvmEngine.Span(p).Select(period => EvmEngine.RefOf(p, period)).ToList();
    }

    public async Task<EvmStatusDto> GetStatusAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var p = await RequireLoadedAsync(db, projectId, cancellationToken);
        var period = EvmEngine.PeriodOf(p, fiscalPeriodId);
        var (rows, basis) = await EvmEngine.RowsAsync(p, period, costs, db, cancellationToken);
        return EvmEngine.BuildStatus(p, period, rows, basis);
    }

    public async Task<EvmSeriesDto> GetSeriesAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var p = await RequireLoadedAsync(db, projectId, cancellationToken);
        return await EvmEngine.SeriesAsync(p, costs, db, cancellationToken);
    }

    public async Task<EvmPeriodRefDto> ClosePeriodAsync(Guid projectId, Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var p = await RequireLoadedAsync(db, projectId, cancellationToken);
        var period = EvmEngine.PeriodOf(p, fiscalPeriodId);
        if (p.ClosedFor(period.Id) is not null)
        {
            throw new ArgumentException($"{period.Name} is already closed.");
        }

        if (period.Start > Today)
        {
            throw new ArgumentException($"{period.Name} hasn't started yet.");
        }

        if (p.Closed.LastOrDefault() is { } later && later.PeriodStart > period.Start)
        {
            throw new ArgumentException($"Status periods close in order and {later.PeriodEnd:yyyy-MM} is already closed.");
        }

        var cost = await EvmEngine.CumulativeCostAsync(costs, db, projectId, period.End, cancellationToken);
        var rows = EvmEngine.LiveRows(p, period, cost);
        var now = clock.GetUtcNow().UtcDateTime;
        var status = new EvmStatusPeriod
        {
            ProjectId = projectId,
            FiscalPeriodId = period.Id,
            PeriodStart = period.Start,
            PeriodEnd = period.End,
            BaselineRevision = p.Baseline.Revision,
            ManagementReserve = p.Baseline.ManagementReserve,
            UndistributedBudget = p.Baseline.UndistributedBudget,
            ClosedAtUtc = now,
            ClosedBy = audit.Actor,
        };
        status.Snapshots.AddRange(rows.Select(r => new EvmPeriodSnapshot
        {
            StatusPeriodId = status.Id,
            WorkPackageId = r.WorkPackageId,
            WbsElementId = r.WbsElementId,
            ControlAccountWbsId = r.ControlAccountId,
            Technique = r.Technique,
            Budget = r.Budget,
            Bcws = r.Bcws,
            Bcwp = r.Bcwp,
            Acwp = r.Acwp,
            PercentComplete = r.Percent,
        }));
        db.Set<EvmStatusPeriod>().Add(status);

        // Milestones seen done on the plan sheet keep the date they were first counted, so later periods don't move.
        var milestoneIds = p.WorkPackages.SelectMany(w => w.Milestones)
            .Where(m => m.CompletedOn is null && m.ItemId is { } i && p.Plan.IsDone(i)).Select(m => m.Id).ToList();
        if (milestoneIds.Count > 0)
        {
            foreach (var m in await db.Set<EvmMilestone>().Where(m => milestoneIds.Contains(m.Id)).ToListAsync(cancellationToken))
            {
                m.CompletedOn = Today < period.End ? Today : period.End;
            }
        }

        audit.Record(db, nameof(EvmStatusPeriod), status.Id, "Closed", after: new
        {
            Period = period.Name,
            Bcws = rows.Sum(r => r.Bcws),
            Bcwp = rows.Sum(r => r.Bcwp),
            Acwp = rows.Sum(r => r.Acwp),
            p.Baseline.Revision,
        });
        await db.SaveChangesAsync(cancellationToken);
        return new EvmPeriodRefDto(period.Id, period.Name, period.Start, period.End, EvmPeriodState.Closed, status.ClosedAtUtc, status.ClosedBy);
    }

    public async Task<EvmPeriodRefDto> ReopenPeriodAsync(Guid projectId, Guid fiscalPeriodId, ReopenEvmPeriodRequest request, CancellationToken cancellationToken = default)
    {
        var reason = request.Reason.Required("A reason");
        if (reason.Length > 1000)
        {
            throw new ArgumentException("The reason is limited to 1000 characters.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EvmEngine.RequireProjectAsync(db, projectId, cancellationToken);
        var closed = (await db.Set<EvmStatusPeriod>().Where(s => s.ProjectId == projectId && s.ReopenedAtUtc == null).ToListAsync(cancellationToken))
            .OrderBy(s => s.PeriodStart).ToList();
        var target = closed.FirstOrDefault(s => s.FiscalPeriodId == fiscalPeriodId)
            ?? throw new ArgumentException("That period isn't closed.");
        if (closed[^1] != target)
        {
            throw new ArgumentException($"Reopen {closed[^1].PeriodEnd:yyyy-MM} first: only the latest closed period can be reopened.");
        }

        target.ReopenedAtUtc = clock.GetUtcNow().UtcDateTime;
        target.ReopenedBy = audit.Actor;
        target.ReopenReason = reason;
        audit.Record(db, nameof(EvmStatusPeriod), target.Id, "Reopened", reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstAsync(f => f.Id == fiscalPeriodId, cancellationToken);
        var state = period.Start > Today ? EvmPeriodState.Future : period.End >= Today ? EvmPeriodState.Reporting : EvmPeriodState.Unstatused;
        return new EvmPeriodRefDto(period.Id, period.Name, period.Start, period.End, state, null, null);
    }

    public async Task<IReadOnlyList<EvmVarianceDto>> ListVariancesAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var p = await RequireLoadedAsync(db, projectId, cancellationToken);
        var period = EvmEngine.PeriodOf(p, fiscalPeriodId);
        var (rows, basis) = await EvmEngine.RowsAsync(p, period, costs, db, cancellationToken);
        return Variances(p, period, EvmEngine.BuildStatus(p, period, rows, basis));
    }

    internal static List<EvmVarianceDto> Variances(EvmProject p, FiscalPeriod period, EvmStatusDto status) =>
        status.ControlAccounts.Select(ca =>
        {
            var v = p.Variances.FirstOrDefault(x => x.ControlAccountWbsId == ca.WbsElementId && x.FiscalPeriodId == period.Id);
            var m = ca.Cumulative;
            return new EvmVarianceDto(ca.WbsElementId, ca.Code, ca.Name, period.Id, period.Name, ca.NeedsVar, ca.VarianceReasons,
                m.Sv, m.Cv, m.SvPercent, m.CvPercent, m.Bac, m.Eac, m.Vac, v?.Cause, v?.Impact, v?.CorrectiveAction, v?.ManagerEac,
                v?.UpdatedAtUtc, v?.UpdatedBy, status.IsFrozen);
        }).ToList();

    public async Task<EvmVarianceDto> SaveVarianceAsync(Guid projectId, Guid fiscalPeriodId, Guid controlAccountWbsId, SaveVarianceRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ManagerEac is < 0)
        {
            throw new ArgumentException("The manager's EAC can't be negative.");
        }

        if ((request.Cause?.Length ?? 0) > MaxText || (request.Impact?.Length ?? 0) > MaxText || (request.CorrectiveAction?.Length ?? 0) > MaxText)
        {
            throw new ArgumentException($"Each narrative is limited to {MaxText} characters.");
        }

        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var p = await RequireLoadedAsync(db, projectId, cancellationToken);
            var period = EvmEngine.PeriodOf(p, fiscalPeriodId);
            if (p.ClosedFor(period.Id) is not null)
            {
                throw new ArgumentException($"{period.Name} is closed; its variance analysis is frozen.");
            }

            if (!p.WorkPackages.Any(w => w.ControlAccountWbsId == controlAccountWbsId))
            {
                throw WorkbenchNotFoundException.For("Control account", controlAccountWbsId);
            }

            var v = await db.Set<EvmVarianceAnalysis>().FirstOrDefaultAsync(
                x => x.ProjectId == projectId && x.FiscalPeriodId == fiscalPeriodId && x.ControlAccountWbsId == controlAccountWbsId, cancellationToken);
            var before = v is null ? null : new { v.Cause, v.Impact, v.CorrectiveAction, v.ManagerEac };
            if (v is null)
            {
                v = new EvmVarianceAnalysis { ProjectId = projectId, FiscalPeriodId = fiscalPeriodId, ControlAccountWbsId = controlAccountWbsId, UpdatedBy = audit.Actor };
                db.Set<EvmVarianceAnalysis>().Add(v);
            }

            v.Cause = Clean(request.Cause);
            v.Impact = Clean(request.Impact);
            v.CorrectiveAction = Clean(request.CorrectiveAction);
            v.ManagerEac = request.ManagerEac is { } eac ? Financials.Money.Round(eac) : null;
            v.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            v.UpdatedBy = audit.Actor;
            audit.Record(db, nameof(EvmVarianceAnalysis), v.Id, before is null ? "Created" : "Updated", before, new { v.Cause, v.Impact, v.CorrectiveAction, v.ManagerEac });
            await db.SaveChangesAsync(cancellationToken);
        }

        var all = await ListVariancesAsync(projectId, fiscalPeriodId, cancellationToken);
        return all.First(x => x.ControlAccountWbsId == controlAccountWbsId);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private async Task<EvmProject> RequireLoadedAsync(IWorkbenchDbContext db, Guid projectId, CancellationToken ct) =>
        await EvmEngine.LoadAsync(db, projectId, Today, ct)
            ?? throw new ArgumentException("This project has no performance measurement baseline yet.");

    private static async Task<EvmBaseline> RequireBaselineAsync(IWorkbenchDbContext db, Guid projectId, CancellationToken ct)
    {
        await EvmEngine.RequireProjectAsync(db, projectId, ct);
        return await db.Set<EvmBaseline>().FirstOrDefaultAsync(b => b.ProjectId == projectId, ct)
            ?? throw new ArgumentException("This project has no performance measurement baseline yet.");
    }
}
