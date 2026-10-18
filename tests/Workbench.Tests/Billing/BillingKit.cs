using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Billing;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Accounting;
using Workbench.Tests.Time;

namespace Workbench.Tests.Billing;

/// <summary>
/// Billing services over an in-memory database with the default chart, FY2027 (Oct 2026 – Sep 2027) open, and contract
/// W1 for the Army with four CLINs: 0001 CPFF 7% fee, 0002 cost (no fee), 0003 T&amp;M and 0004 FFP. Dana (SE2, $50/h cost)
/// has approved hours on the T&amp;M WBS (Oct 12–13 at the $100 bill rate, Oct 20 at the $120 rate from Oct 15) and on the
/// CPFF WBS; the T&amp;M WBS has approved travel ($500), materials ($1,000), an unallowable dinner ($75) and a submitted
/// ODC ($300). Fee withhold is 15%, material handling 5%. Cost-plus uses <see cref="Burdened"/> unless built without it.
/// </summary>
internal sealed class BillingKit
{
    public static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    public static readonly DateOnly October = new(2026, 10, 1);
    public static readonly DateOnly OctoberEnd = new(2026, 10, 31);

    private BillingKit(IWorkbenchDbContextFactory factory, TestUser user, TestClock clock, LedgerKit gl, FakeBurdenedCost? burdened, RecordingPublisher publisher)
    {
        Factory = factory;
        User = user;
        Clock = clock;
        Gl = gl;
        Burdened = burdened;
        Publisher = publisher;
        var audit = new AuditRecorder(user, clock);
        var ledger = new BillingLedger(gl.Ledger);
        var generator = new InvoiceGenerator(new BillingCostSource(burdened is null ? [] : [burdened]));
        Accruals = new UnbilledAccruals(generator, ledger, audit);
        Invoices = new InvoiceService(factory, generator, ledger, audit, clock, new InvoiceDocuments(Options.Create(new BillingOptions { CompanyName = "Test Co" })), Accruals);
        Milestones = new BillingMilestoneService(factory, audit);
        Payments = new PaymentService(factory, ledger, audit, clock);
        Ar = new ArReportService(factory, new OverdueInvoiceCheck(factory, publisher, clock), clock);
        CloseCheck = new UnbilledAccrualCloseCheck(Accruals);
    }

    public IWorkbenchDbContextFactory Factory { get; }
    public TestUser User { get; }
    public TestClock Clock { get; }
    public LedgerKit Gl { get; }
    public FakeBurdenedCost? Burdened { get; }
    public RecordingPublisher Publisher { get; }
    public UnbilledAccruals Accruals { get; }
    public InvoiceService Invoices { get; }
    public BillingMilestoneService Milestones { get; }
    public PaymentService Payments { get; }
    public ArReportService Ar { get; }
    public UnbilledAccrualCloseCheck CloseCheck { get; }

    public Guid CustomerId { get; private set; }
    public Guid ContractId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Clin CostClin { get; private set; } = default!;
    public Clin OdcClin { get; private set; } = default!;
    public Clin TmClin { get; private set; } = default!;
    public Clin FfpClin { get; private set; } = default!;
    public WbsElement CostWbs { get; private set; } = default!;
    public WbsElement OdcWbs { get; private set; } = default!;
    public WbsElement TmWbs { get; private set; } = default!;
    public Guid DanaId { get; private set; }
    public Guid CategoryId { get; private set; }
    public Guid UnallowableOdcId { get; private set; }

    public static async Task<BillingKit> CreateAsync(bool withIndirectRates = true)
    {
        var factory = TestDb.CreateFactory();
        var user = new TestUser("finance@example.com");
        var clock = new TestClock(Now);
        var gl = await LedgerKit.CreateAsync(factory, user, clock);
        var kit = new BillingKit(factory, user, clock, gl, withIndirectRates ? new FakeBurdenedCost() : null, new RecordingPublisher());
        await kit.SeedAsync();
        return kit;
    }

    private async Task SeedAsync()
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

