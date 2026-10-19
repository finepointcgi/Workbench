using Microsoft.EntityFrameworkCore;
using Workbench.Application.Payroll;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Hr;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Payroll;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Hr;

/// <summary>Compensation changes: HR then Finance over the threshold, written to payroll's effective-dated pay rate history.</summary>
public sealed class CompensationTests
{
    private static readonly DateOnly Effective = HrKit.Today.AddDays(30);

    [Fact]
    public async Task A_small_raise_needs_hr_only_and_becomes_the_pay_rate_from_its_date()
    {
        await using var kit = await HrKit.CreateAsync();
        var change = await kit.As(HrKit.Hr).Compensation.ProposeAsync(kit.Fx.AnalystId,
            new ProposeCompensationRequest(Effective, PayTypeKind.Hourly, null, 52m, CompensationReason.Merit, "Annual review"));
        Assert.Equal(CompensationChangeStatus.Pending, change.Status);
        Assert.Equal(50m * 2080, change.PreviousAnnualized);
        Assert.Equal(2m * 2080, change.Increase); // 4,160: under the 10,000 Finance threshold
        Assert.Equal("Waiting for HR", change.WaitingOn);

        // The proposer is HR, but Harper isn't an employee here: another HR member (an admin) decides.
        var applied = await kit.As(Admin).Compensation.ApproveAsync(change.Id, new CompensationDecisionRequest(null));
        Assert.Equal(CompensationChangeStatus.Applied, applied.Status);

        // Payroll's history now has the new row from its date; earlier dates keep the old rate.
        await using var db = await kit.Factory.CreateAsync();
        var profile = await db.Set<EmployeePayProfile>().AsNoTracking().FirstAsync(p => p.EmployeeId == kit.Fx.AnalystId);
        var history = await PayRates.HistoryAsync(db, profile, CancellationToken.None);
        Assert.Equal(50m, PayRates.InEffect(history, Effective.AddDays(-1))!.HourlyRate);
        Assert.Equal(52m, PayRates.InEffect(history, Effective)!.HourlyRate);
        Assert.Equal(52m, profile.HourlyRate); // the profile mirrors the latest row
        Assert.Contains("Merit", PayRates.InEffect(history, Effective)!.Reason);

        var masked = await kit.As(Finance).Payroll.Profiles.GetAsync(kit.Fx.AnalystId);
        Assert.Contains(masked.Rates!, r => r.EffectiveFrom == Effective);

        var seen = await kit.As(HrKit.Hr).Compensation.GetHistoryAsync(kit.Fx.AnalystId);
        Assert.Equal(52m, seen.Rates.First().HourlyRate);
        Assert.Equal(CompensationChangeStatus.Applied, Assert.Single(seen.Changes).Status);
    }

    [Fact]
    public async Task A_raise_over_the_threshold_also_needs_finance()
    {
        await using var kit = await HrKit.CreateAsync();
        var change = await kit.As(HrKit.Hr).Compensation.ProposeAsync(kit.Fx.PmId,
            new ProposeCompensationRequest(Effective, PayTypeKind.Salaried, 120_000m, null, CompensationReason.Promotion, null));
        Assert.Equal(16_000m, change.Increase);

        var afterHr = await kit.As(Admin).Compensation.ApproveAsync(change.Id, new CompensationDecisionRequest("Fine by HR"));
        Assert.Equal(CompensationChangeStatus.Pending, afterHr.Status);
        Assert.Equal("Waiting for Finance", afterHr.WaitingOn);
        Assert.Contains(await kit.As(Finance).Get<IApprovalInboxService>().ListMineAsync(), i => i.DocumentType == ApprovalDocumentTypes.Compensation);

        // HR can't take Finance's step; Finance approves through the inbox.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(HrKit.Hr).Compensation.ApproveAsync(change.Id, new CompensationDecisionRequest(null)));
        await kit.As(Finance).Get<IApprovalInboxService>().DecideAsync(ApprovalDocumentTypes.Compensation, change.Id, new ApprovalDecisionRequest(ApprovalDecisionKind.Approve));
        var history = await kit.As(Finance).Compensation.GetHistoryAsync(kit.Fx.PmId);
        Assert.Equal(CompensationChangeStatus.Applied, history.Changes.Single().Status);
        Assert.Equal(120_000m, history.Rates.First().AnnualSalary);
    }

