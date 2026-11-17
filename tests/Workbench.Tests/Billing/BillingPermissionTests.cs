using Workbench.Application;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Billing;

/// <summary>The real registrations: billing is Finance/Admin; pm@ (manager of 1001 on C1) reads C1's invoices only.</summary>
public sealed class BillingPermissionTests
{
    private static async Task<(SeededHost Seeded, ProjectAccountingFixture Fx, Guid C1Invoice, Guid C2Invoice)> CreateAsync()
    {
        var seeded = await SeededHost.CreateAsync();
        var fx = await ProjectAccountingFixture.SeedAsync(seeded);
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Invoice Invoice(Guid contractId, string number) => new()
        {
            ContractId = contractId,
            CustomerId = db.Set<Domain.ProjectAccounting.Contract>().First(c => c.Id == contractId).CustomerId,
            Sequence = 1,
            Number = number,
            Type = InvoiceType.TimeAndMaterials,
            PeriodFrom = new DateOnly(2026, 10, 1),
            PeriodTo = new DateOnly(2026, 10, 31),
            InvoiceDate = new DateOnly(2026, 11, 5),
            DueDate = new DateOnly(2026, 12, 5),
            CreatedBy = Finance,
        };
        var c1 = Invoice(fx.Contract1, "C1-0001");
        var c2 = Invoice(fx.Contract2, "C2-0001");
        db.Set<Invoice>().AddRange(c1, c2);
        await db.SaveChangesAsync();
        return (seeded, fx, c1.Id, c2.Id);
    }

