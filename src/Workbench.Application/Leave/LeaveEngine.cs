using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Leave;
using Workbench.Domain.Leave;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Leave;

/// <summary>
/// The leave ledger's rules in one place. A balance is the sum of an employee's <see cref="LeaveLedgerEntry"/> rows for a
/// policy. Usage is never typed: it comes from approved timesheet hours on the policy's charge code, kept in step by
/// <see cref="SyncUsageAsync"/> with one net row per timesheet (a reopened timesheet's usage is reversed by a new row,
/// never deleted). Accrual and year-end runs are idempotent through <see cref="LeaveLedgerEntry.SourceKey"/>.
/// </summary>
internal static class LeaveEngine
{
    public const string AuditEntityType = "LeaveBalance";

    public static string AccrualKey(Guid payPeriodId) => $"accrual:{payPeriodId}";

    public static string GrantKey(int year) => $"grant:{year}";

    public static string YearEndKey(int year) => $"yearend:{year}";

    public static string UsageKey(Guid timesheetId) => $"usage:{timesheetId}";

    /// <summary>
    /// Brings usage rows in line with approved timesheets for the given employees (all when null): adds the difference
    /// between approved leave hours and the usage already recorded for each timesheet. Saves when anything changed.
    /// </summary>
    public static async Task SyncUsageAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid>? employeeIds, string actor, DateTime utcNow, CancellationToken ct)
    {
        // Holidays come from the calendar and aren't a balance, so their hours are never usage.
        var policies = await db.Set<LeavePolicy>().AsNoTracking().Where(p => p.Type != LeaveType.Holiday).OrderBy(p => p.Code).ToListAsync(ct);
        if (policies.Count == 0)
        {
            return;
        }

        var policyByWbs = policies.GroupBy(p => p.WbsElementId).ToDictionary(g => g.Key, g => g.First().Id);
        var wbsIds = policyByWbs.Keys.ToList();
        var approved = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                where t.Status == TimesheetStatus.Approved && wbsIds.Contains(e.WbsElementId)
                select new { t.Id, t.EmployeeId, t.PeriodEnd, e.WbsElementId, e.Hours })
            .ToListAsync(ct);
        if (employeeIds is not null)
        {
            approved = approved.Where(a => employeeIds.Contains(a.EmployeeId)).ToList();
        }

        var target = approved
            .GroupBy(a => (a.EmployeeId, PolicyId: policyByWbs[a.WbsElementId], Key: UsageKey(a.Id)))
            .ToDictionary(g => g.Key, g => (Hours: -g.Sum(a => a.Hours), Date: g.First().PeriodEnd));

        var usageRows = await db.Set<LeaveLedgerEntry>().AsNoTracking().Where(l => l.Kind == LeaveLedgerKind.Usage).ToListAsync(ct);
        if (employeeIds is not null)
        {
            usageRows = usageRows.Where(r => employeeIds.Contains(r.EmployeeId)).ToList();
        }

        var recorded = usageRows.GroupBy(r => (r.EmployeeId, r.PolicyId, Key: r.SourceKey ?? ""))
            .ToDictionary(g => g.Key, g => (Hours: g.Sum(r => r.Hours), Date: g.Max(r => r.Date)));

        var changed = false;
        foreach (var key in target.Keys.Union(recorded.Keys))
        {
            var want = target.TryGetValue(key, out var t) ? t.Hours : 0m;
            var have = recorded.TryGetValue(key, out var r) ? r.Hours : 0m;
            if (want == have)
            {
                continue;
            }

            db.Set<LeaveLedgerEntry>().Add(new LeaveLedgerEntry
            {
                EmployeeId = key.EmployeeId,
                PolicyId = key.PolicyId,
                Date = t.Date != default ? t.Date : r.Date,
                Kind = LeaveLedgerKind.Usage,
                Hours = want - have,
                SourceKey = key.Key,
                Reason = have == 0 ? "Approved timesheet" : want == 0 ? "Timesheet reopened" : "Timesheet corrected",
                CreatedBy = actor,
                CreatedAtUtc = utcNow,
            });
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Balance per (employee, policy) from every row dated on or before <paramref name="asOf"/> (all rows when null).</summary>
    public static async Task<Dictionary<(Guid EmployeeId, Guid PolicyId), decimal>> BalancesAsync(
        IWorkbenchDbContext db, IReadOnlyCollection<Guid>? employeeIds, DateOnly? asOf, CancellationToken ct)
    {
        var rows = await db.Set<LeaveLedgerEntry>().AsNoTracking()
            .Where(l => asOf == null || l.Date <= asOf)
            .Select(l => new { l.EmployeeId, l.PolicyId, l.Hours })
            .ToListAsync(ct);
        return rows.Where(r => employeeIds is null || employeeIds.Contains(r.EmployeeId))
            .GroupBy(r => (r.EmployeeId, r.PolicyId))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Hours));
    }

    /// <summary>
    /// Approved request hours not yet taken on an approved timesheet, per (employee, policy), optionally ignoring dates
    /// in <paramref name="excludeFrom"/>..<paramref name="excludeTo"/> (the timesheet being checked).
    /// </summary>
    public static async Task<Dictionary<(Guid EmployeeId, Guid PolicyId), decimal>> PlannedAsync(
        IWorkbenchDbContext db, IReadOnlyCollection<Guid>? employeeIds, DateOnly? excludeFrom, DateOnly? excludeTo, CancellationToken ct)
    {
        var days = await (
                from d in db.Set<LeaveRequestDay>().AsNoTracking()
                join r in db.Set<LeaveRequest>().AsNoTracking() on d.RequestId equals r.Id
                join p in db.Set<LeavePolicy>().AsNoTracking() on r.PolicyId equals p.Id
                where r.Status == LeaveRequestStatus.Approved
                select new { r.EmployeeId, r.PolicyId, p.WbsElementId, d.Date, d.Hours })
            .ToListAsync(ct);
        days = days.Where(d => (employeeIds is null || employeeIds.Contains(d.EmployeeId))
            && !(excludeFrom is { } from && excludeTo is { } to && d.Date >= from && d.Date <= to)).ToList();
        if (days.Count == 0)
        {
            return [];
        }

        var employees = days.Select(d => d.EmployeeId).Distinct().ToList();
        var taken = (await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                where t.Status == TimesheetStatus.Approved && employees.Contains(t.EmployeeId)
                select new { t.EmployeeId, e.WbsElementId, e.WorkDate })
            .ToListAsync(ct)).Select(x => (x.EmployeeId, x.WbsElementId, x.WorkDate)).ToHashSet();
        return days.Where(d => !taken.Contains((d.EmployeeId, d.WbsElementId, d.Date)))
            .GroupBy(d => (d.EmployeeId, d.PolicyId))
            .ToDictionary(g => g.Key, g => g.Sum(d => d.Hours));
    }

    public static int YearsOfService(Employee employee, DateOnly asOf)
    {
        var years = asOf.Year - employee.HireDate.Year;
        if (asOf < employee.HireDate.AddYears(years))
        {
            years--;
        }

        return Math.Max(0, years);
    }

    /// <summary>The highest tier the employee has reached, or null when the policy has none they reach.</summary>
    public static LeaveAccrualTier? TierFor(LeavePolicy policy, int yearsOfService) =>
        policy.Tiers.Where(t => t.MinYearsOfService <= yearsOfService).MaxBy(t => t.MinYearsOfService);

    public static decimal? CapFor(LeavePolicy policy, LeaveAccrualTier? tier) => tier?.MaxBalance ?? policy.MaxBalance;

    /// <summary>
    /// Accrues every active accruing policy for the employees in the pay period's pay group it applies to. Per-period and per-hour
    /// policies accrue once per pay period; annual grants once per calendar year (the year of the period's end).
    /// The balance cap stops accrual; the waiting period delays it from the hire date.
    /// </summary>
    public static async Task<AccrualRunResultDto> AccrueAsync(IWorkbenchDbContext db, PayPeriod period, string actor, DateTime utcNow, CancellationToken ct)
    {
        var policies = await db.Set<LeavePolicy>().AsNoTracking().Include(p => p.Tiers)
            .Where(p => p.IsActive && p.Method != AccrualMethod.None).OrderBy(p => p.Code).ToListAsync(ct);
        var employeeIds = await db.Set<EmployeePayProfile>().AsNoTracking()
            .Where(p => p.PayGroupId == period.PayGroupId).Select(p => p.EmployeeId).ToListAsync(ct);
        var employees = (await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToListAsync(ct))
            .Where(e => e.IsActiveOn(period.End)).OrderBy(e => e.DisplayName).ToList();
        var notes = new List<string>();
        if (policies.Count == 0 || employees.Count == 0)
        {
            notes.Add(policies.Count == 0 ? "No active accruing leave policies." : "No active employees in this pay group.");
            return new AccrualRunResultDto(period.Id, period.Start, period.End, 0, 0, 0, notes);
        }

        var ids = employees.Select(e => e.Id).ToList();
        var eligibility = await LeaveEligibility.LoadAsync(db, ids, ct);
        await SyncUsageAsync(db, ids, actor, utcNow, ct);
        var balances = await BalancesAsync(db, ids, period.End, ct);
        // A key counts as accrued while its rows net above zero: a voided payroll's reversal lets the period accrue again.
        var existingKeys = (await db.Set<LeaveLedgerEntry>().AsNoTracking()
                .Where(l => ids.Contains(l.EmployeeId) && l.SourceKey != null && (l.Kind == LeaveLedgerKind.Accrual || l.Kind == LeaveLedgerKind.Grant))
                .Select(l => new { l.EmployeeId, l.PolicyId, l.SourceKey, l.Hours })
                .ToListAsync(ct))
            .GroupBy(k => (k.EmployeeId, k.PolicyId, k.SourceKey!)).Where(g => g.Sum(k => k.Hours) > 0).Select(g => g.Key).ToHashSet();

        Dictionary<Guid, decimal>? worked = null;
        if (policies.Any(p => p.Method == AccrualMethod.PerHourWorked))
        {
            worked = (await (
                    from e in db.Set<TimeEntry>().AsNoTracking()
                    join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                    join w in db.Set<WbsElement>().AsNoTracking() on e.WbsElementId equals w.Id
                    where t.Status == TimesheetStatus.Approved && w.Kind != ChargeKind.Leave
                        && e.WorkDate >= period.Start && e.WorkDate <= period.End && ids.Contains(t.EmployeeId)
                    select new { t.EmployeeId, e.Hours })
                .ToListAsync(ct))
                .GroupBy(x => x.EmployeeId).ToDictionary(g => g.Key, g => g.Sum(x => x.Hours));
        }

        int added = 0, already = 0;
        decimal total = 0;
        foreach (var employee in employees)
        {
            foreach (var policy in policies)
            {
                if (!eligibility.Applies(policy, employee))
                {
                    continue; // The policy doesn't apply to them (Leave › Policies › Applies to).
                }

                var key = policy.Method == AccrualMethod.AnnualGrant ? GrantKey(period.End.Year) : AccrualKey(period.Id);
                if (existingKeys.Contains((employee.Id, policy.Id, key)))
                {
                    already++;
                    continue;
                }

                // Waiting period: no accrual until the employee has been employed long enough.
                var eligibleFrom = employee.HireDate.AddDays(policy.WaitingPeriodDays);
                if (period.End < eligibleFrom)
                {
                    notes.Add($"{employee.DisplayName}: {policy.Name} starts accruing {eligibleFrom:yyyy-MM-dd} (waiting period).");
                    continue;
                }

                var tier = TierFor(policy, YearsOfService(employee, period.End));
                if (tier is null)
                {
                    continue;
                }

                var hours = policy.Method switch
                {
                    AccrualMethod.PerPayPeriod or AccrualMethod.AnnualGrant => tier.Rate,
                    AccrualMethod.PerHourWorked => (worked?.GetValueOrDefault(employee.Id) ?? 0) * tier.Rate,
                    _ => 0,
                };
                hours = Math.Round(hours, 2, MidpointRounding.AwayFromZero);
                var balance = balances.GetValueOrDefault((employee.Id, policy.Id));
                if (CapFor(policy, tier) is { } cap && balance + hours > cap)
                {
                    var capped = Math.Max(0, cap - balance);
                    notes.Add($"{employee.DisplayName}: {policy.Name} capped at {cap:0.##} h ({capped:0.##} of {hours:0.##} h accrued).");
                    hours = capped;
                }

                if (hours <= 0)
                {
                    continue;
                }

                db.Set<LeaveLedgerEntry>().Add(new LeaveLedgerEntry
                {
                    EmployeeId = employee.Id,
                    PolicyId = policy.Id,
                    Date = period.End,
                    Kind = policy.Method == AccrualMethod.AnnualGrant ? LeaveLedgerKind.Grant : LeaveLedgerKind.Accrual,
                    Hours = hours,
                    SourceKey = key,
                    Reason = policy.Method == AccrualMethod.AnnualGrant
                        ? $"Annual grant {period.End.Year}"
                        : $"Pay period {period.Start:yyyy-MM-dd} – {period.End:yyyy-MM-dd}",
                    CreatedBy = actor,
                    CreatedAtUtc = utcNow,
                });
                balances[(employee.Id, policy.Id)] = balance + hours;
                added++;
                total += hours;
            }
        }

        await db.SaveChangesAsync(ct);
        return new AccrualRunResultDto(period.Id, period.Start, period.End, added, total, already, notes);
    }

    /// <summary>
    /// Reverses what a pay period's accrual run added (its per-period accruals, and an annual grant made in it) with
    /// negative rows under the same source keys, so the balance drops back and accruing the period again adds them
    /// again. Idempotent: only what still nets above zero is reversed. Not saved.
    /// </summary>
    public static async Task<(int Entries, decimal Hours)> ReverseAccrualAsync(IWorkbenchDbContext db, PayPeriod period, string reason, string actor, DateTime utcNow, CancellationToken ct)
    {
        var accrualKey = AccrualKey(period.Id);
        var grantKey = GrantKey(period.End.Year);
        var rows = await db.Set<LeaveLedgerEntry>().AsNoTracking()
            .Where(l => (l.Kind == LeaveLedgerKind.Accrual && l.SourceKey == accrualKey)
                || (l.Kind == LeaveLedgerKind.Grant && l.SourceKey == grantKey && l.Date == period.End))
            .ToListAsync(ct);
        int count = 0;
        decimal hours = 0;
        foreach (var g in rows.GroupBy(r => (r.EmployeeId, r.PolicyId, r.Kind, r.SourceKey)).OrderBy(g => g.Key.EmployeeId).ThenBy(g => g.Key.PolicyId))
        {
            var net = g.Sum(r => r.Hours);
            if (net <= 0)
            {
                continue;
            }

            db.Set<LeaveLedgerEntry>().Add(new LeaveLedgerEntry
            {
                EmployeeId = g.Key.EmployeeId,
                PolicyId = g.Key.PolicyId,
                Date = period.End,
                Kind = g.Key.Kind,
                Hours = -net,
                SourceKey = g.Key.SourceKey,
                Reason = Truncate($"Reversed: payroll for {period.Start:yyyy-MM-dd} – {period.End:yyyy-MM-dd} voided ({reason})", 500),
                CreatedBy = actor,
                CreatedAtUtc = utcNow,
            });
            count++;
            hours += net;
        }

        return (count, hours);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>Forfeits hours above each policy's carryover limit as of December 31 of <paramref name="year"/>. Idempotent per year.</summary>
    public static async Task<YearEndResultDto> YearEndAsync(IWorkbenchDbContext db, int year, string actor, DateTime utcNow, CancellationToken ct)
    {
        var yearEnd = new DateOnly(year, 12, 31);
        await SyncUsageAsync(db, null, actor, utcNow, ct);
        var policies = await db.Set<LeavePolicy>().AsNoTracking().Where(p => p.CarryoverLimit != null).ToListAsync(ct);
        var key = YearEndKey(year);
        var done = (await db.Set<LeaveLedgerEntry>().AsNoTracking().Where(l => l.SourceKey == key)
            .Select(l => new { l.EmployeeId, l.PolicyId }).ToListAsync(ct)).Select(x => (x.EmployeeId, x.PolicyId)).ToHashSet();
        var balances = await BalancesAsync(db, null, yearEnd, ct);
        int count = 0;
        decimal hours = 0;
        foreach (var policy in policies)
        {
            foreach (var ((employeeId, policyId), balance) in balances.Where(b => b.Key.PolicyId == policy.Id))
            {
                if (done.Contains((employeeId, policyId)) || balance <= policy.CarryoverLimit!.Value)
                {
                    continue;
                }

                var forfeit = balance - policy.CarryoverLimit.Value;
                db.Set<LeaveLedgerEntry>().Add(new LeaveLedgerEntry
                {
                    EmployeeId = employeeId,
                    PolicyId = policyId,
                    Date = yearEnd,
                    Kind = LeaveLedgerKind.Forfeiture,
                    Hours = -forfeit,
                    SourceKey = key,
                    Reason = $"Year-end {year}: carryover limited to {policy.CarryoverLimit:0.##} h",
                    CreatedBy = actor,
                    CreatedAtUtc = utcNow,
                });
                count++;
                hours += forfeit;
            }
        }

        await db.SaveChangesAsync(ct);
        return new YearEndResultDto(year, count, hours);
    }

    /// <summary>Business days in [start, end] that aren't holidays.</summary>
    public static async Task<List<DateOnly>> WorkingDaysAsync(IWorkbenchDbContext db, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var holidays = (await db.Set<Holiday>().AsNoTracking().Where(h => h.Date >= start && h.Date <= end).Select(h => h.Date).ToListAsync(ct)).ToHashSet();
        var days = new List<DateOnly>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(d))
            {
                days.Add(d);
            }
        }

        return days;
    }

    /// <summary>The employee's standard day (weekly standard hours / 5).</summary>
    public static decimal StandardDay(Employee employee) => Math.Round(employee.StandardHoursPerWeek / 5m, 2, MidpointRounding.AwayFromZero);

    public static LeavePolicyDto ToDto(this LeavePolicy p, string wbsCode) => new(
        p.Id, p.Code, p.Name, (LeaveTypeKind)(int)p.Type, p.WbsElementId, wbsCode, (AccrualMethodKind)(int)p.Method,
        p.Tiers.OrderBy(t => t.MinYearsOfService).Select(t => new AccrualTierDto(t.MinYearsOfService, t.Rate, t.MaxBalance)).ToList(),
        p.MaxBalance, p.CarryoverLimit, p.WaitingPeriodDays, p.AllowNegativeBalance, p.IsActive, LeaveEligibility.ToDto(p));
}
