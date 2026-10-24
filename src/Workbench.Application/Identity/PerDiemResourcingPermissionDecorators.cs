using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Resourcing;
using Workbench.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Identity;
using Workbench.Contracts.PerDiem;
using Workbench.Contracts.Resourcing;
using Workbench.Domain.Resourcing;

namespace Workbench.Application.Identity;

// Per diem: anyone signed in reads the rate tables (travelers pick destinations); Finance or Admin import them. The travel
// per diem service enforces its own owner/supervisor rules (like expense reports). Resource planning: see ResourcingAccess.
// The allocation-based ETC prices allocations at cost rates, so it follows the project financials rule.
// The resource-loaded plan reads like staffing; its cost follows the project financials rule and its burden the burdened
// cost rule (the project's manager, or "See burdened cost"); moving tasks also needs Editor on the plan.

public static class PerDiemResourcingPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchPerDiemResourcingPermissions(this IServiceCollection services)
    {
        services.Decorate<IPerDiemRateService, PermissionCheckingPerDiemRateService>();
        services.Decorate<ITravelPerDiemService, PermissionCheckingTravelPerDiemService>();
        services.Decorate<IResourceAllocationService, PermissionCheckingResourceAllocationService>();
        services.Decorate<IResourcePlanningService, PermissionCheckingResourcePlanningService>();
        services.Decorate<IAllocationForecastService, PermissionCheckingAllocationForecastService>();
        services.Decorate<IProjectStaffingService, PermissionCheckingProjectStaffingService>();
        services.Decorate<IStaffingAssignmentService, PermissionCheckingStaffingAssignmentService>();
        services.Decorate<IResourcePlanService, PermissionCheckingResourcePlanService>();
        return services;
    }
}

public sealed class PermissionCheckingPerDiemRateService(IPerDiemRateService inner, AccessControl access) : IPerDiemRateService
{
    private const string What = "import per diem rates";

    public Task<PerDiemRateTableDto> GetTableAsync(PerDiemRateQuery? query = null, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetTableAsync(query, cancellationToken);
    }

    public Task<IReadOnlyList<PerDiemLocationDto>> ListLocationsAsync(int fiscalYear, PerDiemTable? kind = null, string? search = null, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListLocationsAsync(fiscalYear, kind, search, cancellationToken);
    }

    public Task<ResolvedPerDiemRateDto> LookupAsync(PerDiemLocationDto location, DateOnly date, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.LookupAsync(location, date, cancellationToken);
    }

    public async Task<PerDiemImportResultDto> ImportFileAsync(ImportPerDiemFileRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SetupPerDiem, What, cancellationToken);
        return await inner.ImportFileAsync(request, cancellationToken);
    }

    public async Task<PerDiemImportResultDto> ImportFromGsaApiAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SetupPerDiem, What, cancellationToken);
        return await inner.ImportFromGsaApiAsync(fiscalYear, cancellationToken);
    }

    public async Task<PerDiemImportResultDto> InstallSampleRatesAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.SetupPerDiem, What, cancellationToken);
        return await inner.InstallSampleRatesAsync(fiscalYear, cancellationToken);
    }
}

public sealed class PermissionCheckingTravelPerDiemService(ITravelPerDiemService inner, AccessControl access) : ITravelPerDiemService
{
    public Task<TravelPerDiemDto> GetAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetAsync(expenseReportId, cancellationToken);
    }

    public Task<TravelPerDiemDto> SaveAsync(Guid expenseReportId, SaveTravelPerDiemRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.SaveAsync(expenseReportId, request, cancellationToken);
    }

    public Task<TravelPerDiemDto> ApplyToReportAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ApplyToReportAsync(expenseReportId, cancellationToken);
    }

    public Task RemoveAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.RemoveAsync(expenseReportId, cancellationToken);
    }
}

