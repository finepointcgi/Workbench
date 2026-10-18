using Microsoft.EntityFrameworkCore;
using Workbench.Application.Resourcing;
using Workbench.Domain.Leave;

namespace Workbench.Application.Leave;

/// <summary>
/// Resource planning's view of the leave module: company holidays (one calendar for everyone) and the days of approved
/// leave requests. Capacity callers are already permission-checked, so this reads directly.
/// </summary>
public sealed class LeaveResourceCalendar(IWorkbenchDbContextFactory dbFactory) : IResourceLeaveCalendar
{
    public async Task<IReadOnlyList<HolidayDay>> GetHolidaysAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var holidays = await db.Set<Holiday>().AsNoTracking()
            .Where(h => h.Date >= from && h.Date <= to)
            .OrderBy(h => h.Date)
            .ToListAsync(cancellationToken);
        return holidays.Select(h => new HolidayDay(h.Date, h.Name, Hours: h.Hours)).ToList();
    }

    public async Task<IReadOnlyList<ApprovedLeaveDay>> GetApprovedLeaveAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default)
    {
        if (employeeIds.Count == 0)
        {
            return [];
        }

        var ids = employeeIds.ToList();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var days = await db.Set<LeaveRequestDay>().AsNoTracking()
            .Join(db.Set<LeaveRequest>().AsNoTracking().Where(r => r.Status == LeaveRequestStatus.Approved && ids.Contains(r.EmployeeId)),
                d => d.RequestId, r => r.Id, (d, r) => new { r.EmployeeId, d.Date, d.Hours })
            .Where(d => d.Date >= from && d.Date <= to && d.Hours > 0)
            .ToListAsync(cancellationToken);

        // One row per employee and day, even when two approved requests touch the same day.
        return days.GroupBy(d => (d.EmployeeId, d.Date))
            .Select(g => new ApprovedLeaveDay(g.Key.EmployeeId, g.Key.Date, g.Sum(d => d.Hours)))
            .OrderBy(d => d.Date).ThenBy(d => d.EmployeeId)
            .ToList();
    }
}
