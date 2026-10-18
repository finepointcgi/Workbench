using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Time;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Time;
using Workbench.Domain.Identity;

namespace Workbench.Tests.Time;

/// <summary>
/// A small org for timekeeping tests: supervisor Pat, employee Dana (reports to Pat), and Sam with no supervisor.
/// admin@example.com has the Admin role (no employee record); <see cref="GrantAsync"/> gives others roles.
/// "Today" is Wednesday 2026-10-21, so the current week is Mon 10-19 to Sun 10-25.
/// </summary>
internal sealed class TimeTestHost
{
    public static readonly DateOnly Monday = new(2026, 10, 19);

    public TimeTestHost(TimekeepingOptions? options = null)
    {
        Options = options ?? new TimekeepingOptions();
        Options.TimeZone ??= "UTC";
        Factory = TestDb.CreateFactory();
        var recorder = new AuditRecorder(User, Clock);
        People = new PeopleService(Factory, recorder, User, Clock);
        Projects = new ProjectService(Factory, recorder);
        Calendar = new FiscalCalendarService(Factory, recorder);
        var tk = new TimekeepingClock(Clock, Microsoft.Extensions.Options.Options.Create(Options));
        var access = new AccessControl(Factory, User);
        Timesheets = new TimesheetService(Factory, User, recorder, Projects, tk, access, Notifier);
        Approvals = new TimesheetApprovalService(Factory, User, recorder, tk, access, Notifier);
        Reports = new TimeReportService(Factory, tk);
    }

    public TimekeepingOptions Options { get; }
    public IWorkbenchDbContextFactory Factory { get; }
    public TestUser User { get; } = new("admin@example.com");
    public TestClock Clock { get; } = new(new DateTime(2026, 10, 21, 15, 0, 0, DateTimeKind.Utc));
    public RecordingNotifier Notifier { get; } = new();
    public PeopleService People { get; }
    public ProjectService Projects { get; }
    public FiscalCalendarService Calendar { get; }
    public TimesheetService Timesheets { get; }
    public TimesheetApprovalService Approvals { get; }
    public TimeReportService Reports { get; }

    public EmployeeDto Pat { get; private set; } = null!;
    public EmployeeDto Dana { get; private set; } = null!;
    public EmployeeDto Sam { get; private set; } = null!;
    public WbsElementDto Direct { get; private set; } = null!;
    public WbsElementDto Overhead { get; private set; } = null!;
    public WbsElementDto Unassigned { get; private set; } = null!;
    public IReadOnlyList<FiscalPeriodDto> Periods { get; private set; } = [];

    public static async Task<TimeTestHost> CreateAsync(TimekeepingOptions? options = null)
    {
        var host = new TimeTestHost(options);
        await host.GrantAsync("admin@example.com", OrgRoles.Admin);
        await host.SeedAsync();
        return host;
    }

    /// <summary>Gives a user organization roles (an <see cref="AppUser"/> row, as the admin screen would).</summary>
    public async Task GrantAsync(string email, OrgRoles roles)
    {
        await using var db = await Factory.CreateAsync();
        var user = await db.Set<AppUser>().FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            user = new AppUser { Email = email, DisplayName = email };
            db.Set<AppUser>().Add(user);
        }

        (user.IsAdmin, user.IsFinance, user.IsTimekeeper) = (roles.HasFlag(OrgRoles.Admin), roles.HasFlag(OrgRoles.Finance), roles.HasFlag(OrgRoles.Timekeeper));
        await db.SaveChangesAsync();
    }

    private async Task SeedAsync()
    {
        Periods = await Calendar.CreateFiscalYearAsync(2027, new DateOnly(2026, 10, 1));
        var lcat = await People.CreateLaborCategoryAsync(new SaveLaborCategoryRequest("SE2", "Systems Engineer II"));
        var hired = new DateOnly(2024, 1, 1);
        Pat = await People.CreateEmployeeAsync(new SaveEmployeeRequest("E1", "pat@example.com", "Pat Morgan", null, null, lcat.Id, hired));
        Dana = await People.CreateEmployeeAsync(new SaveEmployeeRequest("E2", "dana@example.com", "Dana Lee", null, Pat.Id, lcat.Id, hired));
        Sam = await People.CreateEmployeeAsync(new SaveEmployeeRequest("E3", "sam@example.com", "Sam Ortiz", null, null, lcat.Id, hired));
        await People.AddCostRateAsync(Dana.Id, new AddCostRateRequest(hired, 50m, "Hire"));
        await People.AddCostRateAsync(Dana.Id, new AddCostRateRequest(new DateOnly(2026, 10, 20), 60m, "Merit"));

        var project = await Projects.CreateProjectAsync(new SaveProjectRequest("1001", "Modernization", null, Pat.Id, ProjectState.Active, null, null));
        Direct = await Projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(null, "1001.01", "Requirements"));
        Unassigned = await Projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(null, "1001.02", "Not assigned"));
        await Projects.AddAssignmentAsync(Direct.Id, new AddChargeAssignmentRequest(Dana.Id));
        var indirect = await Projects.CreateProjectAsync(new SaveProjectRequest("IND", "Indirect", null, null, ProjectState.Active, null, null));
        Overhead = await Projects.CreateWbsElementAsync(indirect.Id, new SaveWbsElementRequest(null, "IND.OH", "Overhead", ChargeCodeKind.Indirect, RequiresAssignment: false));
    }

    public void SignInAs(EmployeeDto employee) => User.Email = employee.Email;

    /// <summary>Dana's current-week timesheet with eight hours a day Monday to Wednesday.</summary>
    public async Task<TimesheetDetailDto> DanaWeekAsync(decimal hoursPerDay = 8m)
    {
        SignInAs(Dana);
        var sheet = await Timesheets.GetOrCreateMyTimesheetAsync(Monday);
        var entries = Enumerable.Range(0, 3).Select(i => new TimeEntryInput(Direct.Id, Monday.AddDays(i), hoursPerDay)).ToList();
        return await Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, new SaveTimeEntriesRequest(entries));
    }

    public async Task<Guid> SubmittedDanaWeekAsync()
    {
        var detail = await DanaWeekAsync();
        await Timesheets.SubmitAsync(detail.Timesheet.Id, new SubmitTimesheetRequest(true));
        return detail.Timesheet.Id;
    }

    public async Task<Guid> ApprovedDanaWeekAsync()
    {
        var id = await SubmittedDanaWeekAsync();
        SignInAs(Pat);
        await Approvals.ApproveAsync(id);
        SignInAs(Dana);
        return id;
    }
}

internal sealed class TestUser(string email) : ICurrentUser
{
    public string Email { get; set; } = email;
}

internal sealed class TestClock(DateTime utcNow) : TimeProvider
{
    public DateTime UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
}

internal sealed class RecordingNotifier : ITimesheetNotifier
{
    public List<TimesheetNotification> Sent { get; } = [];

    public Task NotifyAsync(TimesheetNotification notification, CancellationToken cancellationToken = default)
    {
        Sent.Add(notification);
        return Task.CompletedTask;
    }
}