public sealed class PermissionCheckingResourceAllocationService(IResourceAllocationService inner, ResourcingAccess rules, IWorkbenchDbContextFactory dbFactory)
    : IResourceAllocationService
{
    public async Task<IReadOnlyList<AllocationDto>> ListAsync(AllocationQuery query, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        if (query.ProjectId is { } projectId)
        {
            await rules.Visibility.RequireVisibleAsync(projectId, cancellationToken);
        }

        return await rules.Visibility.FilterAsync(await inner.ListAsync(query, cancellationToken), a => a.ProjectId, cancellationToken);
    }

    public Task<IReadOnlyList<AllocationDto>> ListMineAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        return inner.ListMineAsync(from, to, cancellationToken);
    }

    public async Task<IReadOnlyList<AllocationDto>> CreateAsync(SaveAllocationRequest request, CancellationToken cancellationToken = default)
    {
        await rules.RequireManageProjectAsync(request.ProjectId, cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<AllocationDto> UpdateAsync(Guid allocationId, SaveAllocationRequest request, CancellationToken cancellationToken = default)
    {
        await rules.RequireManageProjectAsync(await ProjectOfAsync(allocationId, cancellationToken), cancellationToken);
        await rules.RequireManageProjectAsync(request.ProjectId, cancellationToken);
        return await inner.UpdateAsync(allocationId, request, cancellationToken);
    }

    public async Task DeleteAsync(Guid allocationId, CancellationToken cancellationToken = default)
    {
        await rules.RequireManageProjectAsync(await ProjectOfAsync(allocationId, cancellationToken), cancellationToken);
        await inner.DeleteAsync(allocationId, cancellationToken);
    }

    public async Task<GenerateAllocationsResultDto> GenerateFromPlanAsync(Guid projectId, GenerateAllocationsRequest? request = null, CancellationToken cancellationToken = default)
    {
        await rules.RequireManageProjectAsync(projectId, cancellationToken);
        return await inner.GenerateFromPlanAsync(projectId, request, cancellationToken);
    }

    public Task<bool> CanManageAsync(Guid projectId, CancellationToken cancellationToken = default) => inner.CanManageAsync(projectId, cancellationToken);

    private async Task<Guid> ProjectOfAsync(Guid allocationId, CancellationToken ct)
    {
        rules.Access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<ResourceAllocation>().AsNoTracking().Where(a => a.Id == allocationId).Select(a => (Guid?)a.ProjectId).FirstOrDefaultAsync(ct)
            ?? throw WorkbenchNotFoundException.For("Allocation", allocationId);
    }
}

public sealed class PermissionCheckingResourcePlanningService(IResourcePlanningService inner, ResourcingAccess rules) : IResourcePlanningService
{
    public Task<IReadOnlyList<CapacityWeekDto>> GetCapacityAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid>? employeeIds = null, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        return inner.GetCapacityAsync(from, to, employeeIds, cancellationToken);
    }

    public async Task<HeatMapDto> GetHeatMapAsync(HeatMapQuery query, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        if (query.ProjectId is { } projectId)
        {
            await rules.Visibility.RequireVisibleAsync(projectId, cancellationToken); // Who's on a project is the project's data.
        }

        return await inner.GetHeatMapAsync(query, cancellationToken);
    }

    public async Task<ProjectStaffingDto> GetProjectStaffingAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await rules.RequireReadProjectAsync(projectId, cancellationToken);
        return await inner.GetProjectStaffingAsync(projectId, cancellationToken);
    }

    public Task<UtilizationDto> GetUtilizationAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        return inner.GetUtilizationAsync(from, to, cancellationToken);
    }

    public async Task<IReadOnlyList<OpenDemandDto>> ListOpenDemandAsync(Guid? projectId = null, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        if (projectId is { } id)
        {
            await rules.RequireReadProjectAsync(id, cancellationToken);
        }

        return await rules.Visibility.FilterAsync(await inner.ListOpenDemandAsync(projectId, cancellationToken), d => d.ProjectId, cancellationToken);
    }

    public Task<IReadOnlyList<AvailablePersonDto>> FindAvailablePeopleAsync(FindPeopleQuery query, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        return inner.FindAvailablePeopleAsync(query, cancellationToken);
    }
}

/// <summary>Budget against staffing: Finance, administrators, resource managers and the project's manager (like project staffing).</summary>
public sealed class PermissionCheckingProjectStaffingService(IProjectStaffingService inner, ResourcingAccess rules) : IProjectStaffingService
{
    public async Task<ProjectStaffingPlanDto> GetPlanAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await rules.RequireReadProjectAsync(projectId, cancellationToken);
        return await inner.GetPlanAsync(projectId, cancellationToken);
    }

    public async Task<ProjectSetupProgressDto> GetSetupProgressAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await rules.RequireReadProjectAsync(projectId, cancellationToken);
        return await inner.GetSetupProgressAsync(projectId, cancellationToken);
    }

    public async Task<StaffingCheckDto> CheckAsync(StaffingCheckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await rules.RequireReadProjectAsync(request.ProjectId, cancellationToken);
        return await inner.CheckAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<StaffingCandidateDto>> ListCandidatesAsync(Guid projectId, Guid? laborCategoryId, CancellationToken cancellationToken = default)
    {
        await rules.RequireReadProjectAsync(projectId, cancellationToken);
        return await inner.ListCandidatesAsync(projectId, laborCategoryId, cancellationToken);
    }
}

