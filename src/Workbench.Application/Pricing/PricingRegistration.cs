using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Collaboration;
using Workbench.Application.Security;
using Workbench.Contracts.Pricing;
using Workbench.Contracts.Security;

namespace Workbench.Application.Pricing;

public static class PricingRegistration
{
    /// <summary>
    /// Proposal pricing. Cost volume exports are recorded in the security event log. The opportunity lookup finds nothing
    /// until the CRM module registers its adapter (a later <c>AddScoped</c> wins over this <c>TryAdd</c>). Register above
    /// <c>AddWorkbenchIdentity</c>, whose decorators add the permission checks.
    /// </summary>
    public static IServiceCollection AddWorkbenchPricing(this IServiceCollection services)
    {
        services.AddScoped<IProposalPricingService, PricingService>();
        services.Decorate<IProposalPricingService, SecurityLoggingPricingService>();
        services.TryAddScoped<IPricingOpportunityLookup, NoOpportunityLookup>();
        return services;
    }
}

/// <summary>No CRM yet: proposals link to an opportunity by id and a typed name.</summary>
public sealed class NoOpportunityLookup : IPricingOpportunityLookup
{
    public Task<IReadOnlyList<OpportunityRefDto>> SearchAsync(string? search, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OpportunityRefDto>>([]);

    public Task<OpportunityRefDto?> GetAsync(Guid opportunityId, CancellationToken cancellationToken = default) => Task.FromResult<OpportunityRefDto?>(null);
}

/// <summary>Records each cost volume export (registered inside the permission check, so only allowed exports are recorded).</summary>
public sealed class SecurityLoggingPricingService(IProposalPricingService inner, SecurityEventLog log) : IProposalPricingService
{
    public async Task<PricingExportFile> ExportCostVolumeAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        var file = await inner.ExportCostVolumeAsync(modelId, cancellationToken);
        await log.RecordAsync(SecurityEventTypes.ExportPricing, file.FileName, detail: $"Cost volume, price {modelId}", cancellationToken: cancellationToken);
        return file;
    }

    public Task<IReadOnlyList<ProposalSummaryDto>> ListProposalsAsync(CancellationToken cancellationToken = default) => inner.ListProposalsAsync(cancellationToken);

    public Task<ProposalDto> GetProposalAsync(Guid proposalId, CancellationToken cancellationToken = default) => inner.GetProposalAsync(proposalId, cancellationToken);

    public Task<ProposalDto> CreateProposalAsync(SaveProposalRequest request, CancellationToken cancellationToken = default) => inner.CreateProposalAsync(request, cancellationToken);

    public Task<ProposalDto> UpdateProposalAsync(Guid proposalId, SaveProposalRequest request, CancellationToken cancellationToken = default) =>
        inner.UpdateProposalAsync(proposalId, request, cancellationToken);

    public Task<PriceModelDto> GetModelAsync(Guid modelId, CancellationToken cancellationToken = default) => inner.GetModelAsync(modelId, cancellationToken);

    public Task<PriceModelDto> SaveSettingsAsync(Guid modelId, SavePriceSettingsRequest request, CancellationToken cancellationToken = default) =>
        inner.SaveSettingsAsync(modelId, request, cancellationToken);

    public Task<PriceModelDto> SavePeriodsAsync(Guid modelId, IReadOnlyList<SavePricePeriodRequest> periods, CancellationToken cancellationToken = default) =>
        inner.SavePeriodsAsync(modelId, periods, cancellationToken);

    public Task<PriceModelDto> SaveClinsAsync(Guid modelId, IReadOnlyList<SavePriceClinRequest> clins, CancellationToken cancellationToken = default) =>
        inner.SaveClinsAsync(modelId, clins, cancellationToken);

    public Task<PriceModelDto> SaveWbsAsync(Guid modelId, IReadOnlyList<SavePriceWbsRequest> wbs, CancellationToken cancellationToken = default) =>
        inner.SaveWbsAsync(modelId, wbs, cancellationToken);

    public Task<PriceModelDto> SaveEscalationsAsync(Guid modelId, IReadOnlyList<PriceEscalationDto> escalations, CancellationToken cancellationToken = default) =>
        inner.SaveEscalationsAsync(modelId, escalations, cancellationToken);

    public Task<PriceModelDto> SaveLaborLinesAsync(Guid modelId, IReadOnlyList<SavePriceLaborLineRequest> lines, CancellationToken cancellationToken = default) =>
        inner.SaveLaborLinesAsync(modelId, lines, cancellationToken);

    public Task<PriceModelDto> SaveCostLinesAsync(Guid modelId, IReadOnlyList<SavePriceCostLineRequest> lines, CancellationToken cancellationToken = default) =>
        inner.SaveCostLinesAsync(modelId, lines, cancellationToken);

    public Task<PriceResultDto> CalculateAsync(Guid modelId, CancellationToken cancellationToken = default) => inner.CalculateAsync(modelId, cancellationToken);

    public Task<PriceModelDto> CopyModelAsync(Guid modelId, CopyPriceModelRequest request, CancellationToken cancellationToken = default) =>
        inner.CopyModelAsync(modelId, request, cancellationToken);

    public Task DeleteModelAsync(Guid modelId, CancellationToken cancellationToken = default) => inner.DeleteModelAsync(modelId, cancellationToken);

    public Task<PriceModelDto> LockModelAsync(Guid modelId, LockPriceModelRequest request, CancellationToken cancellationToken = default) =>
        inner.LockModelAsync(modelId, request, cancellationToken);

    public Task<ScenarioComparisonDto> CompareAsync(Guid proposalId, CancellationToken cancellationToken = default) => inner.CompareAsync(proposalId, cancellationToken);

    public Task<IReadOnlyList<PricingHistoryDto>> ListHistoryAsync(Guid proposalId, CancellationToken cancellationToken = default) =>
        inner.ListHistoryAsync(proposalId, cancellationToken);

    public Task<PricingOptionsDto> GetOptionsAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default) => inner.GetOptionsAsync(asOf, cancellationToken);

    public Task<HandoffResultDto> HandOffAsync(Guid modelId, HandoffRequest request, CancellationToken cancellationToken = default) =>
        inner.HandOffAsync(modelId, request, cancellationToken);
}
