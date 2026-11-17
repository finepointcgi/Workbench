using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Billing;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Identity;
using Workbench.Domain.Billing;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Accounting;
using Workbench.Tests.Time;

namespace Workbench.Tests.Billing;

/// <summary>
/// Commercial billing over an in-memory database with the default chart and FY2027 open: customer Acme, agreement A1 with
/// no CLINs, no funding and no ceiling, one project, and Dana (SE2, rate card $150/h) with approved hours on it
/// (Oct 12: 8, Oct 13: 8, Nov 3: 6) and $200 of approved travel. Today is 2026-11-20.
/// </summary>
internal sealed class CommercialKit
{
    public static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private CommercialKit(IWorkbenchDbContextFactory factory, TestUser user, TestClock clock, LedgerKit gl, RecordingPublisher publisher)
    {
        Factory = factory;
        User = user;
        Clock = clock;
        Gl = gl;
        Publisher = publisher;
        var audit = new AuditRecorder(user, clock);
        var ledger = new BillingLedger(gl.Ledger);
        var generator = new InvoiceGenerator(new BillingCostSource([]));
        var accruals = new UnbilledAccruals(generator, ledger, audit);
        Invoices = new InvoiceService(factory, generator, ledger, audit, clock, new InvoiceDocuments(Options.Create(new BillingOptions { CompanyName = "Test Co" })), accruals);
        Agreements = new AgreementBillingService(factory, Invoices, audit, clock, publisher);
        Payments = new PaymentService(factory, ledger, audit, clock);
        var overdue = new OverdueInvoiceCheck(factory, publisher, clock);
        Ar = new ArReportService(factory, overdue, clock);
        Receivables = new ReceivablesService(factory, Ar, Payments, ledger, audit, clock, overdue, new DueSoonInvoiceCheck(factory, publisher, clock));
        Settings = new BillingSettingsService(factory, audit, clock);
    }

    public IWorkbenchDbContextFactory Factory { get; }
    public TestUser User { get; }
    public TestClock Clock { get; }
    public LedgerKit Gl { get; }
    public RecordingPublisher Publisher { get; }
    public InvoiceService Invoices { get; }
    public AgreementBillingService Agreements { get; }
    public PaymentService Payments { get; }
    public ArReportService Ar { get; }
    public ReceivablesService Receivables { get; }
    public BillingSettingsService Settings { get; }

    public Guid CustomerId { get; private set; }
    public Guid ContractId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid CategoryId { get; private set; }

    public static async Task<CommercialKit> CreateAsync(ContractType type = ContractType.FirmFixedPrice)
    {
        var factory = TestDb.CreateFactory();
        var user = new TestUser("finance@example.com");
        var clock = new TestClock(Now);
        var gl = await LedgerKit.CreateAsync(factory, user, clock);
        var kit = new CommercialKit(factory, user, clock, gl, new RecordingPublisher());
        await kit.SeedAsync(type);
        return kit;
    }

    private async Task SeedAsync(ContractType type)
    {
        await using var context = await Factory.CreateAsync();
        var db = (DbContext)context;
        foreach (var (year, start) in new[] { (2026, new DateOnly(2025, 10, 1)), (2027, new DateOnly(2026, 10, 1)) })
        {
            var fy = new FiscalYear { Year = year, Start = start, End = start.AddYears(1).AddDays(-1) };
            db.Set<FiscalYear>().Add(fy);
            db.Set<FiscalPeriod>().AddRange(Enumerable.Range(0, 12).Select(i => new FiscalPeriod
            {
                FiscalYearId = fy.Id,
                Number = i + 1,
                Name = $"{start.AddMonths(i):yyyy-MM}",
                Start = start.AddMonths(i),
                End = start.AddMonths(i + 1).AddDays(-1),
            }));
        }

        var category = new LaborCategory { Code = "SE2", Name = "Systems Engineer II" };
        var dana = new Employee { EmployeeNumber = "E3", Email = "dana@example.com", DisplayName = "Dana Lee", DefaultLaborCategoryId = category.Id, HireDate = new DateOnly(2024, 1, 1) };
        var pat = new Employee { EmployeeNumber = "E1", Email = "pm@example.com", DisplayName = "Pat Morgan", DefaultLaborCategoryId = category.Id, HireDate = new DateOnly(2024, 1, 1) };
        db.AddRange(category, dana, pat, new EmployeeCostRate { EmployeeId = dana.Id, EffectiveFrom = new DateOnly(2024, 1, 1), HourlyCost = 50m });

        var customer = new Customer { Code = "ACME", Name = "Acme Co" };
        var contract = new Contract
        {
            CustomerId = customer.Id,
            ContractNumber = "A1",
            Name = "Advisory services",
            Type = type,
            PeriodStart = new DateOnly(2026, 10, 1),
            PeriodEnd = new DateOnly(2027, 9, 30),
        };
        db.AddRange(customer, contract);
        db.Add(new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = category.Id, EffectiveFrom = new DateOnly(2026, 10, 1), HourlyBillRate = 150m });
        db.Add(new CustomerBillingProfile { CustomerId = customer.Id, PaymentTermsDays = 30 });
        var project = new Project { Code = "A1-1", Name = "Advisory", ContractId = contract.Id, ProjectManagerId = pat.Id };
        var wbs = new WbsElement { ProjectId = project.Id, Code = "A1-1.01", Name = "Consulting" };
        db.AddRange(project, wbs);