/// <summary>
/// Assigning people by total hours, like allocations: Finance, administrators, resource managers and the project's manager
/// read a project's assignments; resource managers, administrators and the project's manager change them. Everyone reads
/// their own hours left.
/// </summary>
public sealed class PermissionCheckingStaffingAssignmentService(IStaffingAssignmentService inner, ResourcingAccess rules, IWorkbenchDbContextFactory dbFactory)
    : IStaffingAssignmentService
{
    public async Task<ProjectAssignmentsDto> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await rules.RequireReadProjectAsync(projectId, cancellationToken);
        return await inner.ListAsync(projectId, cancellationToken);
    }

    public async Task<AssignmentPreviewDto> PreviewAsync(SaveAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireManageAsync(request, cancellationToken);
        return await inner.PreviewAsync(request, cancellationToken);
    }

    public async Task<StaffingAssignmentDto> SaveAsync(SaveAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireManageAsync(request, cancellationToken);
        return await inner.SaveAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<StaffingAssignmentDto>> SaveManyAsync(Guid projectId, IReadOnlyList<SaveAssignmentRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        await rules.RequireManageProjectAsync(projectId, cancellationToken);
        foreach (var id in requests.Select(r => r.Id).OfType<Guid>().Distinct())
        {
            await rules.RequireManageProjectAsync(await ProjectOfAsync(id, cancellationToken), cancellationToken);
        }

        return await inner.SaveManyAsync(projectId, requests, cancellationToken);
    }

    public async Task DeleteAsync(Guid assignmentId, CancellationToken cancellationToken = default)
    {
        await rules.RequireManageProjectAsync(await ProjectOfAsync(assignmentId, cancellationToken), cancellationToken);
        await inner.DeleteAsync(assignmentId, cancellationToken);
    }

    public async Task<StaffingAssignmentDto> SetWeekAsync(Guid assignmentId, DateOnly weekStart, decimal hours, CancellationToken cancellationToken = default)
    {
        await rules.RequireManageProjectAsync(await ProjectOfAsync(assignmentId, cancellationToken), cancellationToken);
        return await inner.SetWeekAsync(assignmentId, weekStart, hours, cancellationToken);
    }

    public async Task<AssignmentPastePreviewDto> PreviewPasteAsync(Guid projectId, string text, CancellationToken cancellationToken = default)
    {
        await rules.RequireManageProjectAsync(projectId, cancellationToken);
        return await inner.PreviewPasteAsync(projectId, text, cancellationToken);
    }

    public async Task<AssignmentPastePreviewDto> PreviewCopyTeamAsync(Guid projectId, Guid fromProjectId, CancellationToken cancellationToken = default)
    {
        await rules.RequireManageProjectAsync(projectId, cancellationToken);
        await rules.RequireReadProjectAsync(fromProjectId, cancellationToken); // the other project's team is its staffing
        return await inner.PreviewCopyTeamAsync(projectId, fromProjectId, cancellationToken);
    }

    public Task<IReadOnlyList<AssignmentHoursLeftDto>> ListMyHoursLeftAsync(DateOnly from, DateOnly to, Guid? excludeTimesheetId = null, CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        return inner.ListMyHoursLeftAsync(from, to, excludeTimesheetId, cancellationToken);
    }

    /// <summary>The project asked for, and for an edit the project the assignment is on now.</summary>
    private async Task RequireManageAsync(SaveAssignmentRequest request, CancellationToken ct)
    {
        await rules.RequireManageProjectAsync(request.ProjectId, ct);
        if (request.Id is { } id)
        {
            await rules.RequireManageProjectAsync(await ProjectOfAsync(id, ct), ct);
        }
    }

    private async Task<Guid> ProjectOfAsync(Guid assignmentId, CancellationToken ct)
    {
        rules.Access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<StaffingAssignment>().AsNoTracking().Where(a => a.Id == assignmentId).Select(a => (Guid?)a.ProjectId).FirstOrDefaultAsync(ct)
            ?? throw WorkbenchNotFoundException.For("Assignment", assignmentId);
    }
}

public sealed class PermissionCheckingAllocationForecastService(IAllocationForecastService inner, ProjectAccess access) : IAllocationForecastService
{
    private const string What = "see project financials";

    public async Task<ProjectFinancialSummaryDto> GetProjectSummaryAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectFinancialsAsync(projectId, What, cancellationToken);
        return await inner.GetProjectSummaryAsync(projectId, budgetVersionId, cancellationToken);
    }

    public async Task<IReadOnlyList<WbsFinancialRowDto>> GetWbsFinancialsAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireProjectFinancialsAsync(projectId, What, cancellationToken);
        return await inner.GetWbsFinancialsAsync(projectId, budgetVersionId, cancellationToken);
    }
}

