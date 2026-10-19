using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.Approvals;
using Workbench.Contracts.Approvals;
using Workbench.Domain;
using Workbench.Domain.Accounting;
using Workbench.Domain.Approvals;
using Workbench.Domain.Billing;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Infrastructure.Persistence;

namespace Workbench.LoadData;

/// <summary>
/// Builds a mid-size contractor: 20 org units, 500 employees (CEO, 4 VPs, 45 managers who approve time, the rest staff),
/// 25 contracts with CLINs and 60 direct projects of 49 WBS elements each (about 3,000 elements with the indirect
/// codes), plan sheets tied to the WBS, 140 more tracker sheets, budgets, and the GL setup. Then five fiscal years of
/// history (see LoadGenerator.History.cs) and the real-service phases (LoadGenerator.Services.cs).
/// </summary>
internal sealed partial class LoadGenerator(GeneratorOptions options, IDbContextFactory<WorkbenchDbContext> factory, IServiceProvider services, SwitchableUser user)
{
    private const string Domain = "loadco.test";
    private const string SystemActor = "loaddata@workbench.local";

    private readonly Random _rng = new(options.Seed);
    private readonly Bulk _bulk = new(factory);

    // ---- The generated world, kept in memory for the history pass ----

    private sealed class Person
    {
        public required Employee E { get; init; }
        public required LaborCategory Lcat { get; init; }
        public required decimal BaseRate { get; init; }
        public Person? Supervisor { get; set; }
        public bool Indirect { get; set; }
        public Guid[] IndirectCodes { get; set; } = [];
        public List<EmployeeCostRate> Rates { get; } = [];

        /// <summary>Per fiscal year: the leaf WBS elements (direct) the person charges.</summary>
        public Dictionary<int, Guid[]> Charges { get; } = [];

        public string Email => E.Email;
    }

    private sealed class Proj
    {
        public required Project P { get; init; }
        public required Ctr Contract { get; init; }
        public List<WbsElement> Leaves { get; } = [];
        public WbsElement OdcLeaf => Leaves[^1];
        public Person? Manager { get; set; }
    }

    private sealed class Ctr
    {
        public required Contract C { get; init; }
        public required Clin LaborClin { get; init; }
        public required Clin OdcClin { get; init; }
        public List<Proj> Projects { get; } = [];
        public Dictionary<(Guid Lcat, int Fy), decimal> BillRates { get; } = [];
        public string Prefix => C.ContractNumber[^4..];
    }

    private readonly List<OrgUnit> _orgUnits = [];
    private readonly List<Person> _people = [];
    private readonly List<LaborCategory> _lcats = [];
    private readonly List<Customer> _customers = [];
    private readonly List<Ctr> _contracts = [];
    private readonly List<Proj> _projects = [];
    private readonly List<FiscalYear> _years = [];
    private readonly List<FiscalPeriod> _periods = [];
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, WbsElement> _wbs = [];
    private readonly Dictionary<string, WbsElement> _indirect = new(StringComparer.Ordinal);
    private readonly List<Vendor> _vendors = [];
    private Guid _timesheetWorkflowVersion;
    private Guid _expenseWorkflowVersion;
    private int _sequence;

    private DateOnly HistoryStart => _years[0].Start;

    /// <summary>Monday of the week that contains <see cref="GeneratorOptions.Today"/> (left for the load test).</summary>
    private DateOnly CurrentWeek => Monday(options.Today);

    /// <summary>The week before: submitted through the real services, pending approval.</summary>
    private DateOnly LastWeek => CurrentWeek.AddDays(-7);

    public async Task RunAsync(bool includeServices)
    {
        var started = DateTime.UtcNow;
        Step("Fiscal calendar and chart of accounts");
        await CalendarAndLedgerAsync();
        Step("Organization, people and users");
        await PeopleAsync();
        Step("Customers, contracts, CLINs, projects, WBS and plan sheets");
        await ContractsAndProjectsAsync();
        Step("Tracker sheets and workspaces");
        await TrackerSheetsAsync();
        Step("Budgets");
        await BudgetsAsync();
        await _bulk.FlushAsync();
        Step("Indirect pools and rates");
        await IndirectSetupAsync();

        Step($"History {HistoryStart:yyyy-MM-dd} to {LastWeek.AddDays(-1):yyyy-MM-dd} (time, ODCs, expenses, AP, payroll, billing)");
        await HistoryAsync();
        await _bulk.FlushAsync();
        Console.WriteLine($"  inserted {_bulk.Total:N0} rows ({_bulk.SaveTime.TotalMinutes:0.0} min saving)");
        foreach (var (name, count) in _bulk.Counts.OrderByDescending(c => c.Value).Take(25))
        {
            Console.WriteLine($"    {name,-28} {count,12:N0}");
        }

        Step("Funding, ceilings and contract values from the history");
        await FundingAsync();

        Step("Indirect allocation runs (the real AllocationEngine), one per period");
        await AllocationsAsync();

        Step("Revenue setup and revenue runs (the real RevenueRunService), last 12 periods");
        await RevenueAsync();

        Step("Close the fiscal periods before last month");
        await ClosePeriodsAsync();

        if (includeServices)
        {
            Step($"Last week ({LastWeek:yyyy-MM-dd}): every employee enters and submits time through the real services");
            await SubmitLastWeekAsync();
        }

        Step("Checks");
        await VerifyAsync();
        Console.WriteLine($"Generated in {(DateTime.UtcNow - started).TotalMinutes:0.0} min.");
    }

