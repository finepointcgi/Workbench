using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Closeout;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Time;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Ceilings;

public sealed class CeilingEnforcementTests
{
    [Fact]
    public async Task A_blocking_hours_ceiling_refuses_a_timesheet_save_past_it_and_allows_up_to_it()
    {
        await using var kit = await CeilingKit.CreateAsync();
        await kit.WbsHoursAsync(12m); // 8 h already on record

        var ex = await Assert.ThrowsAsync<CeilingExceededException>(() => kit.SaveTimeAsync(8m));
        Assert.Contains("Work (WBS 1001.01)", ex.Message); // the name people know, then the code
        Assert.Contains("16 h", ex.Message);

        var saved = await kit.SaveTimeAsync(4m);
        Assert.Contains(saved.Entries, e => e.Hours == 4m);

        // Lowering hours past a ceiling is always allowed.
        await kit.SaveTimeAsync(2m);
    }

    [Fact]
    public async Task A_warning_ceiling_lets_the_save_through_and_alerts_finance_and_the_pm_once_per_threshold()
    {
        await using var kit = await CeilingKit.CreateAsync();
        await kit.WbsHoursAsync(10m, CeilingMode.Warn); // 8/10 = 80%: the first threshold alerts as soon as it's created
        var afterCreate = await kit.NotifiedAsync("ceiling.threshold");
        Assert.Contains(Owner, afterCreate); // pm@ manages 1001
        Assert.Contains(Admin, afterCreate);

        await kit.SaveTimeAsync(4m); // 12/10: 95% and 100% crossed, one alert per recipient
        var all = await kit.NotifiedAsync("ceiling.threshold");
        Assert.Equal(2, all.Count(e => e == Owner));
        Assert.DoesNotContain(Editor, all); // not the person who charged

        await kit.SaveTimeAsync(5m); // nothing new to cross
        Assert.Equal(all.Count, (await kit.NotifiedAsync("ceiling.threshold")).Count);
    }

    [Fact]
    public async Task Submitting_a_timesheet_over_a_blocking_ceiling_is_refused_and_a_warning_ceiling_adds_a_warning()
    {
        await using var kit = await CeilingKit.CreateAsync();
        var sheet = await kit.SaveTimeAsync(8m);
        var ceiling = await kit.WbsHoursAsync(12m); // added after the save: 16 h on record
        kit.As(Editor);
        await Assert.ThrowsAsync<CeilingExceededException>(() => kit.Timesheets.SubmitAsync(sheet.Timesheet.Id, new SubmitTimesheetRequest(true)));

        kit.As(Finance);
        await kit.Ceilings.UpdateAsync(ceiling.Id, new SaveCeilingRequest(CeilingScope.Wbs, HoursCeiling: 12m, Mode: CeilingMode.Warn));
        kit.As(Editor);
        var result = await kit.Timesheets.SubmitAsync(sheet.Timesheet.Id, new SubmitTimesheetRequest(true));
        Assert.Contains(result.Warnings, w => w.Contains("past the ceiling of 12 h"));
    }

    [Fact]
    public async Task A_labor_category_ceiling_counts_only_that_category_on_the_wbs()
    {
        await using var kit = await CeilingKit.CreateAsync();
        await kit.CeilingAsync(new SaveCeilingRequest(CeilingScope.LaborCategory, WbsElementId: kit.Fx.Wbs1, LaborCategoryId: kit.CategoryId, HoursCeiling: 10m, Mode: CeilingMode.Block));

        await Assert.ThrowsAsync<CeilingExceededException>(() => kit.SaveTimeAsync(4m));
        await kit.SaveTimeAsync(2m);
        kit.As(Finance);
        var status = await kit.Ceilings.GetProjectStatusAsync(kit.Fx.Project1);
        var hours = Assert.Single(Assert.Single(status.Ceilings).Measures);
        Assert.Equal((10m, 10m, CeilingAlertLevel.Critical), (hours.Limit, hours.Consumed, hours.Level));
    }