        var sheet = new Timesheet { EmployeeId = dana.Id, PeriodStart = new DateOnly(2026, 10, 12), PeriodEnd = new DateOnly(2026, 11, 8), Status = TimesheetStatus.Approved, CreatedAtUtc = Now, UpdatedAtUtc = Now };
        db.Add(sheet);
        TimeEntry Entry(DateOnly day, decimal hours) => new() { TimesheetId = sheet.Id, WbsElementId = wbs.Id, LaborCategoryId = category.Id, WorkDate = day, Hours = hours, EnteredAtUtc = Now, UpdatedAtUtc = Now };
        db.AddRange(Entry(new DateOnly(2026, 10, 12), 8), Entry(new DateOnly(2026, 10, 13), 8), Entry(new DateOnly(2026, 11, 3), 6));
        db.Add(new OdcEntry
        {
            ProjectId = project.Id,
            WbsElementId = wbs.Id,
            Type = OdcType.Travel,
            IncurredOn = new DateOnly(2026, 10, 16),
            Amount = 200m,
            Description = "Site visit",
            Status = OdcStatus.Approved,
            SubmittedBy = "dana@example.com",
            CreatedAtUtc = Now,
        });
        await db.SaveChangesAsync();

        CustomerId = customer.Id;
        ContractId = contract.Id;
        ProjectId = project.Id;
        CategoryId = category.Id;
    }

    public Task<AgreementBillingPlanDto> RetainerAsync(decimal amount = 4_000m, DateOnly? start = null, DateOnly? end = null, decimal? included = null,
        UnusedHoursKind unused = UnusedHoursKind.Off, decimal? overage = null, bool auto = true) =>
        Agreements.SavePlanAsync(ContractId, new SaveAgreementBillingPlanRequest(AgreementModel.Retainer, BillingCadenceKind.Monthly, start ?? new DateOnly(2026, 10, 1), end,
            amount, "Advisory retainer", IncludedHours: included, UnusedHours: unused, OverageRate: overage, AutoGenerate: auto));

    /// <summary>A second agreement of the same customer, with its own number.</summary>
    public async Task<Guid> AddAgreementAsync(string number)
    {
        await using var db = await Factory.CreateAsync();
        var contract = new Contract
        {
            CustomerId = CustomerId,
            ContractNumber = number,
            Name = $"Agreement {number}",
            Type = ContractType.FirmFixedPrice,
            PeriodStart = new DateOnly(2026, 10, 1),
            PeriodEnd = new DateOnly(2027, 9, 30),
        };
        db.Set<Contract>().Add(contract);
        await db.SaveChangesAsync();
        return contract.Id;
    }

    public async Task<InvoiceDto> ApprovedRetainerAsync(DateOnly from, DateOnly to)
    {
        var draft = await Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(ContractId, InvoiceKind.Retainer, from, to));
        return await Invoices.ApproveAsync(draft.Summary.Id, new ApproveInvoiceRequest());
    }

    public async Task AddFinanceAsync()
    {
        await using var db = await Factory.CreateAsync();
        await Workbench.Application.Identity.RoleStore.AddUserAsync(db, new AppUser { Email = "finance@example.com", DisplayName = "Fin" }, OrgRoles.Finance);
        await db.SaveChangesAsync();
    }
}