        var customer = new Customer { Code = "ARMY", Name = "Army" };
        var contract = new Contract
        {
            CustomerId = customer.Id,
            ContractNumber = "W1",
            Name = "Support",
            Type = ContractType.CostPlusFixedFee,
            PeriodStart = October,
            PeriodEnd = new DateOnly(2028, 9, 30),
            CeilingValue = 670_000m,
            FundedValue = 180_000m,
        };
        db.AddRange(customer, contract);
        Clin NewClin(string number, ClinPricing pricing, decimal funded, decimal ceiling, decimal? fee = null, int order = 0) => new()
        {
            ContractId = contract.Id,
            Number = number,
            Description = $"CLIN {number}",
            Pricing = pricing,
            FeeRate = fee,
            FundedValue = funded,
            CeilingValue = ceiling,
            PeriodStart = contract.PeriodStart,
            PeriodEnd = contract.PeriodEnd,
            Order = order,
        };
        CostClin = NewClin("0001", ClinPricing.CostPlusFixedFee, 100_000m, 500_000m, 0.07m, 0);
        OdcClin = NewClin("0002", ClinPricing.CostNoFee, 10_000m, 50_000m, order: 1);
        TmClin = NewClin("0003", ClinPricing.TimeAndMaterials, 50_000m, 100_000m, order: 2);
        FfpClin = NewClin("0004", ClinPricing.FirmFixedPrice, 20_000m, 20_000m, order: 3);
        db.AddRange(CostClin, OdcClin, TmClin, FfpClin);
        db.AddRange(
            new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = category.Id, EffectiveFrom = October, HourlyBillRate = 100m },
            new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = category.Id, EffectiveFrom = new DateOnly(2026, 10, 15), HourlyBillRate = 120m });
        db.Add(new ContractBillingSettings { ContractId = contract.Id, InvoicePrefix = "W1", FeeWithholdRate = 0.15m, MaterialHandlingRate = 0.05m });
        db.Add(new CustomerBillingProfile { CustomerId = customer.Id, PaymentTermsDays = 30 });

        var project = new Project { Code = "1001", Name = "Modernization", ContractId = contract.Id, ProjectManagerId = pat.Id };
        CostWbs = new WbsElement { ProjectId = project.Id, Code = "1001.01", Name = "Engineering", ClinId = CostClin.Id };
        OdcWbs = new WbsElement { ProjectId = project.Id, Code = "1001.02", Name = "Travel", ClinId = OdcClin.Id };
        TmWbs = new WbsElement { ProjectId = project.Id, Code = "1001.03", Name = "Surge support", ClinId = TmClin.Id };
        db.AddRange(project, CostWbs, OdcWbs, TmWbs);

        var now = Now;
        var sheet = new Timesheet { EmployeeId = dana.Id, PeriodStart = new DateOnly(2026, 10, 12), PeriodEnd = new DateOnly(2026, 10, 25), Status = TimesheetStatus.Approved, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Add(sheet);
        TimeEntry Entry(WbsElement wbs, DateOnly day, decimal hours) => new() { TimesheetId = sheet.Id, WbsElementId = wbs.Id, LaborCategoryId = category.Id, WorkDate = day, Hours = hours, EnteredAtUtc = now, UpdatedAtUtc = now };
        db.AddRange(
            Entry(TmWbs, new DateOnly(2026, 10, 12), 8),
            Entry(TmWbs, new DateOnly(2026, 10, 13), 8),
            Entry(TmWbs, new DateOnly(2026, 10, 20), 4),
            Entry(CostWbs, new DateOnly(2026, 10, 14), 10));

        OdcEntry Odc(WbsElement wbs, OdcType type, decimal amount, string description, OdcStatus status = OdcStatus.Approved, bool unallowable = false) => new()
        {
            ProjectId = project.Id,
            WbsElementId = wbs.Id,
            Type = type,
            IncurredOn = new DateOnly(2026, 10, 16),
            Amount = amount,
            Description = description,
            IsUnallowable = unallowable,
            UnallowableReason = unallowable ? "Alcohol" : null,
            Status = status,
            SubmittedBy = "dana@example.com",
            CreatedAtUtc = now,
        };
        var dinner = Odc(TmWbs, OdcType.Other, 75m, "Team dinner (alcohol)", unallowable: true);
        db.AddRange(
            Odc(TmWbs, OdcType.Travel, 500m, "Site visit"),
            Odc(TmWbs, OdcType.Materials, 1_000m, "Cables"),
            dinner,
            Odc(TmWbs, OdcType.Equipment, 300m, "Not approved yet", OdcStatus.Submitted),
            Odc(OdcWbs, OdcType.Travel, 400m, "Trip on the travel CLIN"));
        await db.SaveChangesAsync();

        CustomerId = customer.Id;
        ContractId = contract.Id;
        ProjectId = project.Id;
        DanaId = dana.Id;
        CategoryId = category.Id;
        UnallowableOdcId = dinner.Id;
    }

    public GenerateInvoiceRequest Request(InvoiceKind type, DateOnly? from = null, DateOnly? to = null, bool release = false) =>
        new(ContractId, type, from ?? October, to ?? OctoberEnd, new DateOnly(2026, 11, 5), release);

    public async Task<InvoiceDto> ApprovedAsync(InvoiceKind type, DateOnly? from = null, DateOnly? to = null, string? overrideReason = null)
    {
        var draft = await Invoices.GenerateDraftAsync(Request(type, from, to));
        return await Invoices.ApproveAsync(draft.Summary.Id, new ApproveInvoiceRequest(overrideReason));
    }

    public async Task<Invoice> LoadAsync(Guid id)
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<Invoice>().AsNoTracking().Include(i => i.Lines).ThenInclude(l => l.Sources).FirstAsync(i => i.Id == id);
    }

    public async Task SetFundingAsync(Clin clin, decimal funded, decimal? ceiling = null)
    {
        await using var db = await Factory.CreateAsync();
        var row = await db.Set<Clin>().FirstAsync(c => c.Id == clin.Id);
        row.FundedValue = funded;
        row.CeilingValue = ceiling ?? row.CeilingValue;
        await db.SaveChangesAsync();
    }
}