    [Fact]
    public async Task An_override_allows_extra_hours_until_it_expires_or_is_revoked()
    {
        await using var kit = await CeilingKit.CreateAsync();
        var ceiling = await kit.WbsHoursAsync(12m);
        kit.As(Finance);
        var granted = await kit.Ceilings.GrantOverrideAsync(ceiling.Id, new GrantCeilingOverrideRequest("Surge approved by the CO", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7), ExtraHours: 8m));
        Assert.True(granted.IsActive);

        await kit.SaveTimeAsync(10m); // 18 ≤ 12 + 8
        await Assert.ThrowsAsync<CeilingExceededException>(() => kit.SaveTimeAsync(13m)); // 21 > 20

        await kit.ExpireOverridesAsync();
        await Assert.ThrowsAsync<CeilingExceededException>(() => kit.SaveTimeAsync(11m));

        // An override with no amounts lifts the block (warnings only) until it's revoked.
        kit.As(Finance);
        var open = await kit.Ceilings.GrantOverrideAsync(ceiling.Id, new GrantCeilingOverrideRequest("Rebaseline pending", DateOnly.FromDateTime(DateTime.UtcNow)));
        await kit.SaveTimeAsync(20m);
        kit.As(Finance);
        await kit.Ceilings.RevokeOverrideAsync(open.Id, new CeilingReasonRequest("Rebaselined"));
        await Assert.ThrowsAsync<CeilingExceededException>(() => kit.SaveTimeAsync(21m));

        await using var db = await kit.Factory.CreateAsync();
        var audit = await db.Set<AuditEvent>().Where(a => a.EntityType == "CeilingOverride").Select(a => a.Action).ToListAsync();
        Assert.Equal(["Granted", "Granted", "Revoked"], audit.Order().ToList());
    }

    [Fact]
    public async Task Overrides_need_a_reason_and_a_future_expiry()
    {
        await using var kit = await CeilingKit.CreateAsync();
        var ceiling = await kit.WbsHoursAsync(12m);
        kit.As(Finance);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Ceilings.GrantOverrideAsync(ceiling.Id, new GrantCeilingOverrideRequest(" ", DateOnly.FromDateTime(DateTime.UtcNow))));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Ceilings.GrantOverrideAsync(ceiling.Id, new GrantCeilingOverrideRequest("Late", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1))));
    }

    [Fact]
    public async Task Submitting_a_vendor_odc_past_a_blocking_cost_ceiling_is_refused()
    {
        await using var kit = await CeilingKit.CreateAsync();
        var ceiling = await kit.ProjectCostAsync(1_000m); // $700 of labor on record
        kit.As(Finance);
        await kit.Odcs.AttachReceiptAsync(kit.Fx.VendorOdcId, "invoice.pdf", new MemoryStream("%PDF-1.4 receipt"u8.ToArray()));
        await Assert.ThrowsAsync<CeilingExceededException>(() => kit.Odcs.SubmitAsync(kit.Fx.VendorOdcId)); // + $500

        await kit.Ceilings.GrantOverrideAsync(ceiling.Id, new GrantCeilingOverrideRequest("Rack approved", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), ExtraCost: 500m));
        var submitted = await kit.Odcs.SubmitAsync(kit.Fx.VendorOdcId);
        Assert.Equal(OdcApprovalState.Submitted, submitted.Status);
        var status = await kit.Ceilings.GetProjectStatusAsync(kit.Fx.Project1);
        Assert.Equal(1_200m, Assert.Single(Assert.Single(status.Ceilings).Measures).Consumed);
    }

    [Fact]
    public async Task Submitting_an_expense_report_past_a_blocking_cost_ceiling_is_refused()
    {
        await using var kit = await CeilingKit.CreateAsync();
        await kit.ProjectCostAsync(750m);
        kit.As(Editor);
        var report = await kit.Expenses.CreateReportAsync(new SaveExpenseReportRequest("Site visit"));
        await kit.Expenses.AddLineAsync(report.Id, new SaveExpenseLineRequest(kit.Fx.Wbs1, OdcCostType.Travel, CeilingKit.WorkDay, 60m, "Taxi"));

        var ex = await Assert.ThrowsAsync<CeilingExceededException>(() => kit.Expenses.SubmitAsync(report.Id));
        Assert.Contains("Project 1001", ex.Message);
    }

    [Fact]
    public async Task Issuing_a_purchase_order_past_a_blocking_cost_ceiling_is_refused_because_commitments_count()
    {
        await using var kit = await CeilingKit.CreateAsync();
        await kit.ProjectCostAsync(3_000m);
        kit.As(Finance);
        var small = await kit.Orders.CreateAsync(kit.Order(2_000m));
        await kit.Orders.IssueAsync(small.Order.Id); // 700 + 2,000 committed

        var big = await kit.Orders.CreateAsync(kit.Order(500m));
        await Assert.ThrowsAsync<CeilingExceededException>(() => kit.Orders.IssueAsync(big.Order.Id)); // 3,200 > 3,000
    }

    [Fact]
    public async Task Approving_ap_bill_lines_past_a_blocking_cost_ceiling_is_refused()
    {
        await using var kit = await CeilingKit.CreateAsync();
        await kit.ProjectCostAsync(1_000m);
        kit.As(Finance);
        var bill = await kit.Bills.CreateAsync(kit.Bill(400m));
        await kit.Bills.SubmitAsync(bill.Bill.Id, new SubmitBillRequest());

        kit.As(Admin);
        await Assert.ThrowsAsync<CeilingExceededException>(() => kit.Bills.ApproveAsync(bill.Bill.Id)); // 700 + 400
    }

    [Fact]
    public async Task Ceilings_validate_their_target_and_amounts()
    {
        await using var kit = await CeilingKit.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => kit.CeilingAsync(new SaveCeilingRequest(CeilingScope.Wbs, WbsElementId: kit.Fx.Wbs1)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.CeilingAsync(new SaveCeilingRequest(CeilingScope.Project, ProjectId: kit.Fx.Project1, BillingCeiling: 5m)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.CeilingAsync(new SaveCeilingRequest(CeilingScope.Project, ProjectId: kit.Fx.Project1, CostCeiling: 5m, AlertThresholds: "eighty")));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.CeilingAsync(new SaveCeilingRequest(CeilingScope.Project, ProjectId: Guid.NewGuid(), CostCeiling: 5m)));

        var contract = await kit.CeilingAsync(new SaveCeilingRequest(CeilingScope.Contract, ContractId: kit.Fx.Contract1, CostCeiling: 10_000m, BillingCeiling: 12_000m));
        Assert.Equal("Contract C1", contract.Label);
        Assert.Equal([80, 95, 100], contract.AlertThresholds);
        kit.As(Finance);
        var inactive = await kit.Ceilings.DeactivateAsync(contract.Id);
        Assert.False(inactive.IsActive);
    }
}

