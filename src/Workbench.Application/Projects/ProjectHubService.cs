using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Contracts;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Financials;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Projects;
using Workbench.Domain;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;

namespace Workbench.Application.Projects;

/// <summary>
/// The Projects hub. Rows are scoped here (like report rows): Finance and administrators get every project, others the
/// projects they manage or charge to; money only on rows the user may read financials for. The permission decorator
/// guards the single-project calls.
/// </summary>
public sealed class ProjectHubService(
    IWorkbenchDbContextFactory dbFactory,
    ProjectHubAccess hubAccess,
    ICeilingService ceilings,
    TimeProvider clock) : IProjectHubService
{
    private const int DueSoonDays = 14;
    private static readonly string[] DoneStatuses = ["Done", "Complete", "Completed"];

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<ProjectHubListDto> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var viewer = await hubAccess.GetViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var charged = await ProjectHubAccess.ChargedProjectIdsAsync(db, viewer.EmployeeId, cancellationToken);
        var query = db.Set<Project>().AsNoTracking();
        if (await hubAccess.Visibility.VisibleProjectIdsAsync(cancellationToken) is { } visible)
        {
            var visibleIds = visible.ToList();
            query = query.Where(p => visibleIds.Contains(p.Id));
        }

        var projects = (await query.ToListAsync(cancellationToken)).OrderBy(p => p.Code, StringComparer.Ordinal).ToList();
        var context = await LoadContextAsync(db, projects, cancellationToken);

        // The financials behind each money column load a project's whole history: a few projects at a time, each on its own context.
        var loaded = new System.Collections.Concurrent.ConcurrentDictionary<Guid, ProjectFinancialData>();
        await Parallel.ForEachAsync(projects.Where(p => CanReadMoney(viewer, p)), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (project, token) =>
            {
                await using var own = await dbFactory.CreateAsync(token);
                loaded[project.Id] = await FinancialsLoader.LoadProjectAsync(own, project.Id, null, null, Today, token);
            });

        var rows = new List<ProjectHubRowDto>();
        foreach (var project in projects)
        {
            var money = loaded.TryGetValue(project.Id, out var data) ? await MoneyAsync(db, project, context, cancellationToken, data) : null;
            rows.Add(Row(viewer, project, context, charged, money is not null, money));
        }

        return new ProjectHubListDto(viewer.IsFinance, rows);
    }

    public async Task<ProjectHubDto> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var viewer = await hubAccess.GetViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        var charged = await ProjectHubAccess.ChargedProjectIdsAsync(db, viewer.EmployeeId, cancellationToken);
        var context = await LoadContextAsync(db, [project], cancellationToken);
        var readsMoney = CanReadMoney(viewer, project);
        var money = readsMoney ? await MoneyAsync(db, project, context, cancellationToken) : null;
        var seesSheet = project.SheetId is { } sheetId && await hubAccess.Access.GetSheetRoleAsync(sheetId, cancellationToken) is not null;
        var row = Row(viewer, project, context, charged, readsMoney || seesSheet, money);

        var contract = project.ContractId is { } cid ? context.Contracts.GetValueOrDefault(cid) : null;
        var customer = contract is null ? null
            : await db.Set<Customer>().AsNoTracking().Where(c => c.Id == contract.CustomerId).Select(c => c.Name).FirstOrDefaultAsync(cancellationToken);

        var schedule = seesSheet ? Schedule(project.SheetId!.Value, context) : null;
        var tabs = new ProjectHubTabsDto(
            Plan: seesSheet,
            Financials: readsMoney,
            Budgets: readsMoney,
            Revenue: readsMoney,
            EarnedValue: readsMoney,
            Ceilings: readsMoney,
            Costs: true, // Finance, admins and the PM see every ODC; everyone else only their own entries.
            Team: readsMoney,
            Setup: viewer.IsFinance || (viewer.IsProjectManager && viewer.EmployeeId is { } pm && project.ProjectManagerId == pm));

        var alerts = new List<ProjectAlertDto>();
        if (money is not null)
        {
            if (money.Health is ProjectHealth.Over or ProjectHealth.AtRisk)
            {
                alerts.AddRange(money.HealthReasons.Select(r =>
                    new ProjectAlertDto(money.Health == ProjectHealth.Over ? ProjectAlertLevel.Critical : ProjectAlertLevel.Warning, "budget", r, "financials")));
            }

            if (project.ContractId is { } fundedContract && context.Funding.TryGetValue(fundedContract, out var funding))
            {
                alerts.AddRange(funding.Alerts.Select(a => new ProjectAlertDto((ProjectAlertLevel)(int)a.Level, "funding", a.Message, "financials")));
            }

            alerts.AddRange(await CeilingAlertsAsync(project.Id, cancellationToken));
        }

        if (schedule is not null)
        {
            if (schedule.LateTasks > 0)
            {
                alerts.Add(new ProjectAlertDto(ProjectAlertLevel.Warning, "schedule", $"{Plural(schedule.LateTasks, "task is", "tasks are")} past the finish date.", "plan"));
            }

            if (schedule.BlockedTasks > 0)
            {
                alerts.Add(new ProjectAlertDto(ProjectAlertLevel.Info, "schedule", $"{Plural(schedule.BlockedTasks, "task is", "tasks are")} blocked.", "plan"));
            }
        }

        return new ProjectHubDto(row, contract?.Name, customer, tabs, schedule,
            alerts.OrderByDescending(a => a.Level).ToList(), Today);
    }

    public async Task<IReadOnlyList<ProjectTeamMemberDto>> GetTeamAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        var wbs = db.Set<WbsElement>().Where(w => w.ProjectId == projectId).Select(w => w.Id);
        var entries = await (
            from e in db.Set<TimeEntry>().AsNoTracking()
            join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
            where wbs.Contains(e.WbsElementId) && (t.Status == TimesheetStatus.Approved || t.Status == TimesheetStatus.Submitted)
            select new { t.EmployeeId, t.Status, e.Hours, e.WorkDate }).ToListAsync(cancellationToken);
        var assigned = await db.Set<ChargeAssignment>().AsNoTracking().Where(a => wbs.Contains(a.WbsElementId)).Select(a => a.EmployeeId).Distinct().ToListAsync(cancellationToken);
        var weekStart = Today.AddDays(-(((int)Today.DayOfWeek + 6) % 7));
        var allocations = await db.Set<ResourceAllocation>().AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.WeekStart >= weekStart)
            .Select(a => new { a.EmployeeId, a.WeekStart, a.Hours })
            .ToListAsync(cancellationToken);

        var ids = entries.Select(e => e.EmployeeId).Concat(assigned).Concat(allocations.Select(a => a.EmployeeId)).ToHashSet();
        if (project.ProjectManagerId is { } pmId)
        {
            ids.Add(pmId);
        }

        var people = await db.Set<Employee>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(cancellationToken);
        var categoryIds = people.Select(p => p.DefaultLaborCategoryId).OfType<Guid>().ToHashSet();
        var categories = await db.Set<LaborCategory>().AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        return people.Select(p =>
            {
                var mine = entries.Where(e => e.EmployeeId == p.Id).ToList();
                var allocated = allocations.Where(a => a.EmployeeId == p.Id).ToList();
                return new ProjectTeamMemberDto(
                    p.Id, p.DisplayName, p.Email,
                    p.DefaultLaborCategoryId is { } lc ? categories.GetValueOrDefault(lc) : null,
                    p.Id == project.ProjectManagerId,
                    assigned.Contains(p.Id),
                    mine.Where(e => e.Status == TimesheetStatus.Approved).Sum(e => e.Hours),
                    mine.Where(e => e.Status == TimesheetStatus.Submitted).Sum(e => e.Hours),
                    mine.Count == 0 ? null : mine.Max(e => e.WorkDate),
                    allocated.Sum(a => a.Hours),
                    allocated.Count == 0 ? null : allocated.Max(a => a.WeekStart).AddDays(6));
            })
            .OrderByDescending(m => m.IsManager)
            .ThenByDescending(m => m.ApprovedHours + m.PendingHours)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool CanReadMoney(ProjectHubAccess.Viewer viewer, Project project) =>
        viewer.IsFinance || (viewer.EmployeeId is { } me && project.ProjectManagerId == me);

    private ProjectHubRowDto Row(ProjectHubAccess.Viewer viewer, Project project, HubContext context, HashSet<Guid> charged, bool showsOpenItems, ProjectMoneyDto? money)
    {
        var contract = project.ContractId is { } cid ? context.Contracts.GetValueOrDefault(cid) : null;
        int? open = showsOpenItems && project.SheetId is { } sheetId && context.StatusColumns.ContainsKey(sheetId)
            ? Schedule(sheetId, context).OpenTasks
            : null;
        return new ProjectHubRowDto(
            project.Id, project.Code, project.Name,
            project.ProjectManagerId, project.ProjectManagerId is { } pm ? context.Managers.GetValueOrDefault(pm) : null,
            project.ContractId, contract?.ContractNumber,
            (ProjectState)(int)project.Status, project.Start, project.Finish, project.SheetId,
            viewer.EmployeeId is { } me && project.ProjectManagerId == me,
            charged.Contains(project.Id),
            open, money);
    }

    private async Task<ProjectMoneyDto> MoneyAsync(IWorkbenchDbContext db, Project project, HubContext context, CancellationToken ct, ProjectFinancialData? loaded = null)
    {
        var data = loaded ?? await FinancialsLoader.LoadProjectAsync(db, project.Id, null, null, Today, ct);
        var totals = FinancialsCalculator.Compute(data.ToInputs()).Totals;
        ContractFundingDto? funding = null;
        if (data.Contract is { } contract && !context.Funding.TryGetValue(contract.Id, out funding))
        {
            funding = await FinancialsLoader.LoadFundingAsync(db, contract, data.AsOf, data.Calendar, ct);
            context.Funding[contract.Id] = funding;
        }

        var start = project.Start ?? data.Contract?.PeriodStart;
        var finish = project.Finish ?? data.Contract?.PeriodEnd;
        decimal? elapsed = start is { } s && finish is { } f && f > s
            ? Math.Clamp((decimal)(Today.DayNumber - s.DayNumber) / (f.DayNumber - s.DayNumber), 0m, 1m)
            : null;
        var fundingUsed = funding?.PercentOfFundedSpent;
        var (health, reasons) = ProjectHealthRules.Evaluate(totals.BudgetAmount, totals.ActualCost, totals.EacAmount, fundingUsed, elapsed);
        return new ProjectMoneyDto(
            totals.BudgetAmount, totals.ActualCost, totals.EtcAmount, totals.EacAmount,
            totals.PercentSpent,
            totals.BudgetAmount > 0 ? totals.EacAmount / totals.BudgetAmount : null,
            funding?.CeilingValue, funding?.FundedValue, funding?.FundedRemaining,
            funding is { CeilingValue: > 0 } ? funding.FundedValue / funding.CeilingValue : null,
            fundingUsed, elapsed, health, reasons);
    }

    private async Task<IEnumerable<ProjectAlertDto>> CeilingAlertsAsync(Guid projectId, CancellationToken ct)
    {
        ProjectCeilingStatusDto status;
        try
        {
            status = await ceilings.GetProjectStatusAsync(projectId, ct);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WorkbenchNotFoundException)
        {
            return [];
        }

        return status.Ceilings
            .Where(c => c.Ceiling.IsActive)
            .SelectMany(c => c.Measures.Where(m => m.Level != CeilingAlertLevel.Ok).Select(m => new ProjectAlertDto(
                m.Level == CeilingAlertLevel.Warning ? ProjectAlertLevel.Warning : ProjectAlertLevel.Critical,
                "ceiling",
                $"{c.Ceiling.Label}: {m.Metric.ToString().ToLowerInvariant()} at {m.PercentConsumed:0.#}% of the ceiling.",
                "ceilings")))
            .ToList();
    }

    private ProjectScheduleDto Schedule(Guid sheetId, HubContext context)
    {
        var items = context.Items.GetValueOrDefault(sheetId) ?? [];
        var parents = items.Where(i => i.ParentId is not null).Select(i => i.ParentId!.Value).ToHashSet();
        var leaves = items.Where(i => !parents.Contains(i.Id)).ToList();
        Guid? statusColumn = context.StatusColumns.TryGetValue(sheetId, out var columnId) ? columnId : null;
        string? StatusOf(Item item) =>
            statusColumn is { } col && item.Values.TryGetValue(col.ToString(), out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        bool IsDone(Item item) => StatusOf(item) is { } s && DoneStatuses.Contains(s, StringComparer.OrdinalIgnoreCase);

        var today = Today;
        var open = leaves.Where(i => !IsDone(i)).ToList();
        var milestones = open
            .Where(i => i.IsMilestone && i.Finish is not null)
            .OrderBy(i => i.Finish)
            .Take(5)
            .Select(i => new ProjectMilestoneDto(i.Id, i.Title, i.Finish!.Value, i.Finish < today))
            .ToList();
        return new ProjectScheduleDto(
            sheetId,
            leaves.Count,
            leaves.Count - open.Count,
            open.Count,
            open.Count(i => i.Finish < today),
            open.Count(i => string.Equals(StatusOf(i), "Blocked", StringComparison.OrdinalIgnoreCase)),
            open.Count(i => i.Finish >= today && i.Finish <= today.AddDays(DueSoonDays)),
            statusColumn is not null,
            milestones);
    }

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    /// <summary>Lookups shared by the rows: contracts, manager names, plan sheet items and their Status columns.</summary>
    private sealed record HubContext(
        Dictionary<Guid, Contract> Contracts,
        Dictionary<Guid, string> Managers,
        Dictionary<Guid, List<Item>> Items,
        Dictionary<Guid, Guid> StatusColumns,
        Dictionary<Guid, ContractFundingDto> Funding);

    private static async Task<HubContext> LoadContextAsync(IWorkbenchDbContext db, IReadOnlyList<Project> projects, CancellationToken ct)
    {
        var contractIds = projects.Select(p => p.ContractId).OfType<Guid>().ToHashSet();
        var managerIds = projects.Select(p => p.ProjectManagerId).OfType<Guid>().ToHashSet();
        var sheetIds = projects.Select(p => p.SheetId).OfType<Guid>().ToHashSet();
        var contracts = await db.Set<Contract>().AsNoTracking().Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var managers = await db.Set<Employee>().AsNoTracking().Where(e => managerIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);

        // Only the Status column is read from item values, and only when it isn't restricted.
        var statusColumns = (await db.Set<Column>().AsNoTracking()
                .Where(c => sheetIds.Contains(c.SheetId) && c.Type == ColumnType.Dropdown && !c.IsRestricted)
                .ToListAsync(ct))
            .Where(c => string.Equals(c.Name, "Status", StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => c.SheetId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Order).First().Id);
        var items = (await db.Set<Item>().AsNoTracking().Where(i => sheetIds.Contains(i.SheetId)).ToListAsync(ct))
            .GroupBy(i => i.SheetId)
            .ToDictionary(g => g.Key, g => g.ToList());
        return new HubContext(contracts, managers, items, statusColumns, []);
    }
}
