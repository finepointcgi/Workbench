using Microsoft.EntityFrameworkCore;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for total time accounting: Robin Hale (E1050, exempt, 40-hour standard week, $60/h cost rate)
/// worked a 50-hour week across projects 1001 and 1002 last week (10 uncompensated hours) after a normal 40-hour week, and
/// the organization uses the Hybrid labor cost method from the earlier week, so last week's $2,400 salary is spread over
/// 50 hours ($48/h). Runs once, after the project manager seed (it charges 1002).
/// </summary>
internal static class TotalTimeSeed
{
    public const string RobinEmail = "robin.hale@example.com";

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<Employee>().AnyAsync(e => e.Email == RobinEmail, cancellationToken)
            || await db.Set<Employee>().FirstOrDefaultAsync(e => e.Email == "pm@example.com", cancellationToken) is not { } pat)
        {
            return;
        }

        var wbs = await db.Set<WbsElement>().Where(w => w.Code == "1001.01.04" || w.Code == "1002.01.01").ToDictionaryAsync(w => w.Code, cancellationToken);
        var category = await db.Set<LaborCategory>().FirstOrDefaultAsync(c => c.Code == "SE2", cancellationToken);
        if (wbs.Count < 2 || category is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var lastWeek = thisMonday.AddDays(-7);
        var weekBefore = thisMonday.AddDays(-14);
        var robin = new Employee
        {
            EmployeeNumber = "E1050", Email = RobinEmail, DisplayName = "Robin Hale", OrgUnitId = pat.OrgUnitId, SupervisorId = pat.Id,
            DefaultLaborCategoryId = category.Id, HireDate = weekBefore.AddYears(-1), StandardHoursPerWeek = 40m, IsExempt = true,
        };
        db.Add(robin);
        db.Add(new EmployeeCostRate { EmployeeId = robin.Id, EffectiveFrom = robin.HireDate, HourlyCost = 60m });
        db.AddRange(wbs.Values.Select(w => new ChargeAssignment { WbsElementId = w.Id, EmployeeId = robin.Id }));

        Timesheet Week(DateOnly monday, decimal on1001, decimal on1002)
        {
            var sheet = new Timesheet
            {
                EmployeeId = robin.Id, PeriodStart = monday, PeriodEnd = monday.AddDays(6), Status = TimesheetStatus.Approved,
                CertifiedAtUtc = now, CertificationText = Contracts.Time.TimesheetCertification.Text, SubmittedAtUtc = now, FirstSubmittedAtUtc = now,
                ApprovedById = pat.Id, ApprovedAtUtc = now, ReviewedBy = pat.Email, CreatedAtUtc = now, UpdatedAtUtc = now,
            };
            for (var d = 0; d < 5; d++)
            {
                foreach (var (code, hours) in new[] { ("1001.01.04", on1001), ("1002.01.01", on1002) })
                {
                    sheet.Entries.Add(new TimeEntry
                    {
                        TimesheetId = sheet.Id, WbsElementId = wbs[code].Id, LaborCategoryId = category.Id, WorkDate = monday.AddDays(d), Hours = hours,
                        EnteredAtUtc = now, UpdatedAtUtc = now,
                    });
                }
            }

            return sheet;
        }

        db.AddRange(Week(weekBefore, 5m, 3m), Week(lastWeek, 6m, 4m)); // 40 hours, then 50 hours (10 uncompensated).

        if (!await db.Set<LaborCostPolicy>().AnyAsync(cancellationToken))
        {
            var policy = new LaborCostPolicy
            {
                EffectiveFrom = weekBefore, Method = LaborCostMethod.Hybrid, CreatedBy = "pm@example.com", CreatedAtUtc = now,
                Reason = "Adopted total time accounting for exempt staff's uncompensated overtime (disclosed accounting practice).",
            };
            db.Add(policy);
            db.Add(new AuditEvent
            {
                EntityType = nameof(LaborCostPolicy), EntityId = policy.Id, Action = "Created", Actor = "pm@example.com", OccurredAtUtc = now, Reason = policy.Reason,
                AfterJson = $$"""{"method":"Hybrid","effectiveFrom":"{{weekBefore:yyyy-MM-dd}}"}""",
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