    private static void Step(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");

    private static DateOnly Monday(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private T Pick<T>(IReadOnlyList<T> list) => list[_rng.Next(list.Count)];

    private decimal Between(decimal min, decimal max) => min + (decimal)_rng.NextDouble() * (max - min);

    private static DateTime Utc(DateOnly date, int hour, int minute = 0) => date.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc);

    private int FiscalYearOf(DateOnly date) => _years.First(y => y.Start <= date && date <= y.End).Year;

    private FiscalPeriod PeriodOf(DateOnly date) => _periods.First(p => p.Start <= date && date <= p.End);

    // ---- Calendar and ledger ----

    private async Task CalendarAndLedgerAsync()
    {
        // Five complete fiscal years of history before the current one (Oct–Sep, named for the year they end in).
        var today = options.Today;
        var currentStart = new DateOnly(today.Month >= 10 ? today.Year : today.Year - 1, 10, 1);
        for (var start = currentStart.AddYears(-options.Years); start <= currentStart; start = start.AddYears(1))
        {
            var end = start.AddYears(1).AddDays(-1);
            var year = new FiscalYear { Year = end.Year, Start = start, End = end };
            _years.Add(year);
            await _bulk.AddAsync(year);
            for (var i = 0; i < 12; i++)
            {
                var period = new FiscalPeriod
                {
                    FiscalYearId = year.Id,
                    Number = i + 1,
                    Name = $"{start.AddMonths(i):yyyy-MM}",
                    Start = start.AddMonths(i),
                    End = start.AddMonths(i + 1).AddDays(-1),
                };
                _periods.Add(period);
                await _bulk.AddAsync(period);
            }
        }

        var now = DateTime.UtcNow;
        var (accounts, rules) = DefaultChartOfAccounts.Build(now, SystemActor);
        foreach (var account in accounts)
        {
            _accounts[account.Number] = account;
        }

        await _bulk.AddRangeAsync(accounts);
        await _bulk.AddRangeAsync(rules);

        // The timesheet and expense workflows as the approval engine creates them on first use (version 1 = the defaults).
        foreach (var type in new[] { ApprovalDocumentTypes.Timesheet, ApprovalDocumentTypes.ExpenseReport })
        {
            var workflow = new ApprovalWorkflow { DocumentType = type, Name = ApprovalDefaults.Name(type), CreatedAtUtc = Utc(HistoryStart, 12), UpdatedAtUtc = Utc(HistoryStart, 12) };
            var version = new ApprovalWorkflowVersion
            {
                WorkflowId = workflow.Id,
                Version = 1,
                DefinitionJson = ApprovalDefaults.Serialize(ApprovalDefaults.For(type)),
                CreatedBy = SystemActor,
                CreatedAtUtc = workflow.CreatedAtUtc,
                Note = "Built-in default",
            };
            workflow.CurrentVersionId = version.Id;
            await _bulk.AddAsync(workflow);
            await _bulk.AddAsync(version);
            if (type == ApprovalDocumentTypes.Timesheet)
            {
                _timesheetWorkflowVersion = version.Id;
            }
            else
            {
                _expenseWorkflowVersion = version.Id;
            }
        }
    }

    // ---- People ----

    private static readonly string[] FirstNames =
        ["James", "Mary", "Robert", "Patricia", "John", "Jennifer", "Michael", "Linda", "David", "Elizabeth", "William", "Barbara", "Richard", "Susan",
         "Joseph", "Jessica", "Thomas", "Sarah", "Charles", "Karen", "Daniel", "Lisa", "Matthew", "Nancy", "Anthony", "Betty", "Mark", "Sandra",
         "Steven", "Ashley", "Andrew", "Kimberly", "Joshua", "Emily", "Kevin", "Donna", "Brian", "Michelle", "Priya", "Wei", "Aisha", "Carlos",
         "Fatima", "Hiroshi", "Olga", "Kwame", "Sofia", "Ravi"];

    private static readonly string[] LastNames =
        ["Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez", "Hernandez", "Lopez", "Gonzalez",
         "Wilson", "Anderson", "Thomas", "Taylor", "Moore", "Jackson", "Martin", "Lee", "Perez", "Thompson", "White", "Harris", "Sanchez", "Clark",
         "Ramirez", "Lewis", "Robinson", "Walker", "Young", "Allen", "King", "Wright", "Scott", "Torres", "Nguyen", "Hill", "Flores", "Patel", "Chen",
         "Okafor", "Kowalski", "Tanaka", "Ivanova", "Mensah", "Rossi", "Sharma"];

    private static readonly (string Code, string Name, decimal Rate)[] LaborCategories =
    [
        ("PM3", "Program Manager III", 92m), ("PM2", "Project Manager II", 74m), ("SE4", "Systems Engineer IV", 84m),
        ("SE3", "Systems Engineer III", 68m), ("SE2", "Systems Engineer II", 54m), ("SW4", "Software Developer IV", 86m),
        ("SW3", "Software Developer III", 70m), ("SW2", "Software Developer II", 55m), ("AN3", "Analyst III", 58m),
        ("AN2", "Analyst II", 44m), ("AN1", "Analyst I", 34m), ("CY3", "Cyber Engineer III", 78m), ("DA3", "Data Scientist III", 80m),
        ("TW2", "Technical Writer II", 42m), ("AD1", "Administrative Specialist", 30m),
    ];

    private async Task PeopleAsync()
    {
        // 1 corporate root, 4 divisions with 3 departments each, and 3 corporate departments = 20 org units.
        var corp = new OrgUnit { Code = "CORP", Name = "LoadCo Federal Services" };
        _orgUnits.Add(corp);
        string[] divisions = ["Defense Programs", "Civilian Health", "Intelligence Solutions", "Digital Services"];
        var departments = new List<(OrgUnit Unit, int Division)>();
        for (var d = 0; d < divisions.Length; d++)
        {
            var division = new OrgUnit { Code = $"D{d + 1}", Name = divisions[d], ParentId = corp.Id };
            _orgUnits.Add(division);
            foreach (var (code, name) in new[] { ("ENG", "Engineering"), ("ANL", "Analytics"), ("OPS", "Operations") })
            {
                var dept = new OrgUnit { Code = $"D{d + 1}-{code}", Name = $"{divisions[d]} {name}", ParentId = division.Id };
                _orgUnits.Add(dept);
                departments.Add((dept, d));
            }
        }

        var fin = new OrgUnit { Code = "FIN", Name = "Finance and Contracts", ParentId = corp.Id };
        var hr = new OrgUnit { Code = "HR", Name = "Human Resources", ParentId = corp.Id };
        var it = new OrgUnit { Code = "IT", Name = "Corporate IT", ParentId = corp.Id };
        _orgUnits.AddRange([fin, hr, it]);
        await _bulk.AddRangeAsync(_orgUnits);

        foreach (var (code, name, _) in LaborCategories)
        {
            _lcats.Add(new LaborCategory { Code = code, Name = name });
        }

        await _bulk.AddRangeAsync(_lcats);
        LaborCategory Lcat(string code) => _lcats.First(l => l.Code == code);
        decimal RateOf(LaborCategory l) => LaborCategories.First(c => c.Code == l.Code).Rate;

        var used = new HashSet<string>(StringComparer.Ordinal);
        var number = 1000;
        Person NewPerson(OrgUnit unit, LaborCategory lcat, string? email = null, bool indirect = false, Person? supervisor = null)
        {
            string first, last, mail;
            do
            {
                first = Pick(FirstNames);
                last = Pick(LastNames);
                mail = email ?? $"{first}.{last}".ToLowerInvariant() + $"@{Domain}";
                if (email is null && used.Contains(mail))
                {
                    mail = $"{first}.{last}.{number}".ToLowerInvariant() + $"@{Domain}";
                }
            }
            while (used.Contains(mail));

            used.Add(mail);
            // Most people were here before the history starts; about 10% joined during it and 8% have left.
            var joinedLate = _rng.NextDouble() < 0.10;
            var hire = joinedLate ? HistoryStart.AddDays(_rng.Next(30, 365 * options.Years - 60)) : HistoryStart.AddDays(-_rng.Next(30, 3650));
            var person = new Person
            {
                E = new Employee
                {
                    EmployeeNumber = $"E{++number:D5}",
                    Email = mail,
                    DisplayName = $"{first} {last}",
                    OrgUnitId = unit.Id,
                    SupervisorId = supervisor?.E.Id,
                    DefaultLaborCategoryId = lcat.Id,
                    HireDate = hire,
                    IsExempt = RateOf(lcat) >= 50m,
                },
                Lcat = lcat,
                BaseRate = Math.Round(RateOf(lcat) * Between(0.88m, 1.15m), 2),
                Supervisor = supervisor,
                Indirect = indirect,
            };
            _people.Add(person);
            return person;
        }

        var ceo = NewPerson(corp, Lcat("PM3"), "dev@workbench.local", indirect: true);
        ceo.E.HireDate = HistoryStart.AddYears(-12);
        var vps = divisions.Select((_, d) => NewPerson(_orgUnits.First(o => o.Code == $"D{d + 1}"), Lcat("PM3"), indirect: true, supervisor: ceo)).ToList();
        foreach (var vp in vps)
        {
            vp.E.HireDate = HistoryStart.AddYears(-8);
        }

        // Corporate: a controller and finance staff, HR and IT; their managers are among the 45 approving managers.
        var controller = NewPerson(fin, Lcat("PM2"), $"finance@{Domain}", indirect: true, supervisor: ceo);
        var hrManager = NewPerson(hr, Lcat("PM2"), indirect: true, supervisor: ceo);
        var itManager = NewPerson(it, Lcat("SE4"), $"itadmin@{Domain}", indirect: true, supervisor: ceo);
        foreach (var p in new[] { controller, hrManager, itManager })
        {
            p.E.HireDate = HistoryStart.AddYears(-6);
        }

        var managers = new List<Person> { controller, hrManager, itManager };
        var perDivision = (45 - 3) / divisions.Length; // 10 or 11 per division
        for (var d = 0; d < divisions.Length; d++)
        {
            var count = d < (45 - 3) % divisions.Length ? perDivision + 1 : perDivision;
            var depts = departments.Where(x => x.Division == d).Select(x => x.Unit).ToList();
            for (var i = 0; i < count; i++)
            {
                var m = NewPerson(depts[i % depts.Count], Lcat(i % 2 == 0 ? "PM2" : "SE4"), supervisor: vps[d]);
                m.E.HireDate = HistoryStart.AddYears(-4).AddDays(-_rng.Next(0, 900));
                managers.Add(m);
            }
        }

        // Staff: corporate departments ~8 each, the rest across the divisions' managers.
        string[] directLcats = ["PM2", "SE4", "SE3", "SE3", "SE2", "SE2", "SW4", "SW3", "SW3", "SW2", "SW2", "AN3", "AN2", "AN2", "AN1", "CY3", "DA3", "TW2"];
        var staffTotal = options.Employees - _people.Count;
        for (var i = 0; i < 24 && i < staffTotal; i++)
        {
            var (unit, boss, lcat) = (i % 3) switch
            {
                0 => (fin, controller, i < 6 ? "AN3" : "AN2"),
                1 => (hr, hrManager, "AN2"),
                _ => (it, itManager, "SE3"),
            };
            NewPerson(unit, Lcat(lcat), indirect: true, supervisor: boss);
        }

        var divisionManagers = managers.Skip(3).ToList();
        while (_people.Count < options.Employees)
        {
            var boss = divisionManagers[(_people.Count) % divisionManagers.Count];
            var unit = _orgUnits.First(o => o.Id == boss.E.OrgUnitId);
            NewPerson(unit, Lcat(Pick(directLcats)), supervisor: boss);
        }

        // About 8% leave during the history (never a manager, so every team keeps its approver).
        foreach (var p in _people.Where(p => p.Supervisor is not null && !managers.Contains(p) && !vps.Contains(p) && _rng.NextDouble() < 0.08))
        {
            var from = p.E.HireDate > HistoryStart ? p.E.HireDate.AddDays(90) : HistoryStart.AddDays(60);
            var last = LastWeek.AddDays(-120);
            if (from < last)
            {
                p.E.TerminationDate = from.AddDays(_rng.Next(0, last.DayNumber - from.DayNumber));
            }
        }

        // Cost rates: the hire (or history start) rate and a 3% raise every January.
        foreach (var p in _people)
        {
            var rate = p.BaseRate;
            var start = p.E.HireDate > HistoryStart ? p.E.HireDate : HistoryStart;
            p.Rates.Add(new EmployeeCostRate { EmployeeId = p.E.Id, EffectiveFrom = start, HourlyCost = rate });
            for (var year = start.Year + 1; year <= options.Today.Year; year++)
            {
                rate = Math.Round(rate * 1.03m, 2);
                p.Rates.Add(new EmployeeCostRate { EmployeeId = p.E.Id, EffectiveFrom = new DateOnly(year, 1, 1), HourlyCost = rate });
            }
        }

        // Insert managers before their reports (supervisor foreign key).
        foreach (var p in _people.OrderBy(Depth))
        {
            await _bulk.AddAsync(p.E);
        }

        await _bulk.AddRangeAsync(_people.SelectMany(p => p.Rates));

        // Sign-in accounts: everyone; CEO and IT admin are Admins, finance staff Finance, two HR timekeepers, one resource manager.
        var now = DateTime.UtcNow;
        var financeStaff = _people.Where(p => p.E.OrgUnitId == fin.Id).Take(7).ToHashSet();
        var hrStaff = _people.Where(p => p.E.OrgUnitId == hr.Id).Take(2).ToHashSet();
        foreach (var p in _people)
        {
            await _bulk.AddAsync(new AppUser
            {
                Email = p.Email,
                DisplayName = p.E.DisplayName,
                IsAdmin = p == ceo || p == itManager,
                IsFinance = financeStaff.Contains(p),
                IsTimekeeper = hrStaff.Contains(p),
                IsResourceManager = p == vps[0],
                CreatedAtUtc = Utc(p.E.HireDate, 14),
                IsActive = p.E.TerminationDate is null,
                DeactivatedAtUtc = p.E.TerminationDate is { } t ? Utc(t, 22) : null,
                DeactivatedBy = p.E.TerminationDate is null ? null : itManager.Email,
            });
        }

        // Org-unit approvers for the divisions (the "org-unit manager" rule).
        for (var d = 0; d < vps.Count; d++)
        {
            await _bulk.AddAsync(new OrgUnitApprover { OrgUnitId = _orgUnits.First(o => o.Code == $"D{d + 1}").Id, EmployeeId = vps[d].E.Id });
        }

        static int Depth(Person p) => p.Supervisor is null ? 0 : 1 + Depth(p.Supervisor);
    }

    // ---- Contracts, projects, WBS, plan sheets ----

    private static readonly string[] Agencies =
        ["Department of the Army", "Department of the Navy", "Department of the Air Force", "Defense Health Agency", "Department of Veterans Affairs",
         "Department of Homeland Security", "Centers for Medicare & Medicaid Services", "Prime: Northrop Integrations Inc."];

    private static readonly string[] TaskNames = ["Program management", "Requirements and analysis", "Design and development", "Test and evaluation", "Operations and sustainment", "Transition and training"];

    private static readonly string[] SubtaskNames = ["Planning", "Analysis", "Build", "Review", "Integration", "Reporting", "Support"];

    private Workspace? _programs;

    private async Task ContractsAndProjectsAsync()
    {
        for (var i = 0; i < Agencies.Length; i++)
        {
            _customers.Add(new Customer { Code = $"C{i + 1:D2}", Name = Agencies[i], Category = i == 7 ? "Prime contractor" : i < 4 ? "DoD" : "Civilian" });
        }

        await _bulk.AddRangeAsync(_customers);
        foreach (var customer in _customers)
        {
            await _bulk.AddAsync(new CustomerBillingProfile { CustomerId = customer.Id, PaymentTermsDays = 30, StatementEmail = $"ap@{customer.Code.ToLowerInvariant()}.example.gov", UpdatedBy = SystemActor, UpdatedAtUtc = DateTime.UtcNow });
        }

        _programs = new Workspace { Name = "Programs" };
        await _bulk.AddAsync(_programs);

        // 25 contracts: 9 cost-plus, 8 T&M, 4 labor hour, 4 FFP. 20 run the whole history; 5 started or ended inside it.
        ContractType[] types = [.. Enumerable.Repeat(ContractType.CostPlusFixedFee, 9), .. Enumerable.Repeat(ContractType.TimeAndMaterials, 8),
            .. Enumerable.Repeat(ContractType.LaborHour, 4), .. Enumerable.Repeat(ContractType.FirmFixedPrice, 4)];
        var historyEnd = _years[^1].End;
        var projectNumber = 2000;
        for (var i = 0; i < 25; i++)
        {
            var type = types[i];
            var start = i < 20 ? HistoryStart : HistoryStart.AddMonths(6 * (i - 19));
            var end = i < 20 ? historyEnd.AddYears(1) : i % 2 == 0 ? start.AddYears(3).AddDays(-1) : historyEnd.AddYears(2);
            var contract = new Contract
            {
                CustomerId = _customers[i % _customers.Count].Id,
                ContractNumber = $"W91{i % 10}QY-{21 + i / 10:D2}-C-{1000 + i * 7:D4}",
                Name = $"{Agencies[i % Agencies.Length].Split(' ')[^1]} {(type is ContractType.FirmFixedPrice ? "Modernization" : "Engineering Support")} {(char)('A' + i)}",
                Type = type,
                Status = end < LastWeek ? ContractStatus.Closed : ContractStatus.Active,
                IsPrime = i % 8 != 7,
                PeriodStart = start,
                PeriodEnd = end,
            };
            var pricing = type switch
            {
                ContractType.CostPlusFixedFee => ClinPricing.CostPlusFixedFee,
                ContractType.TimeAndMaterials => ClinPricing.TimeAndMaterials,
                ContractType.LaborHour => ClinPricing.LaborHour,
                _ => ClinPricing.FirmFixedPrice,
            };
            var labor = new Clin
            {
                ContractId = contract.Id, Number = "0001", Description = "Labor", Pricing = pricing, PeriodStart = start, PeriodEnd = end,
                FeeRate = pricing == ClinPricing.CostPlusFixedFee ? 0.07m : null, Order = 0,
            };
            var odc = new Clin
            {
                ContractId = contract.Id, Number = "0002", Description = "Travel and other direct costs",
                Pricing = type is ContractType.CostPlusFixedFee ? ClinPricing.CostNoFee : type is ContractType.FirmFixedPrice ? ClinPricing.FirmFixedPrice : ClinPricing.TimeAndMaterials,
                PeriodStart = start, PeriodEnd = end, Order = 1,
            };
            var ctr = new Ctr { C = contract, LaborClin = labor, OdcClin = odc };
            _contracts.Add(ctr);
            await _bulk.AddAsync(contract);
            await _bulk.AddRangeAsync([labor, odc]);
            await _bulk.AddAsync(new ContractBillingSettings
            {
                ContractId = contract.Id, InvoicePrefix = $"INV-{ctr.Prefix}-", PaymentTermsDays = 30, UpdatedBy = SystemActor, UpdatedAtUtc = DateTime.UtcNow,
                FeeWithholdRate = 0m,
            });

            // Bill rates by labor category, escalated 3% a fiscal year.
            foreach (var lcat in _lcats)
            {
                var rate = Math.Round(LaborCategories.First(c => c.Code == lcat.Code).Rate * Between(2.0m, 2.4m), 2);
                foreach (var year in _years)
                {
                    ctr.BillRates[(lcat.Id, year.Year)] = rate;
                    if (type is ContractType.TimeAndMaterials or ContractType.LaborHour)
                    {
                        await _bulk.AddAsync(new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = lcat.Id, EffectiveFrom = year.Start, HourlyBillRate = rate });
                    }

                    rate = Math.Round(rate * 1.03m, 2);
                }
            }

            // 10 contracts carry 3 projects and 15 carry 2: 60 direct projects.
            for (var j = 0; j < (i < 10 ? 3 : 2); j++)
            {
                await NewProjectAsync(ctr, ++projectNumber);
            }
        }

        // Indirect and leave charge codes: no assignment needed, pooled by WbsCostPool.
        var indirect = new (string Code, string Name, ChargeKind Kind, IndirectPool Pool, string[] Leaves)[]
        {
            ("OH", "Overhead", ChargeKind.Indirect, IndirectPool.Overhead, ["Training", "Facilities", "IT support", "Division management", "Recruiting support"]),
            ("GA", "General and administrative", ChargeKind.Indirect, IndirectPool.GeneralAndAdministrative, ["Accounting", "Contracts", "Human resources", "Executive", "Corporate IT"]),
            ("BP", "Bid and proposal", ChargeKind.Indirect, IndirectPool.GeneralAndAdministrative, ["Army recompete", "VA proposal", "DHS proposal", "CMS proposal", "Air Force proposal"]),
            ("FR", "Fringe administration", ChargeKind.Indirect, IndirectPool.Fringe, ["Benefits administration"]),
            ("LV", "Paid leave", ChargeKind.Leave, IndirectPool.Fringe, ["Holiday", "Paid time off", "Sick leave"]),
        };
        foreach (var (code, name, kind, pool, leaves) in indirect)
        {
            var project = new Project { Code = code, Name = name, Status = ProjectStatus.Active, Start = HistoryStart.AddYears(-5) };
            await _bulk.AddAsync(project);
            var root = new WbsElement { ProjectId = project.Id, Code = code, Name = name, Kind = kind, IsChargeable = false, RequiresAssignment = false };
            await _bulk.AddAsync(root);
            await _bulk.AddAsync(new WbsCostPool { WbsElementId = root.Id, Pool = pool, UpdatedBy = SystemActor, UpdatedAtUtc = DateTime.UtcNow });
            for (var k = 0; k < leaves.Length; k++)
            {
                var leaf = new WbsElement
                {
                    ProjectId = project.Id, ParentId = root.Id, Code = $"{code}.{k + 1:D2}", Name = leaves[k], Kind = kind, RequiresAssignment = false, Order = k,
                };
                _indirect[leaf.Code] = leaf;
                _wbs[leaf.Id] = leaf;
                await _bulk.AddAsync(leaf);
            }
        }

        // Project managers: 30 senior people, two projects each; they're Owners of the Programs workspace.
        var pms = _people.Where(p => !p.Indirect && p.Lcat.Code is "PM2" or "SE4" && p.E.TerminationDate is null).Take(30).ToList();
        for (var i = 0; i < _projects.Count; i++)
        {
            var pm = pms[i % pms.Count];
            _projects[i].Manager = pm;
            _projects[i].P.ProjectManagerId = pm.E.Id;
        }

        foreach (var pm in pms)
        {
            await _bulk.AddAsync(new WorkspaceMember { WorkspaceId = _programs.Id, UserEmail = pm.Email, Role = WorkspaceRole.Owner });
        }

        foreach (var p in _people.Where(p => !pms.Contains(p)))
        {
            await _bulk.AddAsync(new WorkspaceMember { WorkspaceId = _programs.Id, UserEmail = p.Email, Role = p.Indirect ? WorkspaceRole.Viewer : WorkspaceRole.Editor });
        }

        AssignStaff();
    }

