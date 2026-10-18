using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Financials;

/// <summary>Project and contract financials, computed on every request from budgets, approved time and allowable ODCs.</summary>
public sealed class ProjectFinancialsService(IWorkbenchDbContextFactory dbFactory, TimeProvider clock) : IProjectFinancialsService
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<ProjectFinancialSummaryDto> GetProjectSummaryAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var data = await FinancialsLoader.LoadProjectAsync(db, projectId, budgetVersionId, forecastVersionId, Today, cancellationToken);
        var result = FinancialsCalculator.Compute(data.ToInputs());
        var funding = data.Contract is null ? null : await FinancialsLoader.LoadFundingAsync(db, data.Contract, data.AsOf, data.Calendar, cancellationToken);
        return new ProjectFinancialSummaryDto(
            data.Project.Id, data.Project.Code, data.Project.Name,
            data.Contract?.Id, data.Contract?.ContractNumber, data.Contract is null ? null : (ContractKind)(int)data.Contract.Type,
            data.Budget?.Id, data.Budget?.Name, data.Forecast?.Id, data.Forecast?.Name,
            data.ShowsBillableValue, data.AsOf, result.Totals, funding, data.Notes);
    }

    public async Task<IReadOnlyList<WbsFinancialRowDto>> GetWbsFinancialsAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var data = await FinancialsLoader.LoadProjectAsync(db, projectId, budgetVersionId, forecastVersionId, Today, cancellationToken);
        return FinancialsCalculator.Compute(data.ToInputs()).Rows;
    }

    public async Task<TimePhasedBurnDto> GetTimePhasedBurnAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var data = await FinancialsLoader.LoadProjectAsync(db, projectId, budgetVersionId, null, Today, cancellationToken);
        var series = TimePhasing.Build(data.Calendar, data.Project.Start, data.BudgetLines, data.Labor, data.Odcs, data.AsOf);
        return new TimePhasedBurnDto(projectId, data.Budget?.Id, series.Periods, series.UnphasedBudgetAmount, [.. data.Notes, .. series.Notes]);
    }

    public async Task<ContractFundingDto> GetContractFundingAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var contract = await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Contract", contractId);
        return await FinancialsLoader.LoadFundingAsync(db, contract, Today, null, cancellationToken);
    }

    public async Task<IReadOnlyList<ContractFundingDto>> ListContractFundingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var contracts = await db.Set<Contract>().AsNoTracking().Where(c => c.Status == ContractStatus.Active).ToListAsync(cancellationToken);
        var calendar = await FinancialsLoader.LoadCalendarAsync(db, cancellationToken);
        var result = new List<ContractFundingDto>();
        foreach (var contract in contracts)
        {
            result.Add(await FinancialsLoader.LoadFundingAsync(db, contract, Today, calendar, cancellationToken));
        }

        return result.OrderByDescending(f => f.PercentOfFundedSpent ?? 0).ThenBy(f => f.ContractNumber, StringComparer.Ordinal).ToList();
    }
}
