using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Odcs;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Odcs;
using static Workbench.Tests.Odcs.OdcTestHost;

namespace Workbench.Tests.Accounting;

public sealed class OdcPostingTests
{
    private sealed record Host(OdcTestHost Odc, LedgerKit Gl, PostingOdcApprovalService Approvals);

    private static async Task<Host> CreateAsync()
    {
        var odc = await OdcTestHost.CreateAsync(receiptThreshold: 10_000m);
        var gl = await LedgerKit.CreateAsync(odc.Factory, odc.User, TimeProvider.System);
        return new Host(odc, gl, new PostingOdcApprovalService(odc.Approvals, gl.Engine, odc.Factory));
    }

    private static async Task<OdcEntryDto> SubmittedVendorOdcAsync(Host host, decimal amount = 500m, OdcCostType type = OdcCostType.Materials)
    {
        var odc = await host.Odc.As(Analyst).Odcs.CreateVendorOdcAsync(host.Odc.Project.Id, host.Odc.Vendor(amount, type: type));
        return await host.Odc.Odcs.SubmitAsync(odc.Id);
    }

    [Fact]
    public async Task Approved_vendor_odc_debits_its_mapped_account_and_credits_payables()
    {
        var host = await CreateAsync();
        var odc = await SubmittedVendorOdcAsync(host, 250m, OdcCostType.Subcontract);
        host.Odc.As(Pm);
        await host.Approvals.ApproveOdcAsync(odc.Id);

        var entry = Assert.Single(await host.Gl.EntriesAsync(JournalSource.Odc));
        Assert.Equal(Day, entry.Date);
        var debit = entry.Lines.Single(l => l.Debit > 0);
        Assert.Equal(host.Gl["5140"], debit.AccountId);
        Assert.Equal(250m, debit.Debit);
        Assert.Equal(host.Odc.Leaf.Id, debit.WbsElementId);
        Assert.Equal(host.Odc.Project.Id, debit.ProjectId);
        Assert.Equal(host.Gl["2010"], entry.Lines.Single(l => l.Credit > 0).AccountId);
    }

    [Fact]
    public async Task Odcs_approved_without_the_decorator_are_posted_by_the_backlog()
    {
        var host = await CreateAsync();
        var odc = await SubmittedVendorOdcAsync(host);
        await host.Odc.As(Pm).Approvals.ApproveOdcAsync(odc.Id); // undecorated: approved, not posted
        Assert.Empty(await host.Gl.EntriesAsync(JournalSource.Odc));
        Assert.Single(await host.Gl.Posting.ListUnpostedAsync());

        Assert.Equal(1, (await host.Gl.Posting.PostBacklogAsync()).EntriesPosted);
        Assert.Equal(0, (await host.Gl.Posting.PostBacklogAsync()).EntriesPosted);
        Assert.Equal(500m, await host.Gl.BalanceAsync("5120"));
    }

