using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Hr;
using Workbench.Contracts.Hr;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

// HR: personnel records, positions, compensation, benefits, onboarding/offboarding and reports. HR (or an administrator)
// changes records; the employee works on their own (self-service, requests); supervisors and Finance read the job fields
// (the service shapes each record by who asks). EEO/veteran data is HR, administrators and the employee only.

public static class HrPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchHrPermissions(this IServiceCollection services)
    {
        services.Decorate<IHrPersonnelService, PermissionCheckingHrPersonnelService>();
        services.Decorate<IPositionService, PermissionCheckingPositionService>();
        services.Decorate<ICompensationService, PermissionCheckingCompensationService>();
        services.Decorate<IBenefitService, PermissionCheckingBenefitService>();
        services.Decorate<IBenefitDeductionService, PermissionCheckingBenefitDeductionService>();
        services.Decorate<IOnboardingService, PermissionCheckingOnboardingService>();
        services.Decorate<IHrReportService, PermissionCheckingHrReportService>();
        return services;
    }
}

public sealed class PermissionCheckingHrPersonnelService(IHrPersonnelService inner, HrAccess hr) : IHrPersonnelService
{
    private const string Edit = "change personnel records";

    public async Task<IReadOnlyList<EmployeeSummaryDto>> ListAsync(EmployeeQuery query, CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.ListAsync(query, cancellationToken);
    }

    public async Task<EmployeeRecordDto> GetAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.GetAsync(employeeId, cancellationToken);
    }

    public async Task<EmployeeRecordDto> GetMineAsync(CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.GetMineAsync(cancellationToken);
    }

    public async Task<EmployeeRecordDto> SaveJobAsync(Guid employeeId, SaveJobInfoRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrRecordsManage, Edit, cancellationToken);
        return await inner.SaveJobAsync(employeeId, request, cancellationToken);
    }

    public async Task<EmployeeRecordDto> SavePersonalAsync(Guid employeeId, SavePersonalInfoRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrRecordsManage, Edit, cancellationToken);
        return await inner.SavePersonalAsync(employeeId, request, cancellationToken);
    }

    public async Task<EmployeeRecordDto> SaveEmergencyContactsAsync(Guid employeeId, IReadOnlyList<SaveEmergencyContact> contacts, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrRecordsManage, Edit, cancellationToken);
        return await inner.SaveEmergencyContactsAsync(employeeId, contacts, cancellationToken);
    }

    public async Task<EeoRecordDto> GetEeoAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var viewer = await hr.RequireHrOrSelfAsync(employeeId, "see EEO and veteran information", cancellationToken);
        if (!viewer.IsSelf(employeeId))
        {
            await hr.Access.RequireAsync(Permission.FieldsEeo, "see EEO and veteran information", cancellationToken);
        }

        return await inner.GetEeoAsync(employeeId, cancellationToken);
    }

    public async Task<EeoRecordDto> SaveEeoAsync(Guid employeeId, SaveEeoRequest request, CancellationToken cancellationToken = default)
    {
        var viewer = await hr.RequireHrOrSelfAsync(employeeId, "change EEO and veteran information", cancellationToken);
        if (!viewer.IsSelf(employeeId))
        {
            await hr.Access.RequireAsync(Permission.FieldsEeo, "change EEO and veteran information", cancellationToken);
        }

        return await inner.SaveEeoAsync(employeeId, request, cancellationToken);
    }

    public async Task<CredentialDto> SaveCredentialAsync(Guid employeeId, Guid? credentialId, SaveCredentialRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrRecordsManage, Edit, cancellationToken);
        return await inner.SaveCredentialAsync(employeeId, credentialId, request, cancellationToken);
    }

    public async Task DeleteCredentialAsync(Guid credentialId, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrRecordsManage, Edit, cancellationToken);
        await inner.DeleteCredentialAsync(credentialId, cancellationToken);
    }

    public async Task<IReadOnlyList<CredentialDto>> ListExpiringCredentialsAsync(int withinDays, CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.ListExpiringCredentialsAsync(withinDays, cancellationToken);
    }

    public async Task<EmployeeDocumentDto> UploadDocumentAsync(Guid employeeId, string title, string? category, bool employeeVisible, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrRecordsManage, Edit, cancellationToken);
        return await inner.UploadDocumentAsync(employeeId, title, category, employeeVisible, fileName, content, cancellationToken);
    }

    public async Task<DocumentContent> OpenDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.OpenDocumentAsync(documentId, cancellationToken);
    }

    public async Task<HrChangeRequestDto> RequestChangeAsync(SubmitHrChangeRequest request, CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.RequestChangeAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<HrChangeRequestDto>> ListChangeRequestsAsync(bool pendingOnly = true, CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.ListChangeRequestsAsync(pendingOnly, cancellationToken);
    }

    public async Task<HrChangeRequestDto> ApproveChangeAsync(Guid requestId, HrDecisionRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrRecordsManage, "decide personnel changes", cancellationToken);
        return await inner.ApproveChangeAsync(requestId, request, cancellationToken);
    }

    public async Task<HrChangeRequestDto> RejectChangeAsync(Guid requestId, HrDecisionRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrRecordsManage, "decide personnel changes", cancellationToken);
        return await inner.RejectChangeAsync(requestId, request, cancellationToken);
    }

    public async Task<HrChangeRequestDto> CancelChangeAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.CancelChangeAsync(requestId, cancellationToken);
    }

    public async Task<IReadOnlyList<OrgChartNodeDto>> GetOrgChartAsync(CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.GetOrgChartAsync(cancellationToken);
    }
}

