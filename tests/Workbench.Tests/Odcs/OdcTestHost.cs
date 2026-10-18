using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Files;
using Workbench.Application.Odcs;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Odcs;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Collaboration;

namespace Workbench.Tests.Odcs;

/// <summary>
/// ODC services over an in-memory database seeded with: FY periods (Oct 2026 open, Sep 2026 closed); employees
/// boss → pm → dev/analyst (supervisor chain); project 1001 (PM = pm) with a non-chargeable root, an assigned leaf,
/// an unassigned leaf and a closed leaf; and an on-hold project.
/// </summary>
internal sealed class OdcTestHost
{
    public static readonly DateOnly Day = new(2026, 10, 14);
    public static readonly DateOnly ClosedDay = new(2026, 9, 14);

    public const string Boss = "boss@example.com";
    public const string Pm = "pm@example.com";
    public const string Dev = "dev@example.com";
    public const string Analyst = "analyst@example.com";

    public OdcTestHost(decimal receiptThreshold = 75m)
    {
        Factory = TestDb.CreateFactory();
        var recorder = new AuditRecorder(User, TimeProvider.System);
        Projects = new ProjectService(Factory, recorder);
        var files = new FileStorage(Blobs, User, TimeProvider.System);
        var workflow = new OdcWorkflow(Projects, recorder, User, Notifier, files,
            Options.Create(new ExpenseOptions { ReceiptThreshold = receiptThreshold }), TimeProvider.System);
        Odcs = new OdcService(Factory, workflow);
        Expenses = new ExpenseReportService(Factory, workflow);
        Approvals = new OdcApprovalService(Factory, workflow);
        Audit = new AuditLogService(Factory);
    }

    public IWorkbenchDbContextFactory Factory { get; }
    public TestUser User { get; } = new() { Email = Dev };
    public MemoryBlobStore Blobs { get; } = new();
    public RecordingNotifier Notifier { get; } = new();
    public ProjectService Projects { get; }
    public OdcService Odcs { get; }
    public ExpenseReportService Expenses { get; }
    public OdcApprovalService Approvals { get; }
    public AuditLogService Audit { get; }

    public Employee BossEmployee { get; private set; } = default!;
    public Employee PmEmployee { get; private set; } = default!;
    public Employee DevEmployee { get; private set; } = default!;
    public Project Project { get; private set; } = default!;
    public Project OnHoldProject { get; private set; } = default!;
    public WbsElement Root { get; private set; } = default!;
    public WbsElement Leaf { get; private set; } = default!;
    public WbsElement UnassignedLeaf { get; private set; } = default!;
    public WbsElement ClosedLeaf { get; private set; } = default!;
    public WbsElement OnHoldLeaf { get; private set; } = default!;

    public static async Task<OdcTestHost> CreateAsync(decimal receiptThreshold = 75m)
    {
        var host = new OdcTestHost(receiptThreshold);
        await host.SeedAsync();
        return host;
    }

    public OdcTestHost As(string email)
    {
        User.Email = email;
        return this;
    }

