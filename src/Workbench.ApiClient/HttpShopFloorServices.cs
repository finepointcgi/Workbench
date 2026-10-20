using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Materials;

namespace Workbench.ApiClient;

public sealed class HttpKittingService(HttpClient http) : IKittingService
{
    private const string Base = "api/v1/materials/kits";

    public async Task<IReadOnlyList<KitSummaryDto>> ListAsync(KitQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With(Base, ("openOnly", query.OpenOnly), ("workOrderId", query.WorkOrderId), ("projectId", query.ProjectId)), cancellationToken))
            .ReadLedgerAsync<List<KitSummaryDto>>(cancellationToken);

    public async Task<KitDto> GetAsync(Guid kitId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{kitId}", cancellationToken)).ReadLedgerAsync<KitDto>(cancellationToken);

    public async Task<KitDto> CreateForWorkOrderAsync(CreateWorkOrderKitRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/work-order", request, cancellationToken)).ReadLedgerAsync<KitDto>(cancellationToken);

    public async Task<KitDto> CreateForProjectAsync(CreateProjectKitRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/project", request, cancellationToken)).ReadLedgerAsync<KitDto>(cancellationToken);

    public async Task<KitDto> StageAsync(Guid kitId, StageKitRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{kitId}/stage", request, cancellationToken)).ReadLedgerAsync<KitDto>(cancellationToken);

    public async Task<KitDto> IssueAsync(Guid kitId, IssueKitRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{kitId}/issue", request, cancellationToken)).ReadLedgerAsync<KitDto>(cancellationToken);

    public async Task<KitDto> CancelAsync(Guid kitId, CancelKitRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{kitId}/cancel", request, cancellationToken)).ReadLedgerAsync<KitDto>(cancellationToken);
}

public sealed class HttpBackflushService(HttpClient http) : IBackflushService
{
    private const string Base = "api/v1/materials";

    public async Task<IReadOnlyList<BackflushSettingDto>> ListSettingsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/backflush/settings", cancellationToken)).ReadLedgerAsync<List<BackflushSettingDto>>(cancellationToken);

    public async Task<IReadOnlyList<BackflushComponentDto>> ListComponentsAsync(Guid? bomId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/backflush/components", ("bomId", bomId)), cancellationToken)).ReadLedgerAsync<List<BackflushComponentDto>>(cancellationToken);

    public async Task<BackflushSettingDto> SaveSettingAsync(SaveBackflushSettingRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/backflush/settings", request, cancellationToken)).ReadLedgerAsync<BackflushSettingDto>(cancellationToken);

    public async Task DeleteSettingAsync(Guid settingId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/backflush/settings/{settingId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<IReadOnlyList<BackflushShortageDto>> ListShortagesAsync(Guid? workOrderId = null, bool openOnly = true, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/backflush/shortages", ("workOrderId", workOrderId), ("openOnly", openOnly)), cancellationToken))
            .ReadLedgerAsync<List<BackflushShortageDto>>(cancellationToken);

    public async Task<BackflushResultDto> RetryAsync(Guid workOrderId, DateOnly date, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync(LedgerUrls.With($"{Base}/work-orders/{workOrderId}/backflush", ("date", date)), null, cancellationToken)).ReadLedgerAsync<BackflushResultDto>(cancellationToken);
}

public sealed class HttpLandedCostService(HttpClient http) : ILandedCostService
{
    private const string Base = "api/v1/materials/landed-cost";

    public async Task<IReadOnlyList<LandedChargeSummaryDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Base, cancellationToken)).ReadLedgerAsync<List<LandedChargeSummaryDto>>(cancellationToken);

    public async Task<LandedChargeDto> GetAsync(Guid chargeId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{chargeId}", cancellationToken)).ReadLedgerAsync<LandedChargeDto>(cancellationToken);

    public async Task<LandedCostOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/options", cancellationToken)).ReadLedgerAsync<LandedCostOptionsDto>(cancellationToken);

    public async Task<IReadOnlyList<LandedReceiptLineDto>> ListReceiptLinesAsync(DateOnly? from = null, string? search = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/receipt-lines", ("from", from), ("search", search)), cancellationToken)).ReadLedgerAsync<List<LandedReceiptLineDto>>(cancellationToken);

    public async Task<IReadOnlyList<LandedBillLineDto>> ListBillLinesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/bill-lines", cancellationToken)).ReadLedgerAsync<List<LandedBillLineDto>>(cancellationToken);

    public async Task<LandedChargeDto> CreateAsync(SaveLandedChargeRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<LandedChargeDto>(cancellationToken);

    public async Task<LandedChargeDto> UpdateAsync(Guid chargeId, SaveLandedChargeRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{chargeId}", request, cancellationToken)).ReadLedgerAsync<LandedChargeDto>(cancellationToken);

    public async Task<LandedChargeDto> PostAsync(Guid chargeId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{chargeId}/post", null, cancellationToken)).ReadLedgerAsync<LandedChargeDto>(cancellationToken);

    public async Task<LandedChargeDto> CancelAsync(Guid chargeId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{chargeId}/cancel", null, cancellationToken)).ReadLedgerAsync<LandedChargeDto>(cancellationToken);

    public async Task<IReadOnlyList<ReceiptLandedCostDto>> ListReceiptsAsync(DateOnly? from = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/receipts", ("from", from)), cancellationToken)).ReadLedgerAsync<List<ReceiptLandedCostDto>>(cancellationToken);

    public async Task<ReceiptLandedCostDto> GetReceiptAsync(Guid purchaseReceiptId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/receipts/{purchaseReceiptId}", cancellationToken)).ReadLedgerAsync<ReceiptLandedCostDto>(cancellationToken);
}

public sealed class HttpPhysicalInventoryService(HttpClient http) : IPhysicalInventoryService
{
    private const string Base = "api/v1/materials/physical-inventory";

    public async Task<IReadOnlyList<PhysicalInventorySummaryDto>> ListAsync(bool openOnly = false, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With(Base, ("openOnly", openOnly)), cancellationToken)).ReadLedgerAsync<List<PhysicalInventorySummaryDto>>(cancellationToken);

    public async Task<PhysicalInventoryDto> GetAsync(Guid physicalInventoryId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{physicalInventoryId}", cancellationToken)).ReadLedgerAsync<PhysicalInventoryDto>(cancellationToken);

    public async Task<PhysicalInventoryDto> CreateAsync(CreatePhysicalInventoryRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<PhysicalInventoryDto>(cancellationToken);

    public async Task<CountResultDto> EnterCountAsync(Guid physicalInventoryId, EnterCountRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{physicalInventoryId}/counts", request, cancellationToken)).ReadLedgerAsync<CountResultDto>(cancellationToken);

    public async Task<CountResultDto> AddTagAsync(Guid physicalInventoryId, AddCountTagRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{physicalInventoryId}/tags", request, cancellationToken)).ReadLedgerAsync<CountResultDto>(cancellationToken);

    public async Task<PhysicalInventoryDto> VoidTagAsync(Guid physicalInventoryId, int tagNumber, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{physicalInventoryId}/tags/{tagNumber}/void", null, cancellationToken)).ReadLedgerAsync<PhysicalInventoryDto>(cancellationToken);

    public async Task<CountSheetFileDto> ExportCountSheetAsync(Guid physicalInventoryId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{physicalInventoryId}/count-sheet", cancellationToken)).ReadLedgerAsync<CountSheetFileDto>(cancellationToken);

    public async Task<PhysicalInventoryDto> PostAsync(Guid physicalInventoryId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{physicalInventoryId}/post", null, cancellationToken)).ReadLedgerAsync<PhysicalInventoryDto>(cancellationToken);

    public async Task<PhysicalInventoryDto> CancelAsync(Guid physicalInventoryId, string reason, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{physicalInventoryId}/cancel", new { Reason = reason }, cancellationToken)).ReadLedgerAsync<PhysicalInventoryDto>(cancellationToken);
}

public sealed class HttpCapacitySchedulingService(HttpClient http) : ICapacitySchedulingService
{
    private const string Base = "api/v1/materials/capacity";

    public async Task<IReadOnlyList<WorkCenterCalendarDto>> ListCalendarsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/calendars", cancellationToken)).ReadLedgerAsync<List<WorkCenterCalendarDto>>(cancellationToken);

    public async Task<WorkCenterCalendarDto> SaveCalendarAsync(Guid workCenterId, SaveWorkCenterCalendarRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/calendars/{workCenterId}", request, cancellationToken)).ReadLedgerAsync<WorkCenterCalendarDto>(cancellationToken);

    public async Task<WorkCenterCalendarDto> SaveExceptionAsync(Guid workCenterId, SaveCapacityExceptionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/calendars/{workCenterId}/exceptions", request, cancellationToken)).ReadLedgerAsync<WorkCenterCalendarDto>(cancellationToken);

    public async Task<WorkCenterCalendarDto> DeleteExceptionAsync(Guid exceptionId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/exceptions/{exceptionId}", cancellationToken)).ReadLedgerAsync<WorkCenterCalendarDto>(cancellationToken);

    public async Task<CapacityScheduleDto> RunAsync(RunScheduleRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/schedule", request, cancellationToken)).ReadLedgerAsync<CapacityScheduleDto>(cancellationToken);

    public async Task<CapacityScheduleDto> GetAsync(DateOnly? from = null, int weeks = 8, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/schedule", ("from", from), ("weeks", weeks)), cancellationToken)).ReadLedgerAsync<CapacityScheduleDto>(cancellationToken);

    public async Task<CapacityScheduleDto> MoveAsync(Guid scheduleId, MoveOperationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/schedule/{scheduleId}/move", request, cancellationToken)).ReadLedgerAsync<CapacityScheduleDto>(cancellationToken);

    public async Task<CapacityScheduleDto> ResequenceAsync(Guid workCenterId, ResequenceRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/work-centers/{workCenterId}/queue", request, cancellationToken)).ReadLedgerAsync<CapacityScheduleDto>(cancellationToken);
}

public static class ShopFloorClientRegistration
{
    public static IServiceCollection AddShopFloorClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IKittingService, HttpKittingService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IBackflushService, HttpBackflushService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ILandedCostService, HttpLandedCostService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPhysicalInventoryService, HttpPhysicalInventoryService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICapacitySchedulingService, HttpCapacitySchedulingService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
