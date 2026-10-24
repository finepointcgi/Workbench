using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Time;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;

namespace Workbench.Application.Time;

/// <summary>
/// The employee's start/stop timer. Stopping goes through <see cref="ITimesheetService.SaveEntriesAsync"/> for the day, with
/// every entry already there plus the timer's hours on its code, so the timesheet's rules (open period, chargeable code, the
/// 24-hour day, revisions with a reason after submitting) apply exactly as when typing. Nothing is added to a submitted or
/// approved timesheet: the caller is asked to recall it first, and the timer keeps running meanwhile.
/// </summary>
public sealed class TimesheetTimerService(
    IWorkbenchDbContextFactory dbFactory,
    ICurrentUser currentUser,
    ITimesheetService timesheets,
    IProjectService projects,
    TimekeepingClock clock) : ITimesheetTimerService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const int MaxNoteLength = 500;

    private decimal Increment => clock.Options.TimerIncrementHours is > 0 and <= 24 and var i ? i : 0.25m;

    public async Task<TimerStateDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await MeAsync(db, cancellationToken);
        var today = clock.Today;
        var codes = (await projects.ListChargeCodesAsync(me.Id, today, cancellationToken)).Where(c => c.Kind != ChargeCodeKind.Leave).ToList();
        var ids = codes.Select(c => c.WbsElementId).ToList();

        var recent = await db.Set<TimeEntry>().AsNoTracking()
            .Where(e => ids.Contains(e.WbsElementId) && db.Set<Timesheet>().Any(t => t.Id == e.TimesheetId && t.EmployeeId == me.Id))
            .OrderByDescending(e => e.UpdatedAtUtc)
            .Select(e => e.WbsElementId)
            .Take(50)
            .ToListAsync(cancellationToken);
        var staffed = await StaffedAsync(db, me.Id, today, cancellationToken);
        var defaultId = recent.Cast<Guid?>().FirstOrDefault() ?? codes.Select(c => c.WbsElementId).Cast<Guid?>().FirstOrDefault(id => staffed.Contains(id!.Value));

        var choices = codes
            .Select(c => new TimerChargeCodeDto(c, staffed.Contains(c.WbsElementId), recent.Contains(c.WbsElementId)))
            .OrderByDescending(c => c.ChargeCode.WbsElementId == defaultId)
            .ThenByDescending(c => c.RecentlyUsed)
            .ThenByDescending(c => c.Staffed)
            .ThenBy(c => c.ChargeCode.Code, StringComparer.Ordinal)
            .ToList();

        var timer = await db.Set<RunningTimer>().AsNoTracking().FirstOrDefaultAsync(t => t.EmployeeId == me.Id, cancellationToken);
        var todaySheet = await db.Set<Timesheet>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.EmployeeId == me.Id && t.PeriodStart <= today && t.PeriodEnd >= today, cancellationToken);
        return new TimerStateDto(
            timer is null ? null : await ToDtoAsync(db, timer, codes, cancellationToken),
            choices,
            defaultId,
            Increment,
            todaySheet?.Status is TimesheetStatus.Submitted or TimesheetStatus.Approved);
    }

    public async Task<RunningTimerDto> StartAsync(StartTimerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var note = NormalizeNote(request.Note);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await MeAsync(db, cancellationToken);
        if (await db.Set<RunningTimer>().AsNoTracking().FirstOrDefaultAsync(t => t.EmployeeId == me.Id, cancellationToken) is { } running)
        {
            var label = await LabelAsync(db, running.WbsElementId, cancellationToken);
            throw new ArgumentException($"A timer is already running on {label} since {clock.LocalTime(running.StartedAtUtc).ToString("h:mm tt", Inv)}. Stop it first.");
        }

        // The same rules as typing hours today: a code you can charge today, in an open period.
        var today = clock.Today;
        var codes = await projects.ListChargeCodesAsync(me.Id, today, cancellationToken);
        if (codes.All(c => c.WbsElementId != request.WbsElementId))
        {
            throw new ArgumentException("You can't charge that code today: it isn't one of your open charge codes.");
        }

        var day = await TodayAsync(today, cancellationToken);
        if (day is { IsInOpenPeriod: false })
        {
            throw new ArgumentException($"{today:yyyy-MM-dd} isn't in an open fiscal period, so time can't be recorded today.");
        }

        if (day is { IsEmployed: false })
        {
            throw new ArgumentException($"{today:yyyy-MM-dd} is outside your employment dates.");
        }

        var timer = new RunningTimer { EmployeeId = me.Id, WbsElementId = request.WbsElementId, StartedAtUtc = clock.UtcNow, Note = note };
        db.Set<RunningTimer>().Add(timer);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another device started one at the same moment: the unique index keeps it to one.
            throw new ArgumentException("A timer is already running. Stop it first.");
        }

        return await ToDtoAsync(db, timer, codes, cancellationToken);
    }

    public async Task<RunningTimerDto> SetNoteAsync(string? note, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await MeAsync(db, cancellationToken);
        var timer = await db.Set<RunningTimer>().FirstOrDefaultAsync(t => t.EmployeeId == me.Id, cancellationToken)
            ?? throw new ArgumentException("No timer is running.");
        timer.Note = NormalizeNote(note);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, timer, await projects.ListChargeCodesAsync(me.Id, clock.Today, cancellationToken), cancellationToken);
    }

    public async Task DiscardAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await MeAsync(db, cancellationToken);
        if (await db.Set<RunningTimer>().FirstOrDefaultAsync(t => t.EmployeeId == me.Id, cancellationToken) is { } timer)
        {
            db.Set<RunningTimer>().Remove(timer);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<StopTimerResult> StopAsync(StopTimerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await MeAsync(db, cancellationToken);
        var timer = await db.Set<RunningTimer>().FirstOrDefaultAsync(t => t.EmployeeId == me.Id, cancellationToken)
            ?? throw new ArgumentException("No timer is running.");
        var now = clock.UtcNow;
        var wbsId = request.WbsElementId ?? timer.WbsElementId;
        var note = request.Note is not null ? NormalizeNote(request.Note) : timer.Note;
        var actual = SplitByDay(timer.StartedAtUtc, now);

        // A timer over midnight: the person says how the time splits across the days.
        List<TimerDayHours> days;
        if (actual.Count > 1 && request.Split is null)
        {
            var suggestion = actual.Select(d => new TimerDayHours(d.Date, Round(d.Hours))).ToList();
            return new StopTimerResult(TimerStopOutcome.NeedsSplit, suggestion,
                $"This timer started {clock.LocalTime(timer.StartedAtUtc).ToString("ddd MMM d 'at' h:mm tt", Inv)} and ran past midnight. Choose how to split its {Format(Round(actual.Sum(d => d.Hours)))}.");
        }

        if (request.Split is { } split)
        {
            days = ValidateSplit(split, actual);
        }
        else
        {
            days = [new TimerDayHours(actual[0].Date, Round(actual[0].Hours))];
        }

        days = days.Where(d => d.Hours > 0).ToList();
        if (days.Count == 0)
        {
            db.Set<RunningTimer>().Remove(timer);
            await db.SaveChangesAsync(cancellationToken);
            return new StopTimerResult(TimerStopOutcome.TooShort, [],
                $"The timer ran for less than half of the {Format(Increment)} increment, so nothing was added.");
        }

        // Check every day before changing any, so a split never lands half-way.
        var details = new List<(TimerDayHours Day, TimesheetDetailDto Detail)>();
        foreach (var day in days)
        {
            var detail = await timesheets.GetOrCreateMyTimesheetAsync(day.Date, cancellationToken);
            if (detail.Timesheet.IsLocked)
            {
                var state = detail.Timesheet.Status == TimesheetState.Approved ? "approved" : "submitted";
                return new StopTimerResult(TimerStopOutcome.NeedsRecall, days,
                    $"Your timesheet for {day.Date.ToString("ddd MMM d", Inv)} is {state}. {(state == "approved" ? "Reopen it for correction" : "Recall it")} to add the timer's {Format(day.Hours)}; the timer keeps running.",
                    detail.Timesheet.Id, detail.Timesheet.Status);
            }

            if (detail.Days.FirstOrDefault(d => d.Date == day.Date) is { CanEnter: false } closed)
            {
                throw new ArgumentException(closed switch
                {
                    { IsInOpenPeriod: false } => $"{day.Date:yyyy-MM-dd} isn't in an open fiscal period, so the timer's hours can't be added.",
                    { IsEmployed: false } => $"{day.Date:yyyy-MM-dd} is outside your employment dates.",
                    _ => $"Time can't be entered for {day.Date:yyyy-MM-dd}.",
                });
            }

            if (detail.ChargeCodes.FirstOrDefault(c => c.ChargeCode.WbsElementId == wbsId) is not { } code || !code.ChargeableDates.Contains(day.Date))
            {
                throw new ArgumentException($"You can't charge that code on {day.Date:yyyy-MM-dd}. Pick another code to stop the timer, or discard it.");
            }

            details.Add((day, detail));
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? $"Timer {clock.LocalTime(timer.StartedAtUtc).ToString("MMM d h:mm tt", Inv)} to {clock.LocalTime(now).ToString("MMM d h:mm tt", Inv)}"
            : request.Reason.Trim();
        foreach (var (day, detail) in details)
        {
            await timesheets.SaveEntriesAsync(detail.Timesheet.Id, AddTo(detail, day, wbsId, note, detail.Timesheet.ChangesNeedReason ? reason : null), cancellationToken);
        }

        db.Set<RunningTimer>().Remove(timer);
        await db.SaveChangesAsync(cancellationToken);
        var label = await LabelAsync(db, wbsId, cancellationToken);
        return new StopTimerResult(TimerStopOutcome.Added, days,
            $"Added {string.Join(" and ", days.Select(d => $"{Format(d.Hours)} on {d.Date.ToString("ddd MMM d", Inv)}"))} to {label}.");
    }

    /// <summary>
    /// The day's entries as they are, plus <paramref name="day"/>'s hours on the code (added to an entry already there) and
    /// the note appended to its comment.
    /// </summary>
    public static SaveTimeEntriesRequest AddTo(TimesheetDetailDto detail, TimerDayHours day, Guid wbsElementId, string? note, string? reason)
    {
        var entries = detail.Entries.Where(e => e.WorkDate == day.Date)
            .Select(e => new TimeEntryInput(e.WbsElementId, e.WorkDate, e.Hours, e.Comment, e.Id, e.LaborCategoryId))
            .ToList();
        var at = entries.FindIndex(e => e.WbsElementId == wbsElementId);
        if (at >= 0)
        {
            var existing = entries[at];
            entries[at] = existing with { Hours = existing.Hours + day.Hours, Comment = AppendComment(existing.Comment, note) };
        }
        else
        {
            entries.Add(new TimeEntryInput(wbsElementId, day.Date, day.Hours, AppendComment(null, note)));
        }

        return new SaveTimeEntriesRequest(entries, day.Date, reason);
    }

    /// <summary>Adds the note on a new line; a note already in the comment isn't repeated. Kept within the 1,000-character limit.</summary>
    public static string? AppendComment(string? comment, string? note)
    {
        if (string.IsNullOrWhiteSpace(note) || (comment?.Contains(note.Trim(), StringComparison.Ordinal) ?? false))
        {
            return comment;
        }

        var combined = string.IsNullOrWhiteSpace(comment) ? note.Trim() : $"{comment.TrimEnd()}\n{note.Trim()}";
        return combined.Length <= 1000 ? combined : combined[..1000];
    }

    /// <summary>Hours rounded to the nearest increment (half an increment rounds up).</summary>
    public static decimal RoundTo(decimal hours, decimal increment) =>
        Math.Round(hours / increment, MidpointRounding.AwayFromZero) * increment;

    private decimal Round(decimal hours) => RoundTo(hours, Increment);

    /// <summary>The time between two instants on each local day it touches, first day first.</summary>
    public List<(DateOnly Date, decimal Hours)> SplitByDay(DateTime startUtc, DateTime endUtc)
    {
        var start = clock.LocalTime(startUtc);
        var end = clock.LocalTime(endUtc);
        if (end < start)
        {
            end = start;
        }

        var result = new List<(DateOnly, decimal)>();
        for (var date = DateOnly.FromDateTime(start); date <= DateOnly.FromDateTime(end); date = date.AddDays(1))
        {
            var from = Max(start, date.ToDateTime(TimeOnly.MinValue));
            var to = Min(end, date.AddDays(1).ToDateTime(TimeOnly.MinValue));
            result.Add((date, (decimal)(to - from).TotalHours));
        }

        return result;

        static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
        static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    }

    /// <summary>A split must use the timer's own days, in whole increments, and can't add more time than the timer ran.</summary>
    private List<TimerDayHours> ValidateSplit(IReadOnlyList<TimerDayHours> split, List<(DateOnly Date, decimal Hours)> actual)
    {
        var allowed = actual.Select(a => a.Date).ToHashSet();
        if (split.Any(s => !allowed.Contains(s.Date)))
        {
            throw new ArgumentException("Split the timer only across the days it ran.");
        }

        if (split.GroupBy(s => s.Date).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Give each day once.");
        }

        if (split.Any(s => s.Hours < 0 || RoundTo(s.Hours, Increment) != s.Hours))
        {
            throw new ArgumentException($"Hours must be in steps of {Format(Increment)}.");
        }

        var ran = Math.Ceiling(actual.Sum(a => a.Hours) / Increment) * Increment;
        if (split.Sum(s => s.Hours) > ran)
        {
            throw new ArgumentException($"The timer ran {Format(ran)} at most, so the split can't add more.");
        }

        return split.OrderBy(s => s.Date).ToList();
    }

    private async Task<RunningTimerDto> ToDtoAsync(IWorkbenchDbContext db, RunningTimer timer, IReadOnlyList<ChargeCodeDto> codes, CancellationToken cancellationToken)
    {
        var code = codes.FirstOrDefault(c => c.WbsElementId == timer.WbsElementId) ?? await CodeAsync(db, timer.WbsElementId, cancellationToken);
        var elapsed = (decimal)Math.Max(0, (clock.UtcNow - timer.StartedAtUtc).TotalHours);
        var startDate = clock.LocalDate(timer.StartedAtUtc);
        return new RunningTimerDto(timer.Id, code, timer.StartedAtUtc, startDate, timer.Note, Math.Round(elapsed, 4), Round(elapsed), startDate < clock.Today);
    }

    /// <summary>A code's details when it's no longer chargeable today (the timer started on it before it closed).</summary>
    private static async Task<ChargeCodeDto> CodeAsync(IWorkbenchDbContext db, Guid wbsElementId, CancellationToken cancellationToken)
    {
        var element = await db.Set<WbsElement>().AsNoTracking().FirstAsync(w => w.Id == wbsElementId, cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstAsync(p => p.Id == element.ProjectId, cancellationToken);
        return new ChargeCodeDto(element.Id, element.Code, element.Name, project.Id, project.Code, project.Name, (ChargeCodeKind)(int)element.Kind, Guid.Empty);
    }

    private static async Task<string> LabelAsync(IWorkbenchDbContext db, Guid wbsElementId, CancellationToken cancellationToken)
    {
        var element = await db.Set<WbsElement>().AsNoTracking().Where(w => w.Id == wbsElementId).Select(w => new { w.Code, w.Name }).FirstOrDefaultAsync(cancellationToken);
        return element is null ? "that code" : string.Equals(element.Name, element.Code, StringComparison.OrdinalIgnoreCase) ? element.Code : $"{element.Name} ({element.Code})";
    }

    /// <summary>Codes the person is staffed on around <paramref name="today"/>: this week's hours or a total-hours assignment.</summary>
    private static async Task<HashSet<Guid>> StaffedAsync(IWorkbenchDbContext db, Guid employeeId, DateOnly today, CancellationToken cancellationToken)
    {
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var weekly = await db.Set<ResourceAllocation>().AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && a.WbsElementId != null && a.Hours > 0 && a.WeekStart == weekStart)
            .Select(a => a.WbsElementId!.Value).ToListAsync(cancellationToken);
        var assigned = await db.Set<StaffingAssignment>().AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && a.WbsElementId != null && a.From <= today && a.To >= today && a.TotalHours > 0)
            .Select(a => a.WbsElementId!.Value).ToListAsync(cancellationToken);
        return weekly.Concat(assigned).ToHashSet();
    }

    private async Task<TimesheetDayDto?> TodayAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var detail = await timesheets.GetOrCreateMyTimesheetAsync(today, cancellationToken);
        return detail.Days.FirstOrDefault(d => d.Date == today);
    }

    private async Task<Employee> MeAsync(IWorkbenchDbContext db, CancellationToken cancellationToken) =>
        await TimesheetQueries.FindByEmailAsync(db, currentUser.Email, cancellationToken)
            ?? throw new UnauthorizedAccessException("You don't have an employee record, so you can't record time.");

    private static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        note = note.Trim();
        return note.Length <= MaxNoteLength ? note : throw new ArgumentException($"Notes can be at most {MaxNoteLength} characters.");
    }

    private static string Format(decimal hours) => $"{hours.ToString("0.##", Inv)} h";
}
