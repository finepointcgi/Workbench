using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Imports.Costpoint;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Time;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;
using ImportKind = Workbench.Domain.Imports.ImportKind;
using ImportSource = Workbench.Domain.Imports.ImportSource;

namespace Workbench.Application.Imports.Unanet;

/// <summary>
/// Unanet approved time → approved Workbench timesheets, as history: reports, project cost to date and the first period
/// close see the hours, but no approval is started and nothing is posted to the ledger. One row per person, task and day,
/// or one row per person and task with a week-ending date and a column per weekday. Rows are regrouped into
/// Workbench's own timesheet periods. A person who already has a timesheet for a period is skipped, never merged, so
/// running the same file twice changes nothing. Each timesheet and every entry is recorded for rollback, and each
/// timesheet gets an audit event saying where it came from.
/// </summary>
public sealed class UnanetTimesheetImporter : CostpointImporter
{
    private static readonly (string Field, DayOfWeek Day, string[] Aliases)[] Days =
    [
        ("Sun", DayOfWeek.Sunday, ["Sun", "Sunday", "Sun Hours"]),
        ("Mon", DayOfWeek.Monday, ["Mon", "Monday", "Mon Hours"]),
        ("Tue", DayOfWeek.Tuesday, ["Tue", "Tuesday", "Tues", "Tue Hours"]),
        ("Wed", DayOfWeek.Wednesday, ["Wed", "Wednesday", "Wed Hours"]),
        ("Thu", DayOfWeek.Thursday, ["Thu", "Thursday", "Thur", "Thurs", "Thu Hours"]),
        ("Fri", DayOfWeek.Friday, ["Fri", "Friday", "Fri Hours"]),
        ("Sat", DayOfWeek.Saturday, ["Sat", "Saturday", "Sat Hours"]),
    ];

