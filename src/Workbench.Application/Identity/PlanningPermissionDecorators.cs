using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Approvals;
using Workbench.Application.Collaboration;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Planning;
using Workbench.Domain.Planning;

namespace Workbench.Application.Identity;

// Recurring journals, budget versions and budget reports are Finance's (Finance or Admin). In a planning cycle, Finance
// opens, assigns and adopts; an org unit's owner reads and edits their own unit's budget (Finance and Admin may too);
// whoever the approval workflow routes a submission to may read it, and the service checks they may decide it.

public static class PlanningPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchPlanningPermissions(this IServiceCollection services)
    {
        services.Decorate<IRecurringJournalService, PermissionCheckingRecurringJournalService>();
        services.Decorate<IGlBudgetService, PermissionCheckingGlBudgetService>();
        services.Decorate<IBudgetReportService, PermissionCheckingBudgetReportService>();
        services.Decorate<IPlanningCycleService, PermissionCheckingPlanningCycleService>();
        return services;
    }
}

public sealed class PermissionCheckingRecurringJournalService(IRecurringJournalService inner, AccessControl access) : IRecurringJournalService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.GlRecurringManage, "manage recurring journals", ct);

    public async Task<IReadOnlyList<RecurringTemplateDto>> ListTemplatesAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListTemplatesAsync(includeInactive, cancellationToken);
    }

    public async Task<RecurringTemplateDto> GetTemplateAsync(Guid templateId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetTemplateAsync(templateId, cancellationToken);
    }

    public async Task<RecurringTemplateDto> CreateTemplateAsync(SaveRecurringTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CreateTemplateAsync(request, cancellationToken);
    }

    public async Task<RecurringTemplateDto> UpdateTemplateAsync(Guid templateId, SaveRecurringTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.UpdateTemplateAsync(templateId, request, cancellationToken);
    }

    public async Task<RecurringTemplateDto> SetActiveAsync(Guid templateId, bool isActive, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SetActiveAsync(templateId, isActive, cancellationToken);
    }

    public async Task<IReadOnlyList<RecurringRunDto>> GetScheduleAsync(Guid templateId, int upcoming = 6, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetScheduleAsync(templateId, upcoming, cancellationToken);
    }

    public async Task<IReadOnlyList<RecurringRunDto>> ListHistoryAsync(Guid? templateId = null, int take = 200, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListHistoryAsync(templateId, take, cancellationToken);
    }

    public async Task<RecurringGenerationResultDto> GenerateDueAsync(GenerateRecurringRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GenerateDueAsync(request, cancellationToken);
    }

    public async Task<RecurringSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetSettingsAsync(cancellationToken);
    }

    public async Task<RecurringSettingsDto> SaveSettingsAsync(SaveRecurringSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveSettingsAsync(request, cancellationToken);
    }
}

public sealed class PermissionCheckingGlBudgetService(IGlBudgetService inner, AccessControl access) : IGlBudgetService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.BudgetsManage, "manage corporate budgets", ct);

    public async Task<IReadOnlyList<BudgetVersionDto>> ListVersionsAsync(int? fiscalYear = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListVersionsAsync(fiscalYear, cancellationToken);
    }

    public async Task<BudgetVersionDto> CreateVersionAsync(CreateBudgetVersionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CreateVersionAsync(request, cancellationToken);
    }

    public async Task<BudgetGridDto> GetGridAsync(Guid versionId, Guid? orgUnitId = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetGridAsync(versionId, orgUnitId, cancellationToken);
    }

    public async Task<BudgetGridDto> SaveCellsAsync(Guid versionId, SaveBudgetCellsRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveCellsAsync(versionId, request, cancellationToken);
    }

    public async Task<BudgetGridDto> SpreadAsync(Guid versionId, SpreadBudgetRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SpreadAsync(versionId, request, cancellationToken);
    }

    public async Task<BudgetImportResultDto> ImportCsvAsync(Guid versionId, ImportBudgetCsvRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ImportCsvAsync(versionId, request, cancellationToken);
    }

    public async Task<BudgetVersionDto> AdoptAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.AdoptAsync(versionId, cancellationToken);
    }
}

public sealed class PermissionCheckingBudgetReportService(IBudgetReportService inner, AccessControl access) : IBudgetReportService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.BudgetsManage, "see budget vs actual", ct);

    public async Task<BudgetVsActualDto> GetIncomeStatementAsync(BudgetVsActualQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetIncomeStatementAsync(query, cancellationToken);
    }

    public async Task<BudgetVsActualDto> GetByOrgUnitAsync(BudgetVsActualQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetByOrgUnitAsync(query, cancellationToken);
    }

    public async Task<BudgetVsActualDto> GetByPoolAsync(BudgetVsActualQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetByPoolAsync(query, cancellationToken);
    }

    public async Task<BudgetedRatesDto> GetBudgetedRatesAsync(int fiscalYear, Guid? versionId = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetBudgetedRatesAsync(fiscalYear, versionId, cancellationToken);
    }

    public async Task<AccountingExportFile> ExportCsvAsync(BudgetReport report, BudgetVsActualQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExportCsvAsync(report, query, cancellationToken);
    }
}