public sealed class CloseoutTests
{
    [Fact]
    public async Task A_closed_project_refuses_time_odcs_expenses_bills_and_orders_and_reopening_allows_them_again()
    {
        await using var kit = await CeilingKit.CreateAsync();
        await kit.CloseProjectAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.Equal(ProjectStatus.Closed, (await db.Set<Project>().FirstAsync(p => p.Id == kit.Fx.Project1)).Status);
        }

        var time = await Assert.ThrowsAsync<ChargingClosedException>(() => kit.SaveTimeAsync(2m));
        Assert.Contains("Project 1001 is closed", time.Message);
        kit.As(Finance);
        await Assert.ThrowsAsync<ChargingClosedException>(() => kit.Odcs.CreateVendorOdcAsync(kit.Fx.Project1,
            new SaveVendorOdcRequest(kit.Fx.Wbs1, OdcCostType.Materials, CeilingKit.WorkDay, 10m, "Cable", "Acme", "INV-2")));
        await Assert.ThrowsAsync<ChargingClosedException>(() => kit.Odcs.SubmitAsync(kit.Fx.VendorOdcId));
        await Assert.ThrowsAsync<ChargingClosedException>(() => kit.Bills.CreateAsync(kit.Bill(10m)));
        await Assert.ThrowsAsync<ChargingClosedException>(() => kit.Orders.CreateAsync(kit.Order(10m)));
        kit.As(Editor);
        var report = await kit.Expenses.CreateReportAsync(new SaveExpenseReportRequest("Trip"));
        await Assert.ThrowsAsync<ChargingClosedException>(() => kit.Expenses.AddLineAsync(report.Id, new SaveExpenseLineRequest(kit.Fx.Wbs1, OdcCostType.Travel, CeilingKit.WorkDay, 20m, "Taxi")));

