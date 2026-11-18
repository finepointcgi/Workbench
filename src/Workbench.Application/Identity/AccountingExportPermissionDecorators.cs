using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.AccountingExport;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

// Accounting exports and their mappings: "Export to accounting software" (Finance, Admin). The file holds the whole ledger, so
// there is no narrower reader.

public static class AccountingExportPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchAccountingExportPermissions(this IServiceCollection services)
    {
        services.Decorate<IAccountingExportService, PermissionCheckingAccountingExportService>();
        services.Decorate<IAccountingMappingService, PermissionCheckingAccountingMappingService>();
        return services;
    }
}

public sealed class PermissionCheckingAccountingExportService(IAccountingExportService inner, AccessControl access) : IAccountingExportService
{
    private const string What = "export to accounting software";

    public async Task<IReadOnlyList<AccountingFormatDto>> ListFormatsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.GlExport, What, cancellationToken);
        return await inner.ListFormatsAsync(cancellationToken);
    }

    public async Task<AccountingExportPreviewDto> PreviewAsync(AccountingFileRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.GlExport, What, cancellationToken);
        return await inner.PreviewAsync(request, cancellationToken);
    }

    public async Task<AccountingExportResultDto> ExportAsync(AccountingFileRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.GlExport, What, cancellationToken);
        return await inner.ExportAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<AccountingExportRunDto>> ListRunsAsync(AccountingExportKind? kind = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.GlExport, What, cancellationToken);
        return await inner.ListRunsAsync(kind, limit, cancellationToken);
    }

    public async Task<AccountingExportResultDto> DownloadRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.GlExport, What, cancellationToken);
        return await inner.DownloadRunAsync(runId, cancellationToken);
    }
}

public sealed class PermissionCheckingAccountingMappingService(IAccountingMappingService inner, AccessControl access) : IAccountingMappingService
{
    private const string What = "map accounts for accounting exports";

    public async Task<AccountingMappingSetDto> GetAsync(AccountingTarget target, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.GlExport, What, cancellationToken);
        return await inner.GetAsync(target, cancellationToken);
    }

    public async Task<AccountingMappingSetDto> SaveAsync(AccountingTarget target, SaveAccountingMappingsRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.GlExport, What, cancellationToken);
        return await inner.SaveAsync(target, request, cancellationToken);
    }
}
