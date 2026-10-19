using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Collaboration;
using Workbench.Domain.Accounting;
using Workbench.Domain.Companies;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for multicompany: the default company becomes "Workbench Demo Corp" (MAIN), and a subsidiary
/// "Workbench Federal Solutions LLC" (FED) gets its own customer contract, project 4001 and employee Jordan Blake (E1041).
/// Two weeks of intercompany labor post through the ordinary posting engine: Jordan (FED) charges MAIN's project 1001 and
/// Casey Nguyen (E1042, MAIN) charges FED's project 4001, so both ledgers carry due-to/due-from balances that the
/// consolidation eliminates. Runs once, last (it posts into the seeded chart of accounts).
/// </summary>
internal static class CompanySeed
{
    public const string FedCode = "FED";
    private const string Actor = "pm@example.com";

    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<Company>().AnyAsync(c => c.Code == FedCode, cancellationToken) || !await db.Set<Account>().AnyAsync(cancellationToken))
        {
            return;
        }

        var main = await db.Set<Company>().FirstOrDefaultAsync(c => c.Id == Company.DefaultId, cancellationToken);
        if (main is null)
        {
            main = new Company { Id = Company.DefaultId, Code = "MAIN", LegalName = "Main company", CreatedAtUtc = DateTime.UtcNow };
            db.Add(main);
        }

        main.LegalName = "Workbench Demo Corp";
        main.Ein = "12-3456789";
        var fed = new Company { Code = FedCode, LegalName = "Workbench Federal Solutions LLC", Ein = "98-7654321", CreatedAtUtc = DateTime.UtcNow };
        db.Add(fed);

        // The default chart carries the intercompany accounts; add them to a chart installed before it did.
        var accounts = await db.Set<Account>().ToDictionaryAsync(a => a.Number, cancellationToken);
        Account Ensure(string number, string name, AccountType type, string parent)
        {
            if (!accounts.TryGetValue(number, out var account))
            {
                account = new Account { Number = number, Name = name, Type = type, NormalBalance = Account.DefaultNormalBalance(type), ParentId = accounts.GetValueOrDefault(parent)?.Id, CreatedAtUtc = DateTime.UtcNow };
                db.Add(account);
                accounts[number] = account;
            }

            return account;
        }

        var dueFrom = Ensure("1180", "Due from affiliated companies", AccountType.Asset, "1000");
        var dueTo = Ensure("2180", "Due to affiliated companies", AccountType.Liability, "2000");
        db.AddRange(
            new IntercompanyAccount { CompanyId = main.Id, CounterpartyCompanyId = fed.Id, DueFromAccountId = dueFrom.Id, DueToAccountId = dueTo.Id, UpdatedBy = Actor, UpdatedAtUtc = DateTime.UtcNow },
            new IntercompanyAccount { CompanyId = fed.Id, CounterpartyCompanyId = main.Id, DueFromAccountId = dueFrom.Id, DueToAccountId = dueTo.Id, UpdatedBy = Actor, UpdatedAtUtc = DateTime.UtcNow });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var fyStart = await db.Set<FiscalYear>().Where(y => y.Start <= today && y.End >= today).Select(y => (DateOnly?)y.Start).FirstOrDefaultAsync(cancellationToken)
            ?? new DateOnly(today.Month >= 10 ? today.Year : today.Year - 1, 10, 1);
        var category = await db.Set<LaborCategory>().FirstOrDefaultAsync(c => c.Code == "SE2", cancellationToken)
            ?? await db.Set<LaborCategory>().FirstAsync(cancellationToken);
        var customer = await db.Set<Customer>().FirstOrDefaultAsync(c => c.Code == "ARMY", cancellationToken)
            ?? await db.Set<Customer>().FirstAsync(cancellationToken);
        var engineering = await db.Set<OrgUnit>().OrderBy(o => o.Code).FirstOrDefaultAsync(cancellationToken);

        var contract = new Contract
        {
            CompanyId = fed.Id,
            CustomerId = customer.Id,
            ContractNumber = "W91QUZ-27-F-0042",
            Name = "Cyber Readiness Assessments",
            Type = ContractType.TimeAndMaterials,
            PeriodStart = fyStart,
            PeriodEnd = fyStart.AddYears(1).AddDays(-1),
            CeilingValue = 450_000m,
            FundedValue = 150_000m,
        };
        db.Add(contract);
        db.Add(new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = category.Id, EffectiveFrom = fyStart, HourlyBillRate = 155m });
        var project = new Project { CompanyId = fed.Id, Code = "4001", Name = "Cyber Readiness Assessments", ContractId = contract.Id, Start = contract.PeriodStart, Finish = contract.PeriodEnd };
        db.Add(project);
        var fedRoot = new WbsElement { ProjectId = project.Id, Code = "4001.01", Name = "Assessments", IsChargeable = false };
        var fedWork = new WbsElement { ProjectId = project.Id, ParentId = fedRoot.Id, Code = "4001.01.01", Name = "Site assessments", RequiresAssignment = false };
        db.AddRange(fedRoot, fedWork);

        var jordan = new Employee
        {
            CompanyId = fed.Id, EmployeeNumber = "E1041", Email = "jordan.blake@example.com", DisplayName = "Jordan Blake",
            OrgUnitId = engineering?.Id, DefaultLaborCategoryId = category.Id, HireDate = fyStart.AddYears(-1),
        };
        var casey = new Employee
        {
            EmployeeNumber = "E1042", Email = "casey.nguyen@example.com", DisplayName = "Casey Nguyen",
            OrgUnitId = engineering?.Id, DefaultLaborCategoryId = category.Id, HireDate = fyStart.AddYears(-1),
        };
        db.AddRange(jordan, casey);
        db.AddRange(
            new EmployeeCostRate { EmployeeId = jordan.Id, EffectiveFrom = jordan.HireDate, HourlyCost = 58m },
            new EmployeeCostRate { EmployeeId = casey.Id, EffectiveFrom = casey.HireDate, HourlyCost = 61m });

        // MAIN's project 1001: Jordan helps with execution, so FED carries the payroll and MAIN the project cost.
        var execution = await db.Set<WbsElement>().FirstOrDefaultAsync(w => w.Code == "1001.01.04", cancellationToken);
        if (execution is not null)
        {
            db.Add(new ChargeAssignment { WbsElementId = execution.Id, EmployeeId = jordan.Id });
        }

        // The last two full weeks, inside the open period that covers them.
        var now = DateTime.UtcNow;
        var lastMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7) - 7);
        var sheets = new List<Timesheet>();
        foreach (var monday in new[] { lastMonday.AddDays(-7), lastMonday })
        {
            Timesheet Sheet(Employee who, params (WbsElement Wbs, decimal Hours)[] daily)
            {
                var sheet = new Timesheet
                {
                    EmployeeId = who.Id, PeriodStart = monday, PeriodEnd = monday.AddDays(6), Status = TimesheetStatus.Approved,
                    CertifiedAtUtc = now, SubmittedAtUtc = now, ApprovedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now,
                };
                for (var d = 0; d < 5; d++)
                {
                    foreach (var (wbs, hours) in daily)
                    {
                        sheet.Entries.Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = wbs.Id, LaborCategoryId = category.Id, WorkDate = monday.AddDays(d), Hours = hours, EnteredAtUtc = now, UpdatedAtUtc = now });
                    }
                }

                return sheet;
            }

            sheets.Add(execution is null ? Sheet(jordan, (fedWork, 8m)) : Sheet(jordan, (fedWork, 5m), (execution, 3m)));
            sheets.Add(Sheet(casey, (fedWork, 4m)));
        }

        db.AddRange(sheets);
        await db.SaveChangesAsync(cancellationToken);

        // Post the approved time like an approval would: the ledger adds the intercompany lines.
        var factory = (IWorkbenchDbContextFactory)services.GetService(typeof(IWorkbenchDbContextFactory))!;
        var clock = TimeProvider.System;
        var engine = new PostingEngine(factory, new GeneralLedger(new AuditRecorder(new SeedUser(), clock), clock), NullLogger<PostingEngine>.Instance);
        foreach (var sheet in sheets)
        {
            await engine.PostTimesheetAsync(sheet.Id, ct: cancellationToken);
        }
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email => Actor;
    }
}
