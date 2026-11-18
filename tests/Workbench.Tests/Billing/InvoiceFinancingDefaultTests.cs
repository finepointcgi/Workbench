using Workbench.Application.Billing;
using Workbench.Contracts.Billing;

namespace Workbench.Tests.Billing;

/// <summary>
/// A host without the government contracting module gets <see cref="NoInvoiceFinancing"/>: invoices are generated, approved and voided
/// as for a contract with no financing, and the financing-only invoice types simply have nothing to bill. The module's own tests
/// (in the private repository) cover the real hook.
/// </summary>
public sealed class InvoiceFinancingDefaultTests
{
    [Fact]
    public void The_default_financing_is_none()
    {
        Assert.Same(NoInvoiceFinancing.Instance, new InvoiceGenerator(new BillingCostSource([])).Financing);
        Assert.Null(BillingKit.Financing); // The shipped suite builds its billing services without any module.
    }

    [Fact]
    public async Task A_delivery_invoice_is_approved_and_voided_without_any_liquidation()
    {
        var kit = await BillingKit.CreateAsync();

        var approved = await kit.ApprovedAsync(InvoiceKind.TimeAndMaterials, BillingKit.October, BillingKit.OctoberEnd);

        Assert.Equal(InvoiceState.Approved, approved.Summary.Status);
        Assert.DoesNotContain(approved.Lines, l => l.Kind == InvoiceLineType.Liquidation);
        var voided = await kit.Invoices.VoidAsync(approved.Summary.Id, new VoidInvoiceRequest("Test"));
        Assert.Equal(InvoiceState.Void, voided.Summary.Status);
    }

    [Fact]
    public async Task Performance_based_payments_have_nothing_to_bill_and_are_not_offered()
    {
        var kit = await BillingKit.CreateAsync();

        var contract = (await kit.Invoices.ListBillingContractsAsync()).Single(c => c.Id == kit.ContractId);
        Assert.DoesNotContain(InvoiceKind.PerformanceBased, contract.SuggestedTypes);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => kit.Invoices.GenerateDraftAsync(
            new GenerateInvoiceRequest(kit.ContractId, InvoiceKind.PerformanceBased, BillingKit.October, BillingKit.OctoberEnd)));
        Assert.Contains("performance-based", ex.Message);
    }

    [Fact]
    public async Task The_print_data_has_no_preferred_government_form()
    {
        var kit = await BillingKit.CreateAsync();
        var approved = await kit.ApprovedAsync(InvoiceKind.TimeAndMaterials, BillingKit.October, BillingKit.OctoberEnd);

        Assert.Null((await kit.Invoices.GetPrintAsync(approved.Summary.Id)).PreferredForm);
    }
}
