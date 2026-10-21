using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Resourcing;
using Workbench.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Contracts.PerDiem;
using Workbench.Contracts.Resourcing;
using Workbench.Domain.Resourcing;

namespace Workbench.Application.Identity;

// Per diem: anyone signed in reads the rate tables (travelers pick destinations); Finance or Admin import them. The travel
// per diem service enforces its own owner/supervisor rules (like expense reports). Resource planning: see ResourcingAccess.
// The allocation-based ETC prices allocations at cost rates, so it follows the project financials rule.

public static class PerDiemResourcingPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchPerDiemResourcingPermissions(this IServiceCollection services)
    {
        services.Decorate<IPerDiemRateService, PermissionCheckingPerDiemRateService>();
        services.Decorate<ITravelPerDiemService, PermissionCheckingTravelPerDiemService>();
        services.Decorate<IResourceAllocationService, PermissionCheckingResourceAllocationService>();
        services.Decorate<IResourcePlanningService, PermissionCheckingResourcePlanningService>();
        services.Decorate<IAllocationForecastService, PermissionCheckingAllocationForecastService>();
        return services;
    }
}

public sealed class PermissionCheckingPerDiemRateService(IPerDiemRateService inner, AccessControl access) : IPerDiemRateService
{
    private const string What = "import per diem rates";

    public Task<PerDiemRateTableDto> GetTableAsync(PerDiemRateQuery? query = null, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetTableAsync(query, cancellationToken);
    }

    public Task<IReadOnlyList<PerDiemLocationDto>> ListLocationsAsync(int fiscalYear, PerDiemTable? kind = null, string? search = null, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListLocationsAsync(fiscalYear, kind, search, cancellationToken);
    }

    public Task<ResolvedPerDiemRateDto> LookupAsync(PerDiemLocationDto location, DateOnly date, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.LookupAsync(location, date, cancellationToken);
    }

    public async Task<PerDiemImportResultDto> ImportFileAsync(ImportPerDiemFileRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ImportFileAsync(request, cancellationToken);
    }

    public async Task<PerDiemImportResultDto> ImportFromGsaApiAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ImportFromGsaApiAsync(fiscalYear, cancellationToken);
    }

    public async Task<PerDiemImportResultDto> InstallSampleRatesAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.InstallSampleRatesAsync(fiscalYear, cancellationToken);
    }
}

public sealed class PermissionCheckingTravelPerDiemService(ITravelPerDiemService inner, AccessControl access) : ITravelPerDiemService
{
    public Task<TravelPerDiemDto> GetAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetAsync(expenseReportId, cancellationToken);
    }

    public Task<TravelPerDiemDto> SaveAsync(Guid expenseReportId, SaveTravelPerDiemRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.SaveAsync(expenseReportId, request, cancellationToken);
    }

    public Task<TravelPerDiemDto> ApplyToReportAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ApplyToReportAsync(expenseReportId, cancellationToken);
    }

    public Task RemoveAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.RemoveAsync(expenseReportId, cancellationToken);
    }
}