    [Fact]
    public async Task Who_may_propose_see_and_decide_pay_changes()
    {
        await using var kit = await HrKit.CreateAsync();
        var request = new ProposeCompensationRequest(Effective, PayTypeKind.Hourly, null, 51m, CompensationReason.Merit, null);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Finance).Compensation.ProposeAsync(kit.Fx.AnalystId, request));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Compensation.ProposeAsync(kit.Fx.AnalystId, request)); // the manager
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Editor).Compensation.GetHistoryAsync(kit.Fx.AnalystId)); // the employee
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Compensation.ListPendingAsync());

        var change = await kit.As(HrKit.Hr).Compensation.ProposeAsync(kit.Fx.AnalystId, request);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Compensation.ProposeAsync(kit.Fx.AnalystId, request)); // one at a time
        Assert.Single(await kit.As(Finance).Compensation.ListPendingAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => kit.As(Admin).Compensation.RejectAsync(change.Id, new CompensationDecisionRequest(null))); // needs a reason
        var rejected = await kit.Compensation.RejectAsync(change.Id, new CompensationDecisionRequest("Not this cycle"));
        Assert.Equal(CompensationChangeStatus.Rejected, rejected.Status);
    }

    [Fact]
    public async Task Changes_cant_start_before_hire_or_inside_posted_payroll()
    {
        await using var kit = await HrKit.CreateAsync();
        kit.As(HrKit.Hr);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Compensation.ProposeAsync(kit.Fx.AnalystId,
            new ProposeCompensationRequest(new DateOnly(2023, 1, 1), PayTypeKind.Hourly, null, 55m, CompensationReason.Merit, null)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Compensation.ProposeAsync(kit.Fx.AnalystId,
            new ProposeCompensationRequest(Effective, PayTypeKind.Hourly, null, 0m, CompensationReason.Merit, null)));

        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<PayrollRun>().Add(new PayrollRun { PayGroupId = kit.Payroll.Group.Id, PayPeriodId = kit.Payroll.Period.Id, Status = PayrollRunStatus.Posted, CreatedBy = Finance });
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<ArgumentException>(() => kit.Compensation.ProposeAsync(kit.Fx.AnalystId,
            new ProposeCompensationRequest(kit.Payroll.Period.End, PayTypeKind.Hourly, null, 55m, CompensationReason.Correction, null)));
        Assert.Contains("posted", error.Message);
        await kit.Compensation.ProposeAsync(kit.Fx.AnalystId, new ProposeCompensationRequest(kit.Payroll.Period.End.AddDays(1), PayTypeKind.Hourly, null, 55m, CompensationReason.Correction, null));
    }

    [Fact]
    public async Task A_merit_cycle_proposes_raises_against_a_budget_and_submits_them_together()
    {
        await using var kit = await HrKit.CreateAsync();
        kit.As(HrKit.Hr);
        var cycle = await kit.Compensation.CreateCycleAsync(new SaveMeritCycleRequest("FY27 merit", Effective, 0.03m));
        cycle = await kit.Compensation.AddProposalsAsync(cycle.Id, new AddMeritProposalsRequest([kit.Fx.AnalystId, kit.Fx.ViewerId], 0.04m));
        Assert.Equal(2, cycle.Proposals.Count);
        Assert.All(cycle.Proposals, p => Assert.Equal(CompensationChangeStatus.Draft, p.Status));
        Assert.Equal(52m, cycle.Proposals.Single(p => p.EmployeeId == kit.Fx.AnalystId).HourlyRate);
        Assert.Equal(31.2m, cycle.Proposals.Single(p => p.EmployeeId == kit.Fx.ViewerId).HourlyRate);
        Assert.Equal((50m + 30m) * 2080, cycle.CoveredPay);
        Assert.Equal(Math.Round((50m + 30m) * 2080 * 0.03m, 2), cycle.Budget);
        Assert.True(cycle.Remaining < 0); // 4% against a 3% budget

        // Re-adding replaces the draft; drafts aren't in the approval queue until submitted.
        cycle = await kit.Compensation.AddProposalsAsync(cycle.Id, new AddMeritProposalsRequest([kit.Fx.ViewerId], 0.02m));
        Assert.Equal(2, cycle.Proposals.Count);
        Assert.Empty(await kit.Compensation.ListPendingAsync());

        cycle = await kit.Compensation.SubmitCycleAsync(cycle.Id);
        Assert.Equal(MeritCycleStatus.Submitted, cycle.Status);
        Assert.Equal(2, (await kit.Compensation.ListPendingAsync()).Count);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Compensation.AddProposalsAsync(cycle.Id, new AddMeritProposalsRequest([kit.Fx.PmId], 0.02m)));
    }
}
