using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Evm;
using Workbench.Contracts;
using Workbench.Contracts.Evm;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

// Earned value follows the project financials rule: Finance, Admin or the project's manager read and status it, write
// variance analyses and request baseline changes. Approving or rejecting a baseline change and reopening a closed status
// period are Finance's (Finance or Admin), so a PM can't both request and approve a change to their own baseline.

public static class EvmPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchEvmPermissions(this IServiceCollection services)
    {
        services.AddScoped<EvmAccess>();
        services.Decorate<IEvmService, PermissionCheckingEvmService>();
        services.Decorate<IEvmChangeRequestService, PermissionCheckingEvmChangeRequestService>();
        services.Decorate<IEvmExportService, PermissionCheckingEvmExportService>();
        return services;
    }
}

public sealed class EvmAccess(IWorkbenchDbContextFactory dbFactory, ProjectAccess projects)
{
    public AccessControl Access => projects.Access;

    public Task RequireProjectAsync(Guid projectId, string what, CancellationToken ct) => projects.RequireProjectFinancialsAsync(projectId, what, ct);

    public async Task RequireWorkPackageAsync(Guid workPackageId, string what, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var projectId = await EvmLookup.ProjectOfWorkPackageAsync(db, workPackageId, ct) ?? throw WorkbenchNotFoundException.For("Work package", workPackageId);
        await RequireProjectAsync(projectId, what, ct);
    }

    public async Task RequireMilestoneAsync(Guid milestoneId, string what, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var projectId = await EvmLookup.ProjectOfMilestoneAsync(db, milestoneId, ct) ?? throw WorkbenchNotFoundException.For("Milestone", milestoneId);
        await RequireProjectAsync(projectId, what, ct);
    }

    public async Task RequireChangeRequestAsync(Guid changeRequestId, string what, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var projectId = await EvmLookup.ProjectOfChangeRequestAsync(db, changeRequestId, ct) ?? throw WorkbenchNotFoundException.For("Change request", changeRequestId);
        await RequireProjectAsync(projectId, what, ct);
    }
}

public sealed class PermissionCheckingEvmService(IEvmService inner, EvmAccess access) : IEvmService
{
    private const string Read = "see this project's earned value";
    private const string Write = "status this project's earned value";

