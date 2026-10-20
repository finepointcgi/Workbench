using Workbench.Contracts.Billing;

namespace Workbench.Application.Materials;

/// <summary>
/// Turns a shipment's firm-fixed-price deliveries into a draft invoice through billing's public services: one completed
/// billing milestone per CLIN, then the contract's FFP milestone draft for the ship date. Billing owns everything after
/// that (funding checks, progress-payment and performance-based-payment liquidation, approval and posting).
/// </summary>
public sealed class ShipmentInvoicer(IBillingMilestoneService milestones, IInvoiceService invoices)
{
    public async Task<Guid> CreateAsync(
        Guid contractId, string shipment, string shipmentNumber, DateOnly shipDate, IReadOnlyList<(Guid ClinId, string Description, decimal Amount)> lines, CancellationToken ct)
    {
        foreach (var (clinId, description, amount) in lines)
        {
            var text = $"Delivery {shipmentNumber} ({shipment}): {description}";
            var milestone = await milestones.CreateAsync(contractId, new SaveMilestoneRequest(clinId, text.Length > 500 ? text[..500] : text, amount, shipDate), ct);
            await milestones.CompleteAsync(milestone.Id, new CompleteMilestoneRequest(shipDate), ct);
        }

        var invoice = await invoices.GenerateDraftAsync(new GenerateInvoiceRequest(contractId, InvoiceKind.FixedPriceMilestone, shipDate, shipDate, shipDate,
            Notes: $"Delivery of shipment {shipmentNumber} (DD Form 250)."), ct);
        return invoice.Summary.Id;
    }
}
