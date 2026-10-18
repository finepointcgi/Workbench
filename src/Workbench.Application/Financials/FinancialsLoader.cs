using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Financials;

/// <summary>Everything the calculators need for one project, read straight from the entities.</summary>
internal sealed record ProjectFinancialData(
    Project Project,
    Contract? Contract,
    IReadOnlyList<WbsNode> Wbs,
    BudgetVersion? Budget,
    BudgetVersion? Forecast,
    IReadOnlyList<BudgetAmount> BudgetLines,
    IReadOnlyList<BudgetAmount>? ForecastLines,
    IReadOnlyList<CostedLabor> Labor,
    IReadOnlyList<OdcActual> Odcs,
    IReadOnlyList<PeriodInfo> Calendar,
    DateOnly AsOf,
    DateOnly RemainingFrom,
    IReadOnlyList<string> Notes)
{
    public bool ShowsBillableValue => Contract?.Type is ContractType.TimeAndMaterials or ContractType.LaborHour;

    public FinancialInputs ToInputs() => new(Wbs, BudgetLines, Labor, Odcs, ForecastLines, RemainingFrom);
}

internal static class FinancialsLoader
{
    public static async Task<ProjectFinancialData> LoadProjectAsync(
        IWorkbenchDbContext db, Guid projectId, Guid? budgetVersionId, Guid? forecastVersionId, DateOnly asOf, CancellationToken ct)
    {
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        var contract = project.ContractId is { } cid
            ? await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct)
            : null;
        var notes = new List<string>();

        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToListAsync(ct);
        var nodes = wbs.Select(w => new WbsNode(w.Id, w.ParentId, w.Code, w.Name, w.Order)).ToList();

        var versions = await db.Set<BudgetVersion>().AsNoTracking().Where(v => v.ProjectId == projectId).ToListAsync(ct);
        BudgetVersion? budget;
        if (budgetVersionId is { } bid)
        {
            budget = versions.FirstOrDefault(v => v.Id == bid) ?? throw WorkbenchNotFoundException.For("Budget version", bid);
        }
        else
        {
            budget = versions.FirstOrDefault(v => v.Kind == BudgetKind.Current) ?? versions.FirstOrDefault(v => v.Kind == BudgetKind.Baseline);
            if (budget is null)
            {
                notes.Add("This project has no budget yet; create one on the Budgets page.");
            }
            else if (budget.Kind == BudgetKind.Baseline)
            {
                notes.Add("There's no Current budget, so the Baseline is shown.");
            }
        }

        BudgetVersion? forecast = null;
        if (forecastVersionId is { } fid)
        {
            forecast = versions.FirstOrDefault(v => v.Id == fid) ?? throw WorkbenchNotFoundException.For("Budget version", fid);
            if (forecast.Kind != BudgetKind.Forecast)
            {
                throw new ArgumentException($"'{forecast.Name}' isn't a Forecast version.");
            }
        }

        var lineVersionIds = new[] { budget?.Id, forecast?.Id }.OfType<Guid>().ToList();
        var lines = await db.Set<BudgetLine>().AsNoTracking().Where(l => lineVersionIds.Contains(l.BudgetVersionId)).ToListAsync(ct);
        List<BudgetAmount> LinesOf(BudgetVersion? v) => v is null
            ? []
            : lines.Where(l => l.BudgetVersionId == v.Id).Select(l => new BudgetAmount(l.WbsElementId, l.PeriodStart, l.Hours, l.Amount)).ToList();

        var calendar = await LoadCalendarAsync(db, ct);
        var (labor, odcs) = await LoadActualsAsync(db, [projectId], contract, notes, ct);
        var remainingFrom = calendar.FirstOrDefault(p => p.Start <= asOf && asOf <= p.End)?.Start ?? asOf;

        return new ProjectFinancialData(project, contract, nodes, budget, forecast, LinesOf(budget), forecast is null ? null : LinesOf(forecast),
            labor, odcs, calendar, asOf, remainingFrom, notes);
    }

    public static async Task<IReadOnlyList<PeriodInfo>> LoadCalendarAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var periods = await db.Set<FiscalPeriod>().AsNoTracking().ToListAsync(ct);
        return periods.OrderBy(p => p.Start).Select(p => new PeriodInfo(p.Id, p.Name, p.Start, p.End)).ToList();
    }

    /// <summary>
    /// Approved time (timesheet status Approved) priced at cost and at the contract's bill rates, and approved,
    /// allowable ODCs, for the given projects.
    /// </summary>
    public static async Task<(IReadOnlyList<CostedLabor> Labor, IReadOnlyList<OdcActual> Odcs)> LoadActualsAsync(
        IWorkbenchDbContext db, IReadOnlyCollection<Guid> projectIds, Contract? contract, List<string> notes, CancellationToken ct)
    {
        var wbsIds = await db.Set<WbsElement>().AsNoTracking().Where(w => projectIds.Contains(w.ProjectId)).Select(w => w.Id).ToListAsync(ct);
        var labor = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                where t.Status == TimesheetStatus.Approved && wbsIds.Contains(e.WbsElementId)
                select new LaborActual(e.WbsElementId, t.EmployeeId, e.LaborCategoryId, e.WorkDate, e.Hours))
            .ToListAsync(ct);

        var employeeIds = labor.Select(l => l.EmployeeId).Distinct().ToList();
        var costRates = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => employeeIds.Contains(r.EmployeeId)).ToListAsync(ct))
        {
            costRates.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
        }

        EffectiveRateTable<Guid>? billRates = null;
        if (contract is not null)
        {
            billRates = new EffectiveRateTable<Guid>();
            foreach (var r in await db.Set<ContractLaborRate>().AsNoTracking().Where(r => r.ContractId == contract.Id).ToListAsync(ct))
            {
                billRates.Add(r.LaborCategoryId, r.EffectiveFrom, r.HourlyBillRate);
            }
        }

        var costed = LaborCosting.Cost(labor, costRates, billRates, out var missing);
        if (missing > 0)
        {
            notes.Add($"{missing:N1} approved hours have no cost rate on their work date and are costed at $0.");
        }

        var odcs = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => projectIds.Contains(o.ProjectId) && o.Status == OdcStatus.Approved && !o.IsUnallowable)
            .Select(o => new OdcActual(o.WbsElementId, o.IncurredOn, o.Amount))
            .ToListAsync(ct);
        return (costed, odcs);
    }

    public static async Task<ContractFundingDto> LoadFundingAsync(IWorkbenchDbContext db, Contract contract, DateOnly asOf, IReadOnlyList<PeriodInfo>? calendar, CancellationToken ct)
    {
        var projectIds = await db.Set<Project>().AsNoTracking().Where(p => p.ContractId == contract.Id).Select(p => p.Id).ToListAsync(ct);
        var (labor, odcs) = await LoadActualsAsync(db, projectIds, contract, [], ct);
        var billable = contract.Type is ContractType.TimeAndMaterials or ContractType.LaborHour;
        var spend = labor.Select(l => new SpendEntry(l.WorkDate, billable ? l.BillableValue : l.Cost))
            .Concat(odcs.Select(o => new SpendEntry(o.IncurredOn, o.Amount)))
            .ToList();
        var terms = new ContractTerms(contract.Id, contract.ContractNumber, contract.Name, (ContractKind)(int)contract.Type, contract.PeriodEnd,
            contract.CeilingValue, contract.FundedValue);
        return FundingForecaster.Forecast(terms, projectIds.Count, spend, calendar ?? await LoadCalendarAsync(db, ct), asOf);
    }
}
