using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Application.Identity;

/// <summary>
/// The project plan with Finance: anyone who may see the project's financials reads it (its manager, Finance, Admin).
/// Setting its terms, approving and returning it take "Set up every project" on the project (Finance, Admin); submitting
/// it takes managing the project (or that permission). The Can* flags say what the caller may do now.
/// </summary>
public sealed class PermissionCheckingProjectPlanService(IProjectPlanService inner, ProjectAccess projects) : IProjectPlanService
{
    private const string What = "see this project's plan";

    public async Task<ProjectPlanDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, What, cancellationToken);
        return await WithFlagsAsync(await inner.GetAsync(projectId, cancellationToken), cancellationToken);
    }

    public async Task<ProjectTermsDto> TermsFromContractAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        projects.Access.RequireSignedIn();
        var permissions = await projects.Access.GetPermissionsAsync(cancellationToken);
        if (!permissions.Has(Permission.SetupProjects) && !permissions.Has(Permission.ProjectsCreate))
        {
            throw new WorkbenchForbiddenException("Only Finance, administrators and project managers can start projects.");
        }

        return await inner.TermsFromContractAsync(contractId, cancellationToken);
    }

    public async Task<ProjectPlanDto> SaveTermsAsync(Guid projectId, SaveProjectTermsRequest request, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(projectId, "set a project's ceiling and funding", cancellationToken);
        return await WithFlagsAsync(await inner.SaveTermsAsync(projectId, request, cancellationToken), cancellationToken);
    }

    public async Task<ProjectPlanDto> SaveCategoriesAsync(Guid projectId, IReadOnlyList<Guid> laborCategoryIds, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(projectId, "choose a project's labor categories", cancellationToken);
        return await WithFlagsAsync(await inner.SaveCategoriesAsync(projectId, laborCategoryIds, cancellationToken), cancellationToken);
    }

    public async Task<ProjectPlanDto> HandToManagerAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(projectId, "hand a project to its manager", cancellationToken);
        return await WithFlagsAsync(await inner.HandToManagerAsync(projectId, cancellationToken), cancellationToken);
    }

    public async Task<ProjectPlanDto> SubmitAsync(Guid projectId, string? note = null, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, What, cancellationToken);
        if (!await projects.ManagesProjectAsync(projectId, cancellationToken) && !await IsFinanceAsync(projectId, cancellationToken))
        {
            throw new WorkbenchForbiddenException("Only the project's manager submits its plan.");
        }

        return await WithFlagsAsync(await inner.SubmitAsync(projectId, note, cancellationToken), cancellationToken);
    }

    public async Task<ProjectPlanDto> ApproveAsync(Guid projectId, string? comment = null, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(projectId, "approve project plans", cancellationToken);
        return await WithFlagsAsync(await inner.ApproveAsync(projectId, comment, cancellationToken), cancellationToken);
    }

    public async Task<ProjectPlanDto> ReturnAsync(Guid projectId, string comment, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(projectId, "return project plans", cancellationToken);
        return await WithFlagsAsync(await inner.ReturnAsync(projectId, comment, cancellationToken), cancellationToken);
    }

    private Task<bool> IsFinanceAsync(Guid projectId, CancellationToken ct) => projects.Access.HasForProjectAsync(Permission.SetupProjects, projectId, ct);

    private async Task RequireFinanceAsync(Guid projectId, string what, CancellationToken ct)
    {
        await projects.Visibility.RequireVisibleAsync(projectId, ct);
        if (!await IsFinanceAsync(projectId, ct))
        {
            throw await projects.Access.DenyAsync($"Only Finance and administrators can {what}.", ct);
        }
    }

    private async Task<ProjectPlanDto> WithFlagsAsync(ProjectPlanDto plan, CancellationToken ct)
    {
        var finance = await IsFinanceAsync(plan.ProjectId, ct);
        var manages = await projects.ManagesProjectAsync(plan.ProjectId, ct);
        var open = plan.Status is PlanApprovalStatus.Draft or PlanApprovalStatus.Returned || (plan.Status == PlanApprovalStatus.Approved && plan.ChangedSinceApproval);
        return plan with
        {
            CanEditTerms = finance,
            CanSubmit = (manages || finance) && open,
            CanReview = finance && plan.Status == PlanApprovalStatus.Submitted
                && !string.Equals(plan.SubmittedBy, projects.Access.Email, StringComparison.OrdinalIgnoreCase),
        };
    }
}
