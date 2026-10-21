using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Materials;

namespace Workbench.Application.Identity;

/// <summary>
/// Kitting, backflushing, landed cost, physical inventory and capacity scheduling: reads are Materials, Finance or Admin;
/// changes are the Materials role (or Admin). Landed cost drafts are Materials or Finance; posting landed cost and posting a
/// physical inventory's adjustments (they change inventory value in the GL) are Finance or Admin.
/// </summary>
public static class ShopFloorPermissionRegistration
{
    public static IServiceCollection AddWorkbenchShopFloorPermissions(this IServiceCollection services)
    {
        services.Decorate<IKittingService, PermissionCheckingKittingService>();
        services.Decorate<IBackflushService, PermissionCheckingBackflushService>();
        services.Decorate<ILandedCostService, PermissionCheckingLandedCostService>();
        services.Decorate<IPhysicalInventoryService, PermissionCheckingPhysicalInventoryService>();
        services.Decorate<ICapacitySchedulingService, PermissionCheckingCapacitySchedulingService>();
        return services;
    }
}

internal static class ShopFloorAccess
{
    public static Task RequireMaterialsOrFinanceAsync(this AccessControl access, CancellationToken ct) =>
        access.RequireAsync(p => p.HasAny(Permission.MaterialsManage, Permission.MaterialsCosting), "Only the Materials role, Finance and administrators can enter landed cost.", ct);
}

public sealed class PermissionCheckingKittingService(IKittingService inner, AccessControl access) : IKittingService
{
    public async Task<IReadOnlyList<KitSummaryDto>> ListAsync(KitQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListAsync(query, cancellationToken);
    }

    public async Task<KitDto> GetAsync(Guid kitId, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.GetAsync(kitId, cancellationToken);
    }

    public async Task<KitDto> CreateForWorkOrderAsync(CreateWorkOrderKitRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.CreateForWorkOrderAsync(request, cancellationToken);
    }

    public async Task<KitDto> CreateForProjectAsync(CreateProjectKitRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.CreateForProjectAsync(request, cancellationToken);
    }

    public async Task<KitDto> StageAsync(Guid kitId, StageKitRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.StageAsync(kitId, request, cancellationToken);
    }

    public async Task<KitDto> IssueAsync(Guid kitId, IssueKitRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.IssueAsync(kitId, request, cancellationToken);
    }