public sealed class PermissionCheckingPositionService(IPositionService inner, HrAccess hr) : IPositionService
{
    // Salaries are a sensitive field (Permission.FieldsSalaries): masked for anyone without it.
    private async Task<T> MaskedAsync<T>(Task<T> read, CancellationToken ct) => FieldMasker.Mask(await read, await hr.Access.GetPermissionsAsync(ct));

    public async Task<IReadOnlyList<PositionDto>> ListAsync(bool includeClosed = false, CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await MaskedAsync(inner.ListAsync(includeClosed, cancellationToken), cancellationToken);
    }

    public async Task<PositionDto> GetAsync(Guid positionId, CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await MaskedAsync(inner.GetAsync(positionId, cancellationToken), cancellationToken);
    }

    public async Task<PositionDto> CreateAsync(SavePositionRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrPositionsManage, "change positions", cancellationToken);
        return await MaskedAsync(inner.CreateAsync(request, cancellationToken), cancellationToken);
    }

    public async Task<PositionDto> UpdateAsync(Guid positionId, SavePositionRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrPositionsManage, "change positions", cancellationToken);
        return await MaskedAsync(inner.UpdateAsync(positionId, request, cancellationToken), cancellationToken);
    }
}

public sealed class PermissionCheckingCompensationService(ICompensationService inner, HrAccess hr) : ICompensationService
{
    // Salaries are a sensitive field (Permission.FieldsSalaries): masked for anyone without it.
    private async Task<T> MaskedAsync<T>(Task<T> read, CancellationToken ct) => FieldMasker.Mask(await read, await hr.Access.GetPermissionsAsync(ct));

    private const string Read = "see compensation";
    private const string Change = "propose compensation changes";

    public async Task<CompensationHistoryDto> GetHistoryAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync(Read, cancellationToken);
        return await MaskedAsync(inner.GetHistoryAsync(employeeId, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<CompensationChangeDto>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync(Read, cancellationToken);
        return await MaskedAsync(inner.ListPendingAsync(cancellationToken), cancellationToken);
    }

    public async Task<CompensationChangeDto> ProposeAsync(Guid employeeId, ProposeCompensationRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrCompensationManage, Change, cancellationToken);
        return await MaskedAsync(inner.ProposeAsync(employeeId, request, cancellationToken), cancellationToken);
    }

    public async Task<CompensationChangeDto> ApproveAsync(Guid changeId, CompensationDecisionRequest request, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync("approve compensation changes", cancellationToken);
        return await MaskedAsync(inner.ApproveAsync(changeId, request, cancellationToken), cancellationToken);
    }

    public async Task<CompensationChangeDto> RejectAsync(Guid changeId, CompensationDecisionRequest request, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync("reject compensation changes", cancellationToken);
        return await MaskedAsync(inner.RejectAsync(changeId, request, cancellationToken), cancellationToken);
    }

