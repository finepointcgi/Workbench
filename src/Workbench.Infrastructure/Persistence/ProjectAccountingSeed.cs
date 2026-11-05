using Microsoft.EntityFrameworkCore;
using Workbench.Domain;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for the project-accounting core: people with cost rates, a CPFF contract,
/// a project whose WBS matches the "Integrated Master Schedule" sheet, indirect and leave charge codes,
/// and fiscal years. Runs once, right after the work-management sample data.
/// </summary>
internal static class ProjectAccountingSeed
{
    public static async Task SeedAsync(WorkbenchDbContext db, Sheet planSheet, CancellationToken cancellationToken)
    {
        if (await db.Set<Employee>().AnyAsync(cancellationToken))
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var fyStart = new DateOnly(today.Month >= 10 ? today.Year : today.Year - 1, 10, 1);

        // Fiscal years run October to September (the federal fiscal year); the current and next one.
        foreach (var start in new[] { fyStart, fyStart.AddYears(1) })
        {
            var year = new FiscalYear { Year = start.Year + 1, Start = start, End = start.AddYears(1).AddDays(-1) };
            db.Add(year);
            db.AddRange(Enumerable.Range(0, 12).Select(i => new FiscalPeriod
            {
                FiscalYearId = year.Id,
                Number = i + 1,
                Name = $"{start.AddMonths(i):yyyy-MM}",
                Start = start.AddMonths(i),
                End = start.AddMonths(i + 1).AddDays(-1),
            }));
        }

        var ops = new OrgUnit { Code = "OPS", Name = "Operations" };
        var eng = new OrgUnit { Code = "ENG", Name = "Engineering", ParentId = ops.Id };
        db.AddRange(ops, eng);

        var pmCat = new LaborCategory { Code = "PM3", Name = "Program Manager III" };
        var sysEng = new LaborCategory { Code = "SE2", Name = "Systems Engineer II" };
        var analystCat = new LaborCategory { Code = "BA2", Name = "Business Analyst II" };
        var admin = new LaborCategory { Code = "ADM1", Name = "Administrative I" };
        db.AddRange(pmCat, sysEng, analystCat, admin);

        var hired = fyStart.AddYears(-2);
        var pm = new Employee { EmployeeNumber = "E1001", Email = "pm@example.com", DisplayName = "Pat Morgan", OrgUnitId = ops.Id, DefaultLaborCategoryId = pmCat.Id, HireDate = hired };
        var analyst = new Employee { EmployeeNumber = "E1002", Email = "analyst@example.com", DisplayName = "Alex Rivera", OrgUnitId = eng.Id, SupervisorId = pm.Id, DefaultLaborCategoryId = analystCat.Id, HireDate = hired };
        var engineer = new Employee { EmployeeNumber = "E1003", Email = "dev@workbench.local", DisplayName = "Dana Lee", OrgUnitId = eng.Id, SupervisorId = pm.Id, DefaultLaborCategoryId = sysEng.Id, HireDate = hired };
        var viewer = new Employee { EmployeeNumber = "E1004", Email = "viewer@example.com", DisplayName = "Val Chen", OrgUnitId = ops.Id, SupervisorId = pm.Id, DefaultLaborCategoryId = admin.Id, HireDate = hired, StandardHoursPerWeek = 20, IsExempt = false };
        db.AddRange(pm, analyst, engineer, viewer);
        db.AddRange(
            new EmployeeCostRate { EmployeeId = pm.Id, EffectiveFrom = hired, HourlyCost = 85m },
            new EmployeeCostRate { EmployeeId = pm.Id, EffectiveFrom = fyStart, HourlyCost = 88.50m },
            new EmployeeCostRate { EmployeeId = analyst.Id, EffectiveFrom = hired, HourlyCost = 52m },
            new EmployeeCostRate { EmployeeId = engineer.Id, EffectiveFrom = hired, HourlyCost = 64m },
            new EmployeeCostRate { EmployeeId = viewer.Id, EffectiveFrom = hired, HourlyCost = 31m });

        var army = new Customer { Code = "ARMY", Name = "U.S. Army PEO EIS", Category = "DoD" };
        db.Add(army);
        var contract = new Contract
        {
            CustomerId = army.Id,
            ContractNumber = "W56KGU-26-C-0001",
            Name = "Enterprise Modernization Support",
            Type = ContractType.CostPlusFixedFee,
            PeriodStart = fyStart,
            PeriodEnd = fyStart.AddYears(2).AddDays(-1),
            CeilingValue = 2_400_000m,
            FundedValue = 600_000m,
        };
        db.Add(contract);
        db.AddRange(
            new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = pmCat.Id, EffectiveFrom = fyStart, HourlyBillRate = 185m },
            new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = sysEng.Id, EffectiveFrom = fyStart, HourlyBillRate = 142m },
            new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = analystCat.Id, EffectiveFrom = fyStart, HourlyBillRate = 118m });

        // Direct project: its WBS mirrors the plan sheet's items (matched by the code stored on each item).
        var project = new Project
        {
            Code = "1001",
            Name = "Enterprise Modernization",
            ContractId = contract.Id,
            ProjectManagerId = pm.Id,
            Start = contract.PeriodStart,
            Finish = contract.PeriodEnd,
            SheetId = planSheet.Id,
        };
        db.Add(project);
        var items = await db.Items.Where(i => i.SheetId == planSheet.Id).ToListAsync(cancellationToken);
        Guid? ItemFor(string code) => items.FirstOrDefault(i => i.CostpointWbsId == code && !items.Any(c => c.ParentId == i.Id && c.CostpointWbsId == code))?.Id;

        var root = new WbsElement { ProjectId = project.Id, Code = "1001.01", Name = "Enterprise Modernization", IsChargeable = false, Order = 0 };
        var planning = new WbsElement { ProjectId = project.Id, ParentId = root.Id, Code = "1001.01.01", Name = "Planning and kickoff", ItemId = ItemFor("1001.01.01"), Order = 0 };
        var requirements = new WbsElement { ProjectId = project.Id, ParentId = root.Id, Code = "1001.01.02", Name = "Requirements", ItemId = ItemFor("1001.01.02"), Order = 1 };
        var baseline = new WbsElement { ProjectId = project.Id, ParentId = root.Id, Code = "1001.01.03", Name = "Baseline schedule", ItemId = ItemFor("1001.01.03"), Order = 2 };
        var execution = new WbsElement { ProjectId = project.Id, ParentId = root.Id, Code = "1001.01.04", Name = "Execution", ItemId = ItemFor("1001.02"), Order = 3 };
        db.AddRange(root, planning, requirements, baseline, execution);
        foreach (var element in new[] { planning, requirements, baseline, execution })
        {
            db.AddRange(new[] { pm, analyst, engineer }.Select(e => new ChargeAssignment { WbsElementId = element.Id, EmployeeId = e.Id }));
        }

        // Indirect and leave codes anyone may charge, so timesheets can account for all hours.
        var indirect = new Project { Code = "IND", Name = "Indirect and leave", Start = fyStart.AddYears(-5) };
        db.Add(indirect);
        var indRoot = new WbsElement { ProjectId = indirect.Id, Code = "IND", Name = "Indirect", Kind = ChargeKind.Indirect, IsChargeable = false, RequiresAssignment = false };
        db.AddRange(
            indRoot,
            new WbsElement { ProjectId = indirect.Id, ParentId = indRoot.Id, Code = "IND.OH", Name = "Overhead", Kind = ChargeKind.Indirect, RequiresAssignment = false, Order = 0 },
            new WbsElement { ProjectId = indirect.Id, ParentId = indRoot.Id, Code = "IND.GA", Name = "General and administrative", Kind = ChargeKind.Indirect, RequiresAssignment = false, Order = 1 },
            new WbsElement { ProjectId = indirect.Id, ParentId = indRoot.Id, Code = "IND.BP", Name = "Bid and proposal", Kind = ChargeKind.Indirect, RequiresAssignment = false, Order = 2 });
        var leaveRoot = new WbsElement { ProjectId = indirect.Id, Code = "LV", Name = "Leave", Kind = ChargeKind.Leave, IsChargeable = false, RequiresAssignment = false, Order = 1 };
        db.AddRange(
            leaveRoot,
            new WbsElement { ProjectId = indirect.Id, ParentId = leaveRoot.Id, Code = "LV.PTO", Name = "Paid time off", Kind = ChargeKind.Leave, RequiresAssignment = false, Order = 0 },
            new WbsElement { ProjectId = indirect.Id, ParentId = leaveRoot.Id, Code = "LV.HOL", Name = "Holiday", Kind = ChargeKind.Leave, RequiresAssignment = false, Order = 1 });

        await db.SaveChangesAsync(cancellationToken);
    }
}
