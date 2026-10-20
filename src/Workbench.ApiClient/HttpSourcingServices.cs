using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Purchasing;

namespace Workbench.ApiClient;

public sealed class HttpRfqService(HttpClient http) : IRfqService
{
    private const string Base = "api/v1/purchasing/rfqs";

    public async Task<IReadOnlyList<RfqSummaryDto>> ListAsync(RfqQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With(Base, ("status", query.Status), ("search", query.Search)), cancellationToken)).ReadLedgerAsync<List<RfqSummaryDto>>(cancellationToken);

    public async Task<RfqDto> GetAsync(Guid rfqId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{rfqId}", cancellationToken)).ReadLedgerAsync<RfqDto>(cancellationToken);

    public async Task<IReadOnlyList<OrderableRequisitionLineDto>> ListSourceableRequisitionLinesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/sourceable-lines", cancellationToken)).ReadLedgerAsync<List<OrderableRequisitionLineDto>>(cancellationToken);

    public async Task<RfqDto> CreateAsync(SaveRfqRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<RfqDto>(cancellationToken);

    public async Task<RfqDto> UpdateAsync(Guid rfqId, SaveRfqRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{rfqId}", request, cancellationToken)).ReadLedgerAsync<RfqDto>(cancellationToken);

    public async Task<RfqDto> IssueAsync(Guid rfqId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{rfqId}/issue", null, cancellationToken)).ReadLedgerAsync<RfqDto>(cancellationToken);

    public async Task<RfqDto> InviteVendorAsync(Guid rfqId, Guid vendorId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{rfqId}/vendors/{vendorId}", null, cancellationToken)).ReadLedgerAsync<RfqDto>(cancellationToken);

    public async Task<RfqDto> RecordQuoteAsync(Guid rfqId, SaveQuoteRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{rfqId}/quotes", request, cancellationToken)).ReadLedgerAsync<RfqDto>(cancellationToken);

    public async Task<RfqComparisonDto> GetComparisonAsync(Guid rfqId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{rfqId}/comparison", cancellationToken)).ReadLedgerAsync<RfqComparisonDto>(cancellationToken);

    public async Task<RfqDto> AwardAsync(Guid rfqId, AwardRfqRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{rfqId}/award", request, cancellationToken)).ReadLedgerAsync<RfqDto>(cancellationToken);

    public async Task<RfqDto> CancelAsync(Guid rfqId, RfqReasonRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{rfqId}/cancel", request, cancellationToken)).ReadLedgerAsync<RfqDto>(cancellationToken);
}

public sealed class HttpSupplierPortalAdminService(HttpClient http) : ISupplierPortalAdminService
{
    private const string Base = "api/v1/purchasing/supplier-portal";

    public async Task<IReadOnlyList<SupplierContactDto>> ListContactsAsync(Guid? vendorId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/contacts", ("vendorId", vendorId)), cancellationToken)).ReadLedgerAsync<List<SupplierContactDto>>(cancellationToken);

    public async Task<SupplierContactDto> AddContactAsync(SaveSupplierContactRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/contacts", request, cancellationToken)).ReadLedgerAsync<SupplierContactDto>(cancellationToken);

    public async Task<SupplierContactDto> SendSignInLinkAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/contacts/{contactId}/send-link", null, cancellationToken)).ReadLedgerAsync<SupplierContactDto>(cancellationToken);

    public async Task<SupplierContactDto> DeactivateContactAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/contacts/{contactId}/deactivate", null, cancellationToken)).ReadLedgerAsync<SupplierContactDto>(cancellationToken);

    public async Task<SupplierContactDto> ReactivateContactAsync(Guid contactId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/contacts/{contactId}/reactivate", null, cancellationToken)).ReadLedgerAsync<SupplierContactDto>(cancellationToken);

    public async Task<IReadOnlyList<SupplierPortalEventDto>> ListActivityAsync(Guid? vendorId = null, int take = 200, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/activity", ("vendorId", vendorId), ("take", take)), cancellationToken)).ReadLedgerAsync<List<SupplierPortalEventDto>>(cancellationToken);

    public async Task<IReadOnlyList<AcknowledgementDto>> ListAcknowledgementsAsync(Guid? purchaseOrderId = null, bool needsReviewOnly = false, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/acknowledgements", ("purchaseOrderId", purchaseOrderId), ("needsReview", needsReviewOnly)), cancellationToken))
            .ReadLedgerAsync<List<AcknowledgementDto>>(cancellationToken);

    public async Task<AcknowledgementDto> MarkAcknowledgementReviewedAsync(Guid acknowledgementId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/acknowledgements/{acknowledgementId}/reviewed", null, cancellationToken)).ReadLedgerAsync<AcknowledgementDto>(cancellationToken);

    public async Task<IReadOnlyList<ShipNoticeDto>> ListShipNoticesAsync(Guid? purchaseOrderId = null, bool inTransitOnly = false, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/ship-notices", ("purchaseOrderId", purchaseOrderId), ("inTransit", inTransitOnly)), cancellationToken))
            .ReadLedgerAsync<List<ShipNoticeDto>>(cancellationToken);

    public async Task<ShipNoticeDto> ReceiveShipNoticeAsync(Guid shipNoticeId, DateOnly receivedOn, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/ship-notices/{shipNoticeId}/receive", new { ReceivedOn = receivedOn }, cancellationToken)).ReadLedgerAsync<ShipNoticeDto>(cancellationToken);
}

public sealed class HttpEdiService(HttpClient http) : IEdiService
{
    private const string Base = "api/v1/edi";

    public async Task<IReadOnlyList<EdiPartnerDto>> ListPartnersAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/partners", cancellationToken)).ReadLedgerAsync<List<EdiPartnerDto>>(cancellationToken);

    public async Task<EdiPartnerDto> SavePartnerAsync(Guid? partnerId, SaveEdiPartnerRequest request, CancellationToken cancellationToken = default) =>
        await (partnerId is { } id
            ? await http.PutAsJsonAsync($"{Base}/partners/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync($"{Base}/partners", request, cancellationToken)).ReadLedgerAsync<EdiPartnerDto>(cancellationToken);

    public async Task<IReadOnlyList<EdiMessageDto>> ListLogAsync(EdiLogQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/messages", ("direction", query.Direction), ("status", query.Status), ("transactionSet", query.TransactionSet),
            ("vendorId", query.VendorId), ("take", query.Take)), cancellationToken)).ReadLedgerAsync<List<EdiMessageDto>>(cancellationToken);

    public async Task<EdiMessageDetailDto> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/messages/{messageId}", cancellationToken)).ReadLedgerAsync<EdiMessageDetailDto>(cancellationToken);

    public async Task<EdiMessageDto> SendPurchaseOrderAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/orders/{purchaseOrderId}/send", null, cancellationToken)).ReadLedgerAsync<EdiMessageDto>(cancellationToken);

    public async Task<EdiPollResultDto> PollAsync(CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/poll", null, cancellationToken)).ReadLedgerAsync<EdiPollResultDto>(cancellationToken);

    public async Task<IReadOnlyList<EdiMessageDto>> ImportAsync(string fileName, string content, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/import", new { FileName = fileName, Content = content }, cancellationToken)).ReadLedgerAsync<List<EdiMessageDto>>(cancellationToken);

    public async Task<IReadOnlyList<EdiMessageDto>> RetryAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/messages/{messageId}/retry", null, cancellationToken)).ReadLedgerAsync<List<EdiMessageDto>>(cancellationToken);
}

public static class SourcingClientRegistration
{
    /// <summary>RFQs, the supplier portal's buyer side and EDI. The vendor-facing portal has no client: vendors use the web pages.</summary>
    public static IServiceCollection AddSourcingClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IRfqService, HttpRfqService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ISupplierPortalAdminService, HttpSupplierPortalAdminService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IEdiService, HttpEdiService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
