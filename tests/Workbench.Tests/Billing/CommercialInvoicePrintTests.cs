using Workbench.Contracts.Billing;

namespace Workbench.Tests.Billing;

/// <summary>What the commercial invoice layout prints, and which layout opens.</summary>
public sealed class CommercialInvoicePrintTests
{
    [Fact]
    public async Task An_agreement_prints_the_commercial_layout_with_the_organizations_terms_and_remittance_details()
    {
        var kit = await CommercialKit.CreateAsync();
        await kit.RetainerAsync();
        await kit.Settings.SaveAsync(new SaveBillingSettingsRequest("Northwind Consulting LLC", "PO Box 12\nSeattle WA 98101", "ACH: routing 000, account 111. Reference the invoice number.",
            "Net 30. Late balances accrue 1.5% a month.", 30, "Thank you for your business.", "billing@northwind.example", 3, InvoiceLayoutKind.Government));
        var invoice = await kit.ApprovedRetainerAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        var print = await kit.Invoices.GetPrintAsync(invoice.Summary.Id);

        var r = print.Remittance!;
        Assert.Equal(InvoiceLayoutKind.Commercial, r.Layout);
        Assert.Equal("Northwind Consulting LLC", r.SellerName);
        Assert.Equal("Northwind Consulting LLC", print.Payee.Name);
        Assert.Contains("Seattle", r.RemitToAddress, StringComparison.Ordinal);
        Assert.Contains("ACH", r.RemittanceInstructions, StringComparison.Ordinal);
        Assert.Equal("Net 30. Late balances accrue 1.5% a month.", r.PaymentTermsText);
        Assert.Equal(30, r.TermsDays);
        Assert.Equal(invoice.Summary.DueDate, r.DueDate);
        Assert.Equal("billing@northwind.example", r.BillingContact);
        Assert.Equal("Thank you for your business.", r.Footer);
    }

    [Fact]
    public async Task The_organizations_default_layout_applies_to_agreements_without_a_plan_only_when_chosen()
    {
        var kit = await CommercialKit.CreateAsync();
        await kit.Agreements.SavePlanAsync(kit.ContractId, new SaveAgreementBillingPlanRequest(AgreementModel.TimeAndMaterials));
        var invoice = await kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kit.ContractId, InvoiceKind.TimeAndMaterials, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30)));

        var print = await kit.Invoices.GetPrintAsync(invoice.Summary.Id);

        Assert.Equal(InvoiceLayoutKind.Commercial, print.Remittance!.Layout);
        Assert.Equal("Test Co", print.Remittance.SellerName); // no organization name saved: the Billing configuration still names the seller
        Assert.Equal(InvoiceLayoutKind.Government, (await kit.Settings.GetAsync()).DefaultLayout);
    }
}