/// <summary>A stand-in for the indirect-rates module: per-WBS cost for each basis, set by the test.</summary>
internal sealed class FakeBurdenedCost : IBurdenedCostService
{
    public Dictionary<RateBasis, List<BurdenedCostLine>> Lines { get; } = [];

    public List<(Guid ProjectId, DateOnly From, DateOnly To, RateBasis Basis)> Calls { get; } = [];

    public void Set(RateBasis basis, params BurdenedCostLine[] lines) => Lines[basis] = [.. lines];

    public Task<BurdenedCostResult> GetProjectCostAsync(Guid projectId, DateOnly from, DateOnly to, RateBasis basis, CancellationToken cancellationToken = default)
    {
        Calls.Add((projectId, from, to, basis));
        var lines = Lines.GetValueOrDefault(basis) ?? [];
        return Task.FromResult(new BurdenedCostResult(lines.Where(l => l.ProjectId == projectId).ToList(),
            [new AppliedRates(basis, 2027, 0.30m, 0.20m, 0.10m)]));
    }
}

internal sealed class RecordingPublisher : INotificationPublisher
{
    public List<(string Email, string Kind, string Title, string? Link)> Sent { get; } = [];

    public Task<bool> PublishAsync(string recipientEmail, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null)
    {
        Sent.Add((recipientEmail, kind, title, link));
        return Task.FromResult(true);
    }

    public async Task<IReadOnlyList<string>> PublishAsync(IEnumerable<string?> recipientEmails, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null)
    {
        var sent = new List<string>();
        foreach (var email in recipientEmails.OfType<string>().Distinct())
        {
            await PublishAsync(email, kind, title, body, link, sheetId, itemId);
            sent.Add(email);
        }

        return sent;
    }
}