    public async Task<EvmBaselineDto?> GetBaselineAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectAsync(projectId, Read, cancellationToken);
        return await inner.GetBaselineAsync(projectId, cancellationToken);
    }

    public async Task<EvmBaselineDto> CreateBaselineAsync(Guid projectId, CreateEvmBaselineRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectAsync(projectId, "build this project's performance measurement baseline", cancellationToken);
        return await inner.CreateBaselineAsync(projectId, request, cancellationToken);
    }

    public async Task<EvmBaselineDto> UpdateWorkPackageAsync(Guid workPackageId, UpdateWorkPackageRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWorkPackageAsync(workPackageId, "change this project's work packages", cancellationToken);
        return await inner.UpdateWorkPackageAsync(workPackageId, request, cancellationToken);
    }

    public async Task<EvmBaselineDto> SetThresholdsAsync(Guid projectId, SetEvmThresholdsRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectAsync(projectId, "change this project's variance thresholds", cancellationToken);
        return await inner.SetThresholdsAsync(projectId, request, cancellationToken);
    }

    public async Task<EvmBaselineDto> SetProgressAsync(Guid workPackageId, SetWorkPackageProgressRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWorkPackageAsync(workPackageId, Write, cancellationToken);
        return await inner.SetProgressAsync(workPackageId, request, cancellationToken);
    }

    public async Task<EvmBaselineDto> SetMilestoneCompletionAsync(Guid milestoneId, SetMilestoneCompletionRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireMilestoneAsync(milestoneId, Write, cancellationToken);
        return await inner.SetMilestoneCompletionAsync(milestoneId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<EvmPeriodRefDto>> ListPeriodsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectAsync(projectId, Read, cancellationToken);
        return await inner.ListPeriodsAsync(projectId, cancellationToken);
    }

    public async Task<EvmStatusDto> GetStatusAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectAsync(projectId, Read, cancellationToken);
        return await inner.GetStatusAsync(projectId, fiscalPeriodId, cancellationToken);
    }

    public async Task<EvmSeriesDto> GetSeriesAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectAsync(projectId, Read, cancellationToken);
        return await inner.GetSeriesAsync(projectId, cancellationToken);
    }

    public async Task<EvmPeriodRefDto> ClosePeriodAsync(Guid projectId, Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectAsync(projectId, "close this project's status periods", cancellationToken);
        return await inner.ClosePeriodAsync(projectId, fiscalPeriodId, cancellationToken);
    }

    public async Task<EvmPeriodRefDto> ReopenPeriodAsync(Guid projectId, Guid fiscalPeriodId, ReopenEvmPeriodRequest request, CancellationToken cancellationToken = default)
    {
        await access.Access.RequireAsync(Permission.EvmManage, "reopen a closed earned value period", cancellationToken);
        return await inner.ReopenPeriodAsync(projectId, fiscalPeriodId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<EvmVarianceDto>> ListVariancesAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectAsync(projectId, Read, cancellationToken);
        return await inner.ListVariancesAsync(projectId, fiscalPeriodId, cancellationToken);
    }

    public async Task<EvmVarianceDto> SaveVarianceAsync(Guid projectId, Guid fiscalPeriodId, Guid controlAccountWbsId, SaveVarianceRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectAsync(projectId, "write this project's variance analyses", cancellationToken);
        return await inner.SaveVarianceAsync(projectId, fiscalPeriodId, controlAccountWbsId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingEvmChangeRequestService(IEvmChangeRequestService inner, EvmAccess access) : IEvmChangeRequestService
{
    public async Task<IReadOnlyList<EvmChangeRequestDto>> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectAsync(projectId, "see this project's baseline changes", cancellationToken);
        return await inner.ListAsync(projectId, cancellationToken);
    }

    public async Task<EvmChangeRequestDto> GetAsync(Guid changeRequestId, CancellationToken cancellationToken = default)
    {
        await access.RequireChangeRequestAsync(changeRequestId, "see this project's baseline changes", cancellationToken);
        return await inner.GetAsync(changeRequestId, cancellationToken);
    }

    public async Task<EvmChangeRequestDto> CreateAsync(Guid projectId, CreateChangeRequestRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectAsync(projectId, "request a baseline change", cancellationToken);
        return await inner.CreateAsync(projectId, request, cancellationToken);
    }

    public async Task<EvmChangeRequestDto> ApproveAsync(Guid changeRequestId, DecideChangeRequestRequest request, CancellationToken cancellationToken = default)
    {
        await access.Access.RequireAsync(Permission.EvmManage, "approve baseline changes", cancellationToken);
        return await inner.ApproveAsync(changeRequestId, request, cancellationToken);
    }

    public async Task<EvmChangeRequestDto> RejectAsync(Guid changeRequestId, DecideChangeRequestRequest request, CancellationToken cancellationToken = default)
    {
        await access.Access.RequireAsync(Permission.EvmManage, "reject baseline changes", cancellationToken);
        return await inner.RejectAsync(changeRequestId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingEvmExportService(IEvmExportService inner, EvmAccess access) : IEvmExportService
{
    public async Task<EvmExportFile> ExportFormat1Async(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectAsync(projectId, "export this project's earned value", cancellationToken);
        return await inner.ExportFormat1Async(projectId, fiscalPeriodId, cancellationToken);
    }

    public async Task<EvmExportFile> ExportFormat5Async(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectAsync(projectId, "export this project's earned value", cancellationToken);
        return await inner.ExportFormat5Async(projectId, fiscalPeriodId, cancellationToken);
    }
}
