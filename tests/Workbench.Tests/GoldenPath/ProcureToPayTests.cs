using Workbench.ApiClient;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Tests.Identity;

namespace Workbench.Tests.GoldenPath;

/// <summary>
/// Procure to pay on the real host and the demo seed, each step by the person who does it, over the HTTP API: an employee asks for
/// something for a project, it is approved, Finance orders it from a new vendor, it arrives, the vendor's bill is matched to the
/// order, approved and posted, a payment run pays it, and the cost lands on the project exactly once.
/// </summary>
public sealed class ProcureToPayTests
{
    private const string Finance = "pm@example.com";
    private const string Employee = "analyst@example.com";
    private const string Admin = "dev@workbench.local"; // Dana Lee: someone other than the person who entered the bill

    [Fact]
    public async Task A_requisition_becomes_a_paid_bill_and_one_project_cost()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = web.CreateClientAs(Finance);
        using var employee = web.CreateClientAs(Employee);
        using var admin = web.CreateClientAs(Admin);
        var today = DateOnly.FromDateTime(DateTime.Now);

        var accounting = new HttpAccountingReportService(finance);
        var projects = new HttpProjectService(finance);
        var project = (await projects.ListProjectsAsync()).Single(p => p.Code == "1001");
        var odcBefore = (await new HttpProjectFinancialsService(finance).GetProjectSummaryAsync(project.Id)).Totals.ActualOdcCost;
        var ledgerBefore = (await accounting.GetProjectCostLedgerAsync(project.Id)).Sum(r => r.DirectOdc);

        // ---- Alex asks for two test units for project 1001 ----
        var requisitions = new HttpRequisitionService(employee);
        var options = await requisitions.GetOptionsAsync();
        var charge = options.ChargeCodes.First(c => c.ProjectCode == "1001");
        var requisition = await requisitions.CreateAsync(new SaveRequisitionRequest("Test units for the build",
            [new SaveRequisitionLineRequest("Test unit", 2m, 500m, charge.WbsElementId, OdcCostType.Equipment)]));
        requisition = await requisitions.SubmitAsync(requisition.Requisition.Id);

        // ---- Pat approves it (the project's manager and Finance) ----
        var queue = await new HttpRequisitionService(finance).ListApprovalQueueAsync();
        Assert.Contains(queue, r => r.Id == requisition.Requisition.Id);
        requisition = await new HttpRequisitionService(finance).ApproveAsync(requisition.Requisition.Id, new RequisitionDecisionRequest("Go ahead"));

        // ---- Finance: a new vendor, the order from the requisition, issued; it arrives ----
        var vendor = await new HttpVendorService(finance).CreateAsync(new SaveVendorRequest("GPSUP", "Golden Path Supply"));
        var orders = new HttpPurchaseOrderService(finance);
        var order = await orders.CreateFromRequisitionsAsync(new CreatePurchaseOrderFromRequisitionsRequest([requisition.Requisition.Id], vendor.Id, today));
        order = await orders.IssueAsync(order.Order.Id);
        var line = Assert.Single(order.Lines);
        order = await orders.ReceiveAsync(order.Order.Id, new ReceiveRequest(today, [new ReceiveLineRequest(line.Id, 2m)]));

