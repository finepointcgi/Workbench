using Microsoft.EntityFrameworkCore;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for resource planning: allocations on project 1001 for the seeded employees over the next
/// eight weeks. Dana Lee (dev@) is over-allocated in weeks 3 and 4 (48 h against 36 h of capacity). Runs once, after the
/// project-accounting seed.
/// </summary>
internal static class ResourcingSeed
{
    private const string Actor = "seed@workbench.local";

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<ResourceAllocation>().AnyAsync(cancellationToken))
        {
            return;
        }

        var project = await db.Set<Project>().FirstOrDefaultAsync(p => p.Code == "1001", cancellationToken);
        var employees = await db.Set<Employee>().ToDictionaryAsync(e => e.Email, cancellationToken);
        var wbs = await db.Set<WbsElement>().Where(w => w.ProjectId == (project == null ? Guid.Empty : project.Id)).ToDictionaryAsync(w => w.Code, cancellationToken);
        if (project is null || !wbs.ContainsKey("1001.01.04") || !employees.ContainsKey("dev@workbench.local"))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var thisWeek = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        (string Email, string Wbs, decimal Hours, int FromWeek, int ToWeek)[] plan =
        [
            ("pm@example.com", "1001.01.01", 16, 0, 7),
            ("analyst@example.com", "1001.01.02", 34, 0, 7),
            ("dev@workbench.local", "1001.01.04", 32, 0, 7),
            ("dev@workbench.local", "1001.01.03", 16, 2, 3), // over-allocated: 48 h in weeks 3 and 4
            ("viewer@example.com", "1001.01.01", 8, 0, 7),
        ];
        foreach (var (email, code, hours, fromWeek, toWeek) in plan)
        {
            if (!employees.TryGetValue(email, out var employee))
            {
                continue;
            }

            for (var week = fromWeek; week <= toWeek; week++)
            {
                db.Add(new ResourceAllocation
                {
                    EmployeeId = employee.Id,
                    ProjectId = project.Id,
                    WbsElementId = wbs[code].Id,
                    LaborCategoryId = employee.DefaultLaborCategoryId,
                    WeekStart = thisWeek.AddDays(7 * week),
                    Hours = hours,
                    Source = AllocationSource.Manual,
                    CreatedBy = Actor,
                    CreatedAtUtc = now,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