    private async Task NewProjectAsync(Ctr ctr, int number)
    {
        var code = number.ToString();
        var start = ctr.C.PeriodStart;
        var finish = ctr.C.PeriodEnd;
        var now = Utc(start, 13);
        var sheet = new Sheet { WorkspaceId = _programs!.Id, Name = $"{code} plan", Description = $"Plan for project {code}", SchedulingEnabled = true, CreatedAtUtc = now, UpdatedAtUtc = now };
        var project = new Project
        {
            Code = code, Name = $"{ctr.C.Name} – {(ctr.Projects.Count switch { 0 => "Core", 1 => "Surge", _ => "Sustainment" })}",
            ContractId = ctr.C.Id, Status = ctr.C.Status == ContractStatus.Closed ? ProjectStatus.Closed : ProjectStatus.Active, Start = start, Finish = finish, SheetId = sheet.Id,
        };
        var proj = new Proj { P = project, Contract = ctr };
        ctr.Projects.Add(proj);
        _projects.Add(proj);
        await _bulk.AddAsync(sheet);
        await _bulk.AddAsync(project);

        Column status = new() { SheetId = sheet.Id, Name = "Status", Type = ColumnType.Dropdown, Order = 0, Options = ["Not started", "In progress", "Blocked", "Done"] };
        Column owner = new() { SheetId = sheet.Id, Name = "Owner", Type = ColumnType.Person, Order = 1 };
        Column hours = new() { SheetId = sheet.Id, Name = "Planned hours", Type = ColumnType.Number, Order = 2 };
        Column percent = new() { SheetId = sheet.Id, Name = "% complete", Type = ColumnType.Number, Order = 3 };
        await _bulk.AddRangeAsync([status, owner, hours, percent]);

        // 1 root + 6 tasks + 6 × 7 subtasks = 49 WBS elements, each tied to a plan task.
        var span = Math.Max(30, finish.DayNumber - start.DayNumber);
        var rootItem = new Item { SheetId = sheet.Id, Order = 0, Title = project.Name, Start = start, Finish = finish, CostpointWbsId = code };
        var root = new WbsElement { ProjectId = project.Id, Code = code, Name = project.Name, IsChargeable = false, ItemId = rootItem.Id };
        await _bulk.AddRangeAsync([rootItem, root]);
        _wbs[root.Id] = root;
        for (var t = 0; t < TaskNames.Length; t++)
        {
            var taskStart = start.AddDays(span * t / TaskNames.Length / 2);
            var taskFinish = t is 0 or 4 ? finish : start.AddDays(span * (t + 2) / TaskNames.Length);
            var taskItem = new Item { SheetId = sheet.Id, ParentId = rootItem.Id, Order = t, Title = TaskNames[t], Start = taskStart, Finish = taskFinish };
            var task = new WbsElement { ProjectId = project.Id, ParentId = root.Id, Code = $"{code}.{t + 1:D2}", Name = TaskNames[t], IsChargeable = false, ItemId = taskItem.Id, Order = t };
            taskItem.CostpointWbsId = task.Code;
            await _bulk.AddRangeAsync([taskItem, task]);
            _wbs[task.Id] = task;
            for (var s = 0; s < SubtaskNames.Length; s++)
            {
                var done = taskFinish < options.Today;
                var item = new Item
                {
                    SheetId = sheet.Id, ParentId = taskItem.Id, Order = s, Title = $"{TaskNames[t]}: {SubtaskNames[s]}", Start = taskStart, Finish = taskFinish,
                    Values = new()
                    {
                        [status.Id.ToString()] = done ? "Done" : _rng.NextDouble() < 0.7 ? "In progress" : "Not started",
                        [hours.Id.ToString()] = (_rng.Next(4, 60) * 40).ToString(),
                        [percent.Id.ToString()] = done ? "100" : _rng.Next(0, 95).ToString(),
                    },
                };
                var leaf = new WbsElement
                {
                    ProjectId = project.Id, ParentId = task.Id, Code = $"{code}.{t + 1:D2}.{s + 1:D2}", Name = item.Title, ItemId = item.Id, Order = s,
                    ClinId = t == TaskNames.Length - 1 && s == SubtaskNames.Length - 1 ? ctr.OdcClin.Id : ctr.LaborClin.Id,
                    IsOpen = project.Status != ProjectStatus.Closed,
                };
                item.CostpointWbsId = leaf.Code;
                proj.Leaves.Add(leaf);
                _wbs[leaf.Id] = leaf;
                await _bulk.AddRangeAsync([item, leaf]);
            }
        }
    }

