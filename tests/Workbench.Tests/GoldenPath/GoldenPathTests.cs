using Workbench.ApiClient;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Cash;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Resourcing;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Time;
using Workbench.Tests.Identity;

namespace Workbench.Tests.GoldenPath;

/// <summary>
/// One contract taken through its whole life on the real web host and the demo seed, each step by the role who does it, over
/// the HTTP API (so permissions, validation and the posting hooks are the real ones). After each hand-off the numbers are
/// checked against each other, because the joins between features are where the bugs hide:
/// contract, CLIN, funding, project (Planning), labor categories, WBS and people, submit, approval (Active), time, approval,
/// ledger, allocation run, invoice, payment, bank statement, bank reconciliation, project financials against the ledger,
/// and the close checklist.
/// </summary>
public sealed class GoldenPathTests
{
    private const string Finance = "pm@example.com";   // Pat Morgan: Finance, and Alex's supervisor
    private const string Pm = "sam.pm@example.com";    // Sam Patel: project manager
    private const string Employee = "analyst@example.com"; // Alex Rivera: a BA2

    [Fact]
    public async Task A_contract_goes_from_award_to_a_reconciled_bank_account_with_every_number_tying()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = web.CreateClientAs(Finance);
        using var pm = web.CreateClientAs(Pm);
        using var employee = web.CreateClientAs(Employee);

        // Dates: Alex's current timesheet period, whichever it is on the day this runs (the seed leaves it a draft), and up
        // to three weekdays of it that have already started.
        var today = DateOnly.FromDateTime(DateTime.Now);
        var period0 = (await new HttpTimesheetService(employee).GetOrCreateMyTimesheetAsync(today)).Timesheet;
        var monday = period0.PeriodStart;
        var days = Enumerable.Range(0, 7).Select(i => monday.AddDays(i))
            .Where(d => d <= today && d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).Take(3).ToList();
        var friday = monday.AddDays(4);
        var hours = 8m * days.Count;

        // ---- Finance: the contract, its rate, its CLIN, and its funding ----
        var people = new HttpPeopleService(finance);
        var employees = await people.ListEmployeesAsync();
        var alex = employees.Single(e => e.Email == Employee);
        var sam = employees.Single(e => e.Email == Pm);
        var category = alex.DefaultLaborCategoryId!.Value;

        var contracts = new HttpContractService(finance);
        var customer = (await contracts.ListCustomersAsync()).First();
        var contract = await contracts.CreateContractAsync(new SaveContractRequest(customer.Id, "GP-26-C-0001", "Golden path support", ContractKind.TimeAndMaterials,
            ContractState.Active, true, monday.AddDays(-14), monday.AddYears(1), 500_000m, 0m));
        await contracts.AddBillRateAsync(contract.Id, new AddBillRateRequest(category, monday.AddDays(-14), 100m));
        var clin = await new HttpClinService(finance).CreateClinAsync(contract.Id, new SaveClinRequest("0001", "Labor", ClinPricingKind.TimeAndMaterials,
            monday.AddDays(-14), monday.AddYears(1), 500_000m));
        var modifications = new HttpContractModificationService(finance);
        var funding = await modifications.CreateDraftAsync(contract.Id, new SaveModificationRequest("P00001", monday.AddDays(-14), "Initial funding",
            [new ModificationChangeDto(ModificationChangeKind.Fund, "0001", 200_000m)]));
        await modifications.ExecuteAsync(funding.Id);
        Assert.Equal(200_000m, (await new HttpClinService(finance).GetClinAsync(clin.Id))!.FundedValue);

        // ---- Finance: the project starts in Planning with the contract's labor categories, and Sam is its manager ----
        var project = await new HttpProjectStartService(finance).StartAsync(new StartProjectRequest("GP01", "Golden path support", contract.Id,
            ProjectStartFrom.Contract, Start: monday, ProjectManagerId: sam.Id));
        Assert.Equal((ProjectState.Planning, sam.Id), (project.Status, project.ProjectManagerId));
        var plan = await new HttpProjectPlanService(finance).GetAsync(project.Id);
        Assert.Equal([category], plan.Categories!.Where(c => c.Selected).Select(c => c.Id));
        Assert.True(plan.SetupDone);
        Assert.Equal("plan", plan.Steps!.Single(s => s.Current).Key);