    public async Task<KitDto> CancelAsync(Guid kitId, CancelKitRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.CancelAsync(kitId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingBackflushService(IBackflushService inner, AccessControl access) : IBackflushService
{
    public async Task<IReadOnlyList<BackflushSettingDto>> ListSettingsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListSettingsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BackflushComponentDto>> ListComponentsAsync(Guid? bomId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListComponentsAsync(bomId, cancellationToken);
    }

    public async Task<BackflushSettingDto> SaveSettingAsync(SaveBackflushSettingRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.SaveSettingAsync(request, cancellationToken);
    }

    public async Task DeleteSettingAsync(Guid settingId, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        await inner.DeleteSettingAsync(settingId, cancellationToken);
    }

    public async Task<IReadOnlyList<BackflushShortageDto>> ListShortagesAsync(Guid? workOrderId = null, bool openOnly = true, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListShortagesAsync(workOrderId, openOnly, cancellationToken);
    }

    public async Task<BackflushResultDto> RetryAsync(Guid workOrderId, DateOnly date, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.RetryAsync(workOrderId, date, cancellationToken);
    }
}

public sealed class PermissionCheckingLandedCostService(ILandedCostService inner, AccessControl access) : ILandedCostService
{
    public async Task<IReadOnlyList<LandedChargeSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListAsync(cancellationToken);
    }

    public async Task<LandedChargeDto> GetAsync(Guid chargeId, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.GetAsync(chargeId, cancellationToken);
    }

    public async Task<LandedCostOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.GetOptionsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LandedReceiptLineDto>> ListReceiptLinesAsync(DateOnly? from = null, string? search = null, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListReceiptLinesAsync(from, search, cancellationToken);
    }

    public async Task<IReadOnlyList<LandedBillLineDto>> ListBillLinesAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireMaterialsOrFinanceAsync(cancellationToken);
        return await inner.ListBillLinesAsync(cancellationToken);
    }

    public async Task<LandedChargeDto> CreateAsync(SaveLandedChargeRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireMaterialsOrFinanceAsync(cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<LandedChargeDto> UpdateAsync(Guid chargeId, SaveLandedChargeRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireMaterialsOrFinanceAsync(cancellationToken);
        return await inner.UpdateAsync(chargeId, request, cancellationToken);
    }

    public async Task<LandedChargeDto> PostAsync(Guid chargeId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.MaterialsCosting, "post landed cost (it changes inventory value in the general ledger)", cancellationToken);
        return await inner.PostAsync(chargeId, cancellationToken);
    }

    public async Task<LandedChargeDto> CancelAsync(Guid chargeId, CancellationToken cancellationToken = default)
    {
        await access.RequireMaterialsOrFinanceAsync(cancellationToken);
        return await inner.CancelAsync(chargeId, cancellationToken);
    }

    public async Task<IReadOnlyList<ReceiptLandedCostDto>> ListReceiptsAsync(DateOnly? from = null, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListReceiptsAsync(from, cancellationToken);
    }

    public async Task<ReceiptLandedCostDto> GetReceiptAsync(Guid purchaseReceiptId, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.GetReceiptAsync(purchaseReceiptId, cancellationToken);
    }
}

public sealed class PermissionCheckingPhysicalInventoryService(IPhysicalInventoryService inner, AccessControl access) : IPhysicalInventoryService
{
    public async Task<IReadOnlyList<PhysicalInventorySummaryDto>> ListAsync(bool openOnly = false, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListAsync(openOnly, cancellationToken);
    }

    public async Task<PhysicalInventoryDto> GetAsync(Guid physicalInventoryId, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.GetAsync(physicalInventoryId, cancellationToken);
    }

    public async Task<PhysicalInventoryDto> CreateAsync(CreatePhysicalInventoryRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<CountResultDto> EnterCountAsync(Guid physicalInventoryId, EnterCountRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.EnterCountAsync(physicalInventoryId, request, cancellationToken);
    }

    public async Task<CountResultDto> AddTagAsync(Guid physicalInventoryId, AddCountTagRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.AddTagAsync(physicalInventoryId, request, cancellationToken);
    }

    public async Task<PhysicalInventoryDto> VoidTagAsync(Guid physicalInventoryId, int tagNumber, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.VoidTagAsync(physicalInventoryId, tagNumber, cancellationToken);
    }

    public async Task<CountSheetFileDto> ExportCountSheetAsync(Guid physicalInventoryId, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ExportCountSheetAsync(physicalInventoryId, cancellationToken);
    }

    public async Task<PhysicalInventoryDto> PostAsync(Guid physicalInventoryId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.MaterialsCosting, "post physical inventory adjustments (they post inventory value to the general ledger)", cancellationToken);
        return await inner.PostAsync(physicalInventoryId, cancellationToken);
    }

    public async Task<PhysicalInventoryDto> CancelAsync(Guid physicalInventoryId, string reason, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.CancelAsync(physicalInventoryId, reason, cancellationToken);
    }
}

public sealed class PermissionCheckingCapacitySchedulingService(ICapacitySchedulingService inner, AccessControl access) : ICapacitySchedulingService
{
    public async Task<IReadOnlyList<WorkCenterCalendarDto>> ListCalendarsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListCalendarsAsync(cancellationToken);
    }

    public async Task<WorkCenterCalendarDto> SaveCalendarAsync(Guid workCenterId, SaveWorkCenterCalendarRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.SaveCalendarAsync(workCenterId, request, cancellationToken);
    }

    public async Task<WorkCenterCalendarDto> SaveExceptionAsync(Guid workCenterId, SaveCapacityExceptionRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.SaveExceptionAsync(workCenterId, request, cancellationToken);
    }

    public async Task<WorkCenterCalendarDto> DeleteExceptionAsync(Guid exceptionId, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.DeleteExceptionAsync(exceptionId, cancellationToken);
    }

    public async Task<CapacityScheduleDto> RunAsync(RunScheduleRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.RunAsync(request, cancellationToken);
    }

    public async Task<CapacityScheduleDto> GetAsync(DateOnly? from = null, int weeks = 8, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.GetAsync(from, weeks, cancellationToken);
    }

    public async Task<CapacityScheduleDto> MoveAsync(Guid scheduleId, MoveOperationRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.MoveAsync(scheduleId, request, cancellationToken);
    }

    public async Task<CapacityScheduleDto> ResequenceAsync(Guid workCenterId, ResequenceRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.ResequenceAsync(workCenterId, request, cancellationToken);
    }
}