public sealed class PermissionCheckingResourceAllocationService(IResourceAllocationService inner, ResourcingAccess rules, IWorkbenchDbContextFactory dbFactory)
    : IResourceAllocationService
{
    public async Task<IReadOnlyList<AllocationDto>> ListAsync(AllocationQuery query, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        if (query.ProjectId is { } projectId)
        {
            await rules.Visibility.RequireVisibleAsync(projectId, cancellationToken);
        }

        return await rules.Visibility.FilterAsync(await inner.ListAsync(query, cancellationToken), a => a.ProjectId, cancellationToken);
    }

    public Task<IReadOnlyList<AllocationDto>> ListMineAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        return inner.ListMineAsync(from, to, cancellationToken);
    }

    public async Task<IReadOnlyList<AllocationDto>> CreateAsync(SaveAllocationRequest request, CancellationToken cancellationToken = default)
    {
        await rules.RequireManageProjectAsync(request.ProjectId, cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<AllocationDto> UpdateAsync(Guid allocationId, SaveAllocationRequest request, CancellationToken cancellationToken = default)
    {
        await rules.RequireManageProjectAsync(await ProjectOfAsync(allocationId, cancellationToken), cancellationToken);
        await rules.RequireManageProjectAsync(request.ProjectId, cancellationToken);
        return await inner.UpdateAsync(allocationId, request, cancellationToken);
    }

    public async Task DeleteAsync(Guid allocationId, CancellationToken cancellationToken = default)
    {
        await rules.RequireManageProjectAsync(await ProjectOfAsync(allocationId, cancellationToken), cancellationToken);
        await inner.DeleteAsync(allocationId, cancellationToken);
    }

    public async Task<GenerateAllocationsResultDto> GenerateFromPlanAsync(Guid projectId, GenerateAllocationsRequest? request = null, CancellationToken cancellationToken = default)
    {
        await rules.RequireManageProjectAsync(projectId, cancellationToken);
        return await inner.GenerateFromPlanAsync(projectId, request, cancellationToken);
    }

    public Task<bool> CanManageAsync(Guid projectId, CancellationToken cancellationToken = default) => inner.CanManageAsync(projectId, cancellationToken);

    private async Task<Guid> ProjectOfAsync(Guid allocationId, CancellationToken ct)
    {
        rules.Access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<ResourceAllocation>().AsNoTracking().Where(a => a.Id == allocationId).Select(a => (Guid?)a.ProjectId).FirstOrDefaultAsync(ct)
            ?? throw WorkbenchNotFoundException.For("Allocation", allocationId);
    }
}

public sealed class PermissionCheckingResourcePlanningService(IResourcePlanningService inner, ResourcingAccess rules) : IResourcePlanningService
{
    public Task<IReadOnlyList<CapacityWeekDto>> GetCapacityAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid>? employeeIds = null, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        return inner.GetCapacityAsync(from, to, employeeIds, cancellationToken);
    }

    public async Task<HeatMapDto> GetHeatMapAsync(HeatMapQuery query, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        if (query.ProjectId is { } projectId)
        {
            await rules.Visibility.RequireVisibleAsync(projectId, cancellationToken); // Who's on a project is the project's data.
        }

        return await inner.GetHeatMapAsync(query, cancellationToken);
    }

    public async Task<ProjectStaffingDto> GetProjectStaffingAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await rules.RequireReadProjectAsync(projectId, cancellationToken);
        return await inner.GetProjectStaffingAsync(projectId, cancellationToken);
    }

    public Task<UtilizationDto> GetUtilizationAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        return inner.GetUtilizationAsync(from, to, cancellationToken);
    }

    public async Task<IReadOnlyList<OpenDemandDto>> ListOpenDemandAsync(Guid? projectId = null, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        if (projectId is { } id)
        {
            await rules.RequireReadProjectAsync(id, cancellationToken);
        }

        return await rules.Visibility.FilterAsync(await inner.ListOpenDemandAsync(projectId, cancellationToken), d => d.ProjectId, cancellationToken);
    }

    public Task<IReadOnlyList<AvailablePersonDto>> FindAvailablePeopleAsync(FindPeopleQuery query, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        return inner.FindAvailablePeopleAsync(query, cancellationToken);
    }
}

public sealed class PermissionCheckingAllocationForecastService(IAllocationForecastService inner, ProjectAccess access) : IAllocationForecastService
{
    private const string What = "see project financials";

    public async Task<ProjectFinancialSummaryDto> GetProjectSummaryAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectFinancialsAsync(projectId, What, cancellationToken);
        return await inner.GetProjectSummaryAsync(projectId, budgetVersionId, cancellationToken);
    }

    public async Task<IReadOnlyList<WbsFinancialRowDto>> GetWbsFinancialsAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectFinancialsAsync(projectId, What, cancellationToken);
        return await inner.GetWbsFinancialsAsync(projectId, budgetVersionId, cancellationToken);
    }
}