        // ---- Sam: one task under the CLIN, Alex on it for 40 hours, then submit ----
        var items = new HttpItemService(pm);
        var top = (await items.ListAsync(project.SheetId!.Value)).Single(i => i.ParentId is null);
        var task = await items.CreateAsync(project.SheetId.Value, new CreateItemRequest("Analysis", top.Id));
        task = await items.UpdateAsync(project.SheetId.Value, task.Id, new UpdateItemRequest("Analysis", monday, today.AddDays(14), null, new Dictionary<string, string?>()));
        await new HttpResourcePlanService(pm).ApplyAsync(project.Id, [new PlanChange(PlanChangeKind.SetPerson, ItemId: task.Id, EmployeeId: alex.Id, Hours: 40m, From: monday, To: friday)]);
        var submitted = await new HttpProjectPlanService(pm).SubmitAsync(project.Id, "Ready");

        // ---- The plan is within the contract's ceiling, so it approves itself: the baseline, and the project Active ----
        var approved = submitted;
        Assert.Equal((PlanApprovalStatus.Approved, ProjectState.Active), (approved.Status, approved.ProjectStatus));
        Assert.All(approved.Steps!, s => Assert.True(s.Done));
        var wbs = (await new HttpProjectService(finance).ListWbsAsync(project.Id)).Single(w => w.ItemId == task.Id);

        // ---- Alex: 24 hours over three days; Pat approves ----
        var timesheets = new HttpTimesheetService(employee);
        var sheet = await timesheets.GetOrCreateMyTimesheetAsync(today);
        var reason = "Adding the golden-path hours";
        if (sheet.Timesheet.Status != TimesheetState.Draft)
        {
            sheet = await timesheets.ReopenAsync(sheet.Timesheet.Id, reason); // the demo seed has already submitted this week
        }

        var keep = sheet.Entries.Select(e => new TimeEntryInput(e.WbsElementId, e.WorkDate, e.Hours, e.Comment, e.Id, e.LaborCategoryId));
        sheet = await timesheets.SaveEntriesAsync(sheet.Timesheet.Id, new SaveTimeEntriesRequest(
            keep.Concat(days.Select(d => new TimeEntryInput(wbs.Id, d, 8m))).ToList(), Reason: reason));
        await timesheets.SubmitAsync(sheet.Timesheet.Id, new SubmitTimesheetRequest(true));
        var waiting = (await new HttpApprovalInboxService(finance).ListMineAsync()).Single(i => i.DocumentId == sheet.Timesheet.Id);
        Assert.Equal("Alex Rivera", waiting.Submitter); // the inbox says who by name, not by sign-in email
        await new HttpTimesheetApprovalService(finance).ApproveAsync(sheet.Timesheet.Id);

        // ---- The ledger has the labor, and project financials agree with it ----
        var period = (await new HttpFiscalCalendarService(finance).GetPeriodForDateAsync(monday))!;
        var accounting = new HttpAccountingReportService(finance);
        var ledger = (await accounting.GetProjectCostLedgerAsync(project.Id)).Sum(r => r.DirectLabor);
        var financials = (await new HttpProjectFinancialsService(finance).GetProjectSummaryAsync(project.Id)).Totals;
        Assert.Equal(hours, financials.ActualHours);
        Assert.True(ledger > 0, "approving the time should have posted labor to the ledger");
        Assert.Equal(ledger, financials.ActualLaborCost);
        Assert.Equal(hours * 100m, financials.BillableValue); // hours x the contract's bill rate
        var reconciliation = await accounting.GetReconciliationAsync(project.Id);
        Assert.True(reconciliation.IsReconciled, string.Join("; ", reconciliation.Notes));

        // ---- Finance: the burden allocation run for the period ----
        var rates = new HttpIndirectRateService(finance);
        var preview = await rates.PreviewAllocationAsync(period.Id);
        Assert.False(preview.PeriodClosed);
        var allocation = await rates.PostAllocationAsync(period.Id);
        Assert.NotNull(allocation.Posted);

