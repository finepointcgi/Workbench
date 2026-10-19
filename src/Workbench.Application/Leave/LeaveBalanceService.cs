using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Leave;
using Workbench.Domain.Leave;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Leave;

/// <summary>
/// Leave balances. Employees see their own; supervisors their direct reports'; Finance and administrators everyone's and
/// are the only ones who adjust balances or run accruals.
/// </summary>
public sealed class LeaveBalanceService(IWorkbenchDbContextFactory dbFactory, AccessControl access, AuditRecorder audit, TimeProvider clock) : ILeaveBalanceService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public async Task<IReadOnlyList<LeaveBalanceDto>> GetMyBalancesAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await EmployeeLinks.FindForUserAsync(db, access.Email, cancellationToken)
            ?? throw new UnauthorizedAccessException("You don't have an employee record, so you have no leave balances.");
        return await BuildAsync(db, [me], cancellationToken);
    }

    public async Task<IReadOnlyList<LeaveBalanceDto>> ListBalancesAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var roles = await access.GetOrgRolesAsync(cancellationToken);
        List<Employee> employees;
        if (roles.IsFinance())
        {
            employees = await db.Set<Employee>().AsNoTracking().ToListAsync(cancellationToken);
        }
        else
        {
            var me = await EmployeeLinks.FindForUserAsync(db, access.Email, cancellationToken);
            employees = me is null ? [] : await db.Set<Employee>().AsNoTracking().Where(e => e.SupervisorId == me.Id).ToListAsync(cancellationToken);
        }

        employees = employees.Where(e => e.TerminationDate is null || e.TerminationDate >= Today).OrderBy(e => e.DisplayName).ToList();
        return employees.Count == 0 ? [] : await BuildAsync(db, employees, cancellationToken);
    }

    public async Task<IReadOnlyList<LeaveLedgerEntryDto>> GetHistoryAsync(Guid employeeId, Guid policyId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Employee", employeeId);
        await RequireCanSeeAsync(db, employee, cancellationToken);
        await LeaveEngine.SyncUsageAsync(db, [employeeId], audit.Actor, UtcNow, cancellationToken);
        var rows = await db.Set<LeaveLedgerEntry>().AsNoTracking().Where(l => l.EmployeeId == employeeId && l.PolicyId == policyId).ToListAsync(cancellationToken);
        var running = 0m;
        var history = new List<LeaveLedgerEntryDto>();
        foreach (var r in rows.OrderBy(r => r.Date).ThenBy(r => r.CreatedAtUtc).ThenBy(r => r.Id))
        {
            running += r.Hours;
            history.Add(new LeaveLedgerEntryDto(r.Id, r.Date, (LeaveLedgerEntryKind)(int)r.Kind, r.Hours, running, r.Reason, r.SourceKey, r.CreatedBy, r.CreatedAtUtc));
        }

        history.Reverse();
        return history;
    }

    public async Task<LeaveLedgerEntryDto> AdjustAsync(AdjustLeaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await access.RequireFinanceAsync("adjust leave balances", cancellationToken);
        var reason = request.Reason.Required("Reason");
        if (request.Hours == 0 || decimal.Round(request.Hours, 2) != request.Hours || Math.Abs(request.Hours) > 2000)
        {
            throw new ArgumentException("Enter a non-zero adjustment of at most 2,000 hours, with up to two decimals.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Employee", request.EmployeeId);
        if (!await db.Set<LeavePolicy>().AnyAsync(p => p.Id == request.PolicyId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Leave policy", request.PolicyId);
        }

        var entry = new LeaveLedgerEntry
        {
            EmployeeId = employee.Id,
            PolicyId = request.PolicyId,
            Date = request.Date ?? Today,
            Kind = LeaveLedgerKind.Adjustment,
            Hours = request.Hours,
            Reason = reason,
            SourceKey = $"adjust:{Guid.CreateVersion7()}",
            CreatedBy = audit.Actor,
            CreatedAtUtc = UtcNow,
        };
        db.Set<LeaveLedgerEntry>().Add(entry);
        audit.Record(db, LeaveEngine.AuditEntityType, employee.Id, "Adjusted", after: new { entry.PolicyId, entry.Date, entry.Hours }, reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        var balance = (await LeaveEngine.BalancesAsync(db, [employee.Id], null, cancellationToken)).GetValueOrDefault((employee.Id, request.PolicyId));
        return new LeaveLedgerEntryDto(entry.Id, entry.Date, LeaveLedgerEntryKind.Adjustment, entry.Hours, balance, entry.Reason, entry.SourceKey, entry.CreatedBy, entry.CreatedAtUtc);
    }

    public async Task<AccrualRunResultDto> RunAccrualAsync(Guid payPeriodId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("run leave accruals", cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await db.Set<PayPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == payPeriodId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Pay period", payPeriodId);
        var result = await LeaveEngine.AccrueAsync(db, period, audit.Actor, UtcNow, cancellationToken);
        if (result.EntriesAdded > 0)
        {
            audit.Record(db, LeaveEngine.AuditEntityType, period.Id, "Accrued", after: new { result.EntriesAdded, result.HoursAccrued, period.Start, period.End });
            await db.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    public async Task<YearEndResultDto> RunYearEndAsync(int year, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("run the leave year end", cancellationToken);
        if (year is < 2000 or > 2100)
        {
            throw new ArgumentException("Choose a year between 2000 and 2100.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var result = await LeaveEngine.YearEndAsync(db, year, audit.Actor, UtcNow, cancellationToken);
        if (result.EmployeesForfeited > 0)
        {
            audit.Record(db, LeaveEngine.AuditEntityType, Guid.Empty, "Year end", after: result);
            await db.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    private async Task RequireCanSeeAsync(IWorkbenchDbContext db, Employee employee, CancellationToken ct)
    {
        access.RequireSignedIn();
        if (string.Equals(employee.Email, access.Email, StringComparison.OrdinalIgnoreCase) || (await access.GetOrgRolesAsync(ct)).IsFinance())
        {
            return;
        }

        var me = await EmployeeLinks.FindForUserAsync(db, access.Email, ct);
        if (me is null || employee.SupervisorId != me.Id)
        {
            throw new WorkbenchForbiddenException("Only the employee, their supervisor, Finance and administrators can see this leave balance.");
        }
    }

    /// <summary>Balances for the employees in every active policy (policies without accrual only once they have rows).</summary>
    internal async Task<IReadOnlyList<LeaveBalanceDto>> BuildAsync(IWorkbenchDbContext db, IReadOnlyList<Employee> employees, CancellationToken ct)
    {
        var ids = employees.Select(e => e.Id).ToList();
        await LeaveEngine.SyncUsageAsync(db, ids, audit.Actor, UtcNow, ct);
        var policies = await db.Set<LeavePolicy>().AsNoTracking().Where(p => p.IsActive && p.Type != LeaveType.Holiday).OrderBy(p => p.Code).ToListAsync(ct);
        var codes = await LeavePolicyService.WbsCodesAsync(db, policies.Select(p => p.WbsElementId), ct);
        var eligibility = await LeaveEligibility.LoadAsync(db, ids, ct);
        var balances = await LeaveEngine.BalancesAsync(db, ids, null, ct);
        var planned = await LeaveEngine.PlannedAsync(db, ids, null, null, ct);
        var yearStart = new DateOnly(Today.Year, 1, 1);
        var yearRows = (await db.Set<LeaveLedgerEntry>().AsNoTracking().Where(l => l.Date >= yearStart).ToListAsync(ct))
            .Where(l => ids.Contains(l.EmployeeId)).ToList();
        var result = new List<LeaveBalanceDto>();
        foreach (var e in employees)
        {
            foreach (var p in policies)
            {
                var key = (e.Id, p.Id);
                var hasRows = balances.ContainsKey(key) || planned.ContainsKey(key) || yearRows.Any(r => r.EmployeeId == e.Id && r.PolicyId == p.Id);
                if ((p.Method == AccrualMethod.None || !eligibility.Applies(p, e)) && !hasRows)
                {
                    continue; // A policy that doesn't apply shows only while there's history in it (hours from before).
                }

                var balance = balances.GetValueOrDefault(key);
                var plan = planned.GetValueOrDefault(key);
                var rows = yearRows.Where(r => r.EmployeeId == e.Id && r.PolicyId == p.Id).ToList();
                result.Add(new LeaveBalanceDto(e.Id, e.DisplayName, p.Id, p.Code, p.Name, (LeaveTypeKind)(int)p.Type, codes.GetValueOrDefault(p.WbsElementId, "?"),
                    balance,
                    rows.Where(r => r.Kind is LeaveLedgerKind.Accrual or LeaveLedgerKind.Grant).Sum(r => r.Hours),
                    -rows.Where(r => r.Kind == LeaveLedgerKind.Usage).Sum(r => r.Hours),
                    plan,
                    balance - plan,
                    p.AllowNegativeBalance));
            }
        }

        return result;
    }
}