        kit.As(Finance);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Closeouts.ReopenAsync(CloseoutScope.Project, kit.Fx.Project1, new CloseoutReasonRequest("")));
        var reopened = await kit.Closeouts.ReopenAsync(CloseoutScope.Project, kit.Fx.Project1, new CloseoutReasonRequest("Late subcontractor invoice"));
        Assert.Equal((CloseoutState.Open, 1), (reopened.Status, reopened.ReopenCount));
        Assert.Contains(reopened.History, h => h.Action == "Reopened" && h.Reason == "Late subcontractor invoice");
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.Equal(ProjectStatus.Active, (await db.Set<Project>().FirstAsync(p => p.Id == kit.Fx.Project1)).Status);
        }

        await kit.SaveTimeAsync(2m);
    }

    [Fact]
    public async Task Closing_goes_one_step_at_a_time_and_needs_a_complete_checklist()
    {
        await using var kit = await CeilingKit.CreateAsync();
        kit.As(Finance);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Closeouts.CloseAsync(CloseoutScope.Project, kit.Fx.Project1));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Closeouts.StartClosingAsync(CloseoutScope.Project, kit.Fx.Project1));
        await kit.Closeouts.MarkPhysicallyCompleteAsync(CloseoutScope.Project, kit.Fx.Project1, new PhysicallyCompleteRequest());
        var closing = await kit.Closeouts.StartClosingAsync(CloseoutScope.Project, kit.Fx.Project1);
        Assert.Equal(
            [CloseoutCheck.AllCostsPosted, CloseoutCheck.SubcontractsClosed, CloseoutCheck.PropertyDispositioned, CloseoutCheck.RecordsRetention],
            closing.Items.Select(i => i.Check).ToList());
        Assert.False(closing.Items.Single(i => i.Check == CloseoutCheck.AllCostsPosted).IsSatisfied); // two draft ODCs

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => kit.Closeouts.CloseAsync(CloseoutScope.Project, kit.Fx.Project1));
        Assert.Contains("ODC", refused.Message);
        Assert.Contains("Records retention", refused.Message);
    }

    [Fact]
    public async Task A_contract_closeout_calculates_the_deobligation_and_a_closed_contract_refuses_its_projects_charges()
    {
        await using var kit = await CeilingKit.CreateAsync();
        kit.As(Finance);
        var c = CloseoutScope.Contract;
        var id = kit.Fx.Contract1;
        var start = await kit.Closeouts.GetAsync(c, id);
        Assert.Equal(9, start.Items.Count);
        Assert.Equal(400_000m, start.DeobligationCalculated); // funded 400,000, nothing billed

        var recorded = await kit.Closeouts.RecordDeobligationAsync(id);
        Assert.True(recorded.Items.Single(i => i.Check == CloseoutCheck.FundsDeobligated).IsSatisfied);
        Assert.Equal(400_000m, recorded.DeobligationRecorded);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Closeouts.SaveRecordsAsync(c, id, new SaveCloseoutRecordsRequest(ReleaseOfClaimsOn: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3))));
        await kit.Closeouts.SaveRecordsAsync(c, id, new SaveCloseoutRecordsRequest(new DateOnly(2026, 10, 1), "Signed by the CO", new DateOnly(2026, 10, 2), null, new DateOnly(2032, 9, 30)));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Closeouts.MarkFinalInvoiceAsync(id, new MarkFinalInvoiceRequest(Guid.NewGuid())));

        await kit.Closeouts.MarkPhysicallyCompleteAsync(c, id, new PhysicallyCompleteRequest());
        await kit.Closeouts.StartClosingAsync(c, id);
        foreach (var check in new[] { CloseoutCheck.FinalInvoiceIssued, CloseoutCheck.AllCostsPosted, CloseoutCheck.FinalRatesSettled })
        {
            await kit.Closeouts.WaiveAsync(c, id, new WaiveCloseoutItemRequest(check, "Handled outside Workbench for the test"));
        }

        var closed = await kit.Closeouts.CloseAsync(c, id);
        Assert.True(closed.IsClosed);
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.Equal(ContractStatus.Closed, (await db.Set<Contract>().FirstAsync(x => x.Id == id)).Status);
        }

        var ex = await Assert.ThrowsAsync<ChargingClosedException>(() => kit.SaveTimeAsync(1m));
        Assert.Contains("Contract C1 is closed", ex.Message);
        kit.As(Finance);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Closeouts.SaveRecordsAsync(c, id, new SaveCloseoutRecordsRequest()));
    }
}