        // ---- Finance: the T&M invoice for the hours is exactly hours x rate, and approving it posts to AR and revenue ----
        var invoices = new HttpInvoiceService(finance);
        var draft = await invoices.GenerateDraftAsync(new GenerateInvoiceRequest(contract.Id, InvoiceKind.TimeAndMaterials, monday, today, today));
        Assert.Equal(hours * 100m, draft.Summary.Total);
        Assert.Contains(draft.Lines, l => l.Kind == InvoiceLineType.Labor);
        var invoice = await invoices.ApproveAsync(draft.Summary.Id, new ApproveInvoiceRequest());
        Assert.Equal(InvoiceState.Approved, invoice.Summary.Status);
        Assert.NotNull(invoice.JournalEntryId);

        // ---- The customer pays by EFT; the payment closes the invoice ----
        var reference = "EFT GP26";
        var payments = new HttpPaymentService(finance);
        var payment = await payments.RecordAsync(new RecordPaymentRequest(contract.CustomerId, today, invoice.Summary.Total, CashReceiptMethod.Eft,
            [new PaymentApplicationInput(invoice.Summary.Id, invoice.Summary.Total)], reference));
        Assert.Equal(InvoiceState.Paid, (await invoices.GetAsync(invoice.Summary.Id)).Summary.Status);

        // ---- Bank reconciliation: the demo's statement is for this month and doesn't show our receipt yet, so it is a deposit
        // in transit. The statement's own fee gets booked, and the adjusted bank and book balances then agree.
        var account = (await new HttpBankAccountService(finance).ListAsync(false)).Single();
        var reconciliations = new HttpBankReconciliationService(finance);
        var defaults = await reconciliations.GetDefaultsAsync(account.Id);
        var started = await reconciliations.StartAsync(new StartReconciliationRequest(account.Id, defaults.StatementEndDate ?? today, defaults.StatementEndingBalance));
        await reconciliations.AutoMatchAsync(new AutoMatchRequest(account.Id));
        var workspace = await reconciliations.GetWorkspaceAsync(started.Id);
        var inTransit = workspace.GlItems.Single(g => g.Reference == reference && g.Amount == payment.Amount);
        Assert.Null(inTransit.MatchId);
        var booking = await reconciliations.GetBookingAccountsAsync();
        foreach (var unmatched in workspace.BankItems.Where(b => b.MatchId is null))
        {
            await reconciliations.BookBankItemAsync(new BookBankItemRequest(unmatched.Id, booking.BankFeeAccountId!.Value));
        }

        var done = await reconciliations.CompleteAsync(started.Id, new CompleteReconciliationRequest("Golden path"));
        Assert.Equal(ReconciliationState.Completed, done.Status);

        // ---- The close checklist: what this chain did is in order. (Other items on it are the demo's own loose ends: other
        // people's drafts and expenses. The bank check wants a reconciliation through the period's end, and ours is through
        // the statement date.)
        var checklist = await new HttpPeriodCloseService(finance).GetChecklistAsync(period.Id);
        // The seed posts revenue for the current period only, so the revenue check is meaningful when the timesheet's period is the
        // current one. In the first week of a month the week can start in the month before, whose revenue the seed leaves unposted.
        var currentPeriod = period.Start <= today && today <= period.End;
        Assert.All(checklist.Checks.Where(c => c.Kind is CloseCheckKind.TrialBalanceBalanced or CloseCheckKind.AllocationPosted || (currentPeriod && c.Kind is CloseCheckKind.RevenueRecognized)),
            c => Assert.True(c.Passed, $"{c.Kind}: {c.Summary}"));
        var unposted = await new HttpPostingService(finance).ListUnpostedAsync(period.Id);
        Assert.DoesNotContain(unposted, u => u.SourceId == sheet.Timesheet.Id); // approving posted the labor on its own

        // Billing and the payment changed the ledger, but not cost: project financials still agree with it.
        Assert.True((await accounting.GetReconciliationAsync(project.Id)).IsReconciled);

        // The reconciliation is on record, and the receipt, not on the statement yet, stays outstanding.
        var cleared = await reconciliations.GetReportAsync(done.Id);
        Assert.DoesNotContain(cleared.ClearedGlItems, g => g.Reference == reference);
    }
}