public sealed class PermissionCheckingPlanningCycleService(IPlanningCycleService inner, AccessControl access, IWorkbenchDbContextFactory dbFactory, ApprovalEngine? approvals = null)
    : IPlanningCycleService
{
    private Task RequireFinanceAsync(CancellationToken ct) => access.RequireAsync(Permission.BudgetsManage, "run planning cycles", ct);

    private void RequireSignedIn()
    {
        if (!access.IsAuthenticated)
        {
            throw new WorkbenchForbiddenException("Sign in to see budgets.");
        }
    }

    /// <summary>Finance, Admin or the org unit's owner (to edit); with <paramref name="orApprover"/>, also anyone on its approval route (to read).</summary>
    private async Task RequireSubmissionAccessAsync(Guid submissionId, bool orApprover, CancellationToken ct)
    {
        RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(ct);
        var owner = await db.Set<BudgetSubmission>().AsNoTracking().Where(s => s.Id == submissionId).Select(s => (Guid?)s.OwnerEmployeeId).FirstOrDefaultAsync(ct)
            ?? throw WorkbenchNotFoundException.For("Budget submission", submissionId);
        if (await access.HasAsync(Permission.BudgetsManage, ct) || await access.GetEmployeeIdAsync(ct) == owner)
        {
            return;
        }

        if (orApprover && approvals is not null && await approvals.IsParticipantAsync(db, ApprovalDocumentTypes.BudgetSubmission, submissionId, access.Email, ct))
        {
            return;
        }

        throw await access.DenyAsync(orApprover
            ? "Only Finance, administrators, the org unit's budget owner and its approvers can see this budget."
            : "Only the org unit's budget owner (or Finance) can change this budget.", ct);
    }

    public async Task<IReadOnlyList<PlanningCycleDto>> ListCyclesAsync(CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.ListCyclesAsync(cancellationToken);
    }

    public async Task<PlanningCycleDto> GetCycleAsync(Guid cycleId, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.GetCycleAsync(cycleId, cancellationToken);
    }

    public async Task<PlanningCycleDto> OpenCycleAsync(OpenPlanningCycleRequest request, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.OpenCycleAsync(request, cancellationToken);
    }

    public async Task<PlanningCycleDto> AssignOwnerAsync(Guid cycleId, CycleAssignmentInput assignment, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.AssignOwnerAsync(cycleId, assignment, cancellationToken);
    }

    public async Task<PlanningCycleDto> AdoptAsync(Guid cycleId, CancellationToken cancellationToken = default)
    {
        await RequireFinanceAsync(cancellationToken);
        return await inner.AdoptAsync(cycleId, cancellationToken);
    }

    public async Task<IReadOnlyList<BudgetSubmissionSummaryDto>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        RequireSignedIn();
        return await inner.ListMineAsync(cancellationToken);
    }

    public async Task<BudgetSubmissionDto> GetSubmissionAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        await RequireSubmissionAccessAsync(submissionId, orApprover: true, cancellationToken);
        return await inner.GetSubmissionAsync(submissionId, cancellationToken);
    }

    public async Task<BudgetSubmissionDto> SaveSubmissionCellsAsync(Guid submissionId, SaveSubmissionCellsRequest request, CancellationToken cancellationToken = default)
    {
        await RequireSubmissionAccessAsync(submissionId, orApprover: false, cancellationToken);
        return await inner.SaveSubmissionCellsAsync(submissionId, request, cancellationToken);
    }

    public async Task<BudgetSubmissionDto> SpreadSubmissionAsync(Guid submissionId, SpreadSubmissionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireSubmissionAccessAsync(submissionId, orApprover: false, cancellationToken);
        return await inner.SpreadSubmissionAsync(submissionId, request, cancellationToken);
    }

    public async Task<BudgetSubmissionDto> SubmitAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        await RequireSubmissionAccessAsync(submissionId, orApprover: false, cancellationToken);
        return await inner.SubmitAsync(submissionId, cancellationToken);
    }

    public async Task<BudgetSubmissionDto> RecallAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        await RequireSubmissionAccessAsync(submissionId, orApprover: false, cancellationToken);
        return await inner.RecallAsync(submissionId, cancellationToken);
    }

    // Deciding: the service lets only the workflow's current approvers decide.
    public async Task<BudgetSubmissionDto> ApproveAsync(Guid submissionId, DecideSubmissionRequest request, CancellationToken cancellationToken = default)
    {
        RequireSignedIn();
        return await inner.ApproveAsync(submissionId, request, cancellationToken);
    }

    public async Task<BudgetSubmissionDto> ReturnAsync(Guid submissionId, DecideSubmissionRequest request, CancellationToken cancellationToken = default)
    {
        RequireSignedIn();
        return await inner.ReturnAsync(submissionId, request, cancellationToken);
    }
}
