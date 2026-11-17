using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Approvals;
using Workbench.Application.Billing;
using Workbench.Application.Crm;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Resourcing;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain;
using Workbench.Domain.Billing;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using Workbench.Domain.Scheduling;
using Workbench.Domain.Tasks;
using WorkspaceRole = Workbench.Domain.Identity.WorkspaceRole;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// The commercial demo (<c>Demo:Dataset=Commercial</c>, docs/admin-guide/setup.md): Kestrel Bay Consulting, a 9-person
/// professional-services firm. Three clients (a time-and-materials analytics engagement, a monthly agency-style retainer and a
/// fixed-fee redesign), a pipeline of commercial deals moved through the real gates, projects built from the industry starter outlines
/// with their plans and people, four weeks of timesheets, and invoices generated through the real billing services (a T&amp;M invoice
/// part-paid, a retainer month paid, a fixed-fee milestone sent and a draft). Every name is invented; the sign-in people are the same
/// as the government demo (dev@ Admin, pm@ Finance and a project manager, bd@ business development, and so on).
/// </summary>
internal static class CommercialDemoSeed
{
    private const string Admin = "dev@workbench.local";
    private const string Partner = "pm@example.com";
    private const string BdEmail = "bd@example.com";

    /// <summary>Weekly hours a project manager spends on the management tasks of an engagement (the rest of their project hours go on the phase work).</summary>
    private const decimal ManagementHours = 4m;

    private sealed record Person(string Number, string Email, string Name, string Category, decimal Cost, decimal Bill, string? Boss, decimal WeeklyHours = 40m);

    private static readonly Person[] People =
    [
        new("K1001", Partner, "Pat Morgan", "EP", 120m, 285m, null),
        new("K1002", "sam.pm@example.com", "Sam Patel", "EM", 92m, 215m, Partner),
        new("K1003", "dev@workbench.local", "Dana Lee", "SC", 74m, 175m, Partner),
        new("K1004", "analyst@example.com", "Alex Rivera", "CN", 58m, 140m, Partner),
        new("K1005", "viewer@example.com", "Val Chen", "AN", 42m, 105m, Partner, 20m),
        new("K1006", "morgan.ellis@example.com", "Morgan Ellis", "DS", 55m, 130m, Partner),
        new("K1007", BdEmail, "Blake Diaz", "EP", 110m, 285m, Partner),
    ];

    private static readonly (string Code, string Name)[] Categories =
    [
        ("EP", "Engagement Partner"), ("EM", "Engagement Manager"), ("SC", "Senior Consultant"), ("CN", "Consultant"), ("AN", "Analyst"), ("DS", "Designer"),
    ];

    /// <summary>Weekly hours by person: project code (or IND.OH, IND.GA, IND.BP) → hours. Each row totals the person's standard week.</summary>
    private static readonly Dictionary<string, (string Code, decimal Hours)[]> Weeks = new()
    {
        [Partner] = [("6002", 8), ("6003", 12), ("IND.GA", 8), ("IND.BP", 6), ("IND.OH", 6)],
        ["sam.pm@example.com"] = [("6001", 16), ("6003", 16), ("IND.OH", 8)],
        ["dev@workbench.local"] = [("6001", 20), ("6003", 12), ("IND.OH", 8)],
        ["analyst@example.com"] = [("6001", 14), ("6003", 20), ("IND.OH", 6)],
        ["viewer@example.com"] = [("6001", 8), ("6003", 8), ("IND.OH", 4)],
        ["morgan.ellis@example.com"] = [("6002", 34), ("IND.OH", 6)],
        [BdEmail] = [("IND.BP", 24), ("IND.GA", 8), ("IND.OH", 8)],
    };