public sealed class CeilingPermissionTests
{
    [Fact]
    public async Task Ceilings_are_read_by_finance_and_the_pm_and_changed_by_finance_only()
    {
        await using var kit = await CeilingKit.CreateAsync();
        var ceiling = await kit.WbsHoursAsync(40m);

        kit.As(Owner); // pm@ manages 1001 on C1
        var status = await kit.Ceilings.GetContractStatusAsync(kit.Fx.Contract1);
        Assert.False(status.CanManage);
        Assert.Single((await kit.Ceilings.GetProjectStatusAsync(kit.Fx.Project1)).Ceilings);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Ceilings.GetContractStatusAsync(kit.Fx.Contract2));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Ceilings.CreateAsync(new SaveCeilingRequest(CeilingScope.Project, ProjectId: kit.Fx.Project1, CostCeiling: 1m)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Ceilings.DeactivateAsync(ceiling.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Ceilings.CreateRateCeilingAsync(kit.Fx.Contract1, new SaveRateCeilingRequest(RateCapKind.Overhead, 0.2m)));

        kit.As(Editor);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Ceilings.GetProjectStatusAsync(kit.Fx.Project1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Ceilings.GetAsync(ceiling.Id));
    }

    [Fact]
    public async Task A_pm_may_override_only_where_the_ceiling_allows_it_and_on_their_own_project()
    {
        await using var kit = await CeilingKit.CreateAsync();
        var closedToPm = await kit.WbsHoursAsync(40m);
        var openToPm = await kit.WbsHoursAsync(50m, pmMayOverride: true);
        var other = await kit.CeilingAsync(new SaveCeilingRequest(CeilingScope.Project, ProjectId: kit.Fx.Project2, HoursCeiling: 10m, ProjectManagerMayOverride: true));
        var request = new GrantCeilingOverrideRequest("Customer asked for more", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3), ExtraHours: 4m);

        kit.As(Owner);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Ceilings.GrantOverrideAsync(closedToPm.Id, request));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Ceilings.GrantOverrideAsync(other.Id, request));
        var granted = await kit.Ceilings.GrantOverrideAsync(openToPm.Id, request);
        Assert.Equal(Owner, granted.ApprovedBy);
        Assert.True((await kit.Ceilings.GetProjectStatusAsync(kit.Fx.Project1)).Ceilings.Single(c => c.Ceiling.Id == openToPm.Id).CanOverride);

        kit.As(Editor);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Ceilings.RevokeOverrideAsync(granted.Id, new CeilingReasonRequest("No")));
    }

    [Fact]
    public async Task Closeout_is_read_by_the_pm_and_run_by_finance()
    {
        await using var kit = await CeilingKit.CreateAsync();
        kit.As(Owner);
        var closeout = await kit.Closeouts.GetAsync(CloseoutScope.Contract, kit.Fx.Contract1);
        Assert.False(closeout.CanManage);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Closeouts.MarkPhysicallyCompleteAsync(CloseoutScope.Contract, kit.Fx.Contract1, new PhysicallyCompleteRequest()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Closeouts.ReopenAsync(CloseoutScope.Project, kit.Fx.Project1, new CloseoutReasonRequest("x")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Closeouts.GetAsync(CloseoutScope.Project, kit.Fx.Project2));

        kit.As(Editor);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Closeouts.GetAsync(CloseoutScope.Contract, kit.Fx.Contract1));
    }
}