    [Theory]
    [InlineData(Viewer)]
    [InlineData(Timekeeper)]
    [InlineData(Outsider)]
    public async Task Billing_is_closed_to_everyone_outside_finance_who_manages_no_contract(string email)
    {
        var (seeded, fx, c1, _) = await CreateAsync();
        await using var _ = seeded;
        var host = seeded.As(email).Host;

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IInvoiceService>().ListAsync(new InvoiceQuery()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IInvoiceService>().GetAsync(c1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IInvoiceService>().GetPrintAsync(c1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IBillingMilestoneService>().ListAsync(fx.Contract1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IPaymentService>().ListAsync(new PaymentQuery()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IArReportService>().GetAgingAsync());
    }

    [Fact]
    public async Task A_project_manager_reads_their_contracts_invoices_but_changes_nothing()
    {
        var (seeded, fx, c1, c2) = await CreateAsync();
        await using var _ = seeded;
        var host = seeded.As(Owner).Host;
        var invoices = host.Get<IInvoiceService>();

        Assert.Equal("C1-0001", Assert.Single(await invoices.ListAsync(new InvoiceQuery())).Number);
        Assert.Equal(c1, (await invoices.GetAsync(c1)).Summary.Id);
        Assert.NotNull(await invoices.GetPrintAsync(c1));
        Assert.Equal(fx.Contract1, Assert.Single(await invoices.ListBillingContractsAsync()).Id);
        Assert.Empty(await host.Get<IBillingMilestoneService>().ListAsync(fx.Contract1));
        Assert.NotNull(await invoices.GetContractSummaryAsync(fx.Contract1));

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => invoices.GetAsync(c2));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => invoices.ListAsync(new InvoiceQuery(ContractId: fx.Contract2)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => invoices.GetContractSummaryAsync(fx.Contract2));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => invoices.ApproveAsync(c1, new ApproveInvoiceRequest()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => invoices.VoidAsync(c1, new VoidInvoiceRequest("No")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => invoices.ExportWawfCsvAsync(c1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => invoices.GenerateDraftAsync(
            new GenerateInvoiceRequest(fx.Contract1, InvoiceKind.CostPlus, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IBillingMilestoneService>().CreateAsync(fx.Contract1,
            new SaveMilestoneRequest(Guid.NewGuid(), "M", 1m, new DateOnly(2026, 10, 1))));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IPaymentService>().ListAsync(new PaymentQuery()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IArReportService>().GetAgingAsync());
    }

    [Theory]
    [InlineData(Finance)]
    [InlineData(Admin)]
    public async Task Finance_and_admins_see_and_work_every_contract(string email)
    {
        var (seeded, _, _, c2) = await CreateAsync();
        await using var _ = seeded;
        var host = seeded.As(email).Host;

        Assert.Equal(2, (await host.Get<IInvoiceService>().ListAsync(new InvoiceQuery())).Count);
        Assert.Equal(c2, (await host.Get<IInvoiceService>().GetAsync(c2)).Summary.Id);
        Assert.Empty(await host.Get<IPaymentService>().ListAsync(new PaymentQuery()));
        Assert.Equal(0m, (await host.Get<IArReportService>().GetAgingAsync()).Totals.Total);
        Assert.NotEmpty(await host.Get<IArReportService>().ListCustomersAsync());
    }

    // ---- Commercial billing (agreements, credits, write-offs, settings) ----

    [Theory]
    [InlineData(Viewer)]
    [InlineData(Timekeeper)]
    [InlineData(Outsider)]
    public async Task Commercial_billing_is_closed_to_everyone_outside_finance_who_manages_no_contract(string email)
    {
        var (seeded, fx, c1, _) = await CreateAsync();
        await using var _ = seeded;
        var host = seeded.As(email).Host;

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IAgreementBillingService>().ListPlansAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IAgreementBillingService>().GetPlanAsync(fx.Contract1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IAgreementBillingService>().ListMilestonesAsync(fx.Contract1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IAgreementBillingService>().ListDuePeriodsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IReceivablesService>().GetAgingAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IReceivablesService>().ListCreditsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IReceivablesService>().WriteOffAsync(c1, new WriteOffInvoiceRequest(1m, "No")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IBillingSettingsService>().GetAsync());
    }

    [Fact]
    public async Task A_project_manager_reads_their_contracts_agreement_but_changes_nothing()
    {
        var (seeded, fx, c1, c2) = await CreateAsync();
        await using var _ = seeded;
        var host = seeded.As(Owner).Host;
        var agreements = host.Get<IAgreementBillingService>();

        Assert.Null(await agreements.GetPlanAsync(fx.Contract1));
        Assert.Empty(await agreements.ListMilestonesAsync(fx.Contract1));
        Assert.Empty(await agreements.ListPlansAsync());

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => agreements.GetPlanAsync(fx.Contract2));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => agreements.SavePlanAsync(fx.Contract1, new SaveAgreementBillingPlanRequest(AgreementModel.TimeAndMaterials)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => agreements.CreateMilestoneAsync(fx.Contract1, new SaveAgreementMilestoneRequest("M", 1m, new DateOnly(2026, 12, 1))));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => agreements.CompleteMilestoneAsync(Guid.NewGuid(), new CompleteAgreementMilestoneRequest(new DateOnly(2026, 12, 1))));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => agreements.GenerateDueDraftsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IReceivablesService>().WriteOffAsync(c1, new WriteOffInvoiceRequest(1m, "No")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IReceivablesService>().ApplyCreditAsync(Guid.NewGuid(), new ApplyCreditRequest([])));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IBillingSettingsService>().SaveAsync(
            new SaveBillingSettingsRequest(null, null, null, null, 30, null, null, 3, InvoiceLayoutKind.Government)));
        Assert.NotEqual(c1, c2);
    }

    [Theory]
    [InlineData(Finance)]
    [InlineData(Admin)]
    public async Task Finance_and_admins_work_every_agreement_and_the_organizations_billing_settings(string email)
    {
        var (seeded, fx, _, _) = await CreateAsync();
        await using var _ = seeded;
        var host = seeded.As(email).Host;

        Assert.Empty(await host.Get<IAgreementBillingService>().ListPlansAsync());
        var plan = await host.Get<IAgreementBillingService>().SavePlanAsync(fx.Contract2, new SaveAgreementBillingPlanRequest(AgreementModel.TimeAndMaterials));
        Assert.Equal(fx.Contract2, plan.ContractId);
        Assert.Equal(1m, (await host.Get<IReceivablesService>().GetAgingAsync()).Aging.Totals.Total + 1m);
        Assert.Equal(45, (await host.Get<IBillingSettingsService>().SaveAsync(
            new SaveBillingSettingsRequest("Acme", null, null, null, 45, null, null, 3, InvoiceLayoutKind.Commercial))).DefaultPaymentTermsDays);
    }
}
