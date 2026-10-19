using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Materials;

namespace Workbench.ApiClient;

public sealed class HttpPartService(HttpClient http) : IPartService
{
    private const string Base = "api/v1/materials";

    public async Task<IReadOnlyList<PartSummaryDto>> ListAsync(PartQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/parts", ("search", query.Search), ("activeOnly", query.ActiveOnly), ("source", query.Source)), cancellationToken))
            .ReadLedgerAsync<List<PartSummaryDto>>(cancellationToken);

    public async Task<PartDto> GetAsync(Guid partId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/parts/{partId}", cancellationToken)).ReadLedgerAsync<PartDto>(cancellationToken);

    public async Task<PartDto> CreateAsync(SavePartRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/parts", request, cancellationToken)).ReadLedgerAsync<PartDto>(cancellationToken);

    public async Task<PartDto> UpdateAsync(Guid partId, SavePartRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/parts/{partId}", request, cancellationToken)).ReadLedgerAsync<PartDto>(cancellationToken);

    public async Task<IReadOnlyList<WarehouseDto>> ListWarehousesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/warehouses", cancellationToken)).ReadLedgerAsync<List<WarehouseDto>>(cancellationToken);

    public async Task<WarehouseDto> SaveWarehouseAsync(Guid? warehouseId, SaveWarehouseRequest request, CancellationToken cancellationToken = default) =>
        await (warehouseId is { } id
            ? await http.PutAsJsonAsync($"{Base}/warehouses/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync($"{Base}/warehouses", request, cancellationToken)).ReadLedgerAsync<WarehouseDto>(cancellationToken);

    public async Task<WarehouseDto> SaveBinAsync(Guid warehouseId, Guid? binId, SaveBinRequest request, CancellationToken cancellationToken = default) =>
        await (binId is { } id
            ? await http.PutAsJsonAsync($"{Base}/warehouses/{warehouseId}/bins/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync($"{Base}/warehouses/{warehouseId}/bins", request, cancellationToken)).ReadLedgerAsync<WarehouseDto>(cancellationToken);

    public async Task<IReadOnlyList<ForecastDto>> ListForecastsAsync(Guid? partId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/forecasts", ("partId", partId)), cancellationToken)).ReadLedgerAsync<List<ForecastDto>>(cancellationToken);

    public async Task<ForecastDto> AddForecastAsync(SaveForecastRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/forecasts", request, cancellationToken)).ReadLedgerAsync<ForecastDto>(cancellationToken);

    public async Task DeleteForecastAsync(Guid forecastId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/forecasts/{forecastId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<IReadOnlyList<MaterialRequirementDto>> ListRequirementsAsync(Guid? projectId = null, bool openOnly = true, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/requirements", ("projectId", projectId), ("openOnly", openOnly)), cancellationToken))
            .ReadLedgerAsync<List<MaterialRequirementDto>>(cancellationToken);

    public async Task<MaterialRequirementDto> AddRequirementAsync(SaveMaterialRequirementRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/requirements", request, cancellationToken)).ReadLedgerAsync<MaterialRequirementDto>(cancellationToken);

    public async Task<MaterialRequirementDto> CloseRequirementAsync(Guid requirementId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/requirements/{requirementId}/close", null, cancellationToken)).ReadLedgerAsync<MaterialRequirementDto>(cancellationToken);

    public async Task<MaterialsOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/options", cancellationToken)).ReadLedgerAsync<MaterialsOptionsDto>(cancellationToken);
}

public sealed class HttpInventoryService(HttpClient http) : IInventoryService
{
    private const string Base = "api/v1/materials";

    public async Task<IReadOnlyList<InventoryBalanceDto>> ListBalancesAsync(BalanceQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/inventory/balances", ("partId", query.PartId), ("warehouseId", query.WarehouseId), ("ownership", query.Ownership),
            ("projectId", query.ProjectId), ("search", query.Search)), cancellationToken)).ReadLedgerAsync<List<InventoryBalanceDto>>(cancellationToken);

    public async Task<IReadOnlyList<InventoryTransactionDto>> ListTransactionsAsync(TransactionQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/inventory/transactions", ("partId", query.PartId), ("workOrderId", query.WorkOrderId), ("projectId", query.ProjectId),
            ("type", query.Type), ("from", query.From), ("to", query.To), ("search", query.Search), ("take", query.Take)), cancellationToken))
            .ReadLedgerAsync<List<InventoryTransactionDto>>(cancellationToken);

    public async Task<IReadOnlyList<ReceivablePartLineDto>> ListReceivableLinesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/inventory/receivable", cancellationToken)).ReadLedgerAsync<List<ReceivablePartLineDto>>(cancellationToken);

    public async Task<IReadOnlyList<LinkablePurchaseLineDto>> ListLinkableLinesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/inventory/linkable", cancellationToken)).ReadLedgerAsync<List<LinkablePurchaseLineDto>>(cancellationToken);

    public async Task LinkPurchaseOrderLineAsync(LinkPurchaseLineRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/inventory/link", request, cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<IReadOnlyList<InventoryTransactionDto>> ReceivePurchaseOrderAsync(ReceiveIntoInventoryRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/inventory/receive", request, cancellationToken)).ReadLedgerAsync<List<InventoryTransactionDto>>(cancellationToken);

    public async Task<IReadOnlyList<InventoryTransactionDto>> ReceiveGovernmentFurnishedAsync(GovernmentReceiptRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/inventory/receive-government", request, cancellationToken)).ReadLedgerAsync<List<InventoryTransactionDto>>(cancellationToken);

    public async Task<IReadOnlyList<InventoryTransactionDto>> IssueToProjectAsync(IssueToProjectRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/inventory/issue", request, cancellationToken)).ReadLedgerAsync<List<InventoryTransactionDto>>(cancellationToken);

    public async Task<IReadOnlyList<InventoryTransactionDto>> TransferAsync(TransferRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/inventory/transfer", request, cancellationToken)).ReadLedgerAsync<List<InventoryTransactionDto>>(cancellationToken);

    public async Task<InventoryTransactionDto> AdjustAsync(AdjustInventoryRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/inventory/adjust", request, cancellationToken)).ReadLedgerAsync<InventoryTransactionDto>(cancellationToken);

    public async Task<InventoryTransactionDto?> CycleCountAsync(CycleCountRequest request, CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync($"{Base}/inventory/count", request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            response.Dispose();
            return null;
        }

        return await response.ReadLedgerAsync<InventoryTransactionDto>(cancellationToken);
    }

    public async Task<InventoryTransactionDto> ChangeStandardCostAsync(Guid partId, ChangeStandardCostRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/parts/{partId}/standard-cost", request, cancellationToken)).ReadLedgerAsync<InventoryTransactionDto>(cancellationToken);

    public async Task<TraceDto> TraceAsync(string? lot, string? serial, Guid? partId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/inventory/trace", ("lot", lot), ("serial", serial), ("partId", partId)), cancellationToken)).ReadLedgerAsync<TraceDto>(cancellationToken);
}

public sealed class HttpManufacturingService(HttpClient http) : IManufacturingService
{
    private const string Base = "api/v1/materials";

    public async Task<IReadOnlyList<BomSummaryDto>> ListBomsAsync(Guid? partId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/boms", ("partId", partId)), cancellationToken)).ReadLedgerAsync<List<BomSummaryDto>>(cancellationToken);

    public async Task<BomDto> GetBomAsync(Guid bomId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/boms/{bomId}", cancellationToken)).ReadLedgerAsync<BomDto>(cancellationToken);

    public async Task<BomDto> SaveBomAsync(Guid? bomId, SaveBomRequest request, CancellationToken cancellationToken = default) =>
        await (bomId is { } id
            ? await http.PutAsJsonAsync($"{Base}/boms/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync($"{Base}/boms", request, cancellationToken)).ReadLedgerAsync<BomDto>(cancellationToken);

    public async Task<BomDto> ActivateBomAsync(Guid bomId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/boms/{bomId}/activate", null, cancellationToken)).ReadLedgerAsync<BomDto>(cancellationToken);

    public async Task<BomDto> CopyBomAsync(Guid bomId, string newRevision, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync(LedgerUrls.With($"{Base}/boms/{bomId}/copy", ("revision", newRevision)), null, cancellationToken)).ReadLedgerAsync<BomDto>(cancellationToken);

    public async Task<IReadOnlyList<BomExplosionRowDto>> ExplodeAsync(Guid partId, decimal quantity, DateOnly asOf, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/parts/{partId}/explode", ("quantity", quantity), ("asOf", asOf)), cancellationToken)).ReadLedgerAsync<List<BomExplosionRowDto>>(cancellationToken);

    public async Task<IReadOnlyList<WhereUsedDto>> WhereUsedAsync(Guid partId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/parts/{partId}/where-used", cancellationToken)).ReadLedgerAsync<List<WhereUsedDto>>(cancellationToken);

    public async Task<IReadOnlyList<WorkCenterDto>> ListWorkCentersAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/work-centers", cancellationToken)).ReadLedgerAsync<List<WorkCenterDto>>(cancellationToken);

    public async Task<WorkCenterDto> SaveWorkCenterAsync(Guid? workCenterId, SaveWorkCenterRequest request, CancellationToken cancellationToken = default) =>
        await (workCenterId is { } id
            ? await http.PutAsJsonAsync($"{Base}/work-centers/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync($"{Base}/work-centers", request, cancellationToken)).ReadLedgerAsync<WorkCenterDto>(cancellationToken);

    public async Task<IReadOnlyList<RoutingDto>> ListRoutingsAsync(Guid partId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/parts/{partId}/routings", cancellationToken)).ReadLedgerAsync<List<RoutingDto>>(cancellationToken);

    public async Task<RoutingDto> SaveRoutingAsync(Guid? routingId, SaveRoutingRequest request, CancellationToken cancellationToken = default) =>
        await (routingId is { } id
            ? await http.PutAsJsonAsync($"{Base}/routings/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync($"{Base}/routings", request, cancellationToken)).ReadLedgerAsync<RoutingDto>(cancellationToken);

    public async Task<IReadOnlyList<WorkOrderSummaryDto>> ListWorkOrdersAsync(WorkOrderQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/work-orders", ("status", query.Status), ("partId", query.PartId), ("projectId", query.ProjectId),
            ("openOnly", query.OpenOnly), ("search", query.Search)), cancellationToken)).ReadLedgerAsync<List<WorkOrderSummaryDto>>(cancellationToken);

    public async Task<WorkOrderDto> GetWorkOrderAsync(Guid workOrderId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/work-orders/{workOrderId}", cancellationToken)).ReadLedgerAsync<WorkOrderDto>(cancellationToken);

    public async Task<WorkOrderDto> CreateWorkOrderAsync(SaveWorkOrderRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/work-orders", request, cancellationToken)).ReadLedgerAsync<WorkOrderDto>(cancellationToken);

    public async Task<WorkOrderDto> ReleaseAsync(Guid workOrderId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/work-orders/{workOrderId}/release", null, cancellationToken)).ReadLedgerAsync<WorkOrderDto>(cancellationToken);

    public async Task<WorkOrderDto> IssueComponentsAsync(Guid workOrderId, IssueComponentsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/work-orders/{workOrderId}/issue", request, cancellationToken)).ReadLedgerAsync<WorkOrderDto>(cancellationToken);

    public async Task<WorkOrderDto> ReportLaborAsync(Guid workOrderId, ReportLaborRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/work-orders/{workOrderId}/labor", request, cancellationToken)).ReadLedgerAsync<WorkOrderDto>(cancellationToken);

    public async Task<WorkOrderDto> ScrapAsync(Guid workOrderId, ScrapRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/work-orders/{workOrderId}/scrap", request, cancellationToken)).ReadLedgerAsync<WorkOrderDto>(cancellationToken);

    public async Task<WorkOrderDto> CompleteAsync(Guid workOrderId, CompleteWorkOrderRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/work-orders/{workOrderId}/complete", request, cancellationToken)).ReadLedgerAsync<WorkOrderDto>(cancellationToken);

    public async Task<WorkOrderDto> CloseAsync(Guid workOrderId, CloseWorkOrderRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/work-orders/{workOrderId}/close", request, cancellationToken)).ReadLedgerAsync<WorkOrderDto>(cancellationToken);

    public async Task<WorkOrderDto> CancelAsync(Guid workOrderId, CancelWorkOrderRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/work-orders/{workOrderId}/cancel", request, cancellationToken)).ReadLedgerAsync<WorkOrderDto>(cancellationToken);
}

public sealed class HttpMrpService(HttpClient http) : IMrpService
{
    private const string Base = "api/v1/materials/mrp";

    public async Task<MrpRunDto> RunAsync(RunMrpRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/run", request, cancellationToken)).ReadLedgerAsync<MrpRunDto>(cancellationToken);

    public async Task<MrpRunDto?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync($"{Base}/latest", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            response.Dispose();
            return null;
        }

        return await response.ReadLedgerAsync<MrpRunDto>(cancellationToken);
    }

    public async Task<FirmResultDto> FirmAsync(FirmPlannedOrdersRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/firm", request, cancellationToken)).ReadLedgerAsync<FirmResultDto>(cancellationToken);
}

public sealed class HttpMaterialReportService(HttpClient http) : IMaterialReportService
{
    private const string Base = "api/v1/materials/reports";

    public async Task<InventoryValuationDto> ValuationAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/valuation", cancellationToken)).ReadLedgerAsync<InventoryValuationDto>(cancellationToken);

    public async Task<InventoryAgingDto> AgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/aging", ("asOf", asOf)), cancellationToken)).ReadLedgerAsync<InventoryAgingDto>(cancellationToken);

    public async Task<IReadOnlyList<GfmRowDto>> GovernmentFurnishedAsync(Guid? projectId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/government-furnished", ("projectId", projectId)), cancellationToken)).ReadLedgerAsync<List<GfmRowDto>>(cancellationToken);

    public async Task<WipReportDto> WipAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/wip", cancellationToken)).ReadLedgerAsync<WipReportDto>(cancellationToken);

    public async Task<ProjectMaterialCostDto> ProjectMaterialCostAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/projects/{projectId}", cancellationToken)).ReadLedgerAsync<ProjectMaterialCostDto>(cancellationToken);
}

public static class MaterialsClientRegistration
{
    public static IServiceCollection AddMaterialsClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IPartService, HttpPartService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IInventoryService, HttpInventoryService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IManufacturingService, HttpManufacturingService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IMrpService, HttpMrpService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IMaterialReportService, HttpMaterialReportService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