    private sealed record ProjectSpec(string Code, string Name, string Template, string ContractNumber, string Manager, DateOnly Start, DateOnly Finish, bool Ongoing);

    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, IDbContextFactory<WorkbenchDbContext> contexts, CancellationToken ct)
    {
        if (await db.Set<Employee>().AnyAsync(ct))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var hired = new DateOnly(today.Year - 3, 1, 5);

        // A calendar fiscal year, the chart of accounts and its posting rules (what a new tenant starts with).
        await TenantDefaultsSeed.SeedAsync(services, 1, TimeProvider.System, ct);

        // ---- Organization, labor categories, people ----
        var delivery = new OrgUnit { Code = "DEL", Name = "Delivery" };
        var growth = new OrgUnit { Code = "GRO", Name = "Growth and operations", ParentId = delivery.Id };
        db.AddRange(delivery, growth);
        var categories = Categories.ToDictionary(c => c.Code, c => new LaborCategory { Code = c.Code, Name = c.Name });
        db.AddRange(categories.Values);
        var employees = new Dictionary<string, Employee>();
        Employee? boss = null;
        foreach (var p in People)
        {
            var e = new Employee
            {
                EmployeeNumber = p.Number, Email = p.Email, DisplayName = p.Name, OrgUnitId = p.Email == BdEmail ? growth.Id : delivery.Id,
                DefaultLaborCategoryId = categories[p.Category].Id, HireDate = hired, StandardHoursPerWeek = p.WeeklyHours, IsExempt = p.WeeklyHours >= 40m,
                SupervisorId = boss is not null && p.Boss is not null ? boss.Id : null,
            };
            boss ??= e; // Pat, first in the list, supervises everyone else.
            employees[p.Email] = e;
            db.Add(e);
            db.Add(new EmployeeCostRate { EmployeeId = e.Id, EffectiveFrom = hired, HourlyCost = p.Cost });
        }

        if (!await db.Set<AppUser>().AnyAsync(u => u.Email == BdEmail, ct))
        {
            await RoleStore.AddUserAsync(db, new AppUser { Email = BdEmail, DisplayName = "Blake Diaz", CreatedAtUtc = now }, OrgRoles.BusinessDevelopment, ct);
        }

        // ---- Indirect and leave codes ----
        var indirect = new Project { Code = "IND", Name = "Indirect and leave", Start = hired };
        db.Add(indirect);
        var indRoot = new WbsElement { ProjectId = indirect.Id, Code = "IND", Name = "Indirect", Kind = ChargeKind.Indirect, IsChargeable = false, RequiresAssignment = false };
        var indirectCodes = new Dictionary<string, WbsElement>
        {
            ["IND.OH"] = new() { ProjectId = indirect.Id, ParentId = indRoot.Id, Code = "IND.OH", Name = "Overhead", Kind = ChargeKind.Indirect, RequiresAssignment = false, Order = 0 },
            ["IND.GA"] = new() { ProjectId = indirect.Id, ParentId = indRoot.Id, Code = "IND.GA", Name = "General and administrative", Kind = ChargeKind.Indirect, RequiresAssignment = false, Order = 1 },
            ["IND.BP"] = new() { ProjectId = indirect.Id, ParentId = indRoot.Id, Code = "IND.BP", Name = "Business development and proposals", Kind = ChargeKind.Indirect, RequiresAssignment = false, Order = 2 },
        };
        var leaveRoot = new WbsElement { ProjectId = indirect.Id, Code = "LV", Name = "Leave", Kind = ChargeKind.Leave, IsChargeable = false, RequiresAssignment = false, Order = 1 };
        db.AddRange(indRoot, leaveRoot, new WbsElement { ProjectId = indirect.Id, ParentId = leaveRoot.Id, Code = "LV.PTO", Name = "Paid time off", Kind = ChargeKind.Leave, RequiresAssignment = false, Order = 0 },
            new WbsElement { ProjectId = indirect.Id, ParentId = leaveRoot.Id, Code = "LV.HOL", Name = "Holiday", Kind = ChargeKind.Leave, RequiresAssignment = false, Order = 1 });
        db.AddRange(indirectCodes.Values);

        // ---- Clients, contracts, bill rates ----
        var cobalt = new Customer { Code = "COBALT", Name = "Cobalt Harbor Logistics", Category = "Commercial" };
        var sundial = new Customer { Code = "SUNDIAL", Name = "Sundial Media Group", Category = "Commercial" };
        var marlow = new Customer { Code = "MARLOW", Name = "Marlow Mutual Insurance", Category = "Commercial" };
        db.AddRange(cobalt, sundial, marlow);
        var cobaltStart = thisMonday.AddDays(-28); // The engagements start with the four weeks of timesheets.
        var cobaltEnd = cobaltStart.AddMonths(5);
        var marlowEnd = cobaltStart.AddMonths(4);
        Contract NewContract(Customer c, string number, string name, ContractType type, DateOnly start, DateOnly end, decimal value, decimal funded) =>
            new() { CustomerId = c.Id, ContractNumber = number, Name = name, Type = type, Status = ContractStatus.Active, PeriodStart = start, PeriodEnd = end, CeilingValue = value, FundedValue = funded };
        var cobaltContract = NewContract(cobalt, "CH-2026-014", "Warehouse analytics rollout (time and materials)", ContractType.TimeAndMaterials, cobaltStart, cobaltEnd, 320_000m, 320_000m);
        var sundialContract = NewContract(sundial, "SM-RET-2026", "Brand and campaign retainer (monthly)", ContractType.FirmFixedPrice, cobaltStart, cobaltStart.AddYears(1).AddDays(-1), 264_000m, 264_000m);
        var marlowContract = NewContract(marlow, "MM-2026-031", "Claims workflow redesign (fixed fee)", ContractType.FirmFixedPrice, cobaltStart, marlowEnd, 180_000m, 180_000m);
        var contracts = new Dictionary<string, Contract> { [cobaltContract.ContractNumber] = cobaltContract, [sundialContract.ContractNumber] = sundialContract, [marlowContract.ContractNumber] = marlowContract };
        db.AddRange(contracts.Values);
        var clins = new Dictionary<string, Clin>
        {
            [cobaltContract.ContractNumber] = new() { ContractId = cobaltContract.Id, Number = "0001", Description = "Consulting services", Pricing = ClinPricing.TimeAndMaterials, FundedValue = 320_000m, CeilingValue = 320_000m, PeriodStart = cobaltContract.PeriodStart, PeriodEnd = cobaltContract.PeriodEnd },
            [sundialContract.ContractNumber] = new() { ContractId = sundialContract.Id, Number = "0001", Description = "Monthly retainer", Pricing = ClinPricing.FirmFixedPrice, FundedValue = 264_000m, CeilingValue = 264_000m, PeriodStart = sundialContract.PeriodStart, PeriodEnd = sundialContract.PeriodEnd },
            [marlowContract.ContractNumber] = new() { ContractId = marlowContract.Id, Number = "0001", Description = "Fixed-fee engagement", Pricing = ClinPricing.FirmFixedPrice, FundedValue = 180_000m, CeilingValue = 180_000m, PeriodStart = marlowContract.PeriodStart, PeriodEnd = marlowContract.PeriodEnd },
        };
        db.AddRange(clins.Values);
        foreach (var contract in contracts.Values)
        {
            db.AddRange(People.Where(p => p.Email != BdEmail).Select(p => new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = categories[p.Category].Id, EffectiveFrom = contract.PeriodStart, HourlyBillRate = p.Bill }));
        }

        db.AddRange(
            new BillingMilestone { ContractId = sundialContract.Id, ClinId = clins[sundialContract.ContractNumber].Id, Description = "Retainer month 1, billed in advance", Amount = 22_000m, PlannedDate = cobaltStart, CompletedDate = cobaltStart, CompletedBy = Partner, Order = 0 },
            new BillingMilestone { ContractId = sundialContract.Id, ClinId = clins[sundialContract.ContractNumber].Id, Description = "Retainer month 2, billed in advance", Amount = 22_000m, PlannedDate = cobaltStart.AddMonths(1), Order = 1 },
            new BillingMilestone { ContractId = marlowContract.Id, ClinId = clins[marlowContract.ContractNumber].Id, Description = "Discovery complete and findings accepted", Amount = 54_000m, PlannedDate = cobaltStart.AddDays(18), CompletedDate = cobaltStart.AddDays(18), CompletedBy = Partner, Order = 0 },
            new BillingMilestone { ContractId = marlowContract.Id, ClinId = clins[marlowContract.ContractNumber].Id, Description = "Design signed off", Amount = 72_000m, PlannedDate = cobaltStart.AddMonths(2), Order = 1 },
            new BillingMilestone { ContractId = marlowContract.Id, ClinId = clins[marlowContract.ContractNumber].Id, Description = "Delivery and handover", Amount = 54_000m, PlannedDate = marlowEnd, Order = 2 });

        // ---- Projects: the starter outlines, with plan sheets, WBS, people and an approved plan ----
        var workspace = new Workspace { Name = "Client delivery" };
        db.Add(workspace);
        db.AddRange(
            new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = Partner, Role = WorkspaceRole.Owner },
            new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = "sam.pm@example.com", Role = WorkspaceRole.Editor },
            new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = "analyst@example.com", Role = WorkspaceRole.Editor },
            new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = "viewer@example.com", Role = WorkspaceRole.Viewer });
        var templates = Application.ProjectAccounting.ProjectStartService.BuiltInTemplates.ToDictionary(t => t.Key);
        ProjectSpec[] specs =
        [
            new("6001", "Cobalt Harbor warehouse analytics rollout", "consulting", cobaltContract.ContractNumber, "sam.pm@example.com", cobaltStart, cobaltContract.PeriodEnd, false),
            new("6002", "Sundial brand and campaign retainer", "retainer", sundialContract.ContractNumber, Partner, sundialContract.PeriodStart, sundialContract.PeriodEnd, true),
            new("6003", "Marlow claims workflow redesign", "consulting", marlowContract.ContractNumber, Partner, marlowContract.PeriodStart, marlowContract.PeriodEnd, false),
        ];
        var leaves = new Dictionary<string, List<(WbsElement Wbs, Item Item, bool Management)>>();
        var projects = new Dictionary<string, Project>();
        foreach (var spec in specs)
        {
            var contract = contracts[spec.ContractNumber];
            var clin = clins[spec.ContractNumber];
            var template = templates[spec.Template];
            var sheet = new Sheet
            {
                Workspace = workspace, Name = $"{spec.Code} {spec.Name}", Description = $"Plan for {spec.Name}. Its tasks mirror the WBS.", SchedulingEnabled = true,
                CreatedAtUtc = now, UpdatedAtUtc = now,
            };
            var status = new Column { Name = "Status", Type = ColumnType.Dropdown, Order = 0, Options = ["Not started", "In progress", "Blocked", "Done"] };
            sheet.Columns.Add(status);
            var project = new Project
            {
                Code = spec.Code, Name = spec.Name, ContractId = contract.Id, ProjectManagerId = employees[spec.Manager].Id, Status = ProjectStatus.Active, Start = spec.Start, Finish = spec.Finish, SheetId = sheet.Id,
            };
            db.AddRange(sheet, project);
            projects[spec.Code] = project;

            // Outline → rows. A group with children is a summary; the first group (management) runs the whole project.
            var parsed = template.Outline.Select(l => (Name: l.Trim(), Level: (l.Length - l.TrimStart(' ').Length) / 2)).ToList();
            var leafIndexes = parsed.Select((l, i) => (l, i)).Where(x => x.i == parsed.Count - 1 || parsed[x.i + 1].Level <= x.l.Level).Select(x => x.i).ToList();
            var stackWbs = new List<WbsElement>();
            var stackItem = new List<Item>();
            var siblings = new Dictionary<string, List<string>>();
            var list = leaves[spec.Code] = [];
            var rows = new List<(WbsElement Wbs, Item Item, int Level, int Index, int Group)>();
            var group = -1;
            var days = Workdays(spec.Start, spec.Finish);
            var phaseLeaves = leafIndexes.Where(i => spec.Ongoing || !InFirstGroup(parsed, i)).ToList();
            var slice = Math.Max(1, days.Count / Math.Max(1, phaseLeaves.Count));
            var next = 0;
            for (var i = 0; i < parsed.Count; i++)
            {
                var (name, level) = parsed[i];
                while (stackWbs.Count > level)
                {
                    stackWbs.RemoveAt(stackWbs.Count - 1);
                    stackItem.RemoveAt(stackItem.Count - 1);
                }

                if (level == 0)
                {
                    group++;
                }

                var parentWbs = stackWbs.LastOrDefault();
                var prefix = parentWbs?.Code ?? spec.Code;
                var used = siblings.TryGetValue(prefix, out var u) ? u : siblings[prefix] = [];
                var code = WbsCodes.Next(prefix, used);
                used.Add(code);
                var isLeaf = leafIndexes.Contains(i);
                var management = InFirstGroup(parsed, i) && !spec.Ongoing;
                DateOnly from = spec.Start, to = spec.Finish;
                if (isLeaf && !(management && !spec.Ongoing))
                {
                    var at = Math.Min(next * slice, days.Count - 1);
                    var end = Math.Min(at + slice - 1, days.Count - 1);
                    (from, to) = spec.Ongoing ? (spec.Start, spec.Finish) : (days[at], days[end]);
                    next++;
                }

                var item = new Item
                {
                    Title = name, Order = rows.Count(r => r.Level == level && r.Group == group && level > 0) + (level == 0 ? group : 0), ParentId = stackItem.LastOrDefault()?.Id,
                    Start = from, Finish = to, CostpointWbsId = code, Values = new() { [status.Id.ToString()] = to < today ? "Done" : from <= today ? "In progress" : "Not started" },
                };
                var element = new WbsElement
                {
                    ProjectId = project.Id, ParentId = parentWbs?.Id, Code = code, Name = name, ItemId = item.Id, ClinId = clin.Id, IsChargeable = isLeaf, Order = item.Order,
                };
                sheet.Items.Add(item);
                db.Add(element);
                rows.Add((element, item, level, i, group));
                stackWbs.Add(element);
                stackItem.Add(item);
                if (isLeaf)
                {
                    list.Add((element, item, management));
                }
            }

            // Summaries span their children.
            foreach (var row in rows.Where(r => !leafIndexes.Contains(r.Index)))
            {
                var kids = rows.Where(r => r.Item.ParentId == row.Item.Id).ToList();
                row.Item.Start = kids.Min(k => k.Item.Start);
                row.Item.Finish = kids.Max(k => k.Item.Finish);
            }

            // Each phase's task follows the one before it.
            var phased = list.Where(l => !l.Management).ToList();
            for (var i = 1; i < phased.Count && !spec.Ongoing; i++)
            {
                db.Add(new Dependency { SheetId = sheet.Id, PredecessorId = phased[i - 1].Item.Id, SuccessorId = phased[i].Item.Id, Type = DependencyType.FinishToStart });
            }

            var used6 = template.LaborCategories ?? [];
            foreach (var cat in categories.Values.Where(c => used6.Contains(c.Name) || (spec.Ongoing && c.Code == "DS")))
            {
                db.Add(new ProjectLaborCategory { ProjectId = project.Id, LaborCategoryId = cat.Id, CreatedBy = Partner, CreatedAtUtc = now });
            }

            // Who may charge it.
            foreach (var person in Weeks.Where(w => w.Value.Any(x => x.Code == spec.Code)).Select(w => employees[w.Key]))
            {
                db.AddRange(list.Select(l => new ChargeAssignment { WbsElementId = l.Wbs.Id, EmployeeId = person.Id }));
            }

            db.Add(new ProjectPlan
            {
                ProjectId = project.Id, CreatedBy = Partner, CreatedAtUtc = now.AddDays(-60), Status = ProjectPlanStatus.Approved, SubmittedBy = spec.Manager, SubmittedAtUtc = now.AddDays(-58),
                SubmitNote = "Plan and budget for the engagement.", ReviewedBy = Admin, ReviewedAtUtc = now.AddDays(-57), Approvals = 1,
            });
        }

        // ---- Staffing and the approved budget: each person's weekly hours on each task they work, over that task's dates ----
        var holidays = (await db.Set<Domain.Leave.Holiday>().AsNoTracking().Select(h => h.Date).ToListAsync(ct)).ToHashSet();
        foreach (var spec in specs)
        {
            var project = projects[spec.Code];
            var budget = new List<BudgetLine>();
            foreach (var (email, hours) in Weeks.Where(w => w.Value.Any(x => x.Code == spec.Code)).Select(w => (w.Key, w.Value.First(x => x.Code == spec.Code).Hours)))
            {
                var person = employees[email];
                var meta = People.First(p => p.Email == email);
                var category = categories[meta.Category];
                var isManager = email == spec.Manager && !spec.Ongoing;
                var mine = new List<(WbsElement Wbs, Item Item, decimal Weekly)>();
                var phase = leaves[spec.Code].Where(l => !l.Management).ToList();
                if (spec.Ongoing)
                {
                    mine.AddRange(phase.Select(l => (l.Wbs, l.Item, hours / phase.Count)));
                }
                else
                {
                    mine.AddRange(phase.Select(l => (l.Wbs, l.Item, hours - (isManager ? ManagementHours : 0m))));
                    if (isManager)
                    {
                        var management = leaves[spec.Code].Where(l => l.Management).ToList();
                        mine.AddRange(management.Select(l => (l.Wbs, l.Item, ManagementHours / management.Count)));
                    }
                }

                foreach (var (wbs, item, weekly) in mine)
                {
                    var span = item.Finish!.Value.DayNumber - item.Start!.Value.DayNumber + 1;
                    var total = Math.Round(weekly * span / 7m * 1.1m, 0); // A little contingency, so the engagements read as on track.
                    if (total <= 0)
                    {
                        continue;
                    }

                    budget.Add(new BudgetLine { WbsElementId = wbs.Id, CostType = BudgetCostType.Labor, LaborCategoryId = category.Id, Hours = total, PlanningRate = meta.Cost, Amount = Math.Round(total * meta.Cost, 2) });

                    // Staffing covers what is still ahead of today.
                    var from = item.Start.Value > today ? item.Start.Value : today;
                    var to = item.Finish.Value;
                    bool Working(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(d) && person.IsActiveOn(d);
                    if (to < from || !Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays).Any(Working))
                    {
                        continue;
                    }

                    var ahead = Math.Round(weekly * (to.DayNumber - from.DayNumber + 1) / 7m, 0);
                    var assignment = new StaffingAssignment
                    {
                        EmployeeId = person.Id, ProjectId = project.Id, WbsElementId = wbs.Id, LaborCategoryId = category.Id, From = from, To = to, TotalHours = ahead,
                        FollowsTask = !spec.Ongoing, CreatedBy = spec.Manager, CreatedAtUtc = now,
                    };
                    db.Add(assignment);
                    foreach (var week in AssignmentSpread.Spread(ahead, from, to, Working).Where(w => w.Hours > 0))
                    {
                        db.Add(new ResourceAllocation
                        {
                            EmployeeId = person.Id, ProjectId = project.Id, WbsElementId = wbs.Id, LaborCategoryId = category.Id, WeekStart = week.WeekStart, Hours = week.Hours,
                            Source = AllocationSource.Manual, AssignmentId = assignment.Id, CreatedBy = spec.Manager, CreatedAtUtc = now,
                        });
                    }

                    if (!await db.Set<ItemAssignee>().AnyAsync(a => a.ItemId == item.Id && a.Email == email, ct) && !db.ChangeTracker.Entries<ItemAssignee>().Any(a => a.Entity.ItemId == item.Id && a.Entity.Email == email))
                    {
                        db.Add(new ItemAssignee { SheetId = project.SheetId!.Value, ItemId = item.Id, Email = email, AssignedBy = "staffing", AssignedAtUtc = now });
                    }
                }
            }

            foreach (var kind in new[] { BudgetKind.Baseline, BudgetKind.Current })
            {
                var version = new BudgetVersion
                {
                    ProjectId = project.Id, Kind = kind, Name = kind == BudgetKind.Baseline ? "Approved plan" : "Working plan", CreatedBy = Partner, CreatedAtUtc = now.AddDays(-20),
                    IsLocked = kind == BudgetKind.Baseline, LockedAtUtc = kind == BudgetKind.Baseline ? now.AddDays(-20) : null, LockedBy = kind == BudgetKind.Baseline ? Admin : null,
                    Description = kind == BudgetKind.Baseline ? "The engagement plan, approved." : "The approved plan plus approved changes.",
                };
                db.Add(version);
                db.AddRange(budget.Select(b => new BudgetLine
                {
                    BudgetVersionId = version.Id, WbsElementId = b.WbsElementId, CostType = b.CostType, LaborCategoryId = b.LaborCategoryId, Hours = b.Hours, PlanningRate = b.PlanningRate, Amount = b.Amount,
                }));
            }
        }

        // ---- Four weeks of timesheets: the first three weeks approved, the last approved except Alex's and Val's, still to submit ----
        var pendingEmails = new HashSet<string> { "analyst@example.com", "viewer@example.com" };
        for (var week = 4; week >= 1; week--)
        {
            var monday = thisMonday.AddDays(-7 * week);
            foreach (var person in People)
            {
                var e = employees[person.Email];
                var pending = week == 1 && pendingEmails.Contains(person.Email);
                var sheet = new Timesheet
                {
                    EmployeeId = e.Id, PeriodStart = monday, PeriodEnd = monday.AddDays(6), CreatedAtUtc = now, UpdatedAtUtc = now,
                    Status = pending ? TimesheetStatus.Draft : TimesheetStatus.Approved,
                };
                if (!pending)
                {
                    var at = monday.AddDays(7).ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);
                    sheet.CertifiedAtUtc = at.AddDays(-3);
                    sheet.CertificationText = Workbench.Contracts.Time.TimesheetCertification.Text;
                    sheet.SubmittedAtUtc = at.AddDays(-3);
                    sheet.FirstSubmittedAtUtc = at.AddDays(-3);
                    sheet.ApprovedById = person.Boss is null ? null : employees[Partner].Id;
                    sheet.ApprovedAtUtc = at;
                    sheet.ReviewedBy = person.Boss ?? Admin;
                }

                db.Add(sheet);
                var rowIndex = 0;
                foreach (var (code, hours) in Weeks[person.Email])
                {
                    var isIndirect = indirectCodes.TryGetValue(code, out var ind);
                    var spec = isIndirect ? null : specs.First(sp => sp.Code == code);
                    var lines = new List<(WbsElement Wbs, decimal Hours)>();
                    if (isIndirect)
                    {
                        lines.Add((ind!, hours));
                    }
                    else if (person.Email == spec!.Manager && !spec.Ongoing)
                    {
                        var management = leaves[code].Where(l => l.Management).Select(l => l.Wbs).ToList();
                        lines.Add((LeafFor(leaves[code], spec, monday, rowIndex), hours - ManagementHours));
                        lines.AddRange(management.Select(m => (m, ManagementHours / management.Count)));
                    }
                    else
                    {
                        lines.Add((LeafFor(leaves[code], spec, monday, rowIndex), hours));
                    }

                    rowIndex++;
                    foreach (var (wbs, lineHours) in lines)
                    {
                        // Hours spread over the five days; the remainder goes on the first days.
                        var perDay = (int)lineHours / 5;
                        var extra = (int)lineHours % 5;
                        for (var d = 0; d < 5; d++)
                        {
                            var h = perDay + (d < extra ? 1 : 0);
                            if (h == 0)
                            {
                                continue;
                            }

                            db.Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = wbs.Id, LaborCategoryId = categories[person.Category].Id, WorkDate = monday.AddDays(d), Hours = h, EnteredAtUtc = now, UpdatedAtUtc = now });
                        }
                    }
                }

                if (pending)
                {
                    for (var d = 0; d < 5; d++)
                    {
                        db.Add(new TimeDayRecord { EmployeeId = e.Id, TimesheetId = sheet.Id, WorkDate = monday.AddDays(d), FirstEnteredAtUtc = now });
                    }
                }
            }
        }

        await db.SaveChangesAsync(ct);

        await SeedPipelineAsync(db, services, projects, contracts, ct);
        await SeedInvoicesAsync(db, contexts, contracts, thisMonday, ct);
    }

    private static bool InFirstGroup(List<(string Name, int Level)> outline, int index)
    {
        var group = -1;
        for (var i = 0; i <= index; i++)
        {
            if (outline[i].Level == 0)
            {
                group++;
            }
        }

        return group == 0;
    }

    private static List<DateOnly> Workdays(DateOnly from, DateOnly to)
    {
        var days = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days.Add(d);
            }
        }

        return days;
    }

    /// <summary>The task a person is on in a week: the phase task whose dates cover it (a retainer rotates through its deliverables).</summary>
    private static WbsElement LeafFor(List<(WbsElement Wbs, Item Item, bool Management)> leaves, ProjectSpec spec, DateOnly monday, int salt)
    {
        if (spec.Ongoing)
        {
            return leaves[(int)((monday.DayNumber / 7 + salt) % leaves.Count)].Wbs;
        }

        var phase = leaves.Where(l => !l.Management).ToList();
        return (phase.FirstOrDefault(l => l.Item.Start <= monday.AddDays(4) && l.Item.Finish >= monday) is { Wbs: not null } hit ? hit : phase[^1]).Wbs;
    }

    // ---- CRM: accounts, contacts and a pipeline, entered through the real services ----

    private static async Task SeedPipelineAsync(WorkbenchDbContext db, IServiceProvider services, Dictionary<string, Project> projects,
        Dictionary<string, Contract> contracts, CancellationToken ct)
    {
        var user = new SeedUser { Email = BdEmail };
        var kit = new Kit(services, user);
        var today = DateOnly.FromDateTime(DateTime.Now);

        async Task<Guid> Account(string name, CrmAccountKind kind, string website, string? notes = null) =>
            (await kit.Accounts.CreateAccountAsync(new SaveCrmAccountRequest(name, kind, Website: website, Notes: notes), ct)).Id;
        var cobalt = await Account("Cobalt Harbor Logistics", CrmAccountKind.Other, "cobaltharbor-logistics.example", "Regional freight and warehousing. Client since August 2026.");
        var sundial = await Account("Sundial Media Group", CrmAccountKind.Other, "sundialmedia.example", "Agency-style retainer: brand and campaign work, billed monthly.");
        var marlow = await Account("Marlow Mutual Insurance", CrmAccountKind.Other, "marlowmutual.example", "Regional insurer; claims modernization.");
        var lakeshore = await Account("Lakeshore Outfitters", CrmAccountKind.Other, "lakeshore-outfitters.example", "Outdoor retail, 40 stores.");
        var pinnacle = await Account("Pinnacle Dental Group", CrmAccountKind.Other, "pinnacledental.example", "Eleven clinics; wants a booking platform.");
        var fernhill = await Account("Fernhill Energy", CrmAccountKind.Other, "fernhill-energy.example", "Utility with a smart-meter data backlog.");
        var wrenfield = await Account("Wrenfield Retail", CrmAccountKind.Other, "wrenfield-retail.example");
        var copperline = await Account("Copperline Software", CrmAccountKind.Partner, "copperline.example", "Implementation partner; refers platform work.");
        var hollis = await Account("Hollis & Vance Advisory", CrmAccountKind.Competitor, "hollisvance.example", "Larger advisory firm; usually the incumbent on insurance work.");

        // The clients we bill are customers too.
        user.Email = Admin;
        foreach (var (account, code) in new[] { (cobalt, "COBALT"), (sundial, "SUNDIAL"), (marlow, "MARLOW") })
        {
            var customer = await db.Set<Customer>().AsNoTracking().FirstAsync(c => c.Code == code, ct);
            await kit.Accounts.LinkCustomerAsync(account, customer.Id, ct);
        }

        user.Email = BdEmail;
        async Task<Guid> Contact(Guid account, string first, string last, ContactRole role, string title, string email) =>
            (await kit.Accounts.CreateContactAsync(new SaveCrmContactRequest(account, first, last, role, title, email), ct)).Id;
        var cobaltCo = await Contact(cobalt, "Nadia", "Petrov", ContactRole.Executive, "VP Operations", "nadia.petrov@cobaltharbor-logistics.example");
        await Contact(cobalt, "Marcus", "Webb", ContactRole.ContractingSpecialist, "IT Procurement Manager", "marcus.webb@cobaltharbor-logistics.example");
        var sundialCo = await Contact(sundial, "Rosa", "Alvarez", ContactRole.Executive, "Creative Director", "rosa.alvarez@sundialmedia.example");
        await Contact(marlow, "Priya", "Raman", ContactRole.Executive, "Chief Technology Officer", "priya.raman@marlowmutual.example");
        await Contact(marlow, "Daniel", "Kruger", ContactRole.ProgramManager, "Director of Claims", "daniel.kruger@marlowmutual.example");
        await Contact(lakeshore, "Hannah", "Lopez", ContactRole.Executive, "Head of E-commerce", "hannah.lopez@lakeshore-outfitters.example");
        await Contact(pinnacle, "Ben", "Tanaka", ContactRole.Executive, "Chief Operating Officer", "ben.tanaka@pinnacledental.example");
        await Contact(fernhill, "Elena", "Marsh", ContactRole.TechnicalLead, "Head of Data Engineering", "elena.marsh@fernhill-energy.example");
        await Contact(copperline, "Jordan", "Price", ContactRole.BusinessDevelopment, "Partner Lead", "jordan.price@copperline.example");

        async Task<OpportunityDto> Deal(string name, string description, Guid account, decimal value, int months, ContractKind type, OpportunityKind kind, int pwin, DateOnly close, Guid? incumbent = null) =>
            await kit.Opportunities.CreateAsync(new SaveOpportunityRequest(name, description, null, account, ContractType: type, Kind: kind, EstimatedValue: value, EstimatedAwardDate: close,
                PeriodOfPerformanceMonths: months, ProposalDueOn: close.AddDays(-21), IncumbentAccountId: incumbent, Pwin: pwin, Pgo: 100), ct);

        var omni = await Deal("Lakeshore omnichannel analytics strategy", "Where stores and web channels overlap; recommendations and a 12-month roadmap.", lakeshore, 145_000m, 4, ContractKind.FirmFixedPrice, OpportunityKind.New, 60, today.AddDays(24));
        var booking = await Deal("Pinnacle patient booking platform", "Implementation of an online booking platform across eleven clinics.", pinnacle, 310_000m, 7, ContractKind.TimeAndMaterials, OpportunityKind.New, 35, today.AddDays(70));
        var meter = await Deal("Fernhill meter data platform discovery", "Eight-week discovery for the smart-meter data backlog.", fernhill, 85_000m, 2, ContractKind.FirmFixedPrice, OpportunityKind.New, 25, today.AddDays(45));
        var pricing = await Deal("Wrenfield pricing optimization pilot", "A pilot on two categories.", wrenfield, 60_000m, 3, ContractKind.FirmFixedPrice, OpportunityKind.New, 10, today.AddDays(100));
        var phase2 = await Deal("Cobalt Harbor carrier integration (phase 2)", "Extend the analytics platform to carrier feeds.", cobalt, 120_000m, 3, ContractKind.TimeAndMaterials, OpportunityKind.Recompete, 55, today.AddDays(35));
        var renewal = await Deal("Sundial retainer renewal and scope increase", "Add paid social to the monthly retainer from October.", sundial, 288_000m, 12, ContractKind.FirmFixedPrice, OpportunityKind.Recompete, 85, today.AddDays(20));
        var wonCobalt = await Deal("Cobalt Harbor warehouse analytics rollout", "Warehouse analytics from discovery to handover.", cobalt, 320_000m, 7, ContractKind.TimeAndMaterials, OpportunityKind.New, 60, today.AddDays(-40));
        var wonSundial = await Deal("Sundial brand and campaign retainer", "A 12-month monthly retainer.", sundial, 264_000m, 12, ContractKind.FirmFixedPrice, OpportunityKind.New, 60, today.AddDays(-32));
        var wonMarlow = await Deal("Marlow claims workflow redesign", "Fixed-fee redesign of the claims workflow.", marlow, 180_000m, 4, ContractKind.FirmFixedPrice, OpportunityKind.New, 50, today.AddDays(-30), hollis);
        var lost = await Deal("Fernhill trading dashboard", "A dashboard for the trading desk.", fernhill, 95_000m, 3, ContractKind.FirmFixedPrice, OpportunityKind.New, 40, today.AddDays(-50));
        var passed = await Deal("Pinnacle website refresh", "A marketing site rebuild.", pinnacle, 40_000m, 2, ContractKind.FirmFixedPrice, OpportunityKind.New, 5, today.AddDays(-30));

        var gates = await kit.Gates.ListGatesAsync(cancellationToken: ct);
        async Task PassAsync(OpportunityDto o, OpportunityStage stage)
        {
            var gate = gates.First(g => g.ToStage == stage);
            user.Email = BdEmail;
            var review = await kit.Gates.SubmitAsync(o.Id, new SubmitGateReviewRequest(gate.Id, GateRecommendation.Go, gate.Criteria.Select(c => new GateScoreInput(c.Id, 4)).ToList(), $"{gate.Name} for {o.Number}"), ct);
            if (review.Status == GateReviewStatus.Pending)
            {
                user.Email = Admin;
                await kit.Gates.ApproveAsync(review.Id, new DecideGateRequest("Go."), ct);
                user.Email = BdEmail;
            }
        }

        async Task ToAsync(OpportunityDto o, OpportunityStage target)
        {
            foreach (var stage in OpportunityStages.Open.Where(s => s > OpportunityStage.Identified && s <= target))
            {
                if (gates.Any(g => g.ToStage == stage))
                {
                    await PassAsync(o, stage);
                }
                else
                {
                    await kit.Opportunities.ChangeStageAsync(o.Id, new ChangeStageRequest(stage), ct);
                }
            }
        }

        await ToAsync(meter, OpportunityStage.Qualified);
        await ToAsync(phase2, OpportunityStage.Qualified);
        await ToAsync(booking, OpportunityStage.Capture);
        await ToAsync(omni, OpportunityStage.Proposal);
        await ToAsync(renewal, OpportunityStage.Submitted);
        foreach (var (won, reason, notes, when, value) in new[]
                 {
                     (wonCobalt, OutcomeReason.CustomerRelationship, "Chosen after the paid assessment; COO sponsor.", today.AddDays(-35), 320_000m),
                     (wonSundial, OutcomeReason.PastPerformance, "Referenced our retail campaign work.", today.AddDays(-30), 264_000m),
                     (wonMarlow, OutcomeReason.Technical, "Strongest workflow redesign approach; beat the incumbent advisory firm.", today.AddDays(-28), 180_000m),
                 })
        {
            await ToAsync(won, OpportunityStage.Submitted);
            await kit.Opportunities.RecordOutcomeAsync(won.Id, new RecordOutcomeRequest(OpportunityStage.Won, reason, notes, when, value), ct);
        }

        await ToAsync(lost, OpportunityStage.Submitted);
        await kit.Opportunities.RecordOutcomeAsync(lost.Id, new RecordOutcomeRequest(OpportunityStage.Lost, OutcomeReason.Price, "Debrief: a packaged product was a quarter of our price.", today.AddDays(-50), 70_000m, "Packaged software vendor"), ct);
        await kit.Opportunities.RecordOutcomeAsync(passed.Id, new RecordOutcomeRequest(OpportunityStage.NoBid, OutcomeReason.NotAligned, "Web design isn't what we sell."), ct);

        await kit.Accounts.LogActivityAsync(new SaveCrmActivityRequest(CrmActivityKind.Meeting, "Strategy workshop at Lakeshore HQ", "Agreed the roadmap structure; wants a fixed fee.", lakeshore, null, omni.Id, today.AddDays(-9)), ct);
        await kit.Accounts.LogActivityAsync(new SaveCrmActivityRequest(CrmActivityKind.Call, "Retainer renewal terms", null, sundial, sundialCo, renewal.Id, today.AddDays(-4)), ct);
        await kit.Accounts.LogActivityAsync(new SaveCrmActivityRequest(CrmActivityKind.Task, "Send the phase 2 scope to Nadia", null, cobalt, cobaltCo, phase2.Id, DueOn: today.AddDays(2), AssignedTo: BdEmail), ct);
        await kit.Accounts.LogActivityAsync(new SaveCrmActivityRequest(CrmActivityKind.Task, "Follow up with Elena on discovery dates", null, fernhill, null, meter.Id, DueOn: today.AddDays(-1), AssignedTo: BdEmail), ct);

        // The three wins already have their contracts and projects: link them, so no handoff waits.
        foreach (var (opp, number, code, customerCode) in new[]
                 {
                     (wonCobalt.Id, "CH-2026-014", "6001", "COBALT"), (wonSundial.Id, "SM-RET-2026", "6002", "SUNDIAL"), (wonMarlow.Id, "MM-2026-031", "6003", "MARLOW"),
                 })
        {
            var o = await db.Set<Domain.Crm.Opportunity>().FirstAsync(x => x.Id == opp, ct);
            o.ContractId = contracts[number].Id;
            o.ProjectId = projects[code].Id;
            o.CustomerId = (await db.Set<Customer>().AsNoTracking().FirstAsync(c => c.Code == customerCode, ct)).Id;
        }

        await db.SaveChangesAsync(ct);
    }

    // ---- Invoices, through the real billing services ----

    private static async Task SeedInvoicesAsync(WorkbenchDbContext db, IDbContextFactory<WorkbenchDbContext> contexts, Dictionary<string, Contract> contracts,
        DateOnly thisMonday, CancellationToken ct)
    {
        foreach (var customerCode in new[] { "COBALT", "SUNDIAL", "MARLOW" })
        {
            var customer = await db.Set<Customer>().AsNoTracking().FirstAsync(c => c.Code == customerCode, ct);
            db.Add(new CustomerBillingProfile { CustomerId = customer.Id, PaymentTermsDays = customerCode == "MARLOW" ? 45 : 30, UpdatedBy = Partner, UpdatedAtUtc = DateTime.UtcNow });
        }

        foreach (var contract in contracts.Values)
        {
            db.Add(new ContractBillingSettings { ContractId = contract.Id, InvoicePrefix = InvoiceService.DefaultPrefix(contract.ContractNumber), UpdatedBy = Partner, UpdatedAtUtc = DateTime.UtcNow });
        }

        await db.SaveChangesAsync(ct);

        var clock = TimeProvider.System;
        var factory = new WorkbenchDbContextFactory(contexts);
        var audit = new AuditRecorder(new SeedUser { Email = Partner }, clock);
        var ledger = new BillingLedger(new GeneralLedger(audit, clock));
        var generator = new InvoiceGenerator(new BillingCostSource([]));
        var invoices = new InvoiceService(factory, generator, ledger, audit, clock, new InvoiceDocuments(Options.Create(new BillingOptions())), new UnbilledAccruals(generator, ledger, audit));
        var payments = new PaymentService(factory, ledger, audit, clock);
        var today = DateOnly.FromDateTime(DateTime.Now);

        async Task<InvoiceDto> SentAsync(Contract contract, InvoiceKind kind, DateOnly from, DateOnly to, DateOnly date, string notes)
        {
            var draft = await invoices.GenerateDraftAsync(new GenerateInvoiceRequest(contract.Id, kind, from, to, date, Notes: notes), ct);
            await invoices.ApproveAsync(draft.Summary.Id, new ApproveInvoiceRequest(), ct);
            return await invoices.MarkSentAsync(draft.Summary.Id, ct);
        }

        // Cobalt Harbor: the first three of the four weeks, sent and half paid.
        var cobalt = contracts["CH-2026-014"];
        var billed = await SentAsync(cobalt, InvoiceKind.TimeAndMaterials, thisMonday.AddDays(-28), thisMonday.AddDays(-8), thisMonday.AddDays(-7), "Time and materials, weeks 1–3 of the month.");
        var half = Math.Round(billed.Summary.Total / 2, 2, MidpointRounding.AwayFromZero);
        await payments.RecordAsync(new RecordPaymentRequest(cobalt.CustomerId, Min(today, thisMonday.AddDays(-2)), half, CashReceiptMethod.Eft,
            [new PaymentApplicationInput(billed.Summary.Id, half)], Reference: "EFT 88104", Notes: "Part payment."), ct);

        // Sundial: the first month, billed in advance and paid in full.
        var sundial = contracts["SM-RET-2026"];
        var retainer = await SentAsync(sundial, InvoiceKind.FixedPriceMilestone, thisMonday.AddDays(-28), thisMonday.AddDays(-28), thisMonday.AddDays(-28), "Retainer month 1, billed in advance.");
        await payments.RecordAsync(new RecordPaymentRequest(sundial.CustomerId, Min(today, thisMonday.AddDays(-14)), retainer.Summary.Total, CashReceiptMethod.Check,
            [new PaymentApplicationInput(retainer.Summary.Id, retainer.Summary.Total)], Reference: "Check 20417", Notes: "Paid in full."), ct);

        // Marlow: the discovery milestone, sent and not yet due.
        await SentAsync(contracts["MM-2026-031"], InvoiceKind.FixedPriceMilestone, thisMonday.AddDays(-10), thisMonday.AddDays(-10), thisMonday.AddDays(-7), "Discovery milestone.");

        // Cobalt Harbor: last week's time, a draft for review.
        await invoices.GenerateDraftAsync(new GenerateInvoiceRequest(cobalt.Id, InvoiceKind.TimeAndMaterials, thisMonday.AddDays(-7), thisMonday.AddDays(-1), Min(today, thisMonday),
            Notes: "Time and materials, last week (draft)."), ct);
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private sealed class Kit
    {
        public Kit(IServiceProvider services, SeedUser user)
        {
            var factory = (IWorkbenchDbContextFactory)services.GetService(typeof(IWorkbenchDbContextFactory))!;
            var clock = TimeProvider.System;
            var audit = new AuditRecorder(user, clock);
            var access = new CrmAccess(new AccessControl(factory, user), factory);
            var notifications = new NoNotifications();
            Accounts = new CrmAccountService(factory, access, notifications, clock);
            Opportunities = new OpportunityService(factory, access, audit, notifications, clock);
            Gates = new OpportunityGateService(factory, access, audit, notifications, clock, new ApprovalEngine(clock));
        }

        public CrmAccountService Accounts { get; }
        public OpportunityService Opportunities { get; }
        public OpportunityGateService Gates { get; }
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email { get; set; } = "";
    }

    private sealed class NoNotifications : INotificationPublisher
    {
        public Task<bool> PublishAsync(string recipientEmail, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> PublishAsync(IEnumerable<string?> recipientEmails, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
