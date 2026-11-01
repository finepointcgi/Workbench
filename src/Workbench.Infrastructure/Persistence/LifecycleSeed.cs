using Microsoft.EntityFrameworkCore;
using Workbench.Application.Resourcing;
using Workbench.Domain;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using Workbench.Domain.Scheduling;
using Workbench.Domain.Tasks;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for a project's life, one project at each stage so any stage can be shown without walking there
/// (docs/training/lifecycle-demo.md): 5101 has just been set up (no funding, no labor categories, no manager), 5102 is funded
/// with categories and Sam as manager and waits for Sam's plan, 5103's plan is with Finance, and 5104's plan is approved
/// (baseline and working budget) so the project is Active. Each is on its own T&amp;M contract, so one's funding doesn't
/// stand in for another's. Runs once, after the other seeds.
/// </summary>
internal static class LifecycleSeed
{
    private const string Actor = "pm@example.com";
    private const string Sam = "sam.pm@example.com";

    private sealed record Stage(string Code, string Title, decimal Funded, bool Categories, bool Manager, int Tasks, bool People, PlanStatusSeed Plan);

    private enum PlanStatusSeed { None, Draft, Submitted, Approved }

    private static readonly Stage[] Stages =
    [
        new("5101", "Lifecycle 1: set up", 0m, false, false, 0, false, PlanStatusSeed.None),
        new("5102", "Lifecycle 2: planning", 150_000m, true, true, 3, false, PlanStatusSeed.Draft),
        new("5103", "Lifecycle 3: awaiting approval", 250_000m, true, true, 3, true, PlanStatusSeed.Submitted),
        new("5104", "Lifecycle 4: executing", 250_000m, true, true, 3, true, PlanStatusSeed.Approved),
    ];

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<Project>().AnyAsync(p => p.Code == "5101", cancellationToken)
            || await db.Set<Customer>().FirstOrDefaultAsync(cancellationToken) is not { } customer)
        {
            return;
        }

        var employees = await db.Set<Employee>().ToDictionaryAsync(e => e.Email, cancellationToken);
        var categories = await db.Set<LaborCategory>().ToDictionaryAsync(c => c.Code, cancellationToken);
        if (!employees.TryGetValue(Sam, out var sam) || !employees.TryGetValue("viewer@example.com", out var val) || !categories.TryGetValue("BA2", out var ba2)
            || !categories.TryGetValue("SE2", out var se2) || !categories.TryGetValue("PM3", out var pm3) || !categories.TryGetValue("ADM1", out var adm1))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = Weekday(today);
        var holidays = (await db.Set<Domain.Leave.Holiday>().AsNoTracking().Select(h => h.Date).ToListAsync(cancellationToken)).ToHashSet();
        var rates = (await db.Set<EmployeeCostRate>().AsNoTracking().ToListAsync(cancellationToken))
            .GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First().HourlyCost);

        var workspace = new Workspace { Name = "Lifecycle demo" };
        db.Add(workspace);
        db.AddRange(
            new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = Actor, Role = WorkspaceRole.Owner },
            new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = Sam, Role = WorkspaceRole.Editor });

        foreach (var stage in Stages)
        {
            var contract = new Contract
            {
                CustomerId = customer.Id, ContractNumber = $"N00178-27-C-{stage.Code}", Name = stage.Title, Type = ContractType.TimeAndMaterials,
                Status = ContractStatus.Active, PeriodStart = start.AddDays(-14), PeriodEnd = start.AddYears(1), CeilingValue = 600_000m, FundedValue = stage.Funded,
            };
            var clin = new Clin
            {
                ContractId = contract.Id, Number = "0001", Description = "Labor", Pricing = ClinPricing.TimeAndMaterials, FundedValue = stage.Funded, CeilingValue = 600_000m,
                PeriodStart = contract.PeriodStart, PeriodEnd = contract.PeriodEnd, Order = 0,
            };
            db.AddRange(contract, clin);
            db.AddRange(
                new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = ba2.Id, EffectiveFrom = contract.PeriodStart, HourlyBillRate = 120m },
                new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = se2.Id, EffectiveFrom = contract.PeriodStart, HourlyBillRate = 135m },
                new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = pm3.Id, EffectiveFrom = contract.PeriodStart, HourlyBillRate = 150m },
                new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = adm1.Id, EffectiveFrom = contract.PeriodStart, HourlyBillRate = 65m });

            // The plan sheet, as "start from a contract" makes it: the CLIN as the top row, and tasks under it.
            var sheet = new Sheet
            {
                Workspace = workspace, Name = $"{stage.Code} {stage.Title}", Description = $"Plan for project {stage.Code}. Its tasks mirror the WBS.",
                SchedulingEnabled = true, CreatedAtUtc = now, UpdatedAtUtc = now,
            };
            var status = new Column { Name = "Status", Type = ColumnType.Dropdown, Order = 0, Options = ["Not started", "In progress", "Blocked", "Done"] };
            sheet.Columns.Add(status);
            Item Row(string title, int order, Guid? parent, DateOnly from, DateOnly to, string wbs) => new()
            {
                Title = title, Order = order, ParentId = parent, Start = from, Finish = to, CostpointWbsId = wbs, Values = new() { [status.Id.ToString()] = "Not started" },
            };

            var rootRow = Row("CLIN 0001 Labor", 0, null, start, start, $"{stage.Code}.01");
            sheet.Items.Add(rootRow);
            var project = new Project
            {
                Code = stage.Code, Name = stage.Title, ContractId = contract.Id, Status = stage.Plan == PlanStatusSeed.Approved ? ProjectStatus.Active : ProjectStatus.Planning,
                ProjectManagerId = stage.Manager ? sam.Id : null, Start = start, Finish = contract.PeriodEnd, SheetId = sheet.Id,
            };
            var root = new WbsElement { ProjectId = project.Id, Code = $"{stage.Code}.01", Name = "CLIN 0001 Labor", IsChargeable = false, ItemId = rootRow.Id, Order = 0, ClinId = clin.Id };
            db.AddRange(sheet, project, root);

            var tasks = new List<(Item Item, WbsElement Wbs)>();
            (string Name, int From, int Days)[] shape = [("Kickoff and requirements", 0, 10), ("Documentation and training", 10, 10), ("Delivery and handover", 20, 30)];
            foreach (var (name, from, days) in shape.Take(stage.Tasks))
            {
                var first = Weekday(start.AddDays(from));
                var row = Row(name, tasks.Count, rootRow.Id, first, Weekday(first.AddDays(days - 1)), $"{stage.Code}.01.{tasks.Count + 1:00}");
                var element = new WbsElement { ProjectId = project.Id, ParentId = root.Id, Code = row.CostpointWbsId!, Name = name, ItemId = row.Id, Order = tasks.Count, ClinId = clin.Id };
                sheet.Items.Add(row);
                db.Add(element);
                tasks.Add((row, element));
            }

            // Each task follows the one before it (finish to start), so a slip flows through the plan.
            for (var i = 1; i < tasks.Count; i++)
            {
                db.Add(new Dependency { SheetId = sheet.Id, PredecessorId = tasks[i - 1].Item.Id, SuccessorId = tasks[i].Item.Id, Type = DependencyType.FinishToStart });
            }

            rootRow.Finish = tasks.Count > 0 ? tasks.Max(t => t.Item.Finish) : start;
            if (stage.Categories)
            {
                db.AddRange(new[] { pm3, adm1, ba2 }.Select(c => new ProjectLaborCategory { ProjectId = project.Id, LaborCategoryId = c.Id, CreatedBy = Actor, CreatedAtUtc = now }));
            }

            // Who works what, and for how long: every task has someone, and the hours are small enough to sit beside the sample's other
            // projects, so a plan awaiting approval has nothing to warn about. Sam and Val have room; Alex's and Dana's weeks are full on 1001.
            var lines = new List<(WbsElement Wbs, LaborCategory Category, Employee Person, decimal Hours)>();
            if (stage.People)
            {
                lines.Add((tasks[0].Wbs, pm3, sam, 16m));
                lines.Add((tasks[1].Wbs, adm1, val, 8m));
                lines.Add((tasks[2].Wbs, pm3, sam, 16m));
                foreach (var (wbs, category, person, hours) in lines)
                {
                    var task = tasks.Single(t => t.Wbs.Id == wbs.Id).Item;
                    var from = task.Start > today ? task.Start!.Value : today;
                    var to = task.Finish!.Value;
                    bool Working(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(d) && person.IsActiveOn(d);
                    if (to < from || !Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays).Any(Working))
                    {
                        continue;
                    }

                    var assignment = new StaffingAssignment
                    {
                        EmployeeId = person.Id, ProjectId = project.Id, WbsElementId = wbs.Id, LaborCategoryId = category.Id, From = from, To = to,
                        TotalHours = hours, FollowsTask = true, CreatedBy = Sam, CreatedAtUtc = now,
                    };
                    db.Add(assignment);
                    foreach (var week in AssignmentSpread.Spread(hours, from, to, Working).Where(w => w.Hours > 0))
                    {
                        db.Add(new ResourceAllocation
                        {
                            EmployeeId = person.Id, ProjectId = project.Id, WbsElementId = wbs.Id, LaborCategoryId = category.Id, WeekStart = week.WeekStart,
                            Hours = week.Hours, Source = AllocationSource.Manual, AssignmentId = assignment.Id, CreatedBy = Sam, CreatedAtUtc = now,
                        });
                    }

                    db.Add(new ItemAssignee { SheetId = sheet.Id, ItemId = task.Id, Email = person.Email.ToLowerInvariant(), AssignedBy = "staffing", AssignedAtUtc = now });
                }
            }

            if (stage.Plan != PlanStatusSeed.None)
            {
                db.Add(new ProjectPlan
                {
                    ProjectId = project.Id, CreatedBy = Actor, CreatedAtUtc = now.AddDays(-3),
                    Status = stage.Plan switch { PlanStatusSeed.Draft => ProjectPlanStatus.Draft, PlanStatusSeed.Submitted => ProjectPlanStatus.Submitted, _ => ProjectPlanStatus.Approved },
                    SubmittedBy = stage.Plan >= PlanStatusSeed.Submitted ? Sam : null, SubmittedAtUtc = stage.Plan >= PlanStatusSeed.Submitted ? now.AddDays(-2) : null,
                    SubmitNote = stage.Plan >= PlanStatusSeed.Submitted ? "Ready for your review." : null,
                    ReviewedBy = stage.Plan == PlanStatusSeed.Approved ? Actor : null, ReviewedAtUtc = stage.Plan == PlanStatusSeed.Approved ? now.AddDays(-1) : null,
                    Approvals = stage.Plan == PlanStatusSeed.Approved ? 1 : 0,
                });
            }

            if (stage.Plan == PlanStatusSeed.Approved)
            {
                // The approved plan is the locked baseline and the working budget.
                foreach (var kind in new[] { BudgetKind.Baseline, BudgetKind.Current })
                {
                    var version = new BudgetVersion
                    {
                        ProjectId = project.Id, Kind = kind, Name = kind == BudgetKind.Baseline ? "Approved plan" : "Working plan", CreatedBy = Actor, CreatedAtUtc = now.AddDays(-1),
                        IsLocked = kind == BudgetKind.Baseline, LockedAtUtc = kind == BudgetKind.Baseline ? now.AddDays(-1) : null, LockedBy = kind == BudgetKind.Baseline ? Actor : null,
                        Description = kind == BudgetKind.Baseline ? "The project manager's plan, approved." : "The approved plan, with changes Finance has approved since the baseline.",
                    };
                    db.Add(version);
                    foreach (var (wbs, category, person, hours) in lines)
                    {
                        var rate = rates.GetValueOrDefault(person.Id);
                        db.Add(new BudgetLine
                        {
                            BudgetVersionId = version.Id, WbsElementId = wbs.Id, CostType = BudgetCostType.Labor, LaborCategoryId = category.Id, Hours = hours,
                            PlanningRate = rate, Amount = Math.Round(hours * rate, 2),
                        });
                    }
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static DateOnly Weekday(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Saturday => date.AddDays(2),
        DayOfWeek.Sunday => date.AddDays(1),
        _ => date,
    };
}
