using Microsoft.EntityFrameworkCore;
using Workbench.Application.Resourcing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using Workbench.Domain.Tasks;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for resource planning: people on project 1001's plan tasks, each for hours that fit the task's
/// dates (Unanet-style assignments spread over the working weeks, on the task's dates so they move with it), and each one
/// the task's assignee. Runs once, after the project-accounting seed.
/// </summary>
internal static class ResourcingSeed
{
    private const string Actor = "seed@workbench.local";

    /// <summary>A few skills on the sample people, so "Find available people" has something to filter by.</summary>
    public static async Task SeedSkillsAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<Skill>().AnyAsync(cancellationToken))
        {
            return;
        }

        var employees = await db.Set<Employee>().ToDictionaryAsync(e => e.Email, cancellationToken);
        var now = DateTime.UtcNow;
        var skills = new Dictionary<string, Skill>();
        foreach (var (email, name, category, level, years) in new (string, string, string, SkillLevel, decimal)[]
        {
            ("pm@example.com", "Program management", "Management", SkillLevel.Expert, 12),
            ("pm@example.com", "DCAA audit support", "Finance", SkillLevel.Advanced, 6),
            ("analyst@example.com", "Cost accounting", "Finance", SkillLevel.Advanced, 7),
            ("analyst@example.com", "DCAA audit support", "Finance", SkillLevel.Working, 3),
            ("analyst@example.com", "SQL", "Engineering", SkillLevel.Working, 4),
            ("dev@workbench.local", "C#", "Engineering", SkillLevel.Expert, 9),
            ("dev@workbench.local", "SQL", "Engineering", SkillLevel.Advanced, 8),
            ("dev@workbench.local", "Azure", "Engineering", SkillLevel.Working, 3),
        })
        {
            if (!employees.TryGetValue(email, out var employee))
            {
                continue;
            }

            if (!skills.TryGetValue(name, out var skill))
            {
                skill = skills[name] = new Skill { Name = name, Category = category };
                db.Add(skill);
            }

            db.Add(new EmployeeSkill { EmployeeId = employee.Id, SkillId = skill.Id, Level = level, Years = years, UpdatedAtUtc = now, UpdatedBy = Actor });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<ResourceAllocation>().AnyAsync(cancellationToken) || await db.Set<StaffingAssignment>().AnyAsync(cancellationToken))
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
        var today = DateOnly.FromDateTime(DateTime.Now);
        var holidays = (await db.Set<Domain.Leave.Holiday>().AsNoTracking().Select(h => h.Date).ToListAsync(cancellationToken)).ToHashSet();

        // Who works each task and for how long: the hours fit inside the task's working days.
        (string Email, string Wbs, decimal Hours)[] plan =
        [
            ("pm@example.com", "1001.01.01", 8),        // Kickoff meeting: one day
            ("viewer@example.com", "1001.01.01", 4),
            ("analyst@example.com", "1001.01.02", 60),  // Requirements review: about two weeks
            ("pm@example.com", "1001.01.03", 24),       // Baseline schedule: about two weeks
            ("dev@workbench.local", "1001.01.03", 16),
            ("dev@workbench.local", "1001.01.04", 300), // Execution: about ten weeks
            ("analyst@example.com", "1001.01.04", 150),
        ];
        foreach (var (email, code, hours) in plan)
        {
            if (!employees.TryGetValue(email, out var employee) || !wbs.TryGetValue(code, out var element) || element.ItemId is not { } itemId
                || await db.Items.FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken) is not { Start: { } start, Finish: { } finish } task)
            {
                continue;
            }

            var from = start > today ? start : today;
            if (finish < from)
            {
                continue;
            }

            // A task that falls on a weekend or holiday (the sample plan starts today) has no working days to staff.
            bool Working(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(d) && employee.IsActiveOn(d);
            if (!Enumerable.Range(0, finish.DayNumber - from.DayNumber + 1).Select(from.AddDays).Any(Working))
            {
                continue;
            }

            var assignment = new StaffingAssignment
            {
                EmployeeId = employee.Id, ProjectId = project.Id, WbsElementId = element.Id, LaborCategoryId = employee.DefaultLaborCategoryId,
                From = from, To = finish, TotalHours = hours, FollowsTask = true, CreatedBy = Actor, CreatedAtUtc = now,
            };
            db.Add(assignment);
            foreach (var week in AssignmentSpread.Spread(hours, from, finish, Working).Where(w => w.Hours > 0))
            {
                db.Add(new ResourceAllocation
                {
                    EmployeeId = employee.Id, ProjectId = project.Id, WbsElementId = element.Id, LaborCategoryId = employee.DefaultLaborCategoryId,
                    WeekStart = week.WeekStart, Hours = week.Hours, Source = AllocationSource.Manual, AssignmentId = assignment.Id,
                    CreatedBy = Actor, CreatedAtUtc = now,
                });
            }

            // People with hours on a task are its assignees (the task shows in their My work).
            var lower = email.ToLowerInvariant();
            if (!db.ChangeTracker.Entries<ItemAssignee>().Any(e => e.Entity.ItemId == itemId && e.Entity.Email == lower)
                && !await db.Set<ItemAssignee>().AnyAsync(a => a.ItemId == itemId && a.Email == lower, cancellationToken))
            {
                db.Add(new ItemAssignee { SheetId = task.SheetId, ItemId = itemId, Email = lower, AssignedBy = "staffing", AssignedAtUtc = now });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
