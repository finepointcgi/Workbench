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
        if (!await access.HasAsync(Permission.PricingManage, ct))
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

    // Fee, margin and price are a sensitive field (Permission.FieldsPricingFee): without it pricing shows cost only.
    private async Task<T> MaskedAsync<T>(Task<T> read, CancellationToken ct) => FieldMasker.Mask(await read, await access.GetPermissionsAsync(ct));

    public async Task<IReadOnlyList<ProposalSummaryDto>> ListProposalsAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.ListProposalsAsync(cancellationToken), cancellationToken);
    }

    public async Task<ProposalDto> GetProposalAsync(Guid proposalId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.GetProposalAsync(proposalId, cancellationToken), cancellationToken);
    }

    public async Task<ProposalDto> CreateProposalAsync(SaveProposalRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.CreateProposalAsync(request, cancellationToken), cancellationToken);
    }

    public async Task<ProposalDto> UpdateProposalAsync(Guid proposalId, SaveProposalRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.UpdateProposalAsync(proposalId, request, cancellationToken), cancellationToken);
    }

    public async Task<PriceModelDto> GetModelAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.GetModelAsync(modelId, cancellationToken), cancellationToken);
    }

    public async Task<PriceModelDto> SaveSettingsAsync(Guid modelId, SavePriceSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        if (request?.Settings is { } settings && !await access.HasAsync(Permission.FieldsPricingFee, cancellationToken))
        {
            // Someone who can't see the fee can't set it: keep the model's fee terms (they were sent a masked rate).
            var current = (await inner.GetModelAsync(modelId, cancellationToken)).Settings;
            request = request with
            {
                Settings = settings with
                {
                    FeeMethod = current.FeeMethod, FeeRate = current.FeeRate, WgTechnicalRisk = current.WgTechnicalRisk, WgManagementRisk = current.WgManagementRisk,
                    WgContractTypeRisk = current.WgContractTypeRisk, WgCostEfficiency = current.WgCostEfficiency,
                },
            };
        }

        return await MaskedAsync(inner.SaveSettingsAsync(modelId, request!, cancellationToken), cancellationToken);
    }

    public async Task<PriceModelDto> SavePeriodsAsync(Guid modelId, IReadOnlyList<SavePricePeriodRequest> periods, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.SavePeriodsAsync(modelId, periods, cancellationToken), cancellationToken);
    }

    public async Task<PriceModelDto> SaveClinsAsync(Guid modelId, IReadOnlyList<SavePriceClinRequest> clins, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.SaveClinsAsync(modelId, clins, cancellationToken), cancellationToken);
    }

    public async Task<PriceModelDto> SaveWbsAsync(Guid modelId, IReadOnlyList<SavePriceWbsRequest> wbs, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.SaveWbsAsync(modelId, wbs, cancellationToken), cancellationToken);
    }

    public async Task<PriceModelDto> SaveEscalationsAsync(Guid modelId, IReadOnlyList<PriceEscalationDto> escalations, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.SaveEscalationsAsync(modelId, escalations, cancellationToken), cancellationToken);
    }

    public async Task<PriceModelDto> SaveLaborLinesAsync(Guid modelId, IReadOnlyList<SavePriceLaborLineRequest> lines, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.SaveLaborLinesAsync(modelId, lines, cancellationToken), cancellationToken);
    }

    public async Task<PriceModelDto> SaveCostLinesAsync(Guid modelId, IReadOnlyList<SavePriceCostLineRequest> lines, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.SaveCostLinesAsync(modelId, lines, cancellationToken), cancellationToken);
    }

    public async Task<PriceResultDto> CalculateAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.CalculateAsync(modelId, cancellationToken), cancellationToken);
    }

    public async Task<PriceModelDto> CopyModelAsync(Guid modelId, CopyPriceModelRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.CopyModelAsync(modelId, request, cancellationToken), cancellationToken);
    }

    public async Task DeleteModelAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        await inner.DeleteModelAsync(modelId, cancellationToken);
    }

    public async Task<PriceModelDto> LockModelAsync(Guid modelId, LockPriceModelRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.LockModelAsync(modelId, request, cancellationToken), cancellationToken);
    }

    public async Task<ScenarioComparisonDto> CompareAsync(Guid proposalId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.CompareAsync(proposalId, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<PricingHistoryDto>> ListHistoryAsync(Guid proposalId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.ListHistoryAsync(proposalId, cancellationToken), cancellationToken);
    }

    public async Task<PricingOptionsDto> GetOptionsAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await MaskedAsync(inner.GetOptionsAsync(asOf, cancellationToken), cancellationToken);
    }

    public async Task<PricingExportFile> ExportCostVolumeAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        await access.RequireAsync(Permission.FieldsPricingFee, "export the cost volume (it shows fee and price)", cancellationToken);
        return await inner.ExportCostVolumeAsync(modelId, cancellationToken);
    }

    public async Task<HandoffResultDto> HandOffAsync(Guid modelId, HandoffRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SetupContracts, "hand a won proposal off to its contract and budget", cancellationToken);
        return await inner.HandOffAsync(modelId, request, cancellationToken);
    }
}