public sealed class PermissionCheckingResourcePlanService(IResourcePlanService inner, ResourcingAccess rules, ProjectAccess projects, IWorkbenchDbContextFactory dbFactory)
    : IResourcePlanService
{
    public async Task<ResourcePlanDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await rules.RequireReadProjectAsync(projectId, cancellationToken);
        var plan = await inner.GetAsync(projectId, cancellationToken);
        var (cost, burden) = await CostRightsAsync(projectId, cancellationToken);
        var canChange = await rules.CanManageProjectAsync(projectId, cancellationToken);
        var canMove = canChange && plan.CanMoveTasks && await rules.Access.GetSheetRoleAsync(plan.SheetId, cancellationToken) >= WorkspaceRole.Editor;
        return plan with
        {
            ShowsCost = cost,
            CostIncludesBurden = cost && burden && plan.CostIncludesBurden,
            CanChange = canChange,
            CanMoveTasks = canMove,
            Tasks = plan.Tasks.Select(t => Mask(t, cost, burden)).ToList(),
            NotOnATask = plan.NotOnATask.Select(p => Mask(p, cost, burden)).ToList(),
            Totals = Mask(plan.Totals, cost, burden),
            Notes = cost ? plan.Notes : plan.Notes.Where(n => !n.Contains("cost rate", StringComparison.Ordinal)).ToList(),
        };
    }

    public async Task<PlanTryDto> TryAsync(Guid projectId, IReadOnlyList<PlanChange> changes, CancellationToken cancellationToken = default)
    {
        await rules.RequireReadProjectAsync(projectId, cancellationToken);
        var tried = await inner.TryAsync(projectId, changes, cancellationToken);
        var (cost, burden) = await CostRightsAsync(projectId, cancellationToken);
        return tried with
        {
            Before = Mask(tried.Before, cost, burden),
            After = Mask(tried.After, cost, burden),
            Tasks = tried.Tasks.Select(t => Mask(t, cost, burden)).ToList(),
            NotOnATask = tried.NotOnATask.Select(p => Mask(p, cost, burden)).ToList(),
        };
    }

    public async Task<PlanAppliedDto> ApplyAsync(Guid projectId, IReadOnlyList<PlanChange> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        await rules.RequireManageProjectAsync(projectId, cancellationToken);
        if (changes.Any(c => c.Kind == PlanChangeKind.MoveTask))
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            if (await db.Set<Domain.ProjectAccounting.Project>().AsNoTracking().Where(p => p.Id == projectId).Select(p => p.SheetId).FirstOrDefaultAsync(cancellationToken) is { } sheetId)
            {
                await rules.Access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
            }
        }

        return await inner.ApplyAsync(projectId, changes, cancellationToken);
    }

    public async Task<IReadOnlyList<OverbookingFixDto>> ListFixesAsync(Guid projectId, Guid assignmentId, CancellationToken cancellationToken = default)
    {
        await rules.RequireManageProjectAsync(projectId, cancellationToken);
        return await inner.ListFixesAsync(projectId, assignmentId, cancellationToken);
    }

    /// <summary>Cost: those who may read the project's financials. Burden on top: its manager, or "See burdened cost".</summary>
    private async Task<(bool Cost, bool Burden)> CostRightsAsync(Guid projectId, CancellationToken ct)
    {
        if (!await projects.CanReadProjectFinancialsAsync(projectId, ct))
        {
            return (false, false);
        }

        var burden = await projects.ManagesProjectAsync(projectId, ct) || await rules.Access.HasForProjectAsync(Permission.FieldsBurdenedCost, projectId, ct);
        return (true, burden);
    }

    private static PlanPersonDto Mask(PlanPersonDto p, bool cost, bool burden) =>
        p with { DirectCost = cost ? p.DirectCost : null, BurdenedCost = cost && burden ? p.BurdenedCost : null };

    private static PlanTaskRowDto Mask(PlanTaskRowDto t, bool cost, bool burden) => t with
    {
        DirectCost = cost ? t.DirectCost : null,
        BurdenedCost = cost && burden ? t.BurdenedCost : null,
        People = t.People.Select(p => Mask(p, cost, burden)).ToList(),
    };

    private static PlanTotalsDto Mask(PlanTotalsDto t, bool cost, bool burden) => t with
    {
        DirectCost = cost ? t.DirectCost : null,
        BurdenedCost = cost && burden ? t.BurdenedCost : null,
        BudgetAmount = cost ? t.BudgetAmount : null,
        Categories = t.Categories.Select(c => c with
        {
            DirectCost = cost ? c.DirectCost : null,
            BurdenedCost = cost && burden ? c.BurdenedCost : null,
            BudgetAmount = cost ? c.BudgetAmount : null,
        }).ToList(),
    };
}
