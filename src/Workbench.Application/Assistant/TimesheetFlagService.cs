using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Ceilings;
using Workbench.Application.Identity;
using Workbench.Application.Time;
using Workbench.Contracts;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Time;
using Workbench.Domain.Assistant;
using Workbench.Domain.Ceilings;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;

namespace Workbench.Application.Assistant;

/// <summary>
/// Builds <see cref="FlagSnapshot"/>s from the database and runs <see cref="TimesheetFlagEngine"/>. Only timesheets of
/// people whose time the user may review are read (<see cref="ProjectAccess.VisibleTimeEmployeeIdsAsync"/>: direct
/// reports, or everyone for Timekeeper, Finance and Admin); a user's own timesheet isn't flagged to themselves.
/// </summary>
public sealed class TimesheetFlagService(
    IWorkbenchDbContextFactory dbFactory,
    ProjectAccess projectAccess,
    AssistantRunner runner,
    TimekeepingClock timekeeping,
    TimeProvider clock) : ITimesheetFlagService
{
    private const int HistoryTimesheets = 12;

    public async Task<IReadOnlyList<TimesheetFlagsDto>> ListForTimesheetsAsync(IReadOnlyCollection<Guid> timesheetIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timesheetIds);
        var ids = timesheetIds.Distinct().Take(500).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheets = await VisibleAsync(db, db.Set<Timesheet>().AsNoTracking().Where(t => ids.Contains(t.Id)), cancellationToken);
        return await EvaluateAsync(db, sheets, cancellationToken);
    }

    public async Task<TimesheetFlagsDto> GetAsync(Guid timesheetId, CancellationToken cancellationToken = default) =>
        (await ListForTimesheetsAsync([timesheetId], cancellationToken)).SingleOrDefault()
        ?? throw WorkbenchNotFoundException.For("Timesheet", timesheetId);

    public async Task<IReadOnlyList<TimesheetFlagsDto>> ReportAsync(TimesheetFlagReportQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.To < query.From || query.To.DayNumber - query.From.DayNumber > 370)
        {
            throw new ArgumentException("Pick a date range of at most a year, with the start before the end.");
        }

        if (query.RuleKey is { } rule && !TimesheetFlagRules.All.Contains(rule))
        {
            throw new ArgumentException($"Unknown flag '{rule}'.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheets = await VisibleAsync(db, db.Set<Timesheet>().AsNoTracking()
            .Where(t => t.PeriodStart <= query.To && t.PeriodEnd >= query.From && t.Status != TimesheetStatus.Draft), cancellationToken);
        var flagged = await EvaluateAsync(db, sheets, cancellationToken);
        return flagged
            .Select(f => query.RuleKey is null ? f : f with { Flags = f.Flags.Where(x => x.RuleKey == query.RuleKey).ToList() })
            .Where(f => f.Flags.Count > 0)
            .OrderByDescending(f => f.Flags.Count(x => x.Severity == TimesheetFlagSeverity.Warning))
            .ThenByDescending(f => f.Flags.Count)
            .ThenByDescending(f => f.PeriodStart)
            .ThenBy(f => f.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<TimesheetFlagExplanationDto> ExplainAsync(Guid timesheetId, string ruleKey, CancellationToken cancellationToken = default)
    {
        var flags = await GetAsync(timesheetId, cancellationToken);
        var flag = flags.Flags.FirstOrDefault(f => f.RuleKey == ruleKey) ?? throw new WorkbenchNotFoundException("That flag isn't on this timesheet (any more).");
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var cached = await db.Set<TimesheetFlagExplanation>().AsNoTracking()
                .Where(e => e.TimesheetId == timesheetId && e.RuleKey == ruleKey && e.FactsHash == flag.FactsHash)
                .OrderByDescending(e => e.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (cached is not null)
            {
                return new TimesheetFlagExplanationDto(timesheetId, ruleKey, cached.Text, true);
            }
        }

        var settings = await runner.Gate.LoadSettingsAsync(cancellationToken);
        if (settings?.ExplainTimesheetFlags != true)
        {
            return new TimesheetFlagExplanationDto(timesheetId, ruleKey, flag.Detail, false);
        }

        // Only the flag's own facts are sent: no names, no other timesheets.
        var facts = JsonSerializer.Serialize(new
        {
            title = flag.Title,
            severity = flag.Severity.ToString(),
            detail = flag.Detail,
            facts = flag.Facts.ToDictionary(f => f.Label, f => f.Value),
            period = $"{flags.PeriodStart:yyyy-MM-dd} to {flags.PeriodEnd:yyyy-MM-dd}",
            timesheetStatus = flags.Status.ToString(),
        }, AssistantPrompts.DataJson);
        var completion = await runner.CompleteTextAsync(
            AssistantInteractionKind.FlagExplanation,
            $"Explain the “{flag.Title}” flag on a timesheet for {flags.PeriodStart:yyyy-MM-dd}",
            AssistantPrompts.FlagSystem,
            $"<flag-json>\n{facts}\n</flag-json>",
            null,
            flags.Link,
            "timesheet_flag",
            [$"Timesheet:{timesheetId}", $"Employee:{flags.EmployeeId}"],
            cancellationToken);
        if (completion is null)
        {
            return new TimesheetFlagExplanationDto(timesheetId, ruleKey, flag.Detail, false);
        }

        var text = AssistantRunner.Clip(completion.Text.Trim(), 4_000)!;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            db.Set<TimesheetFlagExplanation>().Add(new TimesheetFlagExplanation
            {
                TimesheetId = timesheetId,
                RuleKey = ruleKey,
                FactsHash = flag.FactsHash,
                Text = text,
                CreatedBy = runner.UserEmail,
                CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        return new TimesheetFlagExplanationDto(timesheetId, ruleKey, text, true);
    }

    private async Task<List<Timesheet>> VisibleAsync(IWorkbenchDbContext db, IQueryable<Timesheet> query, CancellationToken ct)
    {
        var visible = await projectAccess.VisibleTimeEmployeeIdsAsync(ct);
        var me = await projectAccess.Access.GetEmployeeIdAsync(ct);
        if (visible is not null)
        {
            var ids = visible.Where(id => id != me).ToList();
            query = query.Where(t => ids.Contains(t.EmployeeId));
        }
        else if (me is { } self)
        {
            query = query.Where(t => t.EmployeeId != self);
        }

        return await query.Take(2_000).ToListAsync(ct);
    }

    private async Task<IReadOnlyList<TimesheetFlagsDto>> EvaluateAsync(IWorkbenchDbContext db, List<Timesheet> sheets, CancellationToken ct)
    {
        if (sheets.Count == 0)
        {
            return [];
        }

        var sheetIds = sheets.Select(t => t.Id).ToList();
        var employeeIds = sheets.Select(t => t.EmployeeId).Distinct().ToList();
        var employees = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var from = sheets.Min(t => t.PeriodStart);
        var to = sheets.Max(t => t.PeriodEnd);

        // History: each person's earlier submitted or approved timesheets (up to a year back).
        var historyFrom = from.AddDays(-400);
        var historySheets = await db.Set<Timesheet>().AsNoTracking()
            .Where(t => employeeIds.Contains(t.EmployeeId) && t.PeriodStart >= historyFrom && t.PeriodStart < to
                && (t.Status == TimesheetStatus.Submitted || t.Status == TimesheetStatus.Approved))
            .ToListAsync(ct);
        var allIds = sheetIds.Concat(historySheets.Select(t => t.Id)).Distinct().ToList();
        var entries = await db.Set<TimeEntry>().AsNoTracking().Where(e => allIds.Contains(e.TimesheetId)).ToListAsync(ct);
        var wbsIds = entries.Select(e => e.WbsElementId).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var projectIds = wbs.Values.Select(w => w.ProjectId).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var flagEntries = entries.Where(e => wbs.ContainsKey(e.WbsElementId)).ToLookup(e => e.TimesheetId, e =>
        {
            var w = wbs[e.WbsElementId];
            var p = projects[w.ProjectId];
            return new FlagEntry(e.WorkDate, e.Hours, w.Id, w.Code, p.Id, p.Code, w.Kind, w.IsOpen, w.IsChargeable, p.Status);
        });

        var revisions = await db.Set<TimeEntryRevision>().AsNoTracking().Where(r => sheetIds.Contains(r.TimesheetId)).ToListAsync(ct);
        var revisionWbsIds = revisions.SelectMany(r => new[] { r.OldWbsElementId, r.NewWbsElementId }).OfType<Guid>().Except(wbs.Keys).Distinct().ToList();
        var codes = wbs.ToDictionary(w => w.Key, w => w.Value.Code);
        foreach (var w in await db.Set<WbsElement>().AsNoTracking().Where(w => revisionWbsIds.Contains(w.Id)).ToListAsync(ct))
        {
            codes[w.Id] = w.Code;
        }

        var days = (await db.Set<TimeDayRecord>().AsNoTracking().Where(d => sheetIds.Contains(d.TimesheetId)).ToListAsync(ct)).ToLookup(d => d.TimesheetId);
        var allocations = (await db.Set<ResourceAllocation>().AsNoTracking()
                .Where(a => employeeIds.Contains(a.EmployeeId) && a.WeekStart >= from.AddDays(-6) && a.WeekStart <= to)
                .ToListAsync(ct))
            .ToLookup(a => a.EmployeeId);
        var allocationProjects = allocations.SelectMany(g => g).Select(a => a.ProjectId).Where(id => !projects.ContainsKey(id)).Distinct().ToList();
        foreach (var p in await db.Set<Project>().AsNoTracking().Where(p => allocationProjects.Contains(p.Id)).ToListAsync(ct))
        {
            projects[p.Id] = p;
        }

        var holidays = (await db.Set<Holiday>().AsNoTracking().Where(h => h.Date >= from && h.Date <= to).ToListAsync(ct))
            .GroupBy(h => h.Date).ToDictionary(g => g.Key, g => g.First().Name);
        var ceilings = await CeilingsAsync(db, sheets.SelectMany(t => flagEntries[t.Id]).Select(e => e.WbsElementId).Distinct().ToList(), ct);

        var result = new List<TimesheetFlagsDto>();
        foreach (var sheet in sheets)
        {
            var employee = employees.GetValueOrDefault(sheet.EmployeeId);
            var history = historySheets
                .Where(h => h.EmployeeId == sheet.EmployeeId && h.PeriodEnd < sheet.PeriodStart)
                .OrderByDescending(h => h.PeriodStart)
                .Take(HistoryTimesheets)
                .Select(h => new FlagHistory(h.PeriodStart, h.PeriodEnd, flagEntries[h.Id].ToList()))
                .ToList();
            var snapshot = new FlagSnapshot(
                sheet.PeriodStart,
                sheet.PeriodEnd,
                sheet.Revision,
                sheet.FirstSubmittedAtUtc,
                flagEntries[sheet.Id].ToList(),
                revisions.Where(r => r.TimesheetId == sheet.Id).OrderBy(r => r.ChangedAtUtc)
                    .Select(r => new FlagRevision(r.TimesheetRevision, r.ChangedAtUtc, r.WorkDate, r.OldHours, r.NewHours,
                        r.OldWbsElementId is { } o ? codes.GetValueOrDefault(o) : null, r.NewWbsElementId is { } n ? codes.GetValueOrDefault(n) : null, r.Reason))
                    .ToList(),
                days[sheet.Id].Select(d => new FlagDay(d.WorkDate, TimekeepingClock.BusinessDaysAfter(d.WorkDate, timekeeping.LocalDate(d.FirstEnteredAtUtc)))).ToList(),
                history,
                allocations[sheet.EmployeeId]
                    .Where(a => projects.ContainsKey(a.ProjectId))
                    .Select(a => new FlagAllocation(a.ProjectId, projects[a.ProjectId].Code, a.WeekStart, a.Hours))
                    .Where(a => a.WeekStart <= sheet.PeriodEnd && a.WeekStart.AddDays(6) >= sheet.PeriodStart)
                    .ToList(),
                holidays,
                ceilings,
                timekeeping.Options.LateEntryBusinessDays);
            result.Add(new TimesheetFlagsDto(
                sheet.Id,
                sheet.EmployeeId,
                employee?.DisplayName ?? "Unknown employee",
                sheet.PeriodStart,
                sheet.PeriodEnd,
                (TimesheetState)(int)sheet.Status,
                snapshot.Entries.Sum(e => e.Hours),
                $"time/history/{sheet.Id}",
                TimesheetFlagEngine.Evaluate(snapshot)));
        }

        return result;
    }

    private async Task<IReadOnlyList<FlagCeiling>> CeilingsAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> wbsIds, CancellationToken ct)
    {
        if (wbsIds.Count == 0)
        {
            return [];
        }

        var states = await CeilingEngine.ForWbsAsync(db, wbsIds, clock.GetUtcNow().UtcDateTime, ct);
        var list = new List<FlagCeiling>();
        foreach (var state in states)
        {
            var percent = new[] { CeilingMeasure.Hours, CeilingMeasure.Cost }
                .Select(m => state.EffectiveLimit(m) is { } limit && limit > 0 ? state.Consumed(m) / limit : 0m)
                .Max();
            foreach (var wbsId in wbsIds.Where(state.Wbs.Contains))
            {
                list.Add(new FlagCeiling(wbsId, state.Label, decimal.Round(percent, 4), state.Ceiling.Enforcement == CeilingEnforcement.Block));
            }
        }

        return list;
    }
}

/// <summary>Who has submitted a timesheet for a period, among the people whose time the user may see.</summary>
public sealed class TimesheetSubmissionService(IWorkbenchDbContextFactory dbFactory, ProjectAccess projectAccess, TimekeepingClock timekeeping) : ITimesheetSubmissionService
{
    public async Task<IReadOnlyList<TimesheetSubmissionRowDto>> GetStatusAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var visible = await projectAccess.VisibleTimeEmployeeIdsAsync(cancellationToken);
        var me = await projectAccess.Access.GetEmployeeIdAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (start, end) = await timekeeping.PeriodForAsync(db, date, cancellationToken);
        var query = db.Set<Employee>().AsNoTracking();
        if (visible is not null)
        {
            // Supervisors check their reports, not themselves.
            var ids = visible.Where(id => id != me).ToList();
            query = query.Where(e => ids.Contains(e.Id));
        }

        var employees = (await query.ToListAsync(cancellationToken)).Where(e => e.HireDate <= end && (e.TerminationDate is null || e.TerminationDate >= start)).ToList();
        var ids2 = employees.Select(e => e.Id).ToList();
        var sheets = await db.Set<Timesheet>().AsNoTracking()
            .Where(t => ids2.Contains(t.EmployeeId) && t.PeriodStart <= date && t.PeriodEnd >= date)
            .ToListAsync(cancellationToken);
        var sheetIds = sheets.Select(t => t.Id).ToList();
        var hours = (await db.Set<TimeEntry>().AsNoTracking().Where(e => sheetIds.Contains(e.TimesheetId)).Select(e => new { e.TimesheetId, e.Hours }).ToListAsync(cancellationToken))
            .GroupBy(e => e.TimesheetId).ToDictionary(g => g.Key, g => g.Sum(e => e.Hours));
        var supervisorIds = employees.Select(e => e.SupervisorId).OfType<Guid>().Distinct().ToList();
        var supervisors = await db.Set<Employee>().AsNoTracking().Where(e => supervisorIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, cancellationToken);
        return employees
            .Select(e =>
            {
                var sheet = sheets.Where(t => t.EmployeeId == e.Id).OrderByDescending(t => t.UpdatedAtUtc).FirstOrDefault();
                var status = sheet is null ? (TimesheetState?)null : (TimesheetState)(int)sheet.Status;
                var periodStart = sheet?.PeriodStart ?? start;
                var periodEnd = sheet?.PeriodEnd ?? end;
                return new TimesheetSubmissionRowDto(
                    e.Id, e.DisplayName, e.SupervisorId is { } s ? supervisors.GetValueOrDefault(s) : null, periodStart, periodEnd, sheet?.Id, status,
                    sheet is null ? 0 : hours.GetValueOrDefault(sheet.Id), TimekeepingClock.StandardHours(e, periodStart, periodEnd),
                    status is null or TimesheetState.Draft or TimesheetState.Rejected);
            })
            .OrderByDescending(r => r.IsMissing)
            .ThenBy(r => r.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
