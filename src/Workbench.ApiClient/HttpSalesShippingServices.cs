using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Materials;

namespace Workbench.ApiClient;

public sealed class HttpSalesOrderService(HttpClient http) : ISalesOrderService
{
    private const string Base = "api/v1/materials/sales-orders";

    public async Task<IReadOnlyList<SalesOrderSummaryDto>> ListAsync(SalesOrderQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With(Base, ("status", query.Status), ("customerId", query.CustomerId), ("contractId", query.ContractId),
            ("openOnly", query.OpenOnly), ("search", query.Search)), cancellationToken)).ReadLedgerAsync<List<SalesOrderSummaryDto>>(cancellationToken);

    public async Task<SalesOrderDto> GetAsync(Guid salesOrderId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{salesOrderId}", cancellationToken)).ReadLedgerAsync<SalesOrderDto>(cancellationToken);

    public async Task<SalesOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/options", cancellationToken)).ReadLedgerAsync<SalesOptionsDto>(cancellationToken);

    public async Task<SalesOrderDto> CreateAsync(SaveSalesOrderRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<SalesOrderDto>(cancellationToken);

    public async Task<SalesOrderDto> UpdateAsync(Guid salesOrderId, SaveSalesOrderRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{salesOrderId}", request, cancellationToken)).ReadLedgerAsync<SalesOrderDto>(cancellationToken);

    public async Task<SalesOrderDto> AddLinesFromClinsAsync(Guid salesOrderId, AddClinLinesRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{salesOrderId}/clin-lines", request, cancellationToken)).ReadLedgerAsync<SalesOrderDto>(cancellationToken);

    public async Task<SalesOrderDto> ReleaseAsync(Guid salesOrderId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{salesOrderId}/release", null, cancellationToken)).ReadLedgerAsync<SalesOrderDto>(cancellationToken);

    public async Task<SalesOrderDto> SetHoldAsync(Guid salesOrderId, SetHoldRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{salesOrderId}/hold", request, cancellationToken)).ReadLedgerAsync<SalesOrderDto>(cancellationToken);

    public async Task<SalesOrderDto> CloseAsync(Guid salesOrderId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{salesOrderId}/close", null, cancellationToken)).ReadLedgerAsync<SalesOrderDto>(cancellationToken);

    public async Task<SalesOrderDto> CancelAsync(Guid salesOrderId, CancelSalesOrderRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{salesOrderId}/cancel", request, cancellationToken)).ReadLedgerAsync<SalesOrderDto>(cancellationToken);
}

public sealed class HttpShippingService(HttpClient http) : IShippingService
{
    private const string Base = "api/v1/materials";

    public async Task<IReadOnlyList<PickableStockDto>> ListPickableStockAsync(Guid salesOrderLineId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/sales-order-lines/{salesOrderLineId}/stock", cancellationToken)).ReadLedgerAsync<List<PickableStockDto>>(cancellationToken);

    public async Task<PickListDto> CreatePickListAsync(Guid salesOrderId, CreatePickListRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/sales-orders/{salesOrderId}/pick-lists", request, cancellationToken)).ReadLedgerAsync<PickListDto>(cancellationToken);

    public async Task<IReadOnlyList<PickListSummaryDto>> ListPickListsAsync(bool openOnly = true, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/pick-lists", ("openOnly", openOnly)), cancellationToken)).ReadLedgerAsync<List<PickListSummaryDto>>(cancellationToken);

    public async Task<PickListDto> GetPickListAsync(Guid pickListId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/pick-lists/{pickListId}", cancellationToken)).ReadLedgerAsync<PickListDto>(cancellationToken);

    public async Task<PickListDto> ConfirmPickAsync(Guid pickListLineId, ConfirmPickRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/pick-list-lines/{pickListLineId}/pick", request, cancellationToken)).ReadLedgerAsync<PickListDto>(cancellationToken);

    public async Task<PickListDto> CancelPickListAsync(Guid pickListId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/pick-lists/{pickListId}/cancel", null, cancellationToken)).ReadLedgerAsync<PickListDto>(cancellationToken);

    public async Task<ShipmentDto> CreateShipmentAsync(CreateShipmentRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/shipments", request, cancellationToken)).ReadLedgerAsync<ShipmentDto>(cancellationToken);

    public async Task<IReadOnlyList<ShipmentSummaryDto>> ListShipmentsAsync(ShipmentQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/shipments", ("status", query.Status), ("salesOrderId", query.SalesOrderId), ("from", query.From), ("to", query.To),
            ("search", query.Search), ("take", query.Take)), cancellationToken)).ReadLedgerAsync<List<ShipmentSummaryDto>>(cancellationToken);

    public async Task<ShipmentDto> GetShipmentAsync(Guid shipmentId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/shipments/{shipmentId}", cancellationToken)).ReadLedgerAsync<ShipmentDto>(cancellationToken);

    public async Task<ShipmentDto> SaveShipmentAsync(Guid shipmentId, SaveShipmentRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/shipments/{shipmentId}", request, cancellationToken)).ReadLedgerAsync<ShipmentDto>(cancellationToken);

    public async Task<ShipmentDto> ConfirmShipmentAsync(Guid shipmentId, ConfirmShipmentRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/shipments/{shipmentId}/confirm", request, cancellationToken)).ReadLedgerAsync<ShipmentDto>(cancellationToken);

    public async Task<ShipmentDto> CancelShipmentAsync(Guid shipmentId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/shipments/{shipmentId}/cancel", null, cancellationToken)).ReadLedgerAsync<ShipmentDto>(cancellationToken);

    public async Task<Dd250Dto> GetDd250Async(Guid shipmentId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/shipments/{shipmentId}/dd250", cancellationToken)).ReadLedgerAsync<Dd250Dto>(cancellationToken);

    public async Task<ShippingFile> GetDd250PdfAsync(Guid shipmentId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/shipments/{shipmentId}/dd250.pdf", cancellationToken)).ReadLedgerAsync<ShippingFile>(cancellationToken);

    public async Task<WawfValidationDto> ValidateWawfAsync(Guid shipmentId, WawfDocument documentType, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/shipments/{shipmentId}/wawf/validate", ("type", documentType)), cancellationToken))
            .ReadLedgerAsync<WawfValidationDto>(cancellationToken);

    public async Task<ShippingFile> ExportWawfAsync(Guid shipmentId, WawfDocument documentType, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync(LedgerUrls.With($"{Base}/shipments/{shipmentId}/wawf/export", ("type", documentType)), null, cancellationToken))
            .ReadLedgerAsync<ShippingFile>(cancellationToken);

    public async Task<ShipmentDto> SubmitWawfAsync(Guid shipmentId, WawfDocument documentType, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync(LedgerUrls.With($"{Base}/shipments/{shipmentId}/wawf/submit", ("type", documentType)), null, cancellationToken))
            .ReadLedgerAsync<ShipmentDto>(cancellationToken);

    public async Task<ShipmentDto> SetWawfStatusAsync(Guid shipmentId, SetWawfStatusRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/shipments/{shipmentId}/wawf/status", request, cancellationToken)).ReadLedgerAsync<ShipmentDto>(cancellationToken);

    public async Task<ShipmentDto> CreateInvoiceAsync(Guid shipmentId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/shipments/{shipmentId}/invoice", null, cancellationToken)).ReadLedgerAsync<ShipmentDto>(cancellationToken);
}

public static class SalesShippingClientRegistration
{
    public static IServiceCollection AddSalesShippingClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ISalesOrderService, HttpSalesOrderService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IShippingService, HttpShippingService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