    public async Task<CompensationChangeDto> CancelAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrCompensationManage, Change, cancellationToken);
        return await MaskedAsync(inner.CancelAsync(changeId, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<MeritCycleDto>> ListCyclesAsync(CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync(Read, cancellationToken);
        return await MaskedAsync(inner.ListCyclesAsync(cancellationToken), cancellationToken);
    }

    public async Task<MeritCycleDto> GetCycleAsync(Guid cycleId, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync(Read, cancellationToken);
        return await MaskedAsync(inner.GetCycleAsync(cycleId, cancellationToken), cancellationToken);
    }

    public async Task<MeritCycleDto> CreateCycleAsync(SaveMeritCycleRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrCompensationManage, Change, cancellationToken);
        return await MaskedAsync(inner.CreateCycleAsync(request, cancellationToken), cancellationToken);
    }

    public async Task<MeritCycleDto> AddProposalsAsync(Guid cycleId, AddMeritProposalsRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrCompensationManage, Change, cancellationToken);
        return await MaskedAsync(inner.AddProposalsAsync(cycleId, request, cancellationToken), cancellationToken);
    }

    public async Task<MeritCycleDto> SubmitCycleAsync(Guid cycleId, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrCompensationManage, Change, cancellationToken);
        return await MaskedAsync(inner.SubmitCycleAsync(cycleId, cancellationToken), cancellationToken);
    }
}

public sealed class PermissionCheckingBenefitService(IBenefitService inner, HrAccess hr) : IBenefitService
{
    private const string Admin = "set up benefits";

