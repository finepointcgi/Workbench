using Workbench.Contracts.Risks;

namespace Workbench.Application.Identity;

/// <summary>A project's risk register: whoever may see the project's financials (its manager, Finance, Admin) reads and edits it.</summary>
public sealed class PermissionCheckingRiskService(IRiskService inner, ProjectAccess projects) : IRiskService
{
    private const string What = "see this project's risks";

    public async Task<RiskRegisterDto> GetRegisterAsync(Guid projectId, bool includeClosed = false, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, What, cancellationToken);
        return await inner.GetRegisterAsync(projectId, includeClosed, cancellationToken);
    }

    public async Task<RiskDto> GetAsync(Guid riskId, CancellationToken cancellationToken = default)
    {
        var risk = await inner.GetAsync(riskId, cancellationToken);
        await projects.RequireProjectFinancialsAsync(risk.ProjectId, What, cancellationToken);
        return risk;
    }

    public async Task<RiskDto> CreateAsync(Guid projectId, SaveRiskRequest request, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "add a risk to this project", cancellationToken);
        return await inner.CreateAsync(projectId, request, cancellationToken);
    }

    public async Task<RiskDto> UpdateAsync(Guid riskId, SaveRiskRequest request, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync((await inner.GetAsync(riskId, cancellationToken)).ProjectId, "change this project's risks", cancellationToken);
        return await inner.UpdateAsync(riskId, request, cancellationToken);
    }

    public async Task DeleteAsync(Guid riskId, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync((await inner.GetAsync(riskId, cancellationToken)).ProjectId, "delete this project's risks", cancellationToken);
        await inner.DeleteAsync(riskId, cancellationToken);
    }
}