    /// <summary>
    /// Each fiscal year, every direct employee is assigned to 2–4 leaf elements on one or two active projects of their
    /// division (roughly); indirect staff charge their department's overhead or G&amp;A codes.
    /// </summary>
    private void AssignStaff()
    {
        foreach (var p in _people)
        {
            if (p.Indirect)
            {
                p.IndirectCodes = p.E.OrgUnitId == _orgUnits.First(o => o.Code == "FIN").Id ? [_indirect["GA.01"].Id, _indirect["GA.02"].Id, _indirect["BP.01"].Id]
                    : p.E.OrgUnitId == _orgUnits.First(o => o.Code == "HR").Id ? [_indirect["GA.03"].Id, _indirect["FR.01"].Id, _indirect["OH.05"].Id]
                    : p.E.OrgUnitId == _orgUnits.First(o => o.Code == "IT").Id ? [_indirect["GA.05"].Id, _indirect["OH.03"].Id]
                    : [_indirect["GA.04"].Id, _indirect["OH.04"].Id, _indirect["BP.02"].Id];
                continue;
            }

            foreach (var year in _years)
            {
                var active = _projects.Where(x => x.P.Start <= year.End && x.P.Finish >= year.Start && x.P.Status != ProjectStatus.Closed || (x.P.Finish >= year.Start && x.P.Start <= year.End)).ToList();
                var home = active[(int)(((uint)p.E.Id.GetHashCode() + (uint)year.Year * 7) % (uint)active.Count)];
                var codes = new List<Guid>();
                var labor = home.Leaves.Where(l => l.ClinId == home.Contract.LaborClin.Id).ToList();
                for (var k = 0; k < _rng.Next(2, 4); k++)
                {
                    codes.Add(Pick(labor).Id);
                }

                if (_rng.NextDouble() < 0.35)
                {
                    var second = Pick(active);
                    codes.Add(Pick(second.Leaves.Where(l => l.ClinId == second.Contract.LaborClin.Id).ToList()).Id);
                }

                p.Charges[year.Year] = [.. codes.Distinct()];
            }
        }
    }

