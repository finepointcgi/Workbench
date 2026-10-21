using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Companies;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Domain.Assets;
using Workbench.Contracts.Assets;

namespace Workbench.Application.Identity;

/// <summary>Fixed assets are Finance or Admin only: the register, capitalization, depreciation runs, tax books and reports.</summary>
public static class AssetPermissionRegistration
{
    public static IServiceCollection AddWorkbenchAssetPermissions(this IServiceCollection services)
    {
        services.Decorate<IAssetService, PermissionCheckingAssetService>();
        services.Decorate<IDepreciationService, PermissionCheckingDepreciationService>();
        services.Decorate<IAssetReportService, PermissionCheckingAssetReportService>();
        services.Decorate<IAssetTaxBookService, PermissionCheckingAssetTaxBookService>();
        services.Decorate<IAssetTaxBookService, CompanyScopedAssetTaxBookService>(); // Assets of companies the user can't see are not found.
        return services;
    }
}

public sealed class PermissionCheckingAssetService(IAssetService inner, AccessControl access) : IAssetService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.AssetsManage, "work with fixed assets", ct);

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

    public async Task<IReadOnlyList<AssetTransferRowDto>> ListTransfersAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListTransfersAsync(from, to, cancellationToken);
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

    public async Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        await inner.DeleteCategoryAsync(categoryId, cancellationToken);
    }
}

public sealed class PermissionCheckingDepreciationService(IDepreciationService inner, AccessControl access) : IDepreciationService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.AssetsDepreciationRun, "run depreciation", ct);

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
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.AssetsManage, "see fixed asset reports", ct);

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

public sealed class PermissionCheckingAssetTaxBookService(IAssetTaxBookService inner, AccessControl access) : IAssetTaxBookService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.AssetsManage, "work with tax depreciation books", ct);

    public async Task<AssetBooksDto> GetBooksAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetBooksAsync(assetId, cancellationToken);
    }

    public async Task<AssetTaxBookDto> SaveBookAsync(Guid assetId, SaveAssetTaxBookRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveBookAsync(assetId, request, cancellationToken);
    }

    public async Task DeleteBookAsync(Guid assetId, AssetBookKind book, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        await inner.DeleteBookAsync(assetId, book, cancellationToken);
    }

    public async Task<TaxSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetSettingsAsync(cancellationToken);
    }

    public async Task<TaxDepreciationReportDto> GetDepreciationReportAsync(TaxReportQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetDepreciationReportAsync(query, cancellationToken);
    }

    public async Task<BookTaxDifferenceDto> GetBookTaxDifferenceAsync(TaxReportQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetBookTaxDifferenceAsync(query, cancellationToken);
    }

    public async Task<Form4562SummaryDto> GetForm4562Async(TaxReportQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetForm4562Async(query, cancellationToken);
    }

    public async Task<MidQuarterTestDto> GetMidQuarterTestAsync(TaxReportQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetMidQuarterTestAsync(query, cancellationToken);
    }

    public async Task<AssetExportFile> ExportDepreciationReportCsvAsync(TaxReportQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExportDepreciationReportCsvAsync(query, cancellationToken);
    }

    public async Task<AssetExportFile> ExportBookTaxDifferenceCsvAsync(TaxReportQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExportBookTaxDifferenceCsvAsync(query, cancellationToken);
    }
}

/// <summary>Tax books follow their asset's company; tax reports cover one visible company (or every company when the user isn't limited).</summary>
public sealed class CompanyScopedAssetTaxBookService(IAssetTaxBookService inner, CompanyGuard guard) : IAssetTaxBookService
{
    public async Task<AssetBooksDto> GetBooksAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<FixedAsset>(assetId, "Asset", cancellationToken);
        return await inner.GetBooksAsync(assetId, cancellationToken);
    }

    public async Task<AssetTaxBookDto> SaveBookAsync(Guid assetId, SaveAssetTaxBookRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<FixedAsset>(assetId, "Asset", cancellationToken);
        return await inner.SaveBookAsync(assetId, request, cancellationToken);
    }

    public async Task DeleteBookAsync(Guid assetId, AssetBookKind book, CancellationToken cancellationToken = default)
    {
        await guard.EnsureAsync<FixedAsset>(assetId, "Asset", cancellationToken);
        await inner.DeleteBookAsync(assetId, book, cancellationToken);
    }

    public Task<TaxSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => inner.GetSettingsAsync(cancellationToken);

    public async Task<TaxDepreciationReportDto> GetDepreciationReportAsync(TaxReportQuery query, CancellationToken cancellationToken = default) =>
        await inner.GetDepreciationReportAsync(await ScopeAsync(query, cancellationToken), cancellationToken);

    public async Task<BookTaxDifferenceDto> GetBookTaxDifferenceAsync(TaxReportQuery query, CancellationToken cancellationToken = default) =>
        await inner.GetBookTaxDifferenceAsync(await ScopeAsync(query, cancellationToken), cancellationToken);

    public async Task<Form4562SummaryDto> GetForm4562Async(TaxReportQuery query, CancellationToken cancellationToken = default) =>
        await inner.GetForm4562Async(await ScopeAsync(query, cancellationToken), cancellationToken);

    public async Task<MidQuarterTestDto> GetMidQuarterTestAsync(TaxReportQuery query, CancellationToken cancellationToken = default) =>
        await inner.GetMidQuarterTestAsync(await ScopeAsync(query, cancellationToken), cancellationToken);

    public async Task<AssetExportFile> ExportDepreciationReportCsvAsync(TaxReportQuery query, CancellationToken cancellationToken = default) =>
        await inner.ExportDepreciationReportCsvAsync(await ScopeAsync(query, cancellationToken), cancellationToken);

    public async Task<AssetExportFile> ExportBookTaxDifferenceCsvAsync(TaxReportQuery query, CancellationToken cancellationToken = default) =>
        await inner.ExportBookTaxDifferenceCsvAsync(await ScopeAsync(query, cancellationToken), cancellationToken);

    private async Task<TaxReportQuery> ScopeAsync(TaxReportQuery query, CancellationToken ct)
    {
        if ((await guard.ViewAsync(ct)).Visible is not { } visible)
        {
            return query;
        }

        if (query.CompanyId is { } company)
        {
            return visible.Contains(company) ? query : throw WorkbenchNotFoundException.For("Company", company);
        }

        return visible.Count == 1 ? query with { CompanyId = visible.First() } : throw new ArgumentException("Choose one of your companies for the tax report.");
    }
}
