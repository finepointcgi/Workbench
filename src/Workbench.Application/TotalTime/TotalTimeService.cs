using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Time;
using Workbench.Contracts.Time;
using Workbench.Contracts.TotalTime;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.TotalTime;

/// <summary>
/// The labor cost method setting and the total time accounting reports. Costing itself lives in <see cref="LaborCostBook"/>
/// so the GL, financials, indirect rates, ICE and billing all use it.
/// </summary>
public sealed class TotalTimeService(
    IWorkbenchDbContextFactory dbFactory, TimekeepingClock clock, AuditRecorder audit, ITimesheetService timesheets) : ITotalTimeService
{
    public async Task<LaborCostSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await SettingsAsync(db, cancellationToken);
    }

    public async Task<LaborCostSettingsDto> SetMethodAsync(SetLaborCostMethodRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? throw new ArgumentException("Give a reason for the change.") : request.Reason.Trim();
        if (reason.Length > 500)
        {
            throw new ArgumentException("The reason can be at most 500 characters.");
        }

        if (!Enum.IsDefined(request.Method))
        {
            throw new ArgumentException("Choose a labor cost method.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var earliest = await EarliestChangeAsync(db, cancellationToken);
        var (start, _) = await clock.PeriodForAsync(db, request.EffectiveFrom, cancellationToken);
        if (start < earliest)
        {
            throw new ArgumentException(
                $"The method can only change from a future timesheet period, so periods already worked keep their costs. Pick {earliest:MMM d, yyyy} or later.");
        }

        var policies = await LaborCostPolicies.LoadAsync(db, cancellationToken);
        var method = (LaborCostMethod)(int)request.Method;
        if (LaborCostPolicies.MethodOn(policies, start) == method && policies.All(p => p.EffectiveFrom <= start))
        {
            throw new ArgumentException($"{LaborCostMethods.Label(request.Method)} is already the method from {start:MMM d, yyyy}.");
        }

        var policy = new LaborCostPolicy
        {
            EffectiveFrom = start,
            Method = method,
            Reason = reason,
            CreatedBy = audit.Actor,
            CreatedAtUtc = clock.UtcNow,
        };
        db.Set<LaborCostPolicy>().Add(policy);
        audit.Record(db, nameof(LaborCostPolicy), policy.Id, "Created",
            before: new { Method = LaborCostPolicies.MethodOn(policies, start).ToString(), EffectiveFrom = start },
            after: new { Method = method.ToString(), EffectiveFrom = start }, reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return await SettingsAsync(db, cancellationToken);
    }

    public async Task<TimesheetTotalTimeDto> GetTimesheetSummaryAsync(Guid timesheetId, CancellationToken cancellationToken = default)
    {
        var sheet = (await timesheets.GetAsync(timesheetId, cancellationToken)).Timesheet; // Enforces who may see it.
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == sheet.EmployeeId, cancellationToken);
        var method = (LaborCostMethodKind)(int)await LaborCostPolicies.MethodOnAsync(db, sheet.PeriodStart, cancellationToken);
        var uncompensated = employee.IsExempt ? Math.Max(0, sheet.TotalHours - sheet.StandardHours) : 0m;
        return new TimesheetTotalTimeDto(sheet.Id, employee.IsExempt, sheet.StandardHours, sheet.TotalHours, uncompensated, method,
            employee.IsExempt ? LaborCostMethods.TimesheetNote(method) : "Hours beyond your standard week are overtime, paid under your pay policy.");
    }

    public async Task<TotalTimeReportDto> GetReportAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            throw new ArgumentException("The end date can't be before the start date.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ids = await db.Set<Timesheet>().AsNoTracking()
            .Where(t => t.Status == TimesheetStatus.Approved && t.PeriodEnd >= from && t.PeriodStart <= to)
            .Select(t => t.Id).ToListAsync(cancellationToken);
        var applied = await LaborCostBook.LoadAsync(db, ids, cancellationToken);
        var standard = await LaborCostBook.LoadAsync(db, ids, LaborCostMethod.Standard, cancellationToken);
        var totalTime = await LaborCostBook.LoadAsync(db, ids, LaborCostMethod.TotalTimeEffectiveRate, cancellationToken);

        var wbsIds = applied.Sheets.Values.SelectMany(s => s.Sheet.Entries).Select(e => e.WbsElementId).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.ProjectId, cancellationToken);
        var projectIds = wbs.Values.Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);

        var rates = new List<EffectiveRateRowDto>();
        var uncompensated = new List<UncompensatedHoursRowDto>();
        var byProject = new Dictionary<Guid, (decimal Hours, decimal Unpaid, decimal Standard, decimal TotalTime, decimal Applied)>();
        foreach (var (sheet, employee, cost) in applied.Sheets.Values
                     .OrderBy(s => s.Employee.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Sheet.PeriodStart))
        {
            var hourly = cost.Entries.LastOrDefault(e => !e.MissingRate)?.BaseRate;
            rates.Add(new EffectiveRateRowDto(employee.Id, employee.EmployeeNumber, employee.DisplayName, employee.IsExempt, sheet.PeriodStart, sheet.PeriodEnd,
                sheet.Revision, (LaborCostMethodKind)(int)cost.Method, cost.StandardHours, cost.WorkedHours, cost.UncompensatedHours, hourly, cost.PeriodSalary,
                cost.LaborCost, cost.EffectiveRate, cost.EffectiveRateApplied));

            var projectOf = sheet.Entries.ToDictionary(e => e.Id, e => wbs.GetValueOrDefault(e.WbsElementId));
            foreach (var group in cost.Entries.GroupBy(e => projectOf.GetValueOrDefault(e.EntryId)).Where(g => g.Key != Guid.Empty))
            {
                var project = projects[group.Key];
                var unpaid = group.Sum(e => e.UncompensatedHours);
                if (unpaid != 0)
                {
                    uncompensated.Add(new UncompensatedHoursRowDto(employee.Id, employee.EmployeeNumber, employee.DisplayName, project.Id, project.Code, project.Name,
                        sheet.PeriodStart, sheet.PeriodEnd, group.Sum(e => e.Hours), unpaid));
                }

                var totals = byProject.GetValueOrDefault(project.Id);
                byProject[project.Id] = (totals.Hours + group.Sum(e => e.Hours), totals.Unpaid + unpaid,
                    totals.Standard + group.Sum(e => standard.Cost(e.EntryId)), totals.TotalTime + group.Sum(e => totalTime.Cost(e.EntryId)),
                    totals.Applied + group.Sum(e => e.Cost));
            }
        }

        var effect = byProject
            .Select(p => new ProjectCostEffectRowDto(p.Key, projects[p.Key].Code, projects[p.Key].Name, p.Value.Hours, p.Value.Unpaid, p.Value.Standard, p.Value.TotalTime, p.Value.Applied))
            .OrderByDescending(r => Math.Abs(r.Difference)).ThenBy(r => r.ProjectCode, StringComparer.Ordinal)
            .ToList();

        var notes = new List<string>
        {
            "Whole approved timesheet periods that overlap the dates are included, so an employee's hours and salary stay together.",
            "Uncompensated hours are hours exempt salaried employees worked beyond their standard week. They're recorded but not paid as overtime.",
        };
        var shortPeriods = totalTime.Sheets.Values.Count(s => s.Cost.IsExempt && s.Cost.WorkedHours > 0 && s.Cost.WorkedHours < s.Cost.StandardHours);
        if (shortPeriods > 0)
        {
            notes.Add($"{shortPeriods} exempt timesheet period(s) have fewer hours than the standard week. Total time accounting spreads the full salary "
                + "over the hours recorded, which raises their total time cost; record every hour, including leave, to avoid that.");
        }

        var missing = applied.Sheets.Values.Sum(s => s.Cost.MissingRateHours);
        if (missing != 0)
        {
            notes.Add($"{missing:N1} hours have no cost rate on their work date and are costed at $0.");
        }

        var current = (LaborCostMethodKind)(int)await LaborCostPolicies.MethodOnAsync(db, (await clock.PeriodForAsync(db, clock.Today, cancellationToken)).Start, cancellationToken);
        return new TotalTimeReportDto(from, to, current, rates, uncompensated
            .OrderBy(r => r.EmployeeName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.PeriodStart).ThenBy(r => r.ProjectCode, StringComparer.Ordinal).ToList(),
            effect, notes);
    }

    private async Task<DateOnly> EarliestChangeAsync(IWorkbenchDbContext db, CancellationToken ct) =>
        (await clock.PeriodForAsync(db, clock.Today, ct)).End.AddDays(1);

    private async Task<LaborCostSettingsDto> SettingsAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var policies = await LaborCostPolicies.LoadAsync(db, ct);
        var (start, _) = await clock.PeriodForAsync(db, clock.Today, ct);
        return new LaborCostSettingsDto((LaborCostMethodKind)(int)LaborCostPolicies.MethodOn(policies, start), await EarliestChangeAsync(db, ct),
            policies.AsEnumerable().Reverse()
                .Select(p => new LaborCostPolicyDto(p.Id, p.EffectiveFrom, (LaborCostMethodKind)(int)p.Method, p.Reason, p.CreatedBy, p.CreatedAtUtc)).ToList());
    }
}
