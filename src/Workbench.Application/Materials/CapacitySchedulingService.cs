using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Materials;
using Workbench.Domain.Materials;

namespace Workbench.Application.Materials;

/// <summary>
/// Work center calendars and the finite capacity schedule of open work order operations (<see cref="CapacityScheduler"/>).
/// Each run keeps the planner's queue order and pinned dates, replaces the dates and daily load, and flags operations that
/// end after their work order's due date.
/// </summary>
public sealed class CapacitySchedulingService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : ICapacitySchedulingService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    // ---- Calendars ----

    public async Task<IReadOnlyList<WorkCenterCalendarDto>> ListCalendarsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var centers = await db.Set<WorkCenter>().AsNoTracking().Where(c => c.IsActive).ToListAsync(cancellationToken);
        var result = new List<WorkCenterCalendarDto>();
        foreach (var c in centers.OrderBy(c => c.Code, StringComparer.Ordinal))
        {
            result.Add(await CalendarAsync(db, c.Id, cancellationToken));
        }

        return result;
    }

    private async Task<WorkCenterCalendarDto> CalendarAsync(IWorkbenchDbContext db, Guid workCenterId, CancellationToken ct)
    {
        var center = await db.Set<WorkCenter>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == workCenterId, ct) ?? throw WorkbenchNotFoundException.For("Work center", workCenterId);
        var calendar = await db.Set<WorkCenterCalendar>().AsNoTracking().FirstOrDefaultAsync(c => c.WorkCenterId == workCenterId, ct);
        var since = Today.AddDays(-30);
        var exceptions = await db.Set<WorkCenterCapacityException>().AsNoTracking().Where(e => e.WorkCenterId == workCenterId && e.Date >= since)
            .OrderBy(e => e.Date).Select(e => new CapacityExceptionDto(e.Id, e.Date, e.Hours, e.Reason)).ToListAsync(ct);
        var days = calendar?.WorkingDays ?? "YYYYYNN";
        var hours = calendar?.HoursPerDay ?? center.HoursPerDay;
        var units = calendar?.Units ?? 1;
        return new WorkCenterCalendarDto(center.Id, center.Code, center.Name, days, hours, units, hours * units, exceptions);
    }

    public async Task<WorkCenterCalendarDto> SaveCalendarAsync(Guid workCenterId, SaveWorkCenterCalendarRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var days = (request.WorkingDays ?? "").Trim().ToUpperInvariant();
        if (days.Length != 7 || days.Any(c => c is not ('Y' or 'N')) || !days.Contains('Y'))
        {
            throw new ArgumentException("Working days are seven Y or N letters, Monday first, with at least one working day (for example YYYYYNN).");
        }

        if (request.HoursPerDay is <= 0 or > 24 || decimal.Round(request.HoursPerDay, 2) != request.HoursPerDay)
        {
            throw new ArgumentException("Hours per day are more than 0 and at most 24, in hundredths.");
        }

        if (request.Units is < 1 or > 100)
        {
            throw new ArgumentException("Units working in parallel are between 1 and 100.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<WorkCenter>().AnyAsync(c => c.Id == workCenterId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Work center", workCenterId);
        }

        var calendar = await db.Set<WorkCenterCalendar>().FirstOrDefaultAsync(c => c.WorkCenterId == workCenterId, cancellationToken);
        var before = calendar is null ? null : new { calendar.WorkingDays, calendar.HoursPerDay, calendar.Units };
        if (calendar is null)
        {
            calendar = new WorkCenterCalendar { WorkCenterId = workCenterId, UpdatedBy = audit.Actor };
            db.Set<WorkCenterCalendar>().Add(calendar);
        }

        calendar.WorkingDays = days;
        calendar.HoursPerDay = request.HoursPerDay;
        calendar.Units = request.Units;
        calendar.UpdatedBy = audit.Actor;
        calendar.UpdatedAtUtc = UtcNow;
        audit.Record(db, nameof(WorkCenter), workCenterId, "CalendarChanged", before, new { days, request.HoursPerDay, request.Units });
        await db.SaveChangesAsync(cancellationToken);
        return await CalendarAsync(db, workCenterId, cancellationToken);
    }

    public async Task<WorkCenterCalendarDto> SaveExceptionAsync(Guid workCenterId, SaveCapacityExceptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Hours is < 0 or > 2400 || decimal.Round(request.Hours, 2) != request.Hours)
        {
            throw new ArgumentException("Hours that day are 0 or more, in hundredths.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<WorkCenter>().AnyAsync(c => c.Id == workCenterId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Work center", workCenterId);
        }

        var row = await db.Set<WorkCenterCapacityException>().FirstOrDefaultAsync(e => e.WorkCenterId == workCenterId && e.Date == request.Date, cancellationToken);
        if (row is null)
        {
            row = new WorkCenterCapacityException { WorkCenterId = workCenterId, Date = request.Date };
            db.Set<WorkCenterCapacityException>().Add(row);
        }

        row.Hours = request.Hours;
        row.Reason = PurchasingSupport.Optional(request.Reason, "The reason", 200);
        await db.SaveChangesAsync(cancellationToken);
        return await CalendarAsync(db, workCenterId, cancellationToken);
    }

    public async Task<WorkCenterCalendarDto> DeleteExceptionAsync(Guid exceptionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<WorkCenterCapacityException>().FirstOrDefaultAsync(e => e.Id == exceptionId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Capacity exception", exceptionId);
        db.Set<WorkCenterCapacityException>().Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return await CalendarAsync(db, row.WorkCenterId, cancellationToken);
    }

    /// <summary>Hours a work center can work on a date: an exception, else the calendar (or Monday–Friday at its hours per day).</summary>
    internal static async Task<Func<Guid, DateOnly, decimal>> CapacityAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var centers = await db.Set<WorkCenter>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var calendars = await db.Set<WorkCenterCalendar>().AsNoTracking().ToDictionaryAsync(c => c.WorkCenterId, ct);
        var exceptions = await db.Set<WorkCenterCapacityException>().AsNoTracking().ToDictionaryAsync(e => (e.WorkCenterId, e.Date), e => e.Hours, ct);
        return (center, day) =>
        {
            if (exceptions.TryGetValue((center, day), out var hours))
            {
                return hours;
            }

            if (calendars.TryGetValue(center, out var cal))
            {
                return cal.WorksOn(day.DayOfWeek) ? cal.HoursPerDay * cal.Units : 0;
            }

            return day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || !centers.TryGetValue(center, out var c) ? 0 : c.HoursPerDay;
        };
    }

    // ---- Scheduling ----

    public async Task<CapacityScheduleDto> RunAsync(RunScheduleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var asOf = request.AsOf ?? Today;
        await ScheduleAsync(db, request.Direction.ToDomain(), request.Finite, asOf, cancellationToken);
        return await ViewAsync(db, Monday(asOf), 8, cancellationToken);
    }

    private async Task ScheduleAsync(IWorkbenchDbContext db, ScheduleDirection direction, bool finite, DateOnly asOf, CancellationToken ct)
    {
        var orders = await db.Set<WorkOrder>().AsNoTracking().Include(w => w.Operations)
            .Where(w => w.Status == WorkOrderStatus.Released || w.Status == WorkOrderStatus.InProgress).ToListAsync(ct);
        var rows = await db.Set<OperationSchedule>().ToListAsync(ct);
        var byOp = rows.ToDictionary(r => r.WorkOrderOperationId);
        var ops = new List<CapacityScheduler.Op>();
        var keep = new HashSet<Guid>();
        var nextPosition = rows.GroupBy(r => r.WorkCenterId).ToDictionary(g => g.Key, g => g.Max(r => r.QueuePosition));
        foreach (var wo in orders.OrderBy(w => w.DueDate).ThenBy(w => w.Number, StringComparer.Ordinal))
        {
            foreach (var op in wo.Operations.OrderBy(o => o.Sequence))
            {
                var open = Math.Round(Math.Max(0, op.PlannedHours(wo.Quantity) - op.HoursReported), 2, MidpointRounding.AwayFromZero);
                if (open <= 0 || op.QuantityCompleted >= wo.Quantity)
                {
                    continue;
                }

                if (!byOp.TryGetValue(op.Id, out var row))
                {
                    var position = nextPosition.GetValueOrDefault(op.WorkCenterId) + 1;
                    nextPosition[op.WorkCenterId] = position;
                    row = new OperationSchedule { WorkOrderOperationId = op.Id, WorkOrderId = wo.Id, WorkCenterId = op.WorkCenterId, QueuePosition = position };
                    db.Set<OperationSchedule>().Add(row);
                    byOp[op.Id] = row;
                }

                row.WorkCenterId = op.WorkCenterId;
                keep.Add(row.Id);
                ops.Add(new CapacityScheduler.Op
                {
                    Id = row.Id, WorkOrderId = wo.Id, WorkOrderNumber = wo.Number, Sequence = op.Sequence, WorkCenterId = op.WorkCenterId, Hours = open,
                    EarliestStart = wo.StartDate, DueDate = wo.DueDate, QueuePosition = row.QueuePosition, PinnedStart = row.PinnedStart,
                });
            }
        }

        db.Set<OperationSchedule>().RemoveRange(rows.Where(r => !keep.Contains(r.Id)));
        db.Set<OperationLoad>().RemoveRange(await db.Set<OperationLoad>().ToListAsync(ct));
        var scheduler = new CapacityScheduler(await CapacityAsync(db, ct), finite, asOf);
        scheduler.Schedule(ops, direction);
        var rowsById = byOp.Values.ToDictionary(r => r.Id);
        foreach (var op in ops)
        {
            var row = rowsById[op.Id];
            row.Start = op.Start;
            row.End = op.End;
            row.Hours = op.Hours;
            row.IsLate = op.IsLate;
            row.Problem = op.Problem;
            row.ScheduledAtUtc = UtcNow;
            db.Set<OperationLoad>().AddRange(op.Load.Where(l => l.Value > 0)
                .Select(l => new OperationLoad { OperationScheduleId = row.Id, WorkCenterId = op.WorkCenterId, Date = l.Key, Hours = Math.Round(l.Value, 2, MidpointRounding.AwayFromZero) }));
        }

        var text = string.Join("\n", scheduler.Messages.Take(30));
        db.Set<CapacityScheduleRun>().Add(new CapacityScheduleRun
        {
            Direction = direction, Finite = finite, AsOf = asOf, OperationCount = ops.Count, LateCount = ops.Count(o => o.IsLate),
            Messages = text.Length == 0 ? null : text.Length > 4000 ? text[..4000] : text, RunBy = audit.Actor, RunAtUtc = UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private static DateOnly Monday(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    public async Task<CapacityScheduleDto> GetAsync(DateOnly? from = null, int weeks = 8, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await ViewAsync(db, Monday(from ?? Today), Math.Clamp(weeks, 1, 52), cancellationToken);
    }

    private async Task<CapacityScheduleDto> ViewAsync(IWorkbenchDbContext db, DateOnly from, int weeks, CancellationToken ct)
    {
        var run = await db.Set<CapacityScheduleRun>().AsNoTracking().OrderByDescending(r => r.RunAtUtc).FirstOrDefaultAsync(ct);
        var rows = await db.Set<OperationSchedule>().AsNoTracking().ToListAsync(ct);
        var woIds = rows.Select(r => r.WorkOrderId).Distinct().ToList();
        var orders = await db.Set<WorkOrder>().AsNoTracking().Include(w => w.Operations).Where(w => woIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var partIds = orders.Values.Select(w => w.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Display, ct);
        var centers = await db.Set<WorkCenter>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var to = from.AddDays(7 * weeks);
        var loads = await db.Set<OperationLoad>().AsNoTracking().Where(l => l.Date >= from && l.Date < to).ToListAsync(ct);
        var capacity = await CapacityAsync(db, ct);
        var weekStarts = Enumerable.Range(0, weeks).Select(i => from.AddDays(7 * i)).ToList();
        var shownCenters = centers.Values.Where(c => c.IsActive || rows.Any(r => r.WorkCenterId == c.Id)).OrderBy(c => c.Code, StringComparer.Ordinal).ToList();
        var load = shownCenters.Select(c => new WorkCenterLoadDto(c.Id, c.Code, c.Name, weekStarts.Select(w =>
        {
            var cap = Enumerable.Range(0, 7).Sum(d => capacity(c.Id, w.AddDays(d)));
            var hours = loads.Where(l => l.WorkCenterId == c.Id && l.Date >= w && l.Date < w.AddDays(7)).Sum(l => l.Hours);
            return new WorkCenterWeekLoadDto(w, cap, hours, hours > cap);
        }).ToList())).ToList();
        var operations = rows.Where(r => orders.ContainsKey(r.WorkOrderId))
            .Select(r =>
            {
                var wo = orders[r.WorkOrderId];
                var op = wo.Operations.FirstOrDefault(o => o.Id == r.WorkOrderOperationId);
                return new ScheduledOperationDto(r.Id, wo.Id, wo.Number, parts.GetValueOrDefault(wo.PartId) ?? "", r.WorkOrderOperationId, op?.Sequence ?? 0, op?.Description ?? "",
                    r.WorkCenterId, centers.GetValueOrDefault(r.WorkCenterId)?.Code ?? "", r.QueuePosition, r.Start, r.End, r.Hours, wo.DueDate, r.IsLate, r.PinnedStart is not null, r.Problem);
            })
            .OrderBy(o => o.WorkCenter, StringComparer.Ordinal).ThenBy(o => o.QueuePosition).ToList();
        var messages = (run?.Messages ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (run is null)
        {
            messages.Insert(0, "Not scheduled yet: run the schedule to place open operations against capacity.");
        }

        return new CapacityScheduleDto(run?.RunAtUtc, (run?.Direction ?? ScheduleDirection.Forward).ToContract(), run?.Finite ?? true, from, weekStarts, load, operations,
            operations.Count(o => o.IsLate), messages);
    }

    private async Task RescheduleAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var run = await db.Set<CapacityScheduleRun>().AsNoTracking().OrderByDescending(r => r.RunAtUtc).FirstOrDefaultAsync(ct);
        await ScheduleAsync(db, run?.Direction ?? ScheduleDirection.Forward, run?.Finite ?? true, Today, ct);
    }

    public async Task<CapacityScheduleDto> MoveAsync(Guid scheduleId, MoveOperationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<OperationSchedule>().FirstOrDefaultAsync(r => r.Id == scheduleId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Scheduled operation", scheduleId);
        if (request.PinnedStart is { } d && d < Today.AddDays(-7))
        {
            throw new ArgumentException("Pick a date from this week on.");
        }

        var before = row.PinnedStart;
        row.PinnedStart = request.PinnedStart;
        audit.Record(db, nameof(WorkOrder), row.WorkOrderId, "OperationPinned", new { PinnedStart = before }, new { request.PinnedStart });
        await db.SaveChangesAsync(cancellationToken);
        await RescheduleAsync(db, cancellationToken);
        return await ViewAsync(db, Monday(request.PinnedStart is { } p && p < Today ? p : Today), 8, cancellationToken);
    }

    public async Task<CapacityScheduleDto> ResequenceAsync(Guid workCenterId, ResequenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<OperationSchedule>().Where(r => r.WorkCenterId == workCenterId).ToListAsync(cancellationToken);
        var ids = request.ScheduleIds ?? [];
        if (ids.Distinct().Count() != ids.Count || ids.Any(id => rows.All(r => r.Id != id)))
        {
            throw new ArgumentException("List each of the work center's scheduled operations once.");
        }

        var position = 0;
        foreach (var id in ids)
        {
            rows.First(r => r.Id == id).QueuePosition = ++position;
        }

        foreach (var r in rows.Where(r => !ids.Contains(r.Id)).OrderBy(r => r.QueuePosition))
        {
            r.QueuePosition = ++position;
        }

        audit.Record(db, nameof(WorkCenter), workCenterId, "QueueResequenced", after: new { Operations = ids.Count });
        await db.SaveChangesAsync(cancellationToken);
        await RescheduleAsync(db, cancellationToken);
        return await ViewAsync(db, Monday(Today), 8, cancellationToken);
    }
}