    [Fact]
    public async Task Expense_report_posts_to_accrued_reimbursements_and_segregates_unallowable_lines()
    {
        var host = await CreateAsync();
        var report = await host.Odc.As(Dev).Expenses.CreateReportAsync(new SaveExpenseReportRequest("Site visit", Day, Day));
        await host.Odc.Expenses.AddLineAsync(report.Id, host.Odc.Line(120m));
        await host.Odc.Expenses.AddLineAsync(report.Id, host.Odc.Line(60m, description: "Upgrade to first class", unallowable: true, reason: "Excess airfare, FAR 31.205-46"));
        await host.Odc.Expenses.AddLineAsync(report.Id, host.Odc.Line(45m, description: "Team dinner wine", type: OdcCostType.Other, unallowable: true, reason: "Alcohol, FAR 31.205-51"));
        await host.Odc.Expenses.SubmitAsync(report.Id);

        host.Odc.As(Pm);
        await host.Approvals.ApproveReportAsync(report.Id);

        var entries = await host.Gl.EntriesAsync(JournalSource.Odc);
        Assert.Equal(3, entries.Count);
        Assert.Equal(120m, await host.Gl.BalanceAsync("5110"));   // allowable travel
        Assert.Equal(60m, await host.Gl.BalanceAsync("7020"));    // unallowable travel
        Assert.Equal(45m, await host.Gl.BalanceAsync("7010"));    // unallowable entertainment/alcohol
        Assert.Equal(-225m, await host.Gl.BalanceAsync("2110"));  // owed to the employee
        Assert.Equal(0m, await host.Gl.BalanceAsync("2010"));

        var cost = Assert.Single(await host.Gl.Reports.GetProjectCostLedgerAsync(host.Odc.Project.Id));
        Assert.Equal(120m, cost.DirectOdc);
        Assert.Equal(105m, cost.Unallowable);
        Assert.Equal(120m, cost.Allowable);

        // Unallowable is excluded from the project financials, so the reconciliation still matches.
        Assert.True((await host.Gl.Reports.GetReconciliationAsync(host.Odc.Project.Id)).IsReconciled);
    }

    [Fact]
    public async Task Unallowable_rules_only_map_to_unallowable_accounts()
    {
        var host = await CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => host.Gl.Posting.SaveRuleAsync(new SavePostingRuleRequest(PostingRuleType.UnallowableOdc, "", host.Gl["5190"])));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Gl.Posting.SaveRuleAsync(new SavePostingRuleRequest(PostingRuleType.Odc, "Travel", host.Gl["7020"])));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Gl.Posting.SaveRuleAsync(new SavePostingRuleRequest(PostingRuleType.AccountsPayable, "", host.Gl["5190"])));
        var saved = await host.Gl.Posting.SaveRuleAsync(new SavePostingRuleRequest(PostingRuleType.Odc, "Travel", host.Gl["5190"]));
        Assert.Equal("5190", saved.AccountNumber);
    }

    [Fact]
    public async Task Reopening_an_approved_odc_reverses_it_and_reapproval_posts_a_new_revision()
    {
        var host = await CreateAsync();
        var odc = await SubmittedVendorOdcAsync(host);
        host.Odc.As(Pm);
        await host.Approvals.ApproveOdcAsync(odc.Id);
        await host.Approvals.ReopenOdcAsync(odc.Id, "Wrong invoice amount");
        Assert.Equal(0m, await host.Gl.BalanceAsync("5120"));
        Assert.Single(await host.Gl.EntriesAsync(JournalSource.Reversal));

        await host.Odc.As(Analyst).Odcs.UpdateVendorOdcAsync(odc.Id, host.Odc.Vendor(450m));
        await host.Odc.Odcs.SubmitAsync(odc.Id);
        host.Odc.As(Pm);
        await host.Approvals.ApproveOdcAsync(odc.Id);

        var current = (await host.Gl.EntriesAsync(JournalSource.Odc)).Single(e => e.Status == JournalStatus.Posted);
        Assert.Equal(1, current.SourceRevision);
        Assert.Equal(450m, await host.Gl.BalanceAsync("5120"));
        Assert.Equal(0, (await host.Gl.Engine.PostOdcAsync(odc.Id)).Posted); // idempotent
    }

    [Fact]
    public async Task Approving_an_odc_dated_in_a_closed_period_is_refused()
    {
        var host = await CreateAsync();
        var odc = await SubmittedVendorOdcAsync(host);
        await using (var db = await host.Odc.Factory.CreateAsync())
        {
            (await db.Set<OdcEntry>().SingleAsync(o => o.Id == odc.Id)).IncurredOn = ClosedDay;
            await db.SaveChangesAsync();
        }

        host.Odc.As(Pm);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Approvals.ApproveOdcAsync(odc.Id));
        Assert.Contains("closed fiscal period 2026-09", ex.Message);
    }
}