    private async Task SeedAsync()
    {
        await using var db = await Factory.CreateAsync();
        var year = new FiscalYear { Year = 2027, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2027, 9, 30) };
        var prior = new FiscalYear { Year = 2026, Start = new DateOnly(2025, 10, 1), End = new DateOnly(2026, 9, 30) };
        db.Set<FiscalYear>().AddRange(year, prior);
        db.Set<FiscalPeriod>().AddRange(
            new FiscalPeriod { FiscalYearId = year.Id, Name = "2026-10", Number = 1, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2026, 10, 31) },
            new FiscalPeriod { FiscalYearId = prior.Id, Name = "2026-09", Number = 12, Start = new DateOnly(2026, 9, 1), End = new DateOnly(2026, 9, 30), Status = PeriodStatus.Closed });

        var category = new LaborCategory { Code = "SE2", Name = "Systems Engineer II" };
        db.Set<LaborCategory>().Add(category);
        var hired = new DateOnly(2024, 1, 1);
        BossEmployee = new Employee { EmployeeNumber = "E0", Email = Boss, DisplayName = "Bo Boss", DefaultLaborCategoryId = category.Id, HireDate = hired };
        PmEmployee = new Employee { EmployeeNumber = "E1", Email = Pm, DisplayName = "Pat Morgan", SupervisorId = BossEmployee.Id, DefaultLaborCategoryId = category.Id, HireDate = hired };
        DevEmployee = new Employee { EmployeeNumber = "E2", Email = Dev, DisplayName = "Dana Lee", SupervisorId = PmEmployee.Id, DefaultLaborCategoryId = category.Id, HireDate = hired };
        var analyst = new Employee { EmployeeNumber = "E3", Email = Analyst, DisplayName = "Alex Rivera", SupervisorId = PmEmployee.Id, DefaultLaborCategoryId = category.Id, HireDate = hired };
        db.Set<Employee>().AddRange(BossEmployee, PmEmployee, DevEmployee, analyst);

        Project = new Project { Code = "1001", Name = "Modernization", ProjectManagerId = PmEmployee.Id };
        OnHoldProject = new Project { Code = "2002", Name = "Paused", ProjectManagerId = PmEmployee.Id, Status = ProjectStatus.OnHold };
        db.Set<Project>().AddRange(Project, OnHoldProject);
        Root = new WbsElement { ProjectId = Project.Id, Code = "1001.01", Name = "Root", IsChargeable = false };
        Leaf = new WbsElement { ProjectId = Project.Id, ParentId = Root.Id, Code = "1001.01.01", Name = "Work" };
        UnassignedLeaf = new WbsElement { ProjectId = Project.Id, ParentId = Root.Id, Code = "1001.01.02", Name = "Not assigned" };
        ClosedLeaf = new WbsElement { ProjectId = Project.Id, ParentId = Root.Id, Code = "1001.01.03", Name = "Closed", IsOpen = false, RequiresAssignment = false };
        OnHoldLeaf = new WbsElement { ProjectId = OnHoldProject.Id, Code = "2002.01", Name = "Paused work", RequiresAssignment = false };
        db.Set<WbsElement>().AddRange(Root, Leaf, UnassignedLeaf, ClosedLeaf, OnHoldLeaf);
        db.Set<ChargeAssignment>().AddRange(new[] { DevEmployee, analyst, PmEmployee }.Select(e => new ChargeAssignment { WbsElementId = Leaf.Id, EmployeeId = e.Id }));
        await db.SaveChangesAsync();
    }

    public SaveExpenseLineRequest Line(decimal amount = 50m, Guid? wbs = null, DateOnly? on = null, string description = "Taxi to site",
        OdcCostType type = OdcCostType.Travel, bool unallowable = false, string? reason = null) =>
        new(wbs ?? Leaf.Id, type, on ?? Day, amount, description, "Yellow Cab", unallowable, reason);

    public SaveVendorOdcRequest Vendor(decimal amount = 500m, Guid? wbs = null, OdcCostType type = OdcCostType.Materials, bool unallowable = false, string? reason = null) =>
        new(wbs ?? Leaf.Id, type, Day, amount, "Server rack", "Acme Supply", "INV-100", unallowable, reason);

    /// <summary>As dev: a report with the given lines, receipts attached to all of them.</summary>
    public async Task<ExpenseReportDto> DevReportAsync(params decimal[] amounts)
    {
        As(Dev);
        var report = await Expenses.CreateReportAsync(new SaveExpenseReportRequest("Site visit", Day, Day.AddDays(2)));
        foreach (var amount in amounts)
        {
            var line = await Expenses.AddLineAsync(report.Id, Line(amount));
            await Expenses.AttachReceiptAsync(report.Id, line.Id, "receipt.pdf", new MemoryStream([1, 2, 3]));
        }

        return report;
    }

    public async Task<ExpenseReportDto> SubmittedDevReportAsync(params decimal[] amounts)
    {
        var report = await DevReportAsync(amounts.Length == 0 ? [120m] : amounts);
        return await Expenses.SubmitAsync(report.Id);
    }
}

internal sealed class RecordingNotifier : IOdcNotifier
{
    public List<OdcNotification> Sent { get; } = [];

    public Task NotifyAsync(OdcNotification notification, CancellationToken cancellationToken = default)
    {
        Sent.Add(notification);
        return Task.CompletedTask;
    }
}
