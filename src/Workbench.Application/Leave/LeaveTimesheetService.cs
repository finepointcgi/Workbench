using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Time;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Leave;

/// <summary>Leave on the employee's own timesheet: what to pre-fill (approved requests, holidays) and the balance checks.</summary>
public sealed class LeaveTimesheetService(IWorkbenchDbContextFactory dbFactory, AccessControl access, TimeProvider clock) : ILeaveTimesheetService
{
    public async Task<TimesheetLeaveDto> GetForTimesheetAsync(Guid timesheetId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheet = await db.Set<Timesheet>().AsNoTracking().Include(t => t.Entries).FirstOrDefaultAsync(t => t.Id == timesheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Timesheet", timesheetId);
        var owner = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == sheet.EmployeeId, cancellationToken);
        if (!string.Equals(owner.Email, access.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkbenchForbiddenException("Only the employee sees the leave pre-fill for their timesheet.");
        }

        var prefill = sheet.Status is TimesheetStatus.Draft or TimesheetStatus.Rejected
            ? await PrefillAsync(db, owner, sheet, cancellationToken)
            : [];
        var hours = sheet.Entries.GroupBy(e => e.WbsElementId).ToDictionary(g => g.Key, g => g.Sum(e => e.Hours));
        var checks = await LeaveTimesheetChecks.CheckAsync(db, sheet, hours, access.Email, clock.GetUtcNow().UtcDateTime, cancellationToken);
        return new TimesheetLeaveDto(sheet.Id, prefill, checks);
    }

    private static async Task<List<LeavePrefillDto>> PrefillAsync(IWorkbenchDbContext db, Employee owner, Timesheet sheet, CancellationToken ct)
    {
        var existing = sheet.Entries.Select(e => (e.WbsElementId, e.WorkDate)).ToHashSet();
        var policies = await db.Set<LeavePolicy>().AsNoTracking().ToListAsync(ct);
        var codes = await LeavePolicyService.WbsCodesAsync(db, policies.Select(p => p.WbsElementId), ct);
        var result = new List<LeavePrefillDto>();

        var days = await (
                from d in db.Set<LeaveRequestDay>().AsNoTracking()
                join r in db.Set<LeaveRequest>().AsNoTracking() on d.RequestId equals r.Id
                where r.EmployeeId == owner.Id && r.Status == LeaveRequestStatus.Approved && d.Date >= sheet.PeriodStart && d.Date <= sheet.PeriodEnd
                select new { r.PolicyId, d.Date, d.Hours })
            .ToListAsync(ct);
        foreach (var d in days)
        {
            var policy = policies.First(p => p.Id == d.PolicyId);
            if (!existing.Contains((policy.WbsElementId, d.Date)))
            {
                result.Add(new LeavePrefillDto(policy.WbsElementId, codes.GetValueOrDefault(policy.WbsElementId, "?"), d.Date, d.Hours, $"Approved {policy.Name}"));
            }
        }

        // The holiday charge code is pre-filled with the employee's standard day on each company holiday.
        if (policies.Where(p => p.IsActive && p.Type == LeaveType.Holiday).OrderBy(p => p.Code).FirstOrDefault() is { } holidayPolicy)
        {
            var holidays = await db.Set<Holiday>().AsNoTracking().Where(h => h.Date >= sheet.PeriodStart && h.Date <= sheet.PeriodEnd).ToListAsync(ct);
            foreach (var h in holidays.Where(h => owner.IsActiveOn(h.Date) && !existing.Contains((holidayPolicy.WbsElementId, h.Date))))
            {
                var hours = h.Hours ?? LeaveEngine.StandardDay(owner);
                if (hours > 0)
                {
                    result.Add(new LeavePrefillDto(holidayPolicy.WbsElementId, codes.GetValueOrDefault(holidayPolicy.WbsElementId, "?"), h.Date, hours, h.Name));
                }
            }
        }

        return result.OrderBy(p => p.Date).ThenBy(p => p.WbsCode, StringComparer.Ordinal).ToList();
    }
}

internal static class LeaveTimesheetChecks
{
    /// <summary>
    /// For each balance-tracked leave policy with hours on the timesheet: the hours against what's available — the balance
    /// less approved leave planned outside this timesheet's dates (plus this timesheet's own usage when it's already approved).
    /// Holidays aren't a balance and aren't checked.
    /// </summary>
    public static async Task<List<TimesheetLeaveCheckDto>> CheckAsync(
        IWorkbenchDbContext db, Timesheet sheet, IReadOnlyDictionary<Guid, decimal> hoursByWbs, string actor, DateTime utcNow, CancellationToken ct)
    {
        var policies = await db.Set<LeavePolicy>().AsNoTracking().Where(p => p.Type != LeaveType.Holiday).OrderBy(p => p.Code).ToListAsync(ct);
        var relevant = policies.Where(p => hoursByWbs.GetValueOrDefault(p.WbsElementId) > 0).GroupBy(p => p.WbsElementId).Select(g => g.First()).ToList();
        if (relevant.Count == 0)
        {
            return [];
        }

        await LeaveEngine.SyncUsageAsync(db, [sheet.EmployeeId], actor, utcNow, ct);
        var balances = await LeaveEngine.BalancesAsync(db, [sheet.EmployeeId], null, ct);
        var planned = await LeaveEngine.PlannedAsync(db, [sheet.EmployeeId], sheet.PeriodStart, sheet.PeriodEnd, ct);
        var codes = await LeavePolicyService.WbsCodesAsync(db, relevant.Select(p => p.WbsElementId), ct);
        var usageKey = LeaveEngine.UsageKey(sheet.Id);
        var approvedUsage = sheet.Status == TimesheetStatus.Approved
            ? await db.Set<LeaveLedgerEntry>().AsNoTracking()
                .Where(l => l.EmployeeId == sheet.EmployeeId && l.SourceKey == usageKey)
                .GroupBy(l => l.PolicyId).Select(g => new { g.Key, Hours = g.Sum(l => l.Hours) }).ToDictionaryAsync(x => x.Key, x => x.Hours, ct)
            : [];
        return relevant.Select(p =>
        {
            var key = (sheet.EmployeeId, p.Id);
            var available = balances.GetValueOrDefault(key) - planned.GetValueOrDefault(key) - approvedUsage.GetValueOrDefault(p.Id);
            return new TimesheetLeaveCheckDto(p.Id, p.Name, codes.GetValueOrDefault(p.WbsElementId, "?"), hoursByWbs[p.WbsElementId], available, p.AllowNegativeBalance);
        }).ToList();
    }
}

/// <summary>
/// Wraps <see cref="ITimesheetService"/>: saving or submitting leave hours beyond the available balance is refused when the
/// policy doesn't allow a negative balance, and adds a warning on submit when it does.
/// </summary>
public sealed class LeaveCheckingTimesheetService(ITimesheetService inner, IWorkbenchDbContextFactory dbFactory, AccessControl access, TimeProvider clock) : ITimesheetService
{
    public Task<TimesheetDetailDto> GetOrCreateMyTimesheetAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        inner.GetOrCreateMyTimesheetAsync(date, cancellationToken);

