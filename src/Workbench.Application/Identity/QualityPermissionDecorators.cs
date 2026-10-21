using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Quality;

namespace Workbench.Application.Identity;

/// <summary>
/// Quality and engineering change reuse the Materials role: reads are Materials, Finance or Admin; inspections, NCRs,
/// corrective actions and engineering changes change with the Materials role (or Admin). Dispositions and engineering
/// changes are decided by whoever their approval workflow routes them to (the services check that). Accounts payable
/// (Finance) and Materials mark debit memos applied.
/// </summary>
public static class QualityPermissionRegistration
{
    public static IServiceCollection AddWorkbenchQualityPermissions(this IServiceCollection services)
    {
        services.Decorate<IInspectionService, PermissionCheckingInspectionService>();
        services.Decorate<INonconformanceService, PermissionCheckingNonconformanceService>();
        services.Decorate<ICorrectiveActionService, PermissionCheckingCorrectiveActionService>();
        services.Decorate<IQualityFileService, PermissionCheckingQualityFileService>();
        services.Decorate<IEngineeringChangeService, PermissionCheckingEngineeringChangeService>();
        return services;
    }
}

internal static class QualityAccess
{
    public static Task RequireQualityReadAsync(this AccessControl access, CancellationToken ct) =>
        access.RequireAsync(p => p.Has(Permission.MaterialsView), "Only Materials, Finance and administrators can see quality records and engineering changes.", ct);

    public static Task RequireQualityWriteAsync(this AccessControl access, CancellationToken ct) =>
        access.RequireAsync(p => p.Has(Permission.MaterialsManage), "Only the Materials role and administrators can change inspections, NCRs, corrective actions and engineering changes.", ct);
}

public sealed class PermissionCheckingInspectionService(IInspectionService inner, AccessControl access) : IInspectionService
{
    public async Task<IReadOnlyList<InspectionPlanDto>> ListPlansAsync(Guid? partId = null, bool activeOnly = false, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.ListPlansAsync(partId, activeOnly, cancellationToken);
    }