    public async Task<IReadOnlyList<BenefitPlanDto>> ListPlansAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.ListPlansAsync(includeInactive, cancellationToken);
    }

    public async Task<BenefitPlanDto> SavePlanAsync(Guid? planId, SaveBenefitPlanRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrBenefitsManage, Admin, cancellationToken);
        return await inner.SavePlanAsync(planId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<BenefitAccountOptionDto>> ListExpenseAccountsAsync(CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync("map benefit plans to GL accounts", cancellationToken);
        return await inner.ListExpenseAccountsAsync(cancellationToken);
    }

    public async Task<BenefitPlanDto> SetPlanAccountAsync(Guid planId, Guid? expenseAccountId, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync("map benefit plans to GL accounts", cancellationToken);
        return await inner.SetPlanAccountAsync(planId, expenseAccountId, cancellationToken);
    }

    public async Task<IReadOnlyList<OpenEnrollmentWindowDto>> ListWindowsAsync(CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.ListWindowsAsync(cancellationToken);
    }

    public async Task<OpenEnrollmentWindowDto> SaveWindowAsync(Guid? windowId, SaveOpenEnrollmentWindowRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrBenefitsManage, Admin, cancellationToken);
        return await inner.SaveWindowAsync(windowId, request, cancellationToken);
    }

    public async Task<EmployeeBenefitsDto> GetMineAsync(CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.GetMineAsync(cancellationToken);
    }

    public async Task<EmployeeBenefitsDto> GetForEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrSelfAsync(employeeId, "see someone's benefits", cancellationToken);
        return await inner.GetForEmployeeAsync(employeeId, cancellationToken);
    }

    public async Task<DependentDto> SaveDependentAsync(Guid employeeId, Guid? dependentId, SaveDependentRequest request, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrSelfAsync(employeeId, "change someone's dependents", cancellationToken);
        return await inner.SaveDependentAsync(employeeId, dependentId, request, cancellationToken);
    }

    public async Task<ElectionDto> ElectAsync(Guid employeeId, ElectBenefitRequest request, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrSelfAsync(employeeId, "change someone's benefits", cancellationToken);
        return await inner.ElectAsync(employeeId, request, cancellationToken);
    }

    public async Task<LifeEventDto> ReportLifeEventAsync(Guid employeeId, ReportLifeEventRequest request, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrSelfAsync(employeeId, "report someone's life event", cancellationToken);
        return await inner.ReportLifeEventAsync(employeeId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<LifeEventDto>> ListPendingLifeEventsAsync(CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrBenefitsManage, "verify life events", cancellationToken);
        return await inner.ListPendingLifeEventsAsync(cancellationToken);
    }

    public async Task<LifeEventDto> DecideLifeEventAsync(Guid lifeEventId, DecideLifeEventRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrBenefitsManage, "verify life events", cancellationToken);
        return await inner.DecideLifeEventAsync(lifeEventId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingBenefitDeductionService(IBenefitDeductionService inner, HrAccess hr) : IBenefitDeductionService
{
    public async Task<IReadOnlyList<DeductionPeriodDto>> ListPeriodsAsync(CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync("see benefit deductions", cancellationToken);
        return await inner.ListPeriodsAsync(cancellationToken);
    }

    public async Task<DeductionPreviewDto> PreviewAsync(Guid payGroupId, Guid payPeriodId, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync("see benefit deductions", cancellationToken);
        return await inner.PreviewAsync(payGroupId, payPeriodId, cancellationToken);
    }

    public async Task<DeductionFileDto> ExportAsync(Guid payGroupId, Guid payPeriodId, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync("export benefit deductions", cancellationToken);
        return await inner.ExportAsync(payGroupId, payPeriodId, cancellationToken);
    }
}

public sealed class PermissionCheckingOnboardingService(IOnboardingService inner, HrAccess hr) : IOnboardingService
{
    private const string Manage = "manage onboarding and offboarding";

    public async Task<IReadOnlyList<ChecklistTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrOnboardingManage, Manage, cancellationToken);
        return await inner.ListTemplatesAsync(cancellationToken);
    }

    public async Task<ChecklistTemplateDto> SaveTemplateAsync(Guid? templateId, SaveChecklistTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrOnboardingManage, Manage, cancellationToken);
        return await inner.SaveTemplateAsync(templateId, request, cancellationToken);
    }

    public async Task<EmployeeChecklistDto> StartOnboardingAsync(StartOnboardingRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrOnboardingManage, Manage, cancellationToken);
        return await inner.StartOnboardingAsync(request, cancellationToken);
    }

    public async Task<EmployeeChecklistDto> StartOffboardingAsync(StartOffboardingRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrOnboardingManage, Manage, cancellationToken);
        return await inner.StartOffboardingAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<EmployeeChecklistDto>> ListAsync(bool openOnly = true, CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.ListAsync(openOnly, cancellationToken);
    }

    public async Task<EmployeeChecklistDto> GetAsync(Guid checklistId, CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.GetAsync(checklistId, cancellationToken);
    }

    public async Task<IReadOnlyList<MyChecklistTaskDto>> ListMyTasksAsync(CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.ListMyTasksAsync(cancellationToken);
    }

    public async Task<EmployeeChecklistDto> CompleteTaskAsync(Guid taskId, CompleteChecklistTaskRequest request, CancellationToken cancellationToken = default)
    {
        hr.Access.RequireSignedIn();
        return await inner.CompleteTaskAsync(taskId, request, cancellationToken);
    }

    public async Task<EmployeeChecklistDto> SkipTaskAsync(Guid taskId, CompleteChecklistTaskRequest request, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrOnboardingManage, Manage, cancellationToken);
        return await inner.SkipTaskAsync(taskId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingHrReportService(IHrReportService inner, HrAccess hr) : IHrReportService
{
    private const string Workforce = "run workforce reports";
    private const string Restricted = "run EEO and veteran reports";

    public async Task<HeadcountReportDto> GetHeadcountAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync(Workforce, cancellationToken);
        return await inner.GetHeadcountAsync(asOf, cancellationToken);
    }

    public async Task<TurnoverReportDto> GetTurnoverAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync(Workforce, cancellationToken);
        return await inner.GetTurnoverAsync(from, to, cancellationToken);
    }

    public async Task<Eeo1ReportDto> GetEeo1Async(DateOnly snapshotDate, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrRestrictedReports, Restricted, cancellationToken);
        await hr.Access.RequireAsync(Permission.FieldsEeo, Restricted, cancellationToken);
        return await inner.GetEeo1Async(snapshotDate, cancellationToken);
    }

    public async Task<HrExportFile> ExportEeo1Async(DateOnly snapshotDate, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrRestrictedReports, Restricted, cancellationToken);
        await hr.Access.RequireAsync(Permission.FieldsEeo, Restricted, cancellationToken);
        return await inner.ExportEeo1Async(snapshotDate, cancellationToken);
    }

    public async Task<Vets4212ReportDto> GetVets4212Async(DateOnly periodEnd, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrRestrictedReports, Restricted, cancellationToken);
        await hr.Access.RequireAsync(Permission.FieldsEeo, Restricted, cancellationToken);
        return await inner.GetVets4212Async(periodEnd, cancellationToken);
    }

    public async Task<HrExportFile> ExportVets4212Async(DateOnly periodEnd, CancellationToken cancellationToken = default)
    {
        await hr.Access.RequireAsync(Permission.HrRestrictedReports, Restricted, cancellationToken);
        await hr.Access.RequireAsync(Permission.FieldsEeo, Restricted, cancellationToken);
        return await inner.ExportVets4212Async(periodEnd, cancellationToken);
    }

    public async Task<BenefitSummaryDto> GetBenefitSummaryAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync(Workforce, cancellationToken);
        return await inner.GetBenefitSummaryAsync(asOf, cancellationToken);
    }

    public async Task<HrExportFile> ExportBenefitSummaryAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        await hr.RequireHrOrFinanceAsync(Workforce, cancellationToken);
        return await inner.ExportBenefitSummaryAsync(asOf, cancellationToken);
    }
}