    public override ImportSource Source => ImportSource.Unanet;
    public override ImportKind Kind => ImportKind.Timesheets;
    public override string Title => "Timesheets (approved time)";
    public override string Description => "Approved Unanet time for the current fiscal year, brought in as history so hours reports and the first period close carry on. Nothing is posted to the ledger and nobody has to approve it again.";
    public override string? SampleFile => "samples/import/unanet/timesheets.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("EmployeeNumber", "Employee ID", true, "The person (import people first).", "Employee ID", "Employee Number", "Person ID", "Username", "User Name", "Person"),
        new("Project", "Project code", true, "The project charged (import projects first).", "Project Code", "Project ID", "Project"),
        new("Task", "Task code", false, "Blank = the project's general charge code.", "Task Code", "Task", "Task ID"),
        new("Date", "Work date", false, "One row per day. Or use a week-ending date with weekday columns.", "Work Date", "Date", "Entry Date", "Day"),
        new("Hours", "Hours", false, "Hours worked that day (with a work date).", "Hours", "Total Hours", "Entry Hours", "Time"),
        new("WeekEnding", "Week ending", false, "Last day of the week, with a column per weekday.", "Week Ending", "Week End", "Period End", "Week Ending Date", "Timesheet End"),
        .. Days.Select(d => new ImportField(d.Field, d.Field, false, "Hours on this weekday (week-ending layout).", d.Aliases)),
        new("LaborCategory", "Labor category", false, "Labor category charged as. Defaults to the person's labor category.", "Labor Category", "Labor Category Code", "LCAT"),
        new("Status", "Timesheet status", false, "Only Approved / Posted / Billed / Final time is imported. Blank column = all rows are taken as approved.", "Timesheet Status", "Status", "Approval Status"),
        new("ApprovedBy", "Approved by", false, "Kept on the timesheet as who approved it in Unanet.", "Approved By", "Approver", "Approved By Username"),
        new("ApprovedOn", "Approved on", false, "Date Unanet approved the timesheet. Defaults to the end of the period.", "Approved Date", "Approved On", "Approval Date"),
        new("Comment", "Comment", false, "", "Comment", "Comments", "Notes", "Description"),
    ];

    private sealed record Entry(MappedRow Row, EmployeeDto Employee, WbsElementDto Wbs, Guid LaborCategoryId, DateOnly Date, decimal Hours, string? Comment,
        string? ApprovedBy, DateOnly? ApprovedOn);

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var projects = Service<IProjectService>(context);
        var wbs = await UnanetWbs.LoadAsync(projects, cancellationToken);
        var people = Service<IPeopleService>(context);
        var employees = (await people.ListEmployeesAsync(includeInactive: true, cancellationToken)).ToDictionary(e => e.EmployeeNumber, StringComparer.OrdinalIgnoreCase);
        var categories = (await people.ListLaborCategoriesAsync(cancellationToken)).ToDictionary(l => l.Code, l => l.Id, StringComparer.OrdinalIgnoreCase);
        var clock = Service<TimekeepingClock>(context);
        var dbFactory = Service<IWorkbenchDbContextFactory>(context);
        var audit = Service<AuditRecorder>(context);
        var today = clock.Today;
        var entries = new List<Entry>();
        var notApproved = 0;
        var blocked = new List<(string Employee, DateOnly Date)>();

        foreach (var row in rows)
        {
            var number = row.Required("EmployeeNumber");
            var project = row.Required("Project");
            if (row.Get("Status") is { } status && !IsApproved(status))
            {
                notApproved++;
                context.Run.Warning(row.FileName, row.Number, "Status", $"Left out: the timesheet is '{status}' in Unanet, and only approved time is imported.");
                continue;
            }

            var days = new List<(DateOnly Date, decimal Hours)>();
            if (row.Get("Date") is not null)
            {
                var date = row.Date("Date");
                var hours = row.Decimal("Hours");
                if (row.Get("Hours") is null)
                {
                    row.Error("Hours", "Hours is required with a work date.");
                }
                else if (date is not null && hours is { } h)
                {
                    days.Add((date.Value, h));
                }
            }
            else if (row.Get("WeekEnding") is not null)
            {
                var weekEnd = row.Date("WeekEnding");
                foreach (var (field, dayOfWeek, _) in Days)
                {
                    if (weekEnd is { } end && row.Decimal(field) is { } hours && hours != 0)
                    {
                        var offset = ((int)end.DayOfWeek - (int)dayOfWeek + 7) % 7;
                        days.Add((end.AddDays(-offset), hours));
                    }
                }
            }
            else
            {
                row.Error("Date", "Map a work date and Hours, or a week-ending date with a column per weekday.");
            }

            foreach (var (date, hours) in days)
            {
                if (hours < 0)
                {
                    row.Error("Hours", $"Hours {hours} on {date:yyyy-MM-dd} is negative.");
                }
                else if (hours > 24)
                {
                    row.Error("Hours", $"Hours {hours} on {date:yyyy-MM-dd} is more than a day.");
                }

                if (date > today)
                {
                    row.Error("Date", $"{date:yyyy-MM-dd} is in the future.");
                }
            }

            if (number is not null && !employees.ContainsKey(number))
            {
                row.Error("EmployeeNumber", $"Employee {number} isn't in Workbench; import people first.");
            }

            WbsElementDto? element = null;
            if (project is not null)
            {
                var (found, problem) = await wbs.FindAsync(project, row.Get("Task"), cancellationToken);
                element = found;
                if (found is null)
                {
                    row.Error("Task", problem!);
                }
            }

            Guid? categoryId = null;
            if (row.Get("LaborCategory") is { } plc)
            {
                if (categories.TryGetValue(plc, out var c))
                {
                    categoryId = c;
                }
                else
                {
                    row.Error("LaborCategory", $"Labor category {plc} isn't in Workbench; import labor categories first.");
                }
            }

            if (row.HasErrors && number is not null)
            {
                blocked.AddRange(days.Select(d => (number, d.Date)));
            }

            if (row.HasErrors || number is null || element is null)
            {
                continue;
            }

            var employee = employees[number];
            categoryId ??= employee.DefaultLaborCategoryId;
            if (categoryId is null)
            {
                row.Error("LaborCategory", $"{employee.DisplayName} has no labor category in Workbench and the row names none.");
                continue;
            }

            var approvedOn = row.Date("ApprovedOn");
            foreach (var (date, hours) in days.Where(d => d.Hours > 0))
            {
                if (date < employee.HireDate)
                {
                    row.Warning("Date", $"{date:yyyy-MM-dd} is before {employee.DisplayName}'s hire date ({employee.HireDate:yyyy-MM-dd}); imported anyway.");
                }

                entries.Add(new Entry(row, employee, element, categoryId.Value, date, hours, row.Get("Comment"), row.Get("ApprovedBy"), approvedOn));
            }
        }

        if (entries.Count == 0)
        {
            return;
        }

        // A person can't have more than a day on one date, even across charge codes.
        foreach (var day in entries.GroupBy(e => (e.Employee.Id, e.Date)).Where(g => g.Sum(e => e.Hours) > 24))
        {
            day.First().Row.Error("Hours", $"{day.First().Employee.DisplayName} has {day.Sum(e => e.Hours)} hours on {day.Key.Date:yyyy-MM-dd}, more than a day.");
            blocked.Add((day.First().Employee.EmployeeNumber, day.Key.Date));
        }

        entries = entries.Where(e => !e.Row.HasErrors).ToList();

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employeeIds = entries.Select(e => e.Employee.Id).Distinct().ToList();
        var existing = await db.Set<Timesheet>().AsNoTracking().Where(t => employeeIds.Contains(t.EmployeeId))
            .Select(t => new { t.EmployeeId, t.PeriodStart, t.PeriodEnd }).ToListAsync(cancellationToken);
        var periods = new Dictionary<DateOnly, (DateOnly Start, DateOnly End)>();
        foreach (var date in entries.Select(e => e.Date).Distinct())
        {
            periods[date] = await clock.PeriodForAsync(db, date, cancellationToken);
        }

        foreach (var date in blocked.Select(b => b.Date).Distinct().Where(d => !periods.ContainsKey(d)))
        {
            periods[date] = await clock.PeriodForAsync(db, date, cancellationToken);
        }

        var blockedSheets = blocked.Select(b => (b.Employee.ToUpperInvariant(), periods[b.Date].Start)).ToHashSet();
        var now = clock.UtcNow;
        var sheets = entries.GroupBy(e => (e.Employee.Id, periods[e.Date].Start)).OrderBy(g => g.Key.Start).ThenBy(g => g.First().Employee.DisplayName);
        foreach (var sheet in sheets)
        {
            var first = sheet.First();
            var (start, end) = periods[first.Date];
            var label = $"{first.Employee.DisplayName} {start:yyyy-MM-dd} to {end:yyyy-MM-dd}";
            if (blockedSheets.Contains((first.Employee.EmployeeNumber.ToUpperInvariant(), start)))
            {
                context.Run.Skip(first.Row.FileName, first.Row.Number, "Timesheet", label, "Left out: another row for this person and period has an error, and half a timesheet would be wrong");
                continue;
            }

            if (existing.Any(t => t.EmployeeId == first.Employee.Id && t.PeriodStart <= start && t.PeriodEnd >= start))
            {
                context.Run.Skip(first.Row.FileName, first.Row.Number, "Timesheet", label, "Already has a timesheet for this period in Workbench");
                continue;
            }

            // The same person, charge code and day on several rows (several Unanet pay types or lines) is one entry.
            var merged = sheet.GroupBy(e => (e.Wbs.Id, e.Date)).Select(g => new
            {
                g.First().Wbs, g.First().LaborCategoryId, g.Key.Date, Hours = g.Sum(e => e.Hours),
                Comment = string.Join("; ", g.Select(e => e.Comment).OfType<string>().Distinct()),
            }).ToList();
            var total = merged.Sum(m => m.Hours);
            var approvedBy = sheet.Select(e => e.ApprovedBy).FirstOrDefault(a => a is not null);
            var approvedOn = sheet.Select(e => e.ApprovedOn).FirstOrDefault(a => a is not null) ?? end;
            var approvedAt = approvedOn.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
            var summary = $"{total:0.##} hours on {merged.Select(m => m.Wbs.Id).Distinct().Count()} charge code(s), approved";

            var sheetId = await context.Run.CreateAsync(first.Row.FileName, first.Row.Number, typeof(Timesheet), "Timesheet", label, summary, async () =>
            {
                await using var write = await dbFactory.CreateAsync(cancellationToken);
                var created = new Timesheet
                {
                    EmployeeId = first.Employee.Id,
                    PeriodStart = start,
                    PeriodEnd = end,
                    Status = TimesheetStatus.Approved,
                    SubmittedAtUtc = approvedAt,
                    FirstSubmittedAtUtc = approvedAt,
                    CertifiedAtUtc = approvedAt,
                    CertificationText = $"Approved in Unanet; imported as history ({Reason(context)}). Not certified in Workbench.",
                    ApprovedAtUtc = approvedAt,
                    ReviewedBy = approvedBy ?? "Unanet import",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                write.Set<Timesheet>().Add(created);
                audit.Record(write, TimesheetQueries.AuditEntityType, created.Id, "ImportedApproved", after: new { first.Employee.EmployeeNumber, start, end, Hours = total, Source = "Unanet", ApprovedBy = approvedBy },
                    reason: Reason(context));
                await write.SaveChangesAsync(cancellationToken);
                return created.Id;
            }, cancellationToken);

            if (sheetId is not { } id || !context.Run.Applying)
            {
                continue;
            }

            await using var entriesDb = await dbFactory.CreateAsync(cancellationToken);
            var added = merged.Select(m => new TimeEntry
            {
                TimesheetId = id,
                WbsElementId = m.Wbs.Id,
                LaborCategoryId = m.LaborCategoryId,
                WorkDate = m.Date,
                Hours = m.Hours,
                Comment = m.Comment.Length == 0 ? null : m.Comment,
                EnteredAtUtc = approvedAt,
                UpdatedAtUtc = approvedAt,
            }).ToList();
            entriesDb.Set<TimeEntry>().AddRange(added);
            await entriesDb.SaveChangesAsync(cancellationToken);
            foreach (var entry in added)
            {
                await context.Run.RecordCreatedAsync(typeof(TimeEntry), entry.Id, $"Time entry {first.Employee.EmployeeNumber} {entry.WorkDate:yyyy-MM-dd}", cancellationToken);
            }
        }

        if (notApproved > 0)
        {
            context.Run.Warning("", 0, null, $"{notApproved} row(s) were left out because their Unanet timesheet isn't approved.");
        }
    }

    private static bool IsApproved(string status) => status.Trim().ToLowerInvariant() is "approved" or "posted" or "billed" or "final" or "finalized" or "closed" or "complete" or "completed" or "locked";
}
