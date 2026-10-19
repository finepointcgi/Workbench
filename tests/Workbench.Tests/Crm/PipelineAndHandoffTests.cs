using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Crm;
using Workbench.Contracts.MyTodo;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Crm;

/// <summary>Forecast and win/loss over the services, B&amp;P spending from time, the won handoff and the home to-dos.</summary>
public sealed class PipelineAndHandoffTests
{
    [Fact]
    public async Task The_forecast_buckets_by_the_fiscal_calendar_and_weights_by_pwin_and_pgo()
    {
        await using var kit = await CrmKit.CreateAsync();
        await kit.NewAsync("A", 1_000_000m, 50, 80, award: new DateOnly(2026, 11, 15));
        await kit.NewAsync("B", 2_000_000m, 25, 100, award: new DateOnly(2027, 3, 1), kind: OpportunityKind.Recompete);
        await kit.NewAsync("C", 400_000m, 50, 50);

        var forecast = await kit.As(Finance).Pipeline.GetForecastAsync();

        Assert.Equal(["FY27 Q1", "FY27 Q2"], forecast.Quarters.Select(q => q.Label));
        Assert.Equal(400_000m, forecast.Quarters[0].NewWeighted);
        Assert.Equal(500_000m, forecast.Quarters[1].RecompeteWeighted);
        Assert.Equal(1, forecast.UndatedCount);
        Assert.Equal(1_000_000m, forecast.WeightedValue);

        var board = await kit.Pipeline.GetBoardAsync();
        var identified = board.Lanes.Single(l => l.Stage == OpportunityStage.Identified);
        Assert.Equal(3_400_000m, identified.TotalValue);
        Assert.Equal(1_000_000m, identified.WeightedValue);
        Assert.Equal("Qualification", board.Lanes.Single(l => l.Stage == OpportunityStage.Qualified).GateName);
        Assert.DoesNotContain(board.Lanes, l => l.Stage == OpportunityStage.Won);
    }

    [Fact]
    public async Task Win_loss_counts_outcomes_by_reason_agency_and_kind()
    {
        await using var kit = await CrmKit.CreateAsync();
        var won = await kit.AdvanceAsync((await kit.NewAsync("Won", 1_000_000m)).Id, OpportunityStage.Submitted);
        await kit.Opportunities.RecordOutcomeAsync(won.Id, new RecordOutcomeRequest(OpportunityStage.Won, OutcomeReason.Technical, AwardedValue: 900_000m));
        var lost = await kit.AdvanceAsync((await kit.NewAsync("Lost", 3_000_000m, kind: OpportunityKind.Recompete)).Id, OpportunityStage.Submitted);
        await kit.Opportunities.RecordOutcomeAsync(lost.Id, new RecordOutcomeRequest(OpportunityStage.Lost, OutcomeReason.Price, AwardedTo: "Competitor"));
        var noBid = await kit.NewAsync("No-bid");
        await kit.Opportunities.RecordOutcomeAsync(noBid.Id, new RecordOutcomeRequest(OpportunityStage.NoBid, OutcomeReason.NoCapacity));

        var wl = await kit.Pipeline.GetWinLossAsync();

        Assert.Equal((1, 1, 1), (wl.Won, wl.Lost, wl.NoBid));
        Assert.Equal(900_000m, wl.WonValue);
        Assert.Equal(3_000_000m, wl.LostValue);
        Assert.Equal(50m, wl.WinRateByCount);
        Assert.Equal(23.1m, wl.WinRateByValue);
        Assert.Equal(1, wl.Reasons.Single(r => r.Reason == OutcomeReason.Price).Lost);
        Assert.Equal("PEO Enterprise Information Systems", Assert.Single(wl.ByAgency).Group);
        Assert.Equal("FY2027", Assert.Single(wl.ByFiscalYear).Group);
        Assert.Equal(1, wl.ByKind.Single(k => k.Group == "Recompete").Lost);
        Assert.Empty((await kit.Pipeline.GetWinLossAsync(from: CrmKit.Today.AddDays(1))).Reasons);
    }

    [Fact]
    public async Task Bp_spending_reads_time_on_the_opportunity_code_and_shows_cost_to_finance_only()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.AdvanceAsync((await kit.NewAsync()).Id, OpportunityStage.Capture);
        await kit.ChargeAsync(o.BpWbsElementId!.Value, 6m, TimesheetStatus.Approved);
        await kit.ChargeAsync(o.BpWbsElementId!.Value, 2.5m, TimesheetStatus.Draft);

