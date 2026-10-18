using Microsoft.EntityFrameworkCore;
using Workbench.Application.Odcs;
using Workbench.Contracts.Odcs;
using Workbench.Domain.Files;
using static Workbench.Tests.Odcs.OdcTestHost;

namespace Workbench.Tests.Odcs;

public sealed class OdcApprovalTests
{
    // ---- Receipts ----

    [Theory]
    [InlineData(74.99, false)]
    [InlineData(75.00, true)]
    [InlineData(250.00, true)]
    public async Task Receipt_is_required_at_or_above_the_threshold(decimal amount, bool required)
    {
        var host = await CreateAsync();
        var report = await host.As(Dev).Expenses.CreateReportAsync(new SaveExpenseReportRequest("Trip"));
        var line = await host.Expenses.AddLineAsync(report.Id, host.Line(amount));

        if (required)
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Expenses.SubmitAsync(report.Id));
            Assert.Contains("needs a receipt", ex.Message);
            await host.Expenses.AttachReceiptAsync(report.Id, line.Id, "hotel.pdf", new MemoryStream([1]));
        }

        Assert.Equal(OdcApprovalState.Submitted, (await host.Expenses.SubmitAsync(report.Id)).Status);
    }

    [Fact]
    public async Task Receipt_threshold_comes_from_configuration()
    {
        var host = await CreateAsync(receiptThreshold: 25m);
        var report = await host.As(Dev).Expenses.CreateReportAsync(new SaveExpenseReportRequest("Trip"));
        await host.Expenses.AddLineAsync(report.Id, host.Line(30m));

        await Assert.ThrowsAsync<ArgumentException>(() => host.Expenses.SubmitAsync(report.Id));
        Assert.Equal(25m, (await host.Odcs.GetPolicyAsync()).ReceiptThreshold);
    }

    [Fact]
    public async Task Vendor_invoice_needs_a_receipt_too()
    {
        var host = await CreateAsync();
        var odc = await host.As(Analyst).Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(500m));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Odcs.SubmitAsync(odc.Id));

        await host.Odcs.AttachReceiptAsync(odc.Id, "invoice.pdf", new MemoryStream([1, 2]));
        Assert.Equal(OdcApprovalState.Submitted, (await host.Odcs.SubmitAsync(odc.Id)).Status);
    }

    [Fact]
    public async Task Receipts_are_stored_through_the_blob_store_and_can_be_downloaded()
    {
        var host = await CreateAsync();
        var report = await host.As(Dev).Expenses.CreateReportAsync(new SaveExpenseReportRequest("Trip"));
        var line = await host.Expenses.AddLineAsync(report.Id, host.Line(100m));
        line = await host.Expenses.AttachReceiptAsync(report.Id, line.Id, "hotel.pdf", new MemoryStream([7, 8, 9]));

        Assert.Equal("hotel.pdf", line.ReceiptFileName);
        Assert.Single(host.Blobs.Blobs);
        await using var receipt = await host.Odcs.OpenReceiptAsync(line.Id);
        using var copy = new MemoryStream();
        await receipt.Content.CopyToAsync(copy);
        Assert.Equal([7, 8, 9], copy.ToArray());
        Assert.Equal("application/pdf", receipt.ContentType);

        await using var db = await host.Factory.CreateAsync();
        var file = await db.Set<StoredFile>().SingleAsync();
        Assert.Equal(("OdcEntry", line.Id), (file.OwnerType, file.OwnerId));
    }

    [Fact]
    public async Task Disallowed_receipt_file_types_are_rejected()
    {
        var host = await CreateAsync();
        var report = await host.As(Dev).Expenses.CreateReportAsync(new SaveExpenseReportRequest("Trip"));
        var line = await host.Expenses.AddLineAsync(report.Id, host.Line());
        await Assert.ThrowsAsync<ArgumentException>(() => host.Expenses.AttachReceiptAsync(report.Id, line.Id, "run.exe", new MemoryStream([1])));
        Assert.Empty(host.Blobs.Blobs);
    }

    // ---- Routing and self-approval ----

    [Fact]
    public async Task Expense_report_goes_to_the_employees_supervisor()
    {
        var host = await CreateAsync();
        var report = await host.SubmittedDevReportAsync();

        Assert.Single((await host.As(Pm).Approvals.GetMyQueueAsync()).Reports, r => r.Report.Id == report.Id);
        Assert.Empty((await host.As(Boss).Approvals.GetMyQueueAsync()).Reports);
        Assert.Empty((await host.As(Analyst).Approvals.GetMyQueueAsync()).Reports);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.As(Boss).Approvals.ApproveReportAsync(report.Id));

        var approved = await host.As(Pm).Approvals.ApproveReportAsync(report.Id);
        Assert.Equal(OdcApprovalState.Approved, approved.Status);
        Assert.Equal(Pm, approved.ApprovedBy);
    }

    [Fact]
    public async Task Vendor_odc_goes_to_the_project_manager()
    {
        var host = await CreateAsync();
        var odc = await host.As(Analyst).Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(50m));
        await host.Odcs.SubmitAsync(odc.Id);

        Assert.Single((await host.As(Pm).Approvals.GetMyQueueAsync()).VendorOdcs, o => o.Id == odc.Id);
        Assert.Empty((await host.As(Boss).Approvals.GetMyQueueAsync()).VendorOdcs);
        Assert.Equal(OdcApprovalState.Approved, (await host.As(Pm).Approvals.ApproveOdcAsync(odc.Id)).Status);
    }

    [Fact]
    public async Task Vendor_odc_entered_by_the_pm_goes_to_the_pms_supervisor_and_the_pm_cannot_approve_it()
    {
        var host = await CreateAsync();
        var odc = await host.As(Pm).Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(50m));
        await host.Odcs.SubmitAsync(odc.Id);

        Assert.Empty((await host.Approvals.GetMyQueueAsync()).VendorOdcs);
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Approvals.ApproveOdcAsync(odc.Id));
        Assert.Contains("your own", ex.Message);

        Assert.Single((await host.As(Boss).Approvals.GetMyQueueAsync()).VendorOdcs);
        await host.Approvals.ApproveOdcAsync(odc.Id);
    }

    [Fact]
    public async Task Nobody_approves_their_own_expense_report()
    {
        var host = await CreateAsync();
        var report = await host.SubmittedDevReportAsync();
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.As(Dev).Approvals.ApproveReportAsync(report.Id));
        Assert.Contains("your own", ex.Message);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Approvals.RejectReportAsync(report.Id, "no"));
    }

    [Fact]
    public async Task Report_cannot_be_submitted_without_a_supervisor()
    {
        var host = await CreateAsync();
        await using (var db = await host.Factory.CreateAsync())
        {
            db.Set<Workbench.Domain.ProjectAccounting.ChargeAssignment>().Add(new() { WbsElementId = host.Leaf.Id, EmployeeId = host.BossEmployee.Id });
            await db.SaveChangesAsync();
        }

        var report = await host.As(Boss).Expenses.CreateReportAsync(new SaveExpenseReportRequest("Trip"));
        await host.Expenses.AddLineAsync(report.Id, host.Line(20m));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Expenses.SubmitAsync(report.Id));
        Assert.Contains("no supervisor", ex.Message);
    }

    [Fact]
    public async Task Reject_requires_a_reason_and_sends_the_report_back()
    {
        var host = await CreateAsync();
        var report = await host.SubmittedDevReportAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => host.As(Pm).Approvals.RejectReportAsync(report.Id, " "));

        var rejected = await host.Approvals.RejectReportAsync(report.Id, "Missing hotel folio");
        Assert.Equal(OdcApprovalState.Rejected, rejected.Status);
        Assert.Equal("Missing hotel folio", rejected.RejectionReason);

        // The employee can fix and resubmit a rejected report.
        var detail = await host.As(Dev).Expenses.GetReportAsync(report.Id);
        Assert.All(detail.Lines, l => Assert.Equal(OdcApprovalState.Rejected, l.Status));
        await host.Expenses.UpdateLineAsync(report.Id, detail.Lines[0].Id, host.Line(110m));
        Assert.Equal(OdcApprovalState.Submitted, (await host.Expenses.SubmitAsync(report.Id)).Status);
    }

    // ---- Locking and reopen ----

    [Fact]
    public async Task Approved_report_is_locked()
    {
        var host = await CreateAsync();
        var report = await host.SubmittedDevReportAsync();
        await host.As(Pm).Approvals.ApproveReportAsync(report.Id);
        var line = (await host.As(Dev).Expenses.GetReportAsync(report.Id)).Lines[0];
        Assert.Equal(OdcApprovalState.Approved, line.Status);
        Assert.False(line.IsEditable);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Expenses.UpdateLineAsync(report.Id, line.Id, host.Line(1m)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Expenses.AddLineAsync(report.Id, host.Line()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Expenses.RemoveLineAsync(report.Id, line.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Expenses.AttachReceiptAsync(report.Id, line.Id, "x.pdf", new MemoryStream([1])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Expenses.UpdateReportAsync(report.Id, new SaveExpenseReportRequest("New")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Expenses.DeleteReportAsync(report.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Expenses.SubmitAsync(report.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.As(Pm).Approvals.ApproveReportAsync(report.Id));
    }

    [Fact]
    public async Task Submitted_report_is_locked_for_the_employee()
    {
        var host = await CreateAsync();
        var report = await host.SubmittedDevReportAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.As(Dev).Expenses.AddLineAsync(report.Id, host.Line()));
    }

    [Fact]
    public async Task Approved_vendor_odc_is_locked()
    {
        var host = await CreateAsync();
        var odc = await host.As(Analyst).Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(50m));
        await host.Odcs.SubmitAsync(odc.Id);
        await host.As(Pm).Approvals.ApproveOdcAsync(odc.Id);

        host.As(Analyst);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Odcs.UpdateVendorOdcAsync(odc.Id, host.Vendor(60m)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Odcs.DeleteVendorOdcAsync(odc.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Odcs.AttachReceiptAsync(odc.Id, "x.pdf", new MemoryStream([1])));
    }

    [Fact]
    public async Task Reopen_needs_a_reason_and_unlocks_for_correction()
    {
        var host = await CreateAsync();
        var report = await host.SubmittedDevReportAsync();
        await host.As(Pm).Approvals.ApproveReportAsync(report.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => host.Approvals.ReopenReportAsync(report.Id, ""));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.As(Dev).Approvals.ReopenReportAsync(report.Id, "I want to edit"));
        var reopened = await host.As(Pm).Approvals.ReopenReportAsync(report.Id, "Wrong charge code on taxi");
        Assert.Equal(OdcApprovalState.Draft, reopened.Status);
        Assert.Null(reopened.ApprovedBy);

        var line = (await host.As(Dev).Expenses.GetReportAsync(report.Id)).Lines[0];
        Assert.Equal(OdcApprovalState.Draft, line.Status);
        await host.Expenses.UpdateLineAsync(report.Id, line.Id, host.Line(99m));

        var audit = await host.Audit.ListAsync(OdcWorkflow.ReportEntityType, report.Id);
        Assert.Contains(audit, a => a.Action == "Reopened" && a.Reason == "Wrong charge code on taxi" && a.Actor == Pm);
    }

    [Fact]
    public async Task Vendor_odc_reopen_returns_it_to_draft()
    {
        var host = await CreateAsync();
        var odc = await host.As(Analyst).Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(50m));
        await host.Odcs.SubmitAsync(odc.Id);
        await host.As(Pm).Approvals.ApproveOdcAsync(odc.Id);
        Assert.Single((await host.Approvals.ListMyApprovedAsync()).VendorOdcs);

        await host.Approvals.ReopenOdcAsync(odc.Id, "Invoice number typo");
        var updated = await host.As(Analyst).Odcs.UpdateVendorOdcAsync(odc.Id, host.Vendor(50m) with { InvoiceNumber = "INV-101" });
        Assert.Equal(OdcApprovalState.Draft, updated.Status);
        Assert.Equal("INV-101", updated.InvoiceNumber);
    }

    // ---- Unallowable ----

    [Fact]
    public async Task Unallowable_costs_are_recorded_but_excluded_from_allowable_totals()
    {
        var host = await CreateAsync();
        host.As(Analyst);
        var a = await host.Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(40m));
        var b = await host.Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(30m, unallowable: true, reason: "Entertainment, FAR 31.205-14"));
        var c = await host.Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(20m, type: OdcCostType.Subcontract));
        foreach (var id in new[] { a.Id, b.Id, c.Id })
        {
            await host.Odcs.SubmitAsync(id);
            await host.As(Pm).Approvals.ApproveOdcAsync(id);
            host.As(Analyst);
        }

        var list = await host.Odcs.ListProjectOdcsAsync(host.Project.Id);
        Assert.Equal(90m, list.Total);
        Assert.Equal(60m, list.AllowableTotal);
        Assert.Equal(30m, list.UnallowableTotal);
        Assert.Equal(60m, list.ApprovedAllowableTotal);
        var materials = list.ByType.Single(t => t.Type == OdcCostType.Materials);
        Assert.Equal((2, 70m, 40m, 30m), (materials.Count, materials.Amount, materials.AllowableAmount, materials.UnallowableAmount));

        await using var db = await host.Factory.CreateAsync();
        Assert.Equal(60m, await OdcActuals.ApprovedAllowable(db).Where(o => o.ProjectId == host.Project.Id).SumAsync(o => o.Amount));
    }

    [Fact]
    public async Task Approver_can_mark_a_line_unallowable_before_approving()
    {
        var host = await CreateAsync();
        var report = await host.SubmittedDevReportAsync(60m, 40m);
        var lines = (await host.As(Pm).Expenses.GetReportAsync(report.Id)).Lines;

        await Assert.ThrowsAsync<ArgumentException>(() => host.Approvals.SetUnallowableAsync(lines[1].Id, true, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.As(Dev).Approvals.SetUnallowableAsync(lines[1].Id, true, "mine"));
        await host.As(Pm).Approvals.SetUnallowableAsync(lines[1].Id, true, "Alcohol, FAR 31.205-51");
        var approved = await host.Approvals.ApproveReportAsync(report.Id);

        Assert.Equal(100m, approved.Total);
        Assert.Equal(60m, approved.AllowableTotal);
        await using var db = await host.Factory.CreateAsync();
        Assert.Equal(60m, await OdcActuals.ApprovedAllowable(db).SumAsync(o => o.Amount));

        var audit = await host.Audit.ListAsync(OdcWorkflow.OdcEntityType, lines[1].Id);
        Assert.Contains(audit, e => e.Action == "MarkedUnallowable" && e.Reason == "Alcohol, FAR 31.205-51" && e.Actor == Pm);
    }

    [Fact]
    public async Task Project_list_filters_by_type_date_status_and_expenses()
    {
        var host = await CreateAsync();
        await host.SubmittedDevReportAsync(80m);
        host.As(Analyst);
        await host.Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(10m));
        await host.Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(20m, type: OdcCostType.Equipment) with { IncurredOn = Day.AddDays(5) });

        Assert.Equal(30m, (await host.Odcs.ListProjectOdcsAsync(host.Project.Id)).Total);
        Assert.Equal(110m, (await host.Odcs.ListProjectOdcsAsync(host.Project.Id, new OdcListQuery(IncludeExpenses: true))).Total);
        Assert.Equal(20m, (await host.Odcs.ListProjectOdcsAsync(host.Project.Id, new OdcListQuery(Type: OdcCostType.Equipment))).Total);
        Assert.Equal(10m, (await host.Odcs.ListProjectOdcsAsync(host.Project.Id, new OdcListQuery(To: Day))).Total);
        Assert.Equal(80m, (await host.Odcs.ListProjectOdcsAsync(host.Project.Id, new OdcListQuery(Status: OdcApprovalState.Submitted, IncludeExpenses: true))).Total);
    }

    // ---- Audit and notifications ----

    [Fact]
    public async Task Every_state_change_is_audited()
    {
        var host = await CreateAsync();
        var report = await host.SubmittedDevReportAsync();
        await host.As(Pm).Approvals.RejectReportAsync(report.Id, "Need itemized folio");
        await host.As(Dev).Expenses.SubmitAsync(report.Id);
        await host.As(Pm).Approvals.ApproveReportAsync(report.Id, "Looks good");

        var actions = (await host.Audit.ListAsync(OdcWorkflow.ReportEntityType, report.Id)).OrderBy(a => a.OccurredAtUtc).Select(a => (a.Action, a.Actor)).ToList();
        Assert.Equal([("Created", Dev), ("Submitted", Dev), ("Rejected", Pm), ("Submitted", Dev), ("Approved", Pm)], actions);
        var rejected = (await host.Audit.ListAsync(OdcWorkflow.ReportEntityType, report.Id)).Single(a => a.Action == "Rejected");
        Assert.Equal("Need itemized folio", rejected.Reason);

        var lineId = (await host.Expenses.GetReportAsync(report.Id)).Lines[0].Id;
        var lineActions = (await host.Audit.ListAsync(OdcWorkflow.OdcEntityType, lineId)).Select(a => a.Action).ToHashSet();
        Assert.Superset(new HashSet<string> { "Created", "ReceiptAttached" }, lineActions);
    }

    [Fact]
    public async Task Vendor_odc_lifecycle_is_audited_including_deletes()
    {
        var host = await CreateAsync();
        var odc = await host.As(Analyst).Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(50m));
        await host.Odcs.UpdateVendorOdcAsync(odc.Id, host.Vendor(55m));
        await host.Odcs.SubmitAsync(odc.Id);
        await host.As(Pm).Approvals.ApproveOdcAsync(odc.Id);
        await host.Approvals.ReopenOdcAsync(odc.Id, "Amount was wrong");
        await host.As(Analyst).Odcs.DeleteVendorOdcAsync(odc.Id);

        var actions = (await host.Audit.ListAsync(OdcWorkflow.OdcEntityType, odc.Id)).OrderBy(a => a.OccurredAtUtc).Select(a => a.Action).ToList();
        Assert.Equal(["Created", "Updated", "Submitted", "Approved", "Reopened", "Deleted"], actions);
        Assert.Null(await host.Odcs.GetAsync(odc.Id));
    }

    [Fact]
    public async Task Notifications_go_to_the_approver_then_the_submitter()
    {
        var host = await CreateAsync();
        var report = await host.SubmittedDevReportAsync();
        await host.As(Pm).Approvals.ApproveReportAsync(report.Id);

        Assert.Equal(
            [(OdcNotificationKind.NeedsApproval, Pm), (OdcNotificationKind.Approved, Dev)],
            host.Notifier.Sent.Select(n => (n.Kind, n.RecipientEmail)));
    }
}
