using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Assets;

namespace Workbench.Application.Identity;

/// <summary>Fixed assets are Finance or Admin only: the register, capitalization, depreciation runs and reports.</summary>
public static class AssetPermissionRegistration
{
    public static IServiceCollection AddWorkbenchAssetPermissions(this IServiceCollection services)
    {
        services.Decorate<IAssetService, PermissionCheckingAssetService>();
        services.Decorate<IDepreciationService, PermissionCheckingDepreciationService>();
        services.Decorate<IAssetReportService, PermissionCheckingAssetReportService>();
        return services;
    }
}

public sealed class PermissionCheckingAssetService(IAssetService inner, AccessControl access) : IAssetService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireFinanceAsync("work with fixed assets", ct);

    public async Task<IReadOnlyList<AssetSummaryDto>> ListAsync(AssetQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListAsync(query, cancellationToken);
    }

    public async Task<AssetDto> GetAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetAsync(assetId, cancellationToken);
    }

    public async Task<AssetSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetSettingsAsync(cancellationToken);
    }

    public async Task<AssetDto> CreateAsync(SaveAssetRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<AssetDto> UpdateAsync(Guid assetId, SaveAssetRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.UpdateAsync(assetId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<CapitalizableBillLineDto>> ListCapitalizableBillLinesAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListCapitalizableBillLinesAsync(cancellationToken);
    }

    public async Task<AssetDto> CapitalizeAsync(CapitalizeBillLineRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CapitalizeAsync(request, cancellationToken);
    }

    public async Task<AssetDto> TransferAsync(Guid assetId, TransferAssetRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.TransferAsync(assetId, request, cancellationToken);
    }

    public async Task<AssetDto> DisposeAsync(Guid assetId, DisposeAssetRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.DisposeAsync(assetId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<AssetCategoryDto>> ListCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListCategoriesAsync(cancellationToken);
    }

    public async Task<AssetCategoryDto> SaveCategoryAsync(Guid? categoryId, SaveAssetCategoryRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveCategoryAsync(categoryId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingDepreciationService(IDepreciationService inner, AccessControl access) : IDepreciationService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireFinanceAsync("run depreciation", ct);

    public async Task<DepreciationPreviewDto> PreviewAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.PreviewAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<DepreciationRunDto?> RunAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.RunAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<IReadOnlyList<DepreciationRunDto>> ListRunsAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListRunsAsync(cancellationToken);
    }
}

public sealed class PermissionCheckingAssetReportService(IAssetReportService inner, AccessControl access) : IAssetReportService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireFinanceAsync("see fixed asset reports", ct);

    public async Task<IReadOnlyList<AssetSummaryDto>> GetRegisterAsync(AssetQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetRegisterAsync(query, cancellationToken);
    }

    public async Task<DepreciationScheduleDto> GetScheduleAsync(Guid assetId, DepreciationBook book = DepreciationBook.Book, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetScheduleAsync(assetId, book, cancellationToken);
    }

    public async Task<RollForwardDto> GetRollForwardAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetRollForwardAsync(from, to, cancellationToken);
    }

    public async Task<IReadOnlyList<GovernmentPropertyRowDto>> GetGovernmentPropertyAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetGovernmentPropertyAsync(cancellationToken);
    }

    public async Task<AssetExportFile> ExportRegisterCsvAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExportRegisterCsvAsync(cancellationToken);
    }

    public async Task<AssetExportFile> ExportScheduleCsvAsync(Guid assetId, DepreciationBook book = DepreciationBook.Book, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExportScheduleCsvAsync(assetId, book, cancellationToken);
    }

    public async Task<AssetExportFile> ExportRollForwardCsvAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExportRollForwardCsvAsync(from, to, cancellationToken);
    }

    public async Task<AssetExportFile> ExportGovernmentPropertyCsvAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExportGovernmentPropertyCsvAsync(cancellationToken);
    }
}
