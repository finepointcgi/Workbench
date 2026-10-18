namespace Workbench.Contracts.Financials;

/// <summary>
/// Who may read project financials and contract funding, so the UI can hide what the server would refuse.
/// Project financials and budgets: Finance, Admin, or the project's manager. Contract funding: Finance, Admin, or the
/// manager of a project on that contract. The server enforces the same rules on every call.
/// </summary>
public interface IFinancialAccessService
{
    Task<bool> CanReadProjectFinancialsAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<bool> CanReadContractFundingAsync(Guid contractId, CancellationToken cancellationToken = default);
}
