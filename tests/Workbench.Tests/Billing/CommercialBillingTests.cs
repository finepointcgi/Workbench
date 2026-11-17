using Microsoft.EntityFrameworkCore;
using Workbench.Application.Billing;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Tests.Billing;

/// <summary>Agreements without CLINs: each billing model goes through the ordinary invoice pipeline (draft, approval, GL).</summary>
public sealed class CommercialBillingTests
{
    private static readonly DateOnly Oct1 = new(2026, 10, 1);
    private static readonly DateOnly Oct31 = new(2026, 10, 31);
    private static readonly DateOnly Nov1 = new(2026, 11, 1);
    private static readonly DateOnly Nov30 = new(2026, 11, 30);

    // ---- Time and materials on a rate card, no CLINs, no ceiling or funding ----

    [Fact]
    public async Task Time_and_materials_bill_hours_at_the_rate_card_and_expenses_with_no_clin_funding_or_ceiling()
    {
        var kit = await CommercialKit.CreateAsync(ContractType.TimeAndMaterials);
        var plan = await kit.Agreements.SavePlanAsync(kit.ContractId, new SaveAgreementBillingPlanRequest(AgreementModel.TimeAndMaterials));
        Assert.Equal(AgreementModel.TimeAndMaterials, plan.Model);

        var draft = await kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kit.ContractId, InvoiceKind.TimeAndMaterials, Oct1, Nov30));
        var labor = Assert.Single(draft.Lines, l => l.Kind == InvoiceLineType.Labor);
        Assert.Equal(22m, labor.Quantity);
        Assert.Equal(150m, labor.Rate);
        Assert.Equal(3_300m, labor.Amount);
        Assert.Equal(200m, Assert.Single(draft.Lines, l => l.Kind == InvoiceLineType.Odc).Amount);
        Assert.All(draft.Lines, l => Assert.Null(l.ClinId));
        Assert.Empty(draft.Funding);
        Assert.Equal(3_500m, draft.Summary.Total);

        var approved = await kit.Invoices.ApproveAsync(draft.Summary.Id, new ApproveInvoiceRequest());
        Assert.Equal(InvoiceState.Approved, approved.Summary.Status);
        Assert.Equal(3_500m, await kit.Gl.BalanceAsync("1110"));
        Assert.Equal(-3_500m, await kit.Gl.BalanceAsync("4020"));

        // Billed once: the same period has nothing left to bill.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kit.ContractId, InvoiceKind.TimeAndMaterials, Oct1, Nov30)));
    }

    [Fact]
    public async Task A_contract_without_clins_or_a_plan_bills_exactly_as_before()
    {
        var kit = await CommercialKit.CreateAsync(ContractType.TimeAndMaterials);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kit.ContractId, InvoiceKind.TimeAndMaterials, Oct1, Nov30)));
        Assert.Contains("no exercised", ex.Message, StringComparison.Ordinal);
        Assert.Null(await kit.Agreements.GetPlanAsync(kit.ContractId));
        Assert.Empty(await kit.Agreements.ListDuePeriodsAsync());
        var retainer = await Assert.ThrowsAsync<ArgumentException>(() => kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kit.ContractId, InvoiceKind.Retainer, Oct1, Oct31)));
        Assert.Contains("isn't set up", retainer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_is_for_agreements_without_clins()
    {
        var kit = await CommercialKit.CreateAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<Clin>().Add(new Clin
            {
                ContractId = kit.ContractId, Number = "0001", Description = "CLIN", Pricing = ClinPricing.FirmFixedPrice,
                FundedValue = 1m, CeilingValue = 1m, PeriodStart = Oct1, PeriodEnd = new DateOnly(2027, 9, 30),
            });
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => kit.RetainerAsync());
        Assert.Contains("has CLINs", ex.Message, StringComparison.Ordinal);
    }

    // ---- Fixed fee with milestones ----

    [Fact]
    public async Task A_fixed_fee_bills_each_milestone_when_it_is_marked_complete()
    {
        var kit = await CommercialKit.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Agreements.CreateMilestoneAsync(kit.ContractId, new SaveAgreementMilestoneRequest("Discovery", 5_000m, Nov30)));

        await kit.Agreements.SavePlanAsync(kit.ContractId, new SaveAgreementBillingPlanRequest(AgreementModel.FixedFeeMilestones));
        var discovery = await kit.Agreements.CreateMilestoneAsync(kit.ContractId, new SaveAgreementMilestoneRequest("Discovery", 5_000m, Nov30));
        var design = await kit.Agreements.CreateMilestoneAsync(kit.ContractId, new SaveAgreementMilestoneRequest("Design", 3_000m, new DateOnly(2027, 1, 31)));
        Assert.False(discovery.IsComplete);

        var done = await kit.Agreements.CompleteMilestoneAsync(discovery.Id, new CompleteAgreementMilestoneRequest(new DateOnly(2026, 11, 18)));
        Assert.NotNull(done.Draft);
        Assert.Equal(InvoiceKind.FixedPriceMilestone, done.Draft!.Summary.Type);
        Assert.Equal(InvoiceState.Draft, done.Draft.Summary.Status);
        Assert.Equal(5_000m, done.Draft.Summary.Total);
        var line = Assert.Single(done.Draft.Lines);
        Assert.Equal(InvoiceLineType.Milestone, line.Kind);
        Assert.Null(line.ClinId);
        Assert.True(done.Milestone.IsBilled);

        var approved = await kit.Invoices.ApproveAsync(done.Draft.Summary.Id, new ApproveInvoiceRequest());
        Assert.Equal(-5_000m, await kit.Gl.BalanceAsync("4040"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Agreements.ReopenMilestoneAsync(discovery.Id));

        // The second stage isn't billed until it is complete.
        var listed = await kit.Agreements.ListMilestonesAsync(kit.ContractId);
        Assert.False(listed.Single(m => m.Id == design.Id).IsBilled);
        var second = await kit.Agreements.CompleteMilestoneAsync(design.Id, new CompleteAgreementMilestoneRequest(new DateOnly(2026, 11, 19), CreateDraftInvoice: false));
        Assert.Null(second.Draft);
        Assert.False(second.Milestone.IsBilled);

        // Voiding the first invoice releases its milestone to be billed again.
        await kit.Invoices.VoidAsync(approved.Summary.Id, new VoidInvoiceRequest("Customer asked for a re-issue"));
        Assert.False((await kit.Agreements.ListMilestonesAsync(kit.ContractId)).Single(m => m.Id == discovery.Id).IsBilled);
        var rebill = await kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kit.ContractId, InvoiceKind.FixedPriceMilestone, Nov1, Nov30));
        Assert.Equal(8_000m, rebill.Summary.Total);
    }

    [Fact]
    public async Task Milestones_validate_their_amount_and_can_change_until_billed()
    {
        var kit = await CommercialKit.CreateAsync();
        await kit.Agreements.SavePlanAsync(kit.ContractId, new SaveAgreementBillingPlanRequest(AgreementModel.FixedFeeMilestones, InvoiceOnComplete: false));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Agreements.CreateMilestoneAsync(kit.ContractId, new SaveAgreementMilestoneRequest("Free", 0m, Nov30)));

        var m = await kit.Agreements.CreateMilestoneAsync(kit.ContractId, new SaveAgreementMilestoneRequest("Kickoff", 1_000m, Nov30));
        var updated = await kit.Agreements.UpdateMilestoneAsync(m.Id, new SaveAgreementMilestoneRequest("Kickoff workshop", 1_200m, Nov30));
        Assert.Equal(1_200m, updated.Amount);
        var done = await kit.Agreements.CompleteMilestoneAsync(m.Id, new CompleteAgreementMilestoneRequest(new DateOnly(2026, 11, 10)));
        Assert.Null(done.Draft); // the plan says not to invoice on completion
        await kit.Agreements.ReopenMilestoneAsync(m.Id);
        await kit.Agreements.DeleteMilestoneAsync(m.Id);
        Assert.Empty(await kit.Agreements.ListMilestonesAsync(kit.ContractId));
    }

    // ---- Monthly retainer ----

    [Fact]
    public async Task A_retainer_bills_a_fixed_amount_each_period_once()
    {
        var kit = await CommercialKit.CreateAsync();
        var plan = await kit.RetainerAsync();
        Assert.Equal(Oct1, plan.NextPeriodFrom);

        var due = await kit.Agreements.ListDuePeriodsAsync();
        Assert.Equal([Oct1, Nov1], due.Select(d => d.PeriodFrom).ToArray());
        Assert.All(due, d => Assert.Equal(4_000m, d.Amount));

        var generated = await kit.Agreements.GenerateDueDraftsAsync();
        Assert.Equal(2, generated.Created);
        Assert.Equal(0, (await kit.Agreements.GenerateDueDraftsAsync()).Created); // idempotent: both periods already have a draft

        var october = await kit.Invoices.ListAsync(new InvoiceQuery(ContractId: kit.ContractId, Type: InvoiceKind.Retainer));
        Assert.Equal(2, october.Count);
        Assert.All(october, i => Assert.Equal(4_000m, i.Total));

        var first = october.Single(i => i.PeriodFrom == Oct1);
        var detail = await kit.Invoices.GetAsync(first.Id);
        Assert.Equal(InvoiceLineType.Retainer, Assert.Single(detail.Lines).Kind);
        Assert.Contains("October 2026", detail.Lines[0].Description, StringComparison.Ordinal);
        await kit.Invoices.ApproveAsync(first.Id, new ApproveInvoiceRequest());
        await kit.Invoices.ApproveAsync(october.Single(i => i.PeriodFrom == Nov1).Id, new ApproveInvoiceRequest());

        Assert.Equal(8_000m, await kit.Gl.BalanceAsync("1110"));
        Assert.Equal(-8_000m, await kit.Gl.BalanceAsync("4040"));
        Assert.Empty(await kit.Agreements.ListDuePeriodsAsync());
        var after = (await kit.Agreements.GetPlanAsync(kit.ContractId))!;
        Assert.Equal(2, after.PeriodsBilled);
        Assert.Equal(8_000m, after.BilledToDate);
        Assert.Equal(new DateOnly(2026, 12, 1), after.NextPeriodFrom);

        var again = await Assert.ThrowsAsync<ArgumentException>(() => kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kit.ContractId, InvoiceKind.Retainer, Oct1, Oct31)));
        Assert.Contains("already billed", again.Message, StringComparison.Ordinal);
        var off = await Assert.ThrowsAsync<ArgumentException>(() => kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kit.ContractId, InvoiceKind.Retainer, new DateOnly(2026, 10, 5), new DateOnly(2026, 11, 4))));
        Assert.Contains("isn't one of its periods", off.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_last_retainer_period_is_cut_at_the_end_date_and_prorated()
    {
        var kit = await CommercialKit.CreateAsync();
        await kit.RetainerAsync(end: new DateOnly(2026, 11, 15));

        var due = await kit.Agreements.ListDuePeriodsAsync();
        Assert.Equal(2, due.Count);
        Assert.Equal(new DateOnly(2026, 11, 15), due[1].PeriodTo);
        Assert.Equal(2_000m, due[1].Amount);
        var draft = await kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kit.ContractId, InvoiceKind.Retainer, Nov1, new DateOnly(2026, 11, 15)));
        Assert.Equal(2_000m, draft.Summary.Total);
        Assert.Contains("prorated", draft.Lines[0].Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unused_retainer_hours_are_off_by_default_and_expire_or_roll_over_when_chosen()
    {
        // Off: the invoice is the fixed amount and says nothing about hours.
        var off = await CommercialKit.CreateAsync();
        await off.RetainerAsync(included: 20m);
        var plain = await off.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(off.ContractId, InvoiceKind.Retainer, Oct1, Oct31));
        Assert.DoesNotContain("hours used", plain.Lines[0].Description, StringComparison.Ordinal);
        Assert.Single(plain.Lines);

        // Rollover: October used 16 of 20 hours, so 4 carry into November (6 used of 20 + 4).
        var roll = await CommercialKit.CreateAsync();
        await roll.RetainerAsync(included: 20m, unused: UnusedHoursKind.Rollover);
        var october = await roll.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(roll.ContractId, InvoiceKind.Retainer, Oct1, Oct31));
        Assert.Contains("16 of 20 hours used", october.Lines[0].Description, StringComparison.Ordinal);
        Assert.Contains(october.Warnings, w => w.Contains("4 retainer hours were unused", StringComparison.Ordinal) && w.Contains("carry", StringComparison.Ordinal));
        var november = await roll.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(roll.ContractId, InvoiceKind.Retainer, Nov1, Nov30));
        Assert.Contains("6 of 24 hours used", november.Lines[0].Description, StringComparison.Ordinal);
        Assert.Contains("20 included + 4 carried over", november.Lines[0].Description, StringComparison.Ordinal);

        // Expire: nothing carries; hours over the included amount bill at the overage rate.
        var expire = await CommercialKit.CreateAsync();
        await expire.RetainerAsync(included: 10m, unused: UnusedHoursKind.Expire, overage: 200m);
        var over = await expire.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(expire.ContractId, InvoiceKind.Retainer, Oct1, Oct31));
        Assert.Equal(2, over.Lines.Count);
        Assert.Equal(6m, over.Lines[1].Quantity);
        Assert.Equal(1_200m, over.Lines[1].Amount);
        Assert.Equal(5_200m, over.Summary.Total);
        var shortMonth = await expire.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(expire.ContractId, InvoiceKind.Retainer, Nov1, Nov30));
        Assert.Single(shortMonth.Lines); // 6 hours of 10: unused hours expire
        Assert.Contains(shortMonth.Warnings, w => w.Contains("they expire", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tracking_hours_needs_the_included_hours()
    {
        var kit = await CommercialKit.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => kit.RetainerAsync(unused: UnusedHoursKind.Expire));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.RetainerAsync(amount: 0m));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.RetainerAsync(start: Oct1, end: new DateOnly(2026, 9, 30)));
    }

    // ---- Recurring / subscription charges ----

    [Fact]
    public async Task A_recurring_charge_generates_a_draft_per_period_automatically_and_tells_finance()
    {
        var kit = await CommercialKit.CreateAsync();
        await kit.AddFinanceAsync();
        await kit.Agreements.SavePlanAsync(kit.ContractId, new SaveAgreementBillingPlanRequest(AgreementModel.Recurring, BillingCadenceKind.Quarterly,
            new DateOnly(2026, 7, 1), new DateOnly(2027, 6, 30), 1_500m, "Managed services"));

        var due = await kit.Agreements.ListDuePeriodsAsync();
        Assert.Equal([new DateOnly(2026, 7, 1), Oct1], due.Select(d => d.PeriodFrom).ToArray());
        Assert.Equal(new DateOnly(2026, 9, 30), due[0].PeriodTo);

        Assert.Equal(2, await kit.Agreements.RunAutoAsync());
        Assert.Contains(kit.Publisher.Sent, s => s.Kind == AgreementBillingService.DraftsKind && s.Email == "finance@example.com");
        Assert.Equal(0, await kit.Agreements.RunAutoAsync());

        var drafts = await kit.Invoices.ListAsync(new InvoiceQuery(ContractId: kit.ContractId, Type: InvoiceKind.Recurring));
        Assert.Equal(2, drafts.Count);
        Assert.All(drafts, d => Assert.Equal(InvoiceState.Draft, d.Status)); // never approved on its own
        var detail = await kit.Invoices.GetAsync(drafts[0].Id);
        Assert.Equal(InvoiceLineType.Recurring, detail.Lines[0].Kind);
        Assert.StartsWith("Managed services", detail.Lines[0].Description, StringComparison.Ordinal);
        await kit.Invoices.ApproveAsync(drafts[0].Id, new ApproveInvoiceRequest());
        Assert.Equal(-1_500m, await kit.Gl.BalanceAsync("4040"));
    }

    [Fact]
    public async Task A_plan_that_does_not_generate_automatically_is_left_to_the_manual_button()
    {
        var kit = await CommercialKit.CreateAsync();
        await kit.RetainerAsync(auto: false);

        Assert.Equal(0, await kit.Agreements.RunAutoAsync());
        Assert.Equal(2, (await kit.Agreements.GenerateDueDraftsAsync()).Created);
    }

    [Fact]
    public async Task The_billing_model_cannot_change_once_invoices_exist_and_a_billed_plan_cannot_be_removed()
    {
        var kit = await CommercialKit.CreateAsync();
        await kit.RetainerAsync();
        var invoice = await kit.ApprovedRetainerAsync(Oct1, Oct31);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Agreements.SavePlanAsync(kit.ContractId, new SaveAgreementBillingPlanRequest(AgreementModel.FixedFeeMilestones)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.RetainerAsync(start: new DateOnly(2026, 10, 15)));
        Assert.Equal(5_000m, (await kit.RetainerAsync(amount: 5_000m)).Amount); // the amount can change for the periods still to bill
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Agreements.RemovePlanAsync(kit.ContractId));
        Assert.Equal(4_000m, invoice.Summary.Total);
    }
}
