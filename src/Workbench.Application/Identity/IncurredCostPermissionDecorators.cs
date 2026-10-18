using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Security;
using Workbench.Contracts.Security;
using Workbench.Contracts.IncurredCost;

namespace Workbench.Application.Identity;

/// <summary>Incurred cost submissions are Finance or Admin only: generating, editing, the workflow and the export (logged as an export event).</summary>
public static class IncurredCostPermissionRegistration
{
    public static IServiceCollection AddWorkbenchIncurredCostPermissions(this IServiceCollection services)
    {
        services.Decorate<IIncurredCostService, PermissionCheckingIncurredCostService>();
        return services;
    }
}

public sealed class PermissionCheckingIncurredCostService(IIncurredCostService inner, AccessControl access, SecurityEventLog log) : IIncurredCostService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireFinanceAsync("work with incurred cost submissions", ct);

    public async Task<IReadOnlyList<IncurredCostSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListAsync(cancellationToken);
    }

    public async Task<IncurredCostPackageDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetAsync(id, cancellationToken);
    }

    public async Task<IncurredCostPackageDto> CreateAsync(CreateIncurredCostRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<IncurredCostPackageDto> RegenerateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.RegenerateAsync(id, cancellationToken);
    }

    public async Task<IncurredCostPackageDto> SaveInputsAsync(Guid id, SaveIceInputsRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveInputsAsync(id, request, cancellationToken);
    }

    public async Task<IncurredCostPackageDto> MarkReviewedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.MarkReviewedAsync(id, cancellationToken);
    }

    public async Task<IncurredCostPackageDto> ReopenAsync(Guid id, ReopenIncurredCostRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ReopenAsync(id, request, cancellationToken);
    }

    public async Task<IncurredCostPackageDto> CertifyAsync(Guid id, CertifyIncurredCostRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CertifyAsync(id, request, cancellationToken);
    }

    public async Task<IncurredCostPackageDto> SubmitAsync(Guid id, SubmitIncurredCostRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SubmitAsync(id, request, cancellationToken);
    }

    public async Task<IncurredCostExportFile> ExportWorkbookAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        var file = await inner.ExportWorkbookAsync(id, cancellationToken);
        await log.RecordAsync(SecurityEventTypes.ExportFinancialCsv, file.FileName, detail: "Incurred cost submission workbook (.xlsx)", cancellationToken: cancellationToken);
        return file;
    }
}