    public Task<IReadOnlyList<TimesheetDto>> ListMyTimesheetsAsync(CancellationToken cancellationToken = default) => inner.ListMyTimesheetsAsync(cancellationToken);

    public Task<TimesheetDetailDto> GetAsync(Guid timesheetId, CancellationToken cancellationToken = default) => inner.GetAsync(timesheetId, cancellationToken);

    public async Task<TimesheetDetailDto> SaveEntriesAsync(Guid timesheetId, SaveTimeEntriesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            if (await db.Set<Timesheet>().AsNoTracking().Include(t => t.Entries).FirstOrDefaultAsync(t => t.Id == timesheetId, cancellationToken) is { } sheet
                && sheet.Status is TimesheetStatus.Draft or TimesheetStatus.Rejected
                && await IsOwnerAsync(db, sheet, cancellationToken))
            {
                // The hours the timesheet would have: entries outside the saved day are kept as they are.
                var kept = sheet.Entries.Where(e => request.Day is { } day && e.WorkDate != day).Select(e => (e.WbsElementId, e.Hours));
                var hours = kept.Concat((request.Entries ?? []).Select(e => (e.WbsElementId, e.Hours)))
                    .GroupBy(x => x.WbsElementId).ToDictionary(g => g.Key, g => g.Sum(x => x.Hours));
                Refuse(await LeaveTimesheetChecks.CheckAsync(db, sheet, hours, access.Email, clock.GetUtcNow().UtcDateTime, cancellationToken));
            }
        }

        return await inner.SaveEntriesAsync(timesheetId, request, cancellationToken);
    }

    public async Task<SubmitTimesheetResult> SubmitAsync(Guid timesheetId, SubmitTimesheetRequest request, CancellationToken cancellationToken = default)
    {
        List<TimesheetLeaveCheckDto> checks = [];
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            if (await db.Set<Timesheet>().AsNoTracking().Include(t => t.Entries).FirstOrDefaultAsync(t => t.Id == timesheetId, cancellationToken) is { } sheet
                && await IsOwnerAsync(db, sheet, cancellationToken))
            {
                var hours = sheet.Entries.GroupBy(e => e.WbsElementId).ToDictionary(g => g.Key, g => g.Sum(e => e.Hours));
                checks = await LeaveTimesheetChecks.CheckAsync(db, sheet, hours, access.Email, clock.GetUtcNow().UtcDateTime, cancellationToken);
                Refuse(checks);
            }
        }

        var result = await inner.SubmitAsync(timesheetId, request, cancellationToken);
        var warnings = new TimesheetLeaveDto(timesheetId, [], checks).Warnings.ToList();
        return warnings.Count == 0 ? result : result with { Warnings = [.. result.Warnings, .. warnings] };
    }

    public Task<TimesheetDetailDto> ReopenAsync(Guid timesheetId, string reason, CancellationToken cancellationToken = default) =>
        inner.ReopenAsync(timesheetId, reason, cancellationToken);

    /// <summary>Only the owner's own saves are checked here; anyone else is refused by the timesheet service itself.</summary>
    private async Task<bool> IsOwnerAsync(IWorkbenchDbContext db, Timesheet sheet, CancellationToken ct) =>
        await db.Set<Employee>().AsNoTracking().Where(e => e.Id == sheet.EmployeeId).Select(e => e.Email).FirstOrDefaultAsync(ct) is { } email
            && string.Equals(email, access.Email, StringComparison.OrdinalIgnoreCase);

    private static void Refuse(IEnumerable<TimesheetLeaveCheckDto> checks)
    {
        if (checks.FirstOrDefault(c => c.Blocks) is { } block)
        {
            throw new ArgumentException($"{block.HoursOnTimesheet:0.##} h of {block.PolicyName} is more than the {block.Available:0.##} h you have available, "
                + "and the policy doesn't allow a negative balance. Reduce the leave hours or ask for a balance adjustment.");
        }
    }
}
