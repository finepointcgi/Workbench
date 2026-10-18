using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Contracts.Financials;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Resourcing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;

namespace Workbench.Application.Resourcing;

/// <summary>
/// Project financials with an allocation-based ETC: hours allocated from this week on, priced at each person's cost rate
/// effective in that week, plus the remaining ODC budget per WBS element. Budget and actuals are exactly those of
/// <see cref="IProjectFinancialsService"/>; only the ETC (and so EAC and VAC) differs.
/// </summary>
public sealed class AllocationForecastService(IWorkbenchDbContextFactory dbFactory, TimeProvider clock) : IAllocationForecastService
{
    public const string ForecastName = "Allocation-based ETC";

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<ProjectFinancialSummaryDto> GetProjectSummaryAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (data, result) = await ComputeAsync(db, projectId, budgetVersionId, cancellationToken);
        var funding = data.Contract is null ? null : await FinancialsLoader.LoadFundingAsync(db, data.Contract, data.AsOf, data.Calendar, cancellationToken);
        return new ProjectFinancialSummaryDto(
            data.Project.Id, data.Project.Code, data.Project.Name,
            data.Contract?.Id, data.Contract?.ContractNumber, data.Contract is null ? null : (ContractKind)(int)data.Contract.Type,
            data.Budget?.Id, data.Budget?.Name, null, ForecastName,
            data.ShowsBillableValue, data.AsOf, result.Totals, funding, data.Notes);
    }

    public async Task<IReadOnlyList<WbsFinancialRowDto>> GetWbsFinancialsAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await ComputeAsync(db, projectId, budgetVersionId, cancellationToken)).Result.Rows;
    }

    private async Task<(ProjectFinancialData Data, FinancialsResult Result)> ComputeAsync(IWorkbenchDbContext db, Guid projectId, Guid? budgetVersionId, CancellationToken ct)
    {
        var data = await FinancialsLoader.LoadProjectAsync(db, projectId, budgetVersionId, null, Today, ct);
        var thisWeek = CapacityCalculator.WeekStart(Today);
        var allocations = await db.Set<ResourceAllocation>().AsNoTracking().Where(a => a.ProjectId == projectId && a.WeekStart >= thisWeek).ToListAsync(ct);
        var employeeIds = allocations.Select(a => a.EmployeeId).Distinct().ToList();
        var rates = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => employeeIds.Contains(r.EmployeeId)).ToListAsync(ct))
        {
            rates.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
        }

        var odcBudget = data.Budget is null
            ? []
            : await db.Set<BudgetLine>().AsNoTracking().Where(l => l.BudgetVersionId == data.Budget.Id && l.CostType == BudgetCostType.Odc).ToListAsync(ct);
        var fallbackWbs = data.Wbs.Where(w => w.ParentId is null).OrderBy(w => w.Order).ThenBy(w => w.Code, StringComparer.Ordinal).FirstOrDefault()?.Id;
        var notes = data.Notes as List<string> ?? [.. data.Notes];
        var forecast = AllocationEtc.Build(allocations, rates, odcBudget, data.Odcs, fallbackWbs, out var missingRateHours, out var unplacedHours);
        notes.Add($"ETC is allocated hours from the week of {thisWeek:MMM d} ({allocations.Sum(a => a.Hours):N1} h) at each person's cost rate, plus the remaining ODC budget.");
        if (missingRateHours > 0)
        {
            notes.Add($"{missingRateHours:N1} allocated hours have no cost rate and are priced at $0.");
        }

        if (unplacedHours > 0)
        {
            notes.Add($"{unplacedHours:N1} allocated hours have no WBS element and the project has none to hold them.");
        }

        data = data with { Notes = notes };
        return (data, FinancialsCalculator.Compute(data.ToInputs() with { Forecast = forecast, RemainingFrom = null }));
    }
}

/// <summary>The forecast lines behind the allocation-based ETC (unphased, so they all count as remaining).</summary>
public static class AllocationEtc
{
    public static IReadOnlyList<BudgetAmount> Build(
        IReadOnlyList<ResourceAllocation> futureAllocations,
        EffectiveRateTable<Guid> costRates,
        IReadOnlyList<BudgetLine> odcBudget,
        IReadOnlyList<OdcActual> odcActuals,
        Guid? fallbackWbsElementId,
        out decimal missingRateHours,
        out decimal unplacedHours)
    {
        missingRateHours = 0;
        unplacedHours = 0;
        var lines = new List<BudgetAmount>();
        foreach (var a in futureAllocations)
        {
            var wbs = a.WbsElementId ?? fallbackWbsElementId;
            if (wbs is null)
            {
                unplacedHours += a.Hours;
                continue;
            }

            var rate = costRates.Find(a.EmployeeId, a.WeekStart);
            if (rate is null)
            {
                missingRateHours += a.Hours;
            }

            lines.Add(new BudgetAmount(wbs.Value, null, a.Hours, Money.Round(a.Hours * (rate ?? 0))));
        }

        var spent = odcActuals.GroupBy(o => o.WbsElementId).ToDictionary(g => g.Key, g => g.Sum(o => o.Amount));
        foreach (var group in odcBudget.GroupBy(l => l.WbsElementId))
        {
            var remaining = group.Sum(l => l.Amount) - spent.GetValueOrDefault(group.Key);
            if (remaining > 0)
            {
                lines.Add(new BudgetAmount(group.Key, null, 0, remaining));
            }
        }

        return lines;
    }
}
