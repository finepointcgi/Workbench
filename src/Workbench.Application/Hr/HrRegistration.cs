using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Approvals;
using Workbench.Application.Files;
using Workbench.Application.MyTodo;
using Workbench.Contracts.Hr;

namespace Workbench.Application.Hr;

public static class HrRegistration
{
    /// <summary>
    /// HR: personnel records, positions, compensation (writes payroll's pay rate history), benefits (deductions through the
    /// payroll provider adapter), onboarding/offboarding, reports, the approval handlers for personnel and compensation
    /// changes and the home page to-dos. Above AddWorkbenchIdentity, whose <c>AddWorkbenchHrPermissions</c> gates them.
    /// </summary>
    public static IServiceCollection AddWorkbenchHr(this IServiceCollection services)
    {
        services.AddOptions<HrOptions>().BindConfiguration("Hr");
        services.TryAddScoped<FileStorage>();
        services.AddScoped<HrAccess>();
        services.AddScoped<IHrPersonnelService, HrPersonnelService>();
        services.AddScoped<IPositionService, PositionService>();
        services.AddScoped<ICompensationService, CompensationService>();
        services.AddScoped<IBenefitService, BenefitService>();
        services.AddScoped<IBenefitDeductionService, BenefitDeductionService>();
        services.AddScoped<IOnboardingService, OnboardingService>();
        services.AddScoped<IHrReportService, HrReportService>();
        services.AddScoped<IHrAccessService, HrAccessService>();
        services.AddScoped<IApprovalDocumentHandler, HrChangeApprovalHandler>();
        services.AddScoped<IApprovalDocumentHandler, CompensationApprovalHandler>();
        services.AddScoped<IMyTodoProvider, ChecklistTodoProvider>();
        services.AddScoped<IMyTodoProvider, CredentialExpiryTodoProvider>();
        services.AddScoped<IMyTodoProvider, BenefitsTodoProvider>();
        services.AddScoped<IMyTodoProvider, HrRequestTodoProvider>();
        return services;
    }
}

public sealed class HrChangeApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => Contracts.Approvals.ApprovalDocumentTypes.HrChange;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken ct) =>
        services.GetRequiredService<IHrPersonnelService>().ApproveChangeAsync(documentId, new HrDecisionRequest(comment), ct);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken ct) =>
        services.GetRequiredService<IHrPersonnelService>().RejectChangeAsync(documentId, new HrDecisionRequest(reason), ct);
}

public sealed class CompensationApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => Contracts.Approvals.ApprovalDocumentTypes.Compensation;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken ct) =>
        services.GetRequiredService<ICompensationService>().ApproveAsync(documentId, new CompensationDecisionRequest(comment), ct);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken ct) =>
        services.GetRequiredService<ICompensationService>().RejectAsync(documentId, new CompensationDecisionRequest(reason), ct);
}

/// <summary>What the user may do in HR, for the menu (cosmetic: every service checks again).</summary>
public sealed class HrAccessService(HrAccess hrAccess, IWorkbenchDbContextFactory dbFactory) : IHrAccessService
{
    public async Task<HrAccessDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var supervisor = viewer.EmployeeId is { } me
            && await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Set<Domain.ProjectAccounting.Employee>(), e => e.SupervisorId == me, cancellationToken);
        return new HrAccessDto(viewer.IsHr, viewer.IsFinance, supervisor, viewer.EmployeeId, false);
    }
}
