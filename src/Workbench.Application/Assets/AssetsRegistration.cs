using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Accounting;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Assets;

namespace Workbench.Application.Assets;

public static class AssetsRegistration
{
    /// <summary>
    /// Fixed assets: the register, capitalization, depreciation runs, disposals and reports, plus the "depreciation posted"
    /// period-close check. Binds <see cref="AssetOptions"/> from config <c>Assets</c>. Call after AddWorkbenchAccounting and
    /// before AddWorkbenchIdentity, which adds the permission decorators (<c>AddWorkbenchAssetPermissions</c>).
    /// </summary>
    public static IServiceCollection AddWorkbenchAssets(this IServiceCollection services)
    {
        services.AddOptions<AssetOptions>().BindConfiguration(AssetOptions.Section);
        services.AddScoped<IAssetService, AssetService>();
        services.AddScoped<IDepreciationService, DepreciationService>();
        services.AddScoped<IAssetReportService, AssetReportService>();
        services.Decorate<IAssetReportService, SecurityLoggingAssetReportService>(); // export.assets events; the permission decorator wraps it.
        services.AddScoped<IPeriodCloseCheck, DepreciationCloseCheck>();
        return services;
    }
}
