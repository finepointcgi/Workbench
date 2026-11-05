using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Migration;

namespace Workbench.Application.Identity;

public static class MigrationPermissionRegistration
{
    /// <summary>The cut-over checklist and reconciliation: Finance or Admin only.</summary>
    public static IServiceCollection AddWorkbenchMigrationPermissions(this IServiceCollection services)
    {
        services.Decorate<IMigrationService, PermissionCheckingMigrationService>();
        services.Decorate<IReconciliationService, PermissionCheckingReconciliationService>();
        services.Decorate<IParallelRunService, PermissionCheckingParallelRunService>();
        return services;
    }
}

public sealed class PermissionCheckingMigrationService(IMigrationService inner, AccessControl access) : IMigrationService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.SetupImports, "manage the cut-over checklist", ct);

    public async Task<MigrationChecklistDto> GetChecklistAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetChecklistAsync(cancellationToken);
    }

    public async Task<MigrationSettingsDto> SaveSettingsAsync(SaveMigrationSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveSettingsAsync(request, cancellationToken);
    }

    public async Task<MigrationChecklistDto> SetStepAsync(string stepKey, SetMigrationStepRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SetStepAsync(stepKey, request, cancellationToken);
    }
}

public sealed class PermissionCheckingReconciliationService(IReconciliationService inner, AccessControl access) : IReconciliationService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.SetupImports, "reconcile with Costpoint", ct);

    public async Task<IReadOnlyList<ReconReportDto>> ListReportsAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListReportsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReconMappingDto>> ListMappingsAsync(ReconReportKind? kind = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListMappingsAsync(kind, cancellationToken);
    }

    public async Task<ReconMappingDto> SaveMappingAsync(ReconMappingDto mapping, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveMappingAsync(mapping, cancellationToken);
    }

    public async Task DeleteMappingAsync(Guid mappingId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        await inner.DeleteMappingAsync(mappingId, cancellationToken);
    }

    public async Task<ReconFilePreviewDto> PreviewFileAsync(PreviewReconFileRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.PreviewFileAsync(request, cancellationToken);
    }

    public async Task<ReconRunDetailDto> RunAsync(RunReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.RunAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<ReconRunSummaryDto>> ListRunsAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListRunsAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<ReconRunDetailDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetRunAsync(runId, cancellationToken);
    }

    public async Task<ReconDrillDownDto> DrillDownAsync(Guid runId, string key, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.DrillDownAsync(runId, key, cancellationToken);
    }

    public async Task<ReconRunSummaryDto> SignOffAsync(Guid runId, SignOffReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SignOffAsync(runId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<ReconStatusDto>> GetStatusAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetStatusAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<ReconExportFile> ExportAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExportAsync(runId, cancellationToken);
    }
}

public sealed class PermissionCheckingParallelRunService(IParallelRunService inner, AccessControl access) : IParallelRunService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.SetupImports, "run the Costpoint parallel run", ct);

    public async Task<ParallelRunDto> GetAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<ParallelRunDto> UploadAsync(UploadParallelReportRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.UploadAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<ParallelCauseDto>> ExplainAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExplainAsync(runId, cancellationToken);
    }
}

public sealed class PermissionCheckingPayrollLayoutService(Workbench.Contracts.Payroll.IPayrollLayoutService inner, AccessControl access) : Workbench.Contracts.Payroll.IPayrollLayoutService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.PayrollSetup, "configure payroll file layouts", ct);

    public async Task<Workbench.Contracts.Payroll.PayrollLayoutDto> GetAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetAsync(providerKey, cancellationToken);
    }

    public async Task<Workbench.Contracts.Payroll.PayrollLayoutDto> GetDefaultAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetDefaultAsync(providerKey, cancellationToken);
    }

    public async Task<Workbench.Contracts.Payroll.PayrollLayoutDto> SaveAsync(string providerKey, Workbench.Contracts.Payroll.SavePayrollLayoutRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveAsync(providerKey, request, cancellationToken);
    }

    public async Task<Workbench.Contracts.Payroll.PayrollLayoutDto> ResetAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ResetAsync(providerKey, cancellationToken);
    }

    public async Task<Workbench.Contracts.Payroll.RegisterTestResultDto> TestRegisterAsync(string providerKey, Workbench.Contracts.Payroll.TestRegisterSampleRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.TestRegisterAsync(providerKey, request, cancellationToken);
    }

    public async Task<Workbench.Contracts.Payroll.HoursTemplateTestDto> TestHoursTemplateAsync(string providerKey, Workbench.Contracts.Payroll.TestHoursTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.TestHoursTemplateAsync(providerKey, request, cancellationToken);
    }
}
