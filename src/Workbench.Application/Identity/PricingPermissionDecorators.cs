using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Pricing;
using Workbench.Contracts.Security;

namespace Workbench.Application.Identity;

/// <summary>
/// Proposal pricing exposes cost rates, indirect rates and fee: Finance, administrators and the Pricing role only. The won
/// handoff writes contract CLINs, bill rates, WBS and budgets, so it is Finance or Admin.
/// </summary>
public static class PricingPermissionRegistration
{
    public static IServiceCollection AddWorkbenchPricingPermissions(this IServiceCollection services)
    {
        services.Decorate<IProposalPricingService, PermissionCheckingPricingService>();
        services.Decorate<IPricingOpportunityLookup, PermissionCheckingOpportunityLookup>();
        return services;
    }

    internal static async Task RequirePricingAsync(AccessControl access, SecurityEventLog log, CancellationToken ct)
    {
        access.RequireSignedIn();
        if (!(await access.GetOrgRolesAsync(ct)).CanPrice())
        {
            const string message = "Only Finance, administrators and the Pricing role can work with proposal prices.";
            await log.RecordAsync(SecurityEventTypes.PermissionDenied, "pricing", SecurityOutcomes.Denied, message, cancellationToken: ct);
            throw new WorkbenchForbiddenException(message);
        }
    }
}

public sealed class PermissionCheckingOpportunityLookup(IPricingOpportunityLookup inner, AccessControl access, SecurityEventLog log) : IPricingOpportunityLookup
{
    public async Task<IReadOnlyList<OpportunityRefDto>> SearchAsync(string? search, CancellationToken cancellationToken = default)
    {
        await PricingPermissionRegistration.RequirePricingAsync(access, log, cancellationToken);
        return await inner.SearchAsync(search, cancellationToken);
    }

    public async Task<OpportunityRefDto?> GetAsync(Guid opportunityId, CancellationToken cancellationToken = default)
    {
        await PricingPermissionRegistration.RequirePricingAsync(access, log, cancellationToken);
        return await inner.GetAsync(opportunityId, cancellationToken);
    }
}

public sealed class PermissionCheckingPricingService(IProposalPricingService inner, AccessControl access, SecurityEventLog log) : IProposalPricingService
{
    private Task RequireAsync(CancellationToken ct) => PricingPermissionRegistration.RequirePricingAsync(access, log, ct);

    public async Task<IReadOnlyList<ProposalSummaryDto>> ListProposalsAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListProposalsAsync(cancellationToken);
    }

    public async Task<ProposalDto> GetProposalAsync(Guid proposalId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetProposalAsync(proposalId, cancellationToken);
    }

    public async Task<ProposalDto> CreateProposalAsync(SaveProposalRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CreateProposalAsync(request, cancellationToken);
    }

    public async Task<ProposalDto> UpdateProposalAsync(Guid proposalId, SaveProposalRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.UpdateProposalAsync(proposalId, request, cancellationToken);
    }

    public async Task<PriceModelDto> GetModelAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetModelAsync(modelId, cancellationToken);
    }

    public async Task<PriceModelDto> SaveSettingsAsync(Guid modelId, SavePriceSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveSettingsAsync(modelId, request, cancellationToken);
    }

    public async Task<PriceModelDto> SavePeriodsAsync(Guid modelId, IReadOnlyList<SavePricePeriodRequest> periods, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SavePeriodsAsync(modelId, periods, cancellationToken);
    }

    public async Task<PriceModelDto> SaveClinsAsync(Guid modelId, IReadOnlyList<SavePriceClinRequest> clins, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveClinsAsync(modelId, clins, cancellationToken);
    }

    public async Task<PriceModelDto> SaveWbsAsync(Guid modelId, IReadOnlyList<SavePriceWbsRequest> wbs, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveWbsAsync(modelId, wbs, cancellationToken);
    }

    public async Task<PriceModelDto> SaveEscalationsAsync(Guid modelId, IReadOnlyList<PriceEscalationDto> escalations, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveEscalationsAsync(modelId, escalations, cancellationToken);
    }

    public async Task<PriceModelDto> SaveLaborLinesAsync(Guid modelId, IReadOnlyList<SavePriceLaborLineRequest> lines, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveLaborLinesAsync(modelId, lines, cancellationToken);
    }

    public async Task<PriceModelDto> SaveCostLinesAsync(Guid modelId, IReadOnlyList<SavePriceCostLineRequest> lines, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveCostLinesAsync(modelId, lines, cancellationToken);
    }

    public async Task<PriceResultDto> CalculateAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CalculateAsync(modelId, cancellationToken);
    }

    public async Task<PriceModelDto> CopyModelAsync(Guid modelId, CopyPriceModelRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CopyModelAsync(modelId, request, cancellationToken);
    }

    public async Task DeleteModelAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        await inner.DeleteModelAsync(modelId, cancellationToken);
    }

    public async Task<PriceModelDto> LockModelAsync(Guid modelId, LockPriceModelRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.LockModelAsync(modelId, request, cancellationToken);
    }

    public async Task<ScenarioComparisonDto> CompareAsync(Guid proposalId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CompareAsync(proposalId, cancellationToken);
    }

    public async Task<IReadOnlyList<PricingHistoryDto>> ListHistoryAsync(Guid proposalId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListHistoryAsync(proposalId, cancellationToken);
    }

    public async Task<PricingOptionsDto> GetOptionsAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetOptionsAsync(asOf, cancellationToken);
    }

    public async Task<PricingExportFile> ExportCostVolumeAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExportCostVolumeAsync(modelId, cancellationToken);
    }

    public async Task<HandoffResultDto> HandOffAsync(Guid modelId, HandoffRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("hand a won proposal off to its contract and budget", cancellationToken);
        return await inner.HandOffAsync(modelId, request, cancellationToken);
    }
}
