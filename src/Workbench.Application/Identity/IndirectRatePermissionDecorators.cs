using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.IndirectRates;

namespace Workbench.Application.Identity;

// Indirect rates are Finance's (Finance or Admin). Burdened cost follows the project financials rule: Finance, Admin or
// the project's manager. Billing calls IBurdenedCostService in-process for whoever runs billing (Finance). Burdened
// cost is also the one read a trusted background job (Workbench System) may make; nothing here lets it write.

public static class IndirectRatePermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchIndirectRatePermissions(this IServiceCollection services)
    {
        services.Decorate<IIndirectRateService, PermissionCheckingIndirectRateService>();
        services.Decorate<IBurdenedCostService, PermissionCheckingBurdenedCostService>();
        services.Decorate<IBurdenedFinancialsService, PermissionCheckingBurdenedFinancialsService>();
        return services;
    }
}

public sealed class PermissionCheckingIndirectRateService(IIndirectRateService inner, AccessControl access) : IIndirectRateService
{
    private const string What = "manage indirect rates";

    private Task RequireAsync(CancellationToken ct) => access.RequireFinanceAsync(What, ct);

    public async Task<IReadOnlyList<PoolSetupDto>> ListPoolSetupsAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListPoolSetupsAsync(cancellationToken);
    }

    public async Task<PoolSetupDto> SavePoolSetupAsync(CostPool pool, SavePoolSetupRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SavePoolSetupAsync(pool, request, cancellationToken);
    }

    public async Task<int> InstallDefaultPoolsAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.InstallDefaultPoolsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IndirectRateDto>> ListRatesAsync(int? fiscalYear = null, bool includeHistory = false, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListRatesAsync(fiscalYear, includeHistory, cancellationToken);
    }

    public async Task<IndirectRateDto> SaveRateAsync(SaveIndirectRateRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveRateAsync(request, cancellationToken);
    }

    public async Task<IndirectRateDto> ApproveRateAsync(Guid rateId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ApproveRateAsync(rateId, cancellationToken);
    }

    public async Task WithdrawRateAsync(Guid rateId, WithdrawIndirectRateRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        await inner.WithdrawRateAsync(rateId, request, cancellationToken);
    }

    public async Task<RateCalculationDto> GetRateCalculationAsync(Guid fromPeriodId, Guid toPeriodId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetRateCalculationAsync(fromPeriodId, toPeriodId, cancellationToken);
    }

    public async Task<IReadOnlyList<AllocationRunSummaryDto>> ListAllocationRunsAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListAllocationRunsAsync(fiscalYear, cancellationToken);
    }

    public async Task<AllocationPreviewDto> PreviewAllocationAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.PreviewAllocationAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<AllocationPreviewDto> PostAllocationAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.PostAllocationAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<AllocationPreviewDto> ReverseAllocationAsync(Guid fiscalPeriodId, ReverseAllocationRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ReverseAllocationAsync(fiscalPeriodId, request, cancellationToken);
    }

    public async Task<TrueUpDto> GetTrueUpAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetTrueUpAsync(fiscalYear, cancellationToken);
    }

    public async Task<TrueUpDto> PostTrueUpAsync(int fiscalYear, PostTrueUpRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.PostTrueUpAsync(fiscalYear, request, cancellationToken);
    }
}

public sealed class PermissionCheckingBurdenedCostService(IBurdenedCostService inner, ProjectAccess projects) : IBurdenedCostService
{
    public async Task<BurdenedCostResult> GetProjectCostAsync(Guid projectId, DateOnly from, DateOnly to, RateBasis basis, CancellationToken cancellationToken = default)
    {
        // A read the Limitation of Funds background check makes as Workbench System (see SystemExecutionContext).
        if (!projects.Access.IsSystemReader)
        {
            await projects.RequireProjectFinancialsAsync(projectId, "see this project's burdened cost", cancellationToken);
        }

        return await inner.GetProjectCostAsync(projectId, from, to, basis, cancellationToken);
    }
}

public sealed class PermissionCheckingBurdenedFinancialsService(IBurdenedFinancialsService inner, ProjectAccess projects) : IBurdenedFinancialsService
{
    public async Task<BurdenedFinancialsDto> GetProjectAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "see this project's financials", cancellationToken);
        return await inner.GetProjectAsync(projectId, budgetVersionId, forecastVersionId, cancellationToken);
    }
}