    /// <summary>The charge assignments the history implies: one per person, element and fiscal year.</summary>
    private async Task AssignmentsAsync()
    {
        foreach (var p in _people)
        {
            foreach (var (fy, codes) in p.Charges)
            {
                var year = _years.First(y => y.Year == fy);
                foreach (var code in codes)
                {
                    await _bulk.AddAsync(new ChargeAssignment { WbsElementId = code, EmployeeId = p.E.Id, LaborCategoryId = p.Lcat.Id, From = year.Start, To = year.End });
                }
            }
        }
    }

    // ---- Tracker sheets ----

    private async Task TrackerSheetsAsync()
    {
        await AssignmentsAsync();
        var divisions = _orgUnits.Where(o => o.ParentId == _orgUnits[0].Id && o.Code.StartsWith('D')).ToList();
        var workspaces = new List<Workspace>();
        foreach (var division in divisions)
        {
            var ws = new Workspace { Name = division.Name };
            workspaces.Add(ws);
            await _bulk.AddAsync(ws);
            var deptIds = _orgUnits.Where(o => o.ParentId == division.Id).Select(o => o.Id).Append(division.Id).ToHashSet();
            foreach (var p in _people.Where(p => p.E.OrgUnitId is { } o && deptIds.Contains(o)))
            {
                await _bulk.AddAsync(new WorkspaceMember { WorkspaceId = ws.Id, UserEmail = p.Email, Role = p.Supervisor?.Supervisor is null ? WorkspaceRole.Owner : WorkspaceRole.Editor });
            }

            foreach (var p in _people.Where(p => p.Indirect && p.E.OrgUnitId == _orgUnits.First(o => o.Code == "FIN").Id))
            {
                await _bulk.AddAsync(new WorkspaceMember { WorkspaceId = ws.Id, UserEmail = p.Email, Role = WorkspaceRole.Viewer });
            }
        }

        // 140 trackers (200 sheets with the 60 plans): 8 of 2,000 rows, 20 of 500, 50 of 100, 62 of 30.
        var sizes = Enumerable.Repeat(2000, 8).Concat(Enumerable.Repeat(500, 20)).Concat(Enumerable.Repeat(100, 50)).Concat(Enumerable.Repeat(30, 62)).ToList();
        string[] kinds = ["Risk register", "Action items", "Deliverables tracker", "Hiring pipeline", "Issue log", "Asset inventory", "Change requests"];
        string[] statuses = ["Open", "In progress", "Blocked", "Closed"];
        for (var i = 0; i < sizes.Count; i++)
        {
            var ws = workspaces[i % workspaces.Count];
            var members = _people.Where(p => !p.Indirect).ToList();
            var created = Utc(HistoryStart.AddDays(_rng.Next(0, 365 * options.Years)), 15);
            var sheet = new Sheet { WorkspaceId = ws.Id, Name = $"{kinds[i % kinds.Length]} {i + 1:D3}", Description = $"{sizes[i]:N0}-row tracker", CreatedAtUtc = created, UpdatedAtUtc = created.AddDays(_rng.Next(1, 300)) };
            await _bulk.AddAsync(sheet);
            var columns = new List<Column>
            {
                new() { SheetId = sheet.Id, Name = "Status", Type = ColumnType.Dropdown, Order = 0, Options = [.. statuses] },
                new() { SheetId = sheet.Id, Name = "Owner", Type = ColumnType.Person, Order = 1 },
                new() { SheetId = sheet.Id, Name = "Due", Type = ColumnType.Date, Order = 2 },
                new() { SheetId = sheet.Id, Name = "Estimate", Type = ColumnType.Currency, Order = 3 },
                new() { SheetId = sheet.Id, Name = "Priority", Type = ColumnType.Dropdown, Order = 4, Options = ["High", "Medium", "Low"] },
                new() { SheetId = sheet.Id, Name = "Notes", Type = ColumnType.Text, Order = 5 },
                new() { SheetId = sheet.Id, Name = "Done", Type = ColumnType.Checkbox, Order = 6 },
                new() { SheetId = sheet.Id, Name = "Weighted", Type = ColumnType.Currency, Order = 7, Formula = "[Estimate] * 1.1" },
            };
            await _bulk.AddRangeAsync(columns);
            Item? parent = null;
            var childOrder = 0;
            var topOrder = 0;
            for (var r = 0; r < sizes[i]; r++)
            {
                // Every 20th row is a section heading with the next rows under it.
                var heading = r % 20 == 0;
                var due = DateOnly.FromDateTime(created).AddDays(_rng.Next(5, 400));
                var item = new Item
                {
                    SheetId = sheet.Id,
                    ParentId = heading ? null : parent?.Id,
                    Order = heading ? topOrder++ : childOrder++,
                    Title = heading ? $"Section {r / 20 + 1}" : $"{kinds[i % kinds.Length].Split(' ')[0]} item {r + 1}",
                    Start = due.AddDays(-14),
                    Finish = due,
                    Values = heading ? [] : new()
                    {
                        [columns[0].Id.ToString()] = Pick(statuses),
                        [columns[1].Id.ToString()] = Pick(members).Email,
                        [columns[2].Id.ToString()] = due.ToString("yyyy-MM-dd"),
                        [columns[3].Id.ToString()] = (_rng.Next(1, 500) * 100).ToString(),
                        [columns[4].Id.ToString()] = Pick(new[] { "High", "Medium", "Low" }),
                        [columns[5].Id.ToString()] = _rng.NextDouble() < 0.3 ? "Follow up with the customer before the next status meeting." : null,
                        [columns[6].Id.ToString()] = _rng.NextDouble() < 0.4 ? "true" : "false",
                    },
                };
                if (heading)
                {
                    parent = item;
                    childOrder = 0;
                }

                await _bulk.AddAsync(item);
            }
        }
    }