    public async Task<InspectionPlanDto> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.GetPlanAsync(planId, cancellationToken);
    }

    public async Task<InspectionPlanDto> SavePlanAsync(Guid? planId, SaveInspectionPlanRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.SavePlanAsync(planId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<InspectionSummaryDto>> ListAsync(InspectionQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.ListAsync(query, cancellationToken);
    }

    public async Task<InspectionDto> GetAsync(Guid inspectionId, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.GetAsync(inspectionId, cancellationToken);
    }

    public async Task<InspectionDto> CompleteAsync(Guid inspectionId, CompleteInspectionRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.CompleteAsync(inspectionId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<DefectCodeDto>> ListDefectCodesAsync(bool activeOnly = true, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.ListDefectCodesAsync(activeOnly, cancellationToken);
    }

    public async Task<DefectCodeDto> SaveDefectCodeAsync(Guid? defectCodeId, SaveDefectCodeRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.SaveDefectCodeAsync(defectCodeId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingNonconformanceService(INonconformanceService inner, AccessControl access) : INonconformanceService
{
    public async Task<IReadOnlyList<NcrSummaryDto>> ListAsync(NcrQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.ListAsync(query, cancellationToken);
    }

    public async Task<NcrDto> GetAsync(Guid ncrId, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.GetAsync(ncrId, cancellationToken);
    }

    public async Task<NcrDto> CreateAsync(CreateNcrRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<NcrDto> ProposeDispositionAsync(Guid ncrId, ProposeDispositionRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.ProposeDispositionAsync(ncrId, request, cancellationToken);
    }

    public Task<NcrDto> ApproveAsync(Guid ncrId, DecideNcrRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn(); // The workflow decides who may approve.
        return inner.ApproveAsync(ncrId, request, cancellationToken);
    }

    public Task<NcrDto> RejectAsync(Guid ncrId, DecideNcrRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.RejectAsync(ncrId, request, cancellationToken);
    }

    public async Task<NcrDto> WithdrawDispositionAsync(Guid ncrId, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.WithdrawDispositionAsync(ncrId, cancellationToken);
    }

    public Task<IReadOnlyList<NcrSummaryDto>> ListWaitingOnMeAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListWaitingOnMeAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DebitMemoDto>> ListDebitMemosAsync(Guid? vendorId = null, bool openOnly = false, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.ListDebitMemosAsync(vendorId, openOnly, cancellationToken);
    }

    public async Task<DebitMemoDto> ApplyDebitMemoAsync(Guid debitMemoId, ApplyDebitMemoRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(p => p.Has(Permission.QualityDispositions), "Only Finance, Materials and administrators can apply vendor debit memos.", cancellationToken);
        return await inner.ApplyDebitMemoAsync(debitMemoId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingCorrectiveActionService(ICorrectiveActionService inner, AccessControl access) : ICorrectiveActionService
{
    public async Task<IReadOnlyList<CarSummaryDto>> ListAsync(CarQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.ListAsync(query, cancellationToken);
    }

    public async Task<CarDto> GetAsync(Guid carId, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.GetAsync(carId, cancellationToken);
    }

    public async Task<CarDto> SaveAsync(Guid? carId, SaveCarRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.SaveAsync(carId, request, cancellationToken);
    }

    public async Task<CarDto> MarkImplementedAsync(Guid carId, CompleteCarRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.MarkImplementedAsync(carId, request, cancellationToken);
    }

    public async Task<CarDto> VerifyAsync(Guid carId, VerifyCarRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.VerifyAsync(carId, request, cancellationToken);
    }

    public async Task<CarDto> CancelAsync(Guid carId, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.CancelAsync(carId, cancellationToken);
    }

    public async Task<SupplierQualityDto> SupplierQualityAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.SupplierQualityAsync(from, to, cancellationToken);
    }
}

public sealed class PermissionCheckingQualityFileService(IQualityFileService inner, AccessControl access) : IQualityFileService
{
    public async Task<QualityFileDto> AttachAsync(QualityFileOwner owner, Guid ownerId, string kind, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.AttachAsync(owner, ownerId, kind, fileName, content, cancellationToken);
    }

    public async Task<ReceiptContent> OpenAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.OpenAsync(attachmentId, cancellationToken);
    }

    public string GetUrl(Guid attachmentId) => inner.GetUrl(attachmentId);
}

public sealed class PermissionCheckingEngineeringChangeService(IEngineeringChangeService inner, AccessControl access) : IEngineeringChangeService
{
    public async Task<IReadOnlyList<EcoSummaryDto>> ListAsync(EcoQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.ListAsync(query, cancellationToken);
    }

    public async Task<EcoDto> GetAsync(Guid ecoId, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.GetAsync(ecoId, cancellationToken);
    }

    public async Task<EcoDto> SaveAsync(Guid? ecoId, SaveEcoRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.SaveAsync(ecoId, request, cancellationToken);
    }

    public async Task<EcoDto> SubmitAsync(Guid ecoId, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.SubmitAsync(ecoId, cancellationToken);
    }

    public Task<EcoDto> ApproveAsync(Guid ecoId, DecideEcoRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn(); // The workflow decides who may approve.
        return inner.ApproveAsync(ecoId, request, cancellationToken);
    }

    public Task<EcoDto> RejectAsync(Guid ecoId, DecideEcoRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.RejectAsync(ecoId, request, cancellationToken);
    }

    public async Task<EcoDto> CancelAsync(Guid ecoId, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.CancelAsync(ecoId, cancellationToken);
    }

    public async Task<int> ApplyDueAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.ApplyDueAsync(asOf, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkOrderReviewDto>> ListOpenWorkOrderReviewsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.ListOpenWorkOrderReviewsAsync(cancellationToken);
    }

    public async Task<WorkOrderReviewDto> MarkWorkOrderReviewedAsync(Guid reviewId, ReviewWorkOrderRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityWriteAsync(cancellationToken);
        return await inner.MarkWorkOrderReviewedAsync(reviewId, request, cancellationToken);
    }

    public Task<IReadOnlyList<EcoSummaryDto>> ListWaitingOnMeAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListWaitingOnMeAsync(cancellationToken);
    }

    public async Task<RevisionHistoryDto> RevisionHistoryAsync(Guid partId, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.RevisionHistoryAsync(partId, cancellationToken);
    }

    public async Task<IReadOnlyList<ConfigurationUnitDto>> ListUnitsAsync(Guid partId, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.ListUnitsAsync(partId, cancellationToken);
    }

    public async Task<ConfigurationDto> ConfigurationAsync(Guid partId, string? serial = null, string? lot = null, CancellationToken cancellationToken = default)
    {
        await access.RequireQualityReadAsync(cancellationToken);
        return await inner.ConfigurationAsync(partId, serial, lot, cancellationToken);
    }
}
