using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Purchasing;

namespace Workbench.ApiClient;

public sealed class HttpRequisitionService(HttpClient http) : IRequisitionService
{
    private const string Base = "api/v1/purchasing/requisitions";

    public async Task<IReadOnlyList<RequisitionSummaryDto>> ListAsync(RequisitionQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With(Base, ("status", query.Status), ("mine", query.Mine), ("search", query.Search)), cancellationToken))
            .ReadLedgerAsync<List<RequisitionSummaryDto>>(cancellationToken);

    public async Task<RequisitionDto> GetAsync(Guid requisitionId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{requisitionId}", cancellationToken)).ReadLedgerAsync<RequisitionDto>(cancellationToken);

    public async Task<RequisitionOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/options", cancellationToken)).ReadLedgerAsync<RequisitionOptionsDto>(cancellationToken);

    public async Task<RequisitionDto> CreateAsync(SaveRequisitionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<RequisitionDto>(cancellationToken);

    public async Task<RequisitionDto> UpdateAsync(Guid requisitionId, SaveRequisitionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{requisitionId}", request, cancellationToken)).ReadLedgerAsync<RequisitionDto>(cancellationToken);

    public async Task<RequisitionDto> SubmitAsync(Guid requisitionId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{requisitionId}/submit", null, cancellationToken)).ReadLedgerAsync<RequisitionDto>(cancellationToken);

    public async Task<IReadOnlyList<RequisitionSummaryDto>> ListApprovalQueueAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/approvals", cancellationToken)).ReadLedgerAsync<List<RequisitionSummaryDto>>(cancellationToken);

    public async Task<RequisitionDto> ApproveAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{requisitionId}/approve", request, cancellationToken)).ReadLedgerAsync<RequisitionDto>(cancellationToken);

    public async Task<RequisitionDto> RejectAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{requisitionId}/reject", request, cancellationToken)).ReadLedgerAsync<RequisitionDto>(cancellationToken);

    public async Task<RequisitionDto> CancelAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{requisitionId}/cancel", request, cancellationToken)).ReadLedgerAsync<RequisitionDto>(cancellationToken);
}

public sealed class HttpPurchaseOrderService(HttpClient http) : IPurchaseOrderService
{
    private const string Base = "api/v1/purchasing/orders";

    public async Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListAsync(PurchaseOrderQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With(Base, ("status", query.Status), ("vendorId", query.VendorId), ("search", query.Search), ("openOnly", query.OpenOnly)), cancellationToken))
            .ReadLedgerAsync<List<PurchaseOrderSummaryDto>>(cancellationToken);

    public async Task<PurchaseOrderDto> GetAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{purchaseOrderId}", cancellationToken)).ReadLedgerAsync<PurchaseOrderDto>(cancellationToken);

    public async Task<IReadOnlyList<OrderableRequisitionLineDto>> ListOrderableRequisitionLinesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/orderable-lines", cancellationToken)).ReadLedgerAsync<List<OrderableRequisitionLineDto>>(cancellationToken);

    public async Task<PurchaseOrderDto> CreateAsync(SavePurchaseOrderRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<PurchaseOrderDto>(cancellationToken);

    public async Task<PurchaseOrderDto> CreateFromRequisitionsAsync(CreatePurchaseOrderFromRequisitionsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/from-requisitions", request, cancellationToken)).ReadLedgerAsync<PurchaseOrderDto>(cancellationToken);

    public async Task<PurchaseOrderDto> UpdateAsync(Guid purchaseOrderId, SavePurchaseOrderRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{purchaseOrderId}", request, cancellationToken)).ReadLedgerAsync<PurchaseOrderDto>(cancellationToken);

    public async Task<PurchaseOrderDto> IssueAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{purchaseOrderId}/issue", null, cancellationToken)).ReadLedgerAsync<PurchaseOrderDto>(cancellationToken);

    public async Task<PurchaseOrderDto> ChangeOrderAsync(Guid purchaseOrderId, ChangeOrderRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{purchaseOrderId}/change-orders", request, cancellationToken)).ReadLedgerAsync<PurchaseOrderDto>(cancellationToken);

    public async Task<PurchaseOrderDto> ReceiveAsync(Guid purchaseOrderId, ReceiveRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{purchaseOrderId}/receipts", request, cancellationToken)).ReadLedgerAsync<PurchaseOrderDto>(cancellationToken);

    public async Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListReceivableAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/receivable", cancellationToken)).ReadLedgerAsync<List<PurchaseOrderSummaryDto>>(cancellationToken);

    public async Task<PurchaseOrderDto> CloseAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{purchaseOrderId}/close", null, cancellationToken)).ReadLedgerAsync<PurchaseOrderDto>(cancellationToken);

    public async Task<PurchaseOrderDto> CancelAsync(Guid purchaseOrderId, PurchaseOrderReasonRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{purchaseOrderId}/cancel", request, cancellationToken)).ReadLedgerAsync<PurchaseOrderDto>(cancellationToken);
}

public sealed class HttpThreeWayMatchService(HttpClient http) : IThreeWayMatchService
{
    public async Task<IReadOnlyList<MatchExceptionDto>> ListExceptionsAsync(bool includeOverridden = false, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/purchasing/match-exceptions?includeOverridden={includeOverridden}", cancellationToken)).ReadLedgerAsync<List<MatchExceptionDto>>(cancellationToken);

    public async Task<MatchExceptionDto> OverrideAsync(Guid billLineId, MatchOverrideRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/purchasing/match-exceptions/{billLineId}/override", request, cancellationToken)).ReadLedgerAsync<MatchExceptionDto>(cancellationToken);

    public async Task<SaveBillRequest> BuildBillAsync(CreateBillFromPurchaseOrderRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/purchasing/bills/from-order", request, cancellationToken)).ReadLedgerAsync<SaveBillRequest>(cancellationToken);
}

public sealed class HttpCommitmentService(HttpClient http) : ICommitmentService
{
    public async Task<ProjectCommitmentsDto> GetProjectCommitmentsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/purchasing/commitments/projects/{projectId}", cancellationToken)).ReadLedgerAsync<ProjectCommitmentsDto>(cancellationToken);
}

public static class ProcurementClientRegistration
{
    public static IServiceCollection AddProcurementClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IRequisitionService, HttpRequisitionService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPurchaseOrderService, HttpPurchaseOrderService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IThreeWayMatchService, HttpThreeWayMatchService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICommitmentService, HttpCommitmentService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