    // ---- Budgets ----

    private async Task BudgetsAsync()
    {
        foreach (var proj in _projects)
        {
            var created = Utc(proj.P.Start!.Value, 16);
            var pm = proj.Manager?.Email ?? SystemActor;
            var baseline = new BudgetVersion { ProjectId = proj.P.Id, Name = "Baseline", Kind = BudgetKind.Baseline, IsLocked = true, CreatedAtUtc = created, CreatedBy = pm, LockedAtUtc = created.AddDays(10), LockedBy = pm };
            var current = new BudgetVersion { ProjectId = proj.P.Id, Name = "Current", Kind = BudgetKind.Current, CreatedAtUtc = created.AddDays(11), CreatedBy = pm, SourceVersionId = baseline.Id };
            await _bulk.AddRangeAsync([baseline, current]);
            var years = _years.Where(y => y.End >= proj.P.Start && y.Start <= proj.P.Finish).ToList();
            foreach (var version in new[] { baseline, current })
            {
                var growth = version == current ? 1.08m : 1m;
                foreach (var leaf in proj.Leaves)
                {
                    foreach (var year in years)
                    {
                        // Phased by quarter (the first day of each quarter's first period).
                        for (var q = 0; q < 4; q++)
                        {
                            if (leaf == proj.OdcLeaf)
                            {
                                await _bulk.AddAsync(new BudgetLine { BudgetVersionId = version.Id, WbsElementId = leaf.Id, CostType = BudgetCostType.Odc, OdcType = OdcType.Travel, PeriodStart = year.Start.AddMonths(q * 3), Amount = Math.Round(Between(8_000, 30_000) * growth, 2) });
                                continue;
                            }

                            var lcat = _lcats[(leaf.Order + q) % 10];
                            var hours = Math.Round(Between(80, 400) * growth, 0);
                            var rate = LaborCategories.First(c => c.Code == lcat.Code).Rate;
                            await _bulk.AddAsync(new BudgetLine
                            {
                                BudgetVersionId = version.Id, WbsElementId = leaf.Id, CostType = BudgetCostType.Labor, LaborCategoryId = lcat.Id,
                                PeriodStart = year.Start.AddMonths(q * 3), Hours = hours, PlanningRate = rate, Amount = Math.Round(hours * rate, 2),
                            });
                        }
                    }
                }
            }
        }
    }