        var asBd = Assert.Single(await kit.As(CrmKit.Bd).Pipeline.GetBpSpendAsync(o.Id));
        Assert.Equal(6m, asBd.ApprovedHours);
        Assert.Equal(2.5m, asBd.PendingHours);
        Assert.Equal(1, asBd.People);
        Assert.Null(asBd.LaborCost);

        var asFinance = Assert.Single(await kit.As(Finance).Pipeline.GetBpSpendAsync());
        Assert.Equal(6m * 87.5m, asFinance.LaborCost);
        Assert.Equal(o.BpChargeCode, asFinance.ChargeCode);
    }

    [Fact]
    public async Task Without_a_bp_element_capture_still_works_and_the_code_is_created_once_one_is_set()
    {
        await using var kit = await CrmKit.CreateAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            var bp = await db.Set<WbsElement>().FirstAsync(w => w.Id == kit.BpParentId);
            bp.Code = "IND.BIDS";
            bp.Name = "Pursuits";
            await db.SaveChangesAsync();
        }

        var o = await kit.AdvanceAsync((await kit.NewAsync()).Id, OpportunityStage.Capture);
        Assert.Null(o.BpWbsElementId);
        Assert.False((await kit.Pipeline.GetSettingsAsync()).BpParentFound);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Opportunities.EnsureBpChargeCodeAsync(o.Id));

        await kit.As(Finance).Pipeline.SetBpParentAsync(kit.BpParentId);
        Assert.Equal($"IND.BIDS.{o.Number[4..]}", (await kit.As(CrmKit.Bd).Opportunities.EnsureBpChargeCodeAsync(o.Id)).BpChargeCode);
    }

    [Fact]
    public async Task A_won_opportunity_hands_off_to_customer_contract_and_project_shells_with_clin_placeholders()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.AdvanceAsync((await kit.NewAsync("Cloud migration", 3_000_000m)).Id, OpportunityStage.Submitted);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.As(Finance).Opportunities.HandoffAsync(o.Id, new HandoffRequest("W1", "3003", CustomerCode: "PEO")));
        await kit.As(CrmKit.Bd).Opportunities.RecordOutcomeAsync(o.Id, new RecordOutcomeRequest(OpportunityStage.Won, OutcomeReason.Technical,
            DecidedOn: new DateOnly(2026, 10, 15), AwardedValue: 3_000_001m));

        kit.As(Finance);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Opportunities.HandoffAsync(o.Id, new HandoffRequest("C1", "3003", CustomerCode: "PEO"))); // C1 exists.
        var result = await kit.Opportunities.HandoffAsync(o.Id, new HandoffRequest("W56KGU-27-C-0003", "3003", CustomerCode: "peo", ProjectManagerId: kit.Fx.PmId));

        Assert.True(result.CreatedCustomer);
        Assert.Equal(3, result.ClinIds.Count);
        var linked = await kit.Opportunities.GetAsync(o.Id);
        Assert.Equal((result.CustomerId, result.ContractId, result.ProjectId), (linked.CustomerId!.Value, linked.ContractId!.Value, linked.ProjectId!.Value));

        await using (var db = await kit.Factory.CreateAsync())
        {
            var customer = await db.Set<Customer>().SingleAsync(c => c.Id == result.CustomerId);
            Assert.Equal("PEO", customer.Code);
            var contract = await db.Set<Contract>().SingleAsync(c => c.Id == result.ContractId);
            Assert.Equal(ContractStatus.Pending, contract.Status);
            Assert.Equal(new DateOnly(2026, 10, 15), contract.PeriodStart);
            Assert.Equal(new DateOnly(2029, 10, 14), contract.PeriodEnd);
            Assert.Equal(3_000_001m, contract.CeilingValue);
            Assert.Equal(0m, contract.FundedValue);
            var clins = await db.Set<Clin>().Where(c => c.ContractId == contract.Id).OrderBy(c => c.Order).ToListAsync();
            Assert.Equal(["0001", "1001", "2001"], clins.Select(c => c.Number));
            Assert.Equal(3_000_001m, clins.Sum(c => c.CeilingValue));
            Assert.False(clins[0].IsOption);
            Assert.True(clins[1].IsOption && !clins[1].IsExercised);
            Assert.Equal(new DateOnly(2027, 10, 15), clins[1].PeriodStart);
            var project = await db.Set<Project>().SingleAsync(p => p.Id == result.ProjectId);
            Assert.Equal(ProjectStatus.Planning, project.Status);
            Assert.Equal(contract.Id, project.ContractId);
            Assert.Equal(kit.Fx.PmId, project.ProjectManagerId);
        }

        // The agency's office is now that customer, and later wins reuse it.
        Assert.Equal(result.CustomerId, (await kit.As(CrmKit.Bd).Accounts.GetAccountAsync(kit.PeoId)).CustomerId);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.As(Finance).Opportunities.HandoffAsync(o.Id, new HandoffRequest("W2", "3004")));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.As(CrmKit.Bd).Opportunities.ReopenAsync(o.Id, new ChangeStageRequest(OpportunityStage.Submitted)));
    }

    [Fact]
    public async Task Home_to_dos_list_proposal_dues_gate_decisions_and_crm_tasks()
    {
        await using var kit = await CrmKit.CreateAsync();
        kit.As(CrmKit.Bd);
        var o = await kit.Opportunities.CreateAsync(new SaveOpportunityRequest("Logistics", AgencyAccountId: kit.PeoId, ProposalDueOn: CrmKit.Today.AddDays(5)));
        await kit.Opportunities.CreateAsync(new SaveOpportunityRequest("Far away", ProposalDueOn: CrmKit.Today.AddDays(60)));
        await kit.Accounts.LogActivityAsync(new SaveCrmActivityRequest(CrmActivityKind.Task, "Call the CO", OpportunityId: o.Id, DueOn: CrmKit.Today.AddDays(-1), AssignedTo: CrmKit.Bd2));
        await kit.AdvanceAsync(o.Id, OpportunityStage.Qualified);
        await kit.SubmitGateAsync(o.Id, OpportunityStage.Capture);

        var mine = (await kit.As(CrmKit.Bd).Get<IMyTodoService>().GetAsync()).Items.Where(i => i.Key.StartsWith("bd:", StringComparison.Ordinal)).ToList();
        var due = Assert.Single(mine);
        Assert.Equal($"Proposal due: {o.Number} Logistics", due.Title);
        Assert.Equal(TodoUrgency.Later, due.Urgency);

        var theirs = (await kit.As(CrmKit.Bd2).Get<IMyTodoService>().GetAsync()).Items.Where(i => i.Key.StartsWith("bd:", StringComparison.Ordinal)).ToList();
        Assert.Contains(theirs, i => i.Title == "Call the CO" && i.Urgency == TodoUrgency.Overdue);
        Assert.Contains(theirs, i => i.Key.StartsWith("bd:gate:", StringComparison.Ordinal) && i.Link.EndsWith("?tab=gates", StringComparison.Ordinal));

        Assert.DoesNotContain((await kit.As(Viewer).Get<IMyTodoService>().GetAsync()).Items, i => i.Key.StartsWith("bd:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Accounts_form_a_hierarchy_and_link_to_customers()
    {
        await using var kit = await CrmKit.CreateAsync();
        kit.As(CrmKit.Bd);
        var office = await kit.Accounts.CreateAccountAsync(new SaveCrmAccountRequest("Army Contracting Command", CrmAccountKind.Office, "ACC", kit.PeoId));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Accounts.UpdateAccountAsync(kit.ArmyId, new SaveCrmAccountRequest("Department of the Army", CrmAccountKind.Agency, ParentId: office.Id)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Accounts.CreateAccountAsync(new SaveCrmAccountRequest("Acme", CrmAccountKind.Partner, ParentId: kit.ArmyId)));

        var o = await kit.Opportunities.CreateAsync(new SaveOpportunityRequest("At ACC", AgencyAccountId: office.Id));
        Assert.Equal([o.Id], (await kit.Opportunities.ListAsync(new OpportunityFilter(AgencyAccountId: kit.ArmyId))).Select(x => x.Id));
        Assert.Equal(1, (await kit.Accounts.GetAccountAsync(office.Id)).OpenOpportunityCount);

        var contact = await kit.Accounts.CreateContactAsync(new SaveCrmContactRequest(office.Id, "Monica", "Reyes", ContactRole.ContractingOfficer, Email: "Monica.Reyes@Army.mil"));
        Assert.Equal("monica.reyes@army.mil", contact.Email);
        await kit.Accounts.LogActivityAsync(new SaveCrmActivityRequest(CrmActivityKind.Call, "Intro call", ContactId: contact.Id));
        Assert.Single(await kit.Accounts.ListActivitiesAsync(new CrmActivityFilter(AccountId: kit.ArmyId)));

        await using var db = await kit.Factory.CreateAsync();
        var customerId = await db.Set<Customer>().Select(c => c.Id).FirstAsync();
        Assert.NotNull((await kit.As(Finance).Accounts.LinkCustomerAsync(kit.ArmyId, customerId)).CustomerName);
    }
}
