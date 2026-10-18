using Workbench.Application;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

/// <summary>
/// Project accounting on top of <see cref="SeededHost"/>: pm@ manages project 1001 (contract C1, plan sheet = the
/// Program Office "Schedule" sheet); 2002 is on contract C2 with no manager. analyst@ and viewer@ report to pm@;
/// outsider@ has no supervisor. Each of the three has approved hours on <see cref="Day"/>, and there's a vendor ODC
/// (entered by finance@) and an expense line (analyst@'s) on 1001, plus a budget version on 1001.
/// </summary>
internal sealed record ProjectAccountingFixture(
    Guid PmId,
    Guid AnalystId,
    Guid ViewerId,
    Guid OutsiderId,
    Guid Project1,
    Guid Project2,
    Guid Contract1,
    Guid Contract2,
    Guid Wbs1,
    Guid VendorOdcId,
    Guid ExpenseOdcId,
    Guid BudgetVersionId)
{
    public static readonly DateOnly Day = new(2026, 10, 14);

    public static async Task<ProjectAccountingFixture> SeedAsync(SeededHost seeded)
    {
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var year = new FiscalYear { Year = 2027, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2027, 9, 30) };
        db.Set<FiscalYear>().Add(year);
        db.Set<FiscalPeriod>().Add(new FiscalPeriod { FiscalYearId = year.Id, Name = "2026-10", Number = 1, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2026, 10, 31) });

        var category = new LaborCategory { Code = "SE2", Name = "Systems Engineer II" };
        db.Set<LaborCategory>().Add(category);
        var hired = new DateOnly(2024, 1, 1);
        Employee Person(string number, string email, string name, Guid? supervisor = null) =>
            new() { EmployeeNumber = number, Email = email, DisplayName = name, SupervisorId = supervisor, DefaultLaborCategoryId = category.Id, HireDate = hired };
        var pm = Person("E1", Owner, "Pat Morgan");
        var analyst = Person("E2", Editor, "Alex Rivera", pm.Id);
        var viewer = Person("E3", Viewer, "Val Chen", pm.Id);
        var outsider = Person("E4", Outsider, "Olly Out");
        db.Set<Employee>().AddRange(pm, analyst, viewer, outsider);
        db.Set<EmployeeCostRate>().Add(new EmployeeCostRate { EmployeeId = analyst.Id, EffectiveFrom = hired, HourlyCost = 87.5m });

        var customer = new Customer { Code = "ARMY", Name = "Army" };
        var c1 = new Contract { CustomerId = customer.Id, ContractNumber = "C1", Name = "Support", PeriodStart = hired, PeriodEnd = hired.AddYears(5), CeilingValue = 1_000_000m, FundedValue = 400_000m };
        var c2 = new Contract { CustomerId = customer.Id, ContractNumber = "C2", Name = "Other", PeriodStart = hired, PeriodEnd = hired.AddYears(5), CeilingValue = 500_000m, FundedValue = 100_000m };
        db.Set<Customer>().Add(customer);
        db.Set<Contract>().AddRange(c1, c2);
        var p1 = new Project { Code = "1001", Name = "Modernization", ContractId = c1.Id, ProjectManagerId = pm.Id, SheetId = seeded.SheetId };
        var p2 = new Project { Code = "2002", Name = "Unmanaged", ContractId = c2.Id };
        db.Set<Project>().AddRange(p1, p2);
        var w1 = new WbsElement { ProjectId = p1.Id, Code = "1001.01", Name = "Work" };
        var w2 = new WbsElement { ProjectId = p2.Id, Code = "2002.01", Name = "Other work" };
        db.Set<WbsElement>().AddRange(w1, w2);

        var now = DateTime.UtcNow;
        foreach (var (employee, wbs, hours) in new[] { (analyst, w1, 8m), (viewer, w2, 4m), (outsider, w2, 2m) })
        {
            var sheet = new Timesheet { EmployeeId = employee.Id, PeriodStart = new DateOnly(2026, 10, 12), PeriodEnd = new DateOnly(2026, 10, 18), Status = TimesheetStatus.Approved, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Set<Timesheet>().Add(sheet);
            db.Set<TimeEntry>().Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = wbs.Id, LaborCategoryId = category.Id, WorkDate = Day, Hours = hours, EnteredAtUtc = now, UpdatedAtUtc = now });
            db.Set<TimeDayRecord>().Add(new TimeDayRecord { EmployeeId = employee.Id, TimesheetId = sheet.Id, WorkDate = Day, FirstEnteredAtUtc = now });
        }

        var vendor = new OdcEntry { ProjectId = p1.Id, WbsElementId = w1.Id, Type = OdcType.Materials, IncurredOn = Day, Amount = 500m, Description = "Server rack", Vendor = "Acme", InvoiceNumber = "INV-1", SubmittedBy = Finance, CreatedAtUtc = now };
        var expense = new OdcEntry { ProjectId = p1.Id, WbsElementId = w1.Id, Type = OdcType.Travel, IncurredOn = Day, Amount = 40m, Description = "Taxi", EmployeeId = analyst.Id, SubmittedBy = Editor, CreatedAtUtc = now };
        db.Set<OdcEntry>().AddRange(vendor, expense);
        var version = new BudgetVersion { ProjectId = p1.Id, Name = "Baseline", Kind = BudgetKind.Baseline, CreatedBy = Finance, CreatedAtUtc = now };
        db.Set<BudgetVersion>().Add(version);
        await db.SaveChangesAsync();

        return new ProjectAccountingFixture(pm.Id, analyst.Id, viewer.Id, outsider.Id, p1.Id, p2.Id, c1.Id, c2.Id, w1.Id, vendor.Id, expense.Id, version.Id);
    }
}