    // ---- Vendors ----

    private static readonly (string Code, string Name, string Account, decimal Monthly)[] OverheadVendors =
    [
        ("V-RENT", "Reston Office Park LLC", "6120", 145_000m),
        ("V-ITSV", "Northwind IT Services", "6230", 38_000m),
        ("V-CPA", "Smith & Partners CPAs", "6220", 22_000m),
        ("V-SUPP", "Capitol Office Supply", "6130", 9_500m),
        ("V-BENE", "Federal Benefits Trust", "6030", 0m), // health and retirement, priced from the payroll
    ];

    private static readonly string[] OdcVendors = ["Acme Travel Management", "Prima Hardware Corp", "Delta Subcontracting LLC", "Summit Consultants", "Beacon Test Labs", "Keystone Materials"];

    private async Task VendorsAsync()
    {
        foreach (var (code, name, _, _) in OverheadVendors)
        {
            _vendors.Add(new Vendor { Code = code, Name = name, PaymentTermsDays = 30, CreatedAtUtc = Utc(HistoryStart, 12) });
        }

        for (var i = 0; i < OdcVendors.Length; i++)
        {
            _vendors.Add(new Vendor { Code = $"V-ODC{i + 1}", Name = OdcVendors[i], PaymentTermsDays = 30, CreatedAtUtc = Utc(HistoryStart, 12) });
        }

        await _bulk.AddRangeAsync(_vendors);
    }
}
