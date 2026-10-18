using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Contracts;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Identity;

// Contract detail: CLINs, modifications and funding are read by Finance, Admin and the manager of a project on the
// contract (the contract-funding rule); every write, and executing a modification, is Finance or Admin.

public static class ContractDetailPermissionRegistration
{
    public static IServiceCollection AddWorkbenchContractDetailPermissions(this IServiceCollection services)
    {
        services.Decorate<IClinService, PermissionCheckingClinService>();
        services.Decorate<IContractModificationService, PermissionCheckingContractModificationService>();
        services.Decorate<IFundingService, PermissionCheckingFundingService>();
        return services;
    }
}

public sealed class PermissionCheckingClinService(IClinService inner, ProjectAccess projects, IWorkbenchDbContextFactory dbFactory) : IClinService
{
    public async Task<IReadOnlyList<ClinDto>> ListClinsAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await projects.RequireContractFundingAsync(contractId, cancellationToken);
        return await inner.ListClinsAsync(contractId, cancellationToken);
    }

    public async Task<ClinDto?> GetClinAsync(Guid clinId, CancellationToken cancellationToken = default)
    {
        projects.Access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var contractId = await db.Set<Clin>().AsNoTracking().Where(c => c.Id == clinId).Select(c => (Guid?)c.ContractId).FirstOrDefaultAsync(cancellationToken);
        if (contractId is null)
        {
            return null;
        }

        await projects.RequireContractFundingAsync(contractId.Value, cancellationToken);
        return await inner.GetClinAsync(clinId, cancellationToken);
    }

    public async Task<ClinDto> CreateClinAsync(Guid contractId, SaveClinRequest request, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync("set up CLINs", cancellationToken);
        return await inner.CreateClinAsync(contractId, request, cancellationToken);
    }

    public async Task<ClinDto> UpdateClinAsync(Guid clinId, SaveClinRequest request, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync("change CLINs", cancellationToken);
        return await inner.UpdateClinAsync(clinId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<ClinDto>> ListClinsForProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "see this project's CLINs", cancellationToken);
        return await inner.ListClinsForProjectAsync(projectId, cancellationToken);
    }

    public async Task<IReadOnlyList<WbsClinMappingDto>> ListWbsMappingsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "see this project's CLIN mapping", cancellationToken);
        return await inner.ListWbsMappingsAsync(projectId, cancellationToken);
    }

    public async Task<WbsClinMappingDto> SetWbsClinAsync(Guid wbsElementId, Guid? clinId, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync("map the WBS to CLINs", cancellationToken);
        return await inner.SetWbsClinAsync(wbsElementId, clinId, cancellationToken);
    }
}

public sealed class PermissionCheckingContractModificationService(IContractModificationService inner, ProjectAccess projects, IWorkbenchDbContextFactory dbFactory)
    : IContractModificationService
{
    public async Task<IReadOnlyList<ContractModificationDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await projects.RequireContractFundingAsync(contractId, cancellationToken);
        return await inner.ListAsync(contractId, cancellationToken);
    }

    public async Task<ContractModificationDto?> GetAsync(Guid modificationId, CancellationToken cancellationToken = default)
    {
        projects.Access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var contractId = await db.Set<ContractModification>().AsNoTracking().Where(m => m.Id == modificationId)
            .Select(m => (Guid?)m.ContractId).FirstOrDefaultAsync(cancellationToken);
        if (contractId is null)
        {
            return null;
        }

        await projects.RequireContractFundingAsync(contractId.Value, cancellationToken);
        return await inner.GetAsync(modificationId, cancellationToken);
    }

    public async Task<ContractModificationDto> CreateDraftAsync(Guid contractId, SaveModificationRequest request, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync("draft contract modifications", cancellationToken);
        return await inner.CreateDraftAsync(contractId, request, cancellationToken);
    }

    public async Task<ContractModificationDto> UpdateDraftAsync(Guid modificationId, SaveModificationRequest request, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync("edit contract modifications", cancellationToken);
        return await inner.UpdateDraftAsync(modificationId, request, cancellationToken);
    }

    public async Task DeleteDraftAsync(Guid modificationId, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync("delete draft modifications", cancellationToken);
        await inner.DeleteDraftAsync(modificationId, cancellationToken);
    }

    public async Task<ContractModificationDto> ExecuteAsync(Guid modificationId, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync("execute contract modifications", cancellationToken);
        return await inner.ExecuteAsync(modificationId, cancellationToken);
    }
}

public sealed class PermissionCheckingFundingService(IFundingService inner, ProjectAccess projects) : IFundingService
{
    public async Task<IReadOnlyList<FundingActionDto>> ListFundingHistoryAsync(Guid contractId, Guid? clinId = null, CancellationToken cancellationToken = default)
    {
        await projects.RequireContractFundingAsync(contractId, cancellationToken);
        return await inner.ListFundingHistoryAsync(contractId, clinId, cancellationToken);
    }

    public async Task<ContractFundingStatusDto> GetFundingStatusAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await projects.RequireContractFundingAsync(contractId, cancellationToken);
        return await inner.GetFundingStatusAsync(contractId, cancellationToken);
    }

    public async Task<ContractFundingStatusDto?> GetProjectFundingStatusAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "see this project's CLIN funding", cancellationToken);
        return await inner.GetProjectFundingStatusAsync(projectId, cancellationToken);
    }

    public async Task<IReadOnlyList<FundingNoticeDto>> CheckNoticesAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync("run the funding notice check", cancellationToken);
        return await inner.CheckNoticesAsync(contractId, cancellationToken);
    }
}
