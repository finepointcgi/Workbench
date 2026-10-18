using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Revenue;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Revenue;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Identity;

/// <summary>
/// Revenue recognition: Finance and administrators set up methods, approve estimates, run revenue and read every report.
/// A project's manager reads that project's revenue, submits its estimates at completion and records its deliveries, and
/// sees their own projects (and those projects' contracts) in the reports.
/// </summary>
public static class RevenuePermissionRegistration
{
    public static IServiceCollection AddWorkbenchRevenuePermissions(this IServiceCollection services)
    {
        services.Decorate<IRevenueSetupService, PermissionCheckingRevenueSetupService>();
        services.Decorate<IRevenueRunService, PermissionCheckingRevenueRunService>();
        services.Decorate<IRevenueReportService, PermissionCheckingRevenueReportService>();
        return services;
    }
}

public sealed class PermissionCheckingRevenueSetupService(IRevenueSetupService inner, ProjectAccess projects) : IRevenueSetupService
{
    private AccessControl Access => projects.Access;

    private Task RequireFinanceAsync(CancellationToken ct) => Access.RequireFinanceAsync("manage revenue recognition", ct);

    public async Task<IReadOnlyList<RevenueProjectSummaryDto>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.ListProjectsAsync(cancellationToken);
    }

    public async Task<ProjectRevenueDto> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "see a project's revenue", cancellationToken);
        var result = await inner.GetProjectAsync(projectId, cancellationToken);
        return result with { CanManage = (await Access.GetOrgRolesAsync(cancellationToken)).IsFinance() };
    }

    public async Task<RevenueSetupDto> SaveSetupAsync(SaveRevenueSetupRequest request, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.SaveSetupAsync(request, cancellationToken);
    }

    public async Task DeleteSetupAsync(Guid setupId, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        await inner.DeleteSetupAsync(setupId, cancellationToken);
    }

    public async Task<IReadOnlyList<EstimateDto>> ListEstimatesAsync(EstimateState? status = null, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.ListEstimatesAsync(status, cancellationToken);
    }

    public async Task<EstimateDto> SubmitEstimateAsync(SubmitEstimateRequest request, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(request.ProjectId, "submit an estimate at completion", cancellationToken);
        return await inner.SubmitEstimateAsync(request, cancellationToken);
    }

    public async Task<EstimateDto> ApproveEstimateAsync(Guid estimateId, DecideEstimateRequest request, CancellationToken cancellationToken = default)
    {
        await Access.RequireFinanceAsync("approve estimates at completion", cancellationToken);
        return await inner.ApproveEstimateAsync(estimateId, request, cancellationToken);
    }

    public async Task<EstimateDto> RejectEstimateAsync(Guid estimateId, DecideEstimateRequest request, CancellationToken cancellationToken = default)
    {
        await Access.RequireFinanceAsync("reject estimates at completion", cancellationToken);
        return await inner.RejectEstimateAsync(estimateId, request, cancellationToken);
    }

    public async Task<RevenueDeliveryDto> RecordDeliveryAsync(RecordDeliveryRequest request, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(request.ProjectId, "record deliveries", cancellationToken);
        return await inner.RecordDeliveryAsync(request, cancellationToken);
    }

    /// <summary>Project ids the user manages, or null for Finance and administrators.</summary>
    internal static async Task<IReadOnlySet<Guid>?> ManagedProjectIdsAsync(AccessControl access, IWorkbenchDbContextFactory dbFactory, CancellationToken ct)
    {
        access.RequireSignedIn();
        if ((await access.GetOrgRolesAsync(ct)).IsFinance())
        {
            return null;
        }

        if (await access.GetEmployeeIdAsync(ct) is not { } me)
        {
            return new HashSet<Guid>();
        }

        await using var db = await dbFactory.CreateAsync(ct);
        return (await db.Set<Project>().AsNoTracking().Where(p => p.ProjectManagerId == me).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
    }

}

public sealed class PermissionCheckingRevenueRunService(IRevenueRunService inner, AccessControl access) : IRevenueRunService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireFinanceAsync("run revenue recognition", ct);

    public async Task<RevenuePreviewDto> PreviewAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.PreviewAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<PostRevenueRunResultDto> PostAsync(Guid fiscalPeriodId, PostRevenueRunRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.PostAsync(fiscalPeriodId, request, cancellationToken);
    }

    public async Task<RevenueRunDto> ReverseAsync(Guid runId, ReverseRevenueRunRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ReverseAsync(runId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<RevenueRunDto>> ListRunsAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListRunsAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<RevenueRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetRunAsync(runId, cancellationToken);
    }
}

/// <summary>Finance and administrators see every row; project managers see rows for their projects (and those projects' contracts); others are refused.</summary>
public sealed class PermissionCheckingRevenueReportService(IRevenueReportService inner, ProjectAccess projects, IWorkbenchDbContextFactory dbFactory) : IRevenueReportService
{
    private async Task<IReadOnlySet<Guid>?> ManagedAsync(CancellationToken ct)
    {
        var managed = await PermissionCheckingRevenueSetupService.ManagedProjectIdsAsync(projects.Access, dbFactory, ct);
        if (managed is { Count: 0 })
        {
            throw new WorkbenchForbiddenException("Revenue reports are for Finance, administrators and project managers.");
        }

        return managed;
    }

    public async Task<RevenueByPeriodDto> GetRevenueByPeriodAsync(RevenueByPeriodQuery query, CancellationToken cancellationToken = default)
    {
        var managed = await ManagedAsync(cancellationToken);
        var result = await inner.GetRevenueByPeriodAsync(query, cancellationToken);
        if (managed is null)
        {
            return result;
        }

        var visible = query.GroupBy == RevenueGrouping.Contract ? await projects.ManagedContractIdsAsync(cancellationToken) ?? new HashSet<Guid>() : managed;
        return RevenueReportService.Totals(result.Periods, result.Rows.Where(r => visible.Contains(r.Id)).ToList());
    }

    public async Task<RevenueVsBillingsDto> GetRevenueVsBillingsAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        var managed = await ManagedAsync(cancellationToken);
        var result = await inner.GetRevenueVsBillingsAsync(fiscalPeriodId, cancellationToken);
        return managed is null ? result : result with { Rows = result.Rows.Where(r => managed.Contains(r.ProjectId)).ToList() };
    }

    public async Task<BacklogDto> GetBacklogAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        var managed = await ManagedAsync(cancellationToken);
        var result = await inner.GetBacklogAsync(fiscalPeriodId, cancellationToken);
        if (managed is null)
        {
            return result;
        }

        var contracts = await projects.ManagedContractIdsAsync(cancellationToken) ?? new HashSet<Guid>();
        return result with { Rows = result.Rows.Where(r => contracts.Contains(r.ContractId)).ToList() };
    }
}
