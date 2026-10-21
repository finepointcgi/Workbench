using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Compliance;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

/// <summary>Purchasing system review files and CAS disclosure statements are Finance or Admin only.</summary>
public static class CompliancePermissionRegistration
{
    public static IServiceCollection AddWorkbenchCompliancePermissions(this IServiceCollection services)
    {
        services.Decorate<ICpsrService, PermissionCheckingCpsrService>();
        services.Decorate<ICasDisclosureService, PermissionCheckingCasDisclosureService>();
        return services;
    }
}

public sealed class PermissionCheckingCpsrService(ICpsrService inner, AccessControl access) : ICpsrService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.CasCpsrManage, "work with purchasing system review files", ct);

    public async Task<CpsrReadinessDto> GetReadinessAsync(CpsrReviewRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetReadinessAsync(request, cancellationToken);
    }

    public async Task<ProcurementFileDto> GetFileAsync(ProcurementDocumentKind kind, Guid documentId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetFileAsync(kind, documentId, cancellationToken);
    }

    public async Task<ProcurementFileDto> SaveFileAsync(ProcurementDocumentKind kind, Guid documentId, SaveProcurementFileRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveFileAsync(kind, documentId, request, cancellationToken);
    }

    public async Task<ProcurementFileDto> ApproveAsync(ProcurementDocumentKind kind, Guid documentId, ApproveProcurementFileRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ApproveAsync(kind, documentId, request, cancellationToken);
    }

    public async Task<CpsrSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetSettingsAsync(cancellationToken);
    }

    public async Task<ComplianceExportFile> ExportReviewAsync(CpsrReviewRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExportReviewAsync(request, cancellationToken);
    }
}

public sealed class PermissionCheckingCasDisclosureService(ICasDisclosureService inner, AccessControl access) : ICasDisclosureService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.CasCpsrManage, "work with the CAS disclosure statement", ct);

    public async Task<IReadOnlyList<DisclosureVersionDto>> ListVersionsAsync(DisclosureFormType? form = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListVersionsAsync(form, cancellationToken);
    }

    public async Task<DisclosureStatementDto> GetAsync(Guid statementId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetAsync(statementId, cancellationToken);
    }

    public async Task<DisclosureStatementDto> CreateVersionAsync(CreateDisclosureVersionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CreateVersionAsync(request, cancellationToken);
    }

    public async Task<DisclosureStatementDto> SaveAnswersAsync(Guid statementId, IReadOnlyList<SaveDisclosureAnswerRequest> answers, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveAnswersAsync(statementId, answers, cancellationToken);
    }

    public async Task<DisclosureStatementDto> FillFromConfigurationAsync(Guid statementId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.FillFromConfigurationAsync(statementId, cancellationToken);
    }

    public async Task<DisclosureStatementDto> MakeEffectiveAsync(Guid statementId, MakeDisclosureEffectiveRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.MakeEffectiveAsync(statementId, request, cancellationToken);
    }

    public async Task DeleteDraftAsync(Guid statementId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        await inner.DeleteDraftAsync(statementId, cancellationToken);
    }

    public async Task<CasConsistencyDto> CheckConsistencyAsync(Guid? statementId = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CheckConsistencyAsync(statementId, cancellationToken);
    }

    public async Task<ComplianceExportFile> ExportAsync(Guid statementId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ExportAsync(statementId, cancellationToken);
    }
}