        // ---- The bill matches the order; approving and posting it puts the cost on the project ----
        var bills = new HttpApBillService(finance);
        var bill = await bills.CreateAsync(new SaveBillRequest(vendor.Id, "GP-INV-1", today,
            [new SaveBillLineRequest(BillLineKind.ProjectCost, "Test unit", 1_000m, PurchaseOrderLineId: line.Id, Quantity: 2m, UnitPrice: 500m)]));
        bill = await bills.SubmitAsync(bill.Bill.Id, new SubmitBillRequest());
        var sameHands = await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => bills.ApproveAsync(bill.Bill.Id)); // not the person who entered it
        Assert.Contains("Ask Dana Lee", sameHands.Message, StringComparison.Ordinal); // and the refusal says who can
        bill = await new HttpApBillService(admin).ApproveAsync(bill.Bill.Id);
        Assert.Equal(BillState.Posted, bill.Bill.Status); // approving posts it: no separate step
        // A project-cost line posts as an ODC on the project, which has its own ledger entry; the AP entry is for account lines.

        // ---- A payment run pays it ----
        var payments = new HttpApPaymentService(finance);
        var due = Assert.Single(await payments.ListDueBillsAsync(new DueBillQuery(VendorId: vendor.Id)));
        var run = await payments.CreateRunAsync(new CreatePaymentRunRequest(today, PaymentKind.Check, [new PaymentRunItemRequest(bill.Bill.Id, due.Available)]));
        var sameRun = await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => payments.ApproveRunAsync(run.Id)); // not the person who created it
        Assert.Contains("Ask Dana Lee", sameRun.Message, StringComparison.Ordinal);
        run = await new HttpApPaymentService(admin).ApproveRunAsync(run.Id);
        run = await payments.RecordPaymentsAsync(run.Id, new RecordPaymentsRequest(today, run.Payments.Select(p => new PaymentReferenceRequest(p.Id, "9901")).ToList()));
        Assert.Equal(PaymentRunState.Completed, run.Status);
        Assert.Equal(0m, (await bills.GetAsync(bill.Bill.Id)).Bill.Balance); // nothing left to pay

        // ---- The ties: the $1,000 is on the project once, in the ledger and in project financials ----
        var odcAfter = (await new HttpProjectFinancialsService(finance).GetProjectSummaryAsync(project.Id)).Totals.ActualOdcCost;
        var ledgerAfter = (await accounting.GetProjectCostLedgerAsync(project.Id)).Sum(r => r.DirectOdc);
        Assert.Equal(1_000m, ledgerAfter - ledgerBefore);
        Assert.Equal(1_000m, odcAfter - odcBefore);
    }

    [Fact]
    public async Task An_expense_report_becomes_one_project_cost_when_the_supervisor_approves_it()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = web.CreateClientAs(Finance);
        using var employee = web.CreateClientAs(Employee);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var project = (await new HttpProjectService(finance).ListProjectsAsync()).Single(p => p.Code == "1001");
        var leaf = (await new HttpProjectService(finance).ListWbsAsync(project.Id)).Where(w => w.IsChargeable && w.IsOpen && w.Kind == ChargeCodeKind.Direct).OrderBy(w => w.Code, StringComparer.Ordinal).First();
        var accounting = new HttpAccountingReportService(finance);
        var odcBefore = (await new HttpProjectFinancialsService(finance).GetProjectSummaryAsync(project.Id)).Totals.ActualOdcCost;
        var ledgerBefore = (await accounting.GetProjectCostLedgerAsync(project.Id)).Sum(r => r.DirectOdc);

        // ---- Alex: a trip report with one line and its receipt ----
        var expenses = new HttpExpenseReportService(employee);
        var report = await expenses.CreateReportAsync(new SaveExpenseReportRequest("Customer site visit", today, today, "Requirements workshop"));
        var line = await expenses.AddLineAsync(report.Id, new SaveExpenseLineRequest(leaf.Id, OdcCostType.Travel, today, 120m, "Train to the customer site", "Amtrak"));
        await expenses.AttachReceiptAsync(report.Id, line.Id, "amtrak.pdf", new MemoryStream("%PDF-1.4 receipt"u8.ToArray()));
        report = await expenses.SubmitAsync(report.Id);

        // ---- Pat (Alex's supervisor) approves the report ----
        var queue = await new HttpOdcApprovalService(finance).GetMyQueueAsync();
        Assert.Contains(queue.Reports, r => r.Report.Id == report.Id);
        await new HttpOdcApprovalService(finance).ApproveReportAsync(report.Id, "Approved");

        // ---- The cost is on the project once, in the ledger and in project financials ----
        var odcAfter = (await new HttpProjectFinancialsService(finance).GetProjectSummaryAsync(project.Id)).Totals.ActualOdcCost;
        var ledgerAfter = (await accounting.GetProjectCostLedgerAsync(project.Id)).Sum(r => r.DirectOdc);
        Assert.Equal(120m, ledgerAfter - ledgerBefore);
        Assert.Equal(120m, odcAfter - odcBefore);
    }
}
