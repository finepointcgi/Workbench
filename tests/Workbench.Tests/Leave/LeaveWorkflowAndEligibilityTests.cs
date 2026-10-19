using Microsoft.EntityFrameworkCore;
using Workbench.Application.Approvals;
using Workbench.Application.Leave;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Approvals;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Payroll;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Leave;

/// <summary>Leave requests through the approval workflow engine, and leave policy eligibility.</summary>
public sealed class LeaveWorkflowAndEligibilityTests
{
    private static readonly DateOnly Week1 = PayrollKit.PeriodStart;

    // ---- Approval workflow ----

    [Fact]
    public async Task A_leave_request_is_routed_to_the_supervisor_and_decided_from_the_inbox()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync(allowNegative: true);
        kit.As(Editor);
        var request = await kit.Requests.CreateAsync(new CreateLeaveRequest(pto.Id, Week1, Week1.AddDays(1), Comment: "Trip"));

        await using (var db = await kit.Factory.CreateAsync())
        {
            var trip = await db.Set<ApprovalInstance>().SingleAsync(i => i.DocumentType == ApprovalDocumentTypes.LeaveRequest && i.DocumentId == request.Id);
            Assert.Equal((ApprovalInstanceStatus.Pending, Editor), (trip.Status, trip.SubmitterEmail));
            var task = await db.Set<ApprovalTask>().SingleAsync(t => t.InstanceId == trip.Id);
            Assert.Equal(Owner, task.AssigneesCsv);
        }

        // Not in the employee's own inbox; in the supervisor's, labelled as a leave request in hours.
        var inbox = kit.Get<IApprovalInboxService>();
        Assert.Empty(await inbox.ListMineAsync());
        kit.As(Owner);
        var item = Assert.Single(await inbox.ListMineAsync());
        Assert.Equal((ApprovalDocumentTypes.LeaveRequest, "Leave request", request.Id, 16m), (item.DocumentType, item.DocumentLabel, item.DocumentId, item.Amount));
        Assert.True(ApprovalDocumentTypes.AmountIsHours(item.DocumentType));
        Assert.Single(await kit.Requests.ListPendingApprovalsAsync());

        await inbox.DecideAsync(ApprovalDocumentTypes.LeaveRequest, request.Id, new ApprovalDecisionRequest(ApprovalDecisionKind.Approve));

        kit.As(Editor);
        Assert.Equal(LeaveRequestState.Approved, Assert.Single(await kit.Requests.ListMineAsync()).Status);
        var history = Assert.Single(await inbox.GetHistoryAsync(ApprovalDocumentTypes.LeaveRequest, request.Id));
        Assert.Equal(ApprovalInstanceState.Approved, history.Status);
        kit.As(Owner);
        Assert.Empty(await inbox.ListMineAsync());
    }

    [Fact]
    public async Task The_default_workflow_keeps_the_old_rule_supervisor_or_admins_and_never_yourself()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync(allowNegative: true);
        kit.As(Editor);
        var supervised = await kit.Requests.CreateAsync(new CreateLeaveRequest(pto.Id, Week1, Week1));

        // An administrator can't decide for someone who has a supervisor; nobody decides their own.
        kit.As(Admin);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Requests.ApproveAsync(supervised.Id, new DecideLeaveRequest()));
        Assert.Empty(await kit.Requests.ListPendingApprovalsAsync());
        kit.As(Editor);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Requests.ApproveAsync(supervised.Id, new DecideLeaveRequest()));

        // Without a supervisor, the administrators decide (and pm@, who isn't one, doesn't).
        await kit.UpdateEmployeeAsync(kit.Fx.PmId, e => e.SupervisorId = null);
        kit.As(Owner);
        var unsupervised = await kit.Requests.CreateAsync(new CreateLeaveRequest(pto.Id, Week1.AddDays(2), Week1.AddDays(2)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Requests.ApproveAsync(unsupervised.Id, new DecideLeaveRequest()));
        kit.As(Admin);
        Assert.True(await kit.Requests.IsApproverAsync());
        Assert.Equal([unsupervised.Id], (await kit.Requests.ListPendingApprovalsAsync()).Select(r => r.Id));
        Assert.Equal(LeaveRequestState.Approved, (await kit.Requests.ApproveAsync(unsupervised.Id, new DecideLeaveRequest())).Status);

        // Cancelling a pending request ends its trip.
        kit.As(Editor);
        await kit.Requests.CancelAsync(supervised.Id);
        await using var db = await kit.Factory.CreateAsync();
        Assert.Equal(ApprovalInstanceStatus.Cancelled,
            (await db.Set<ApprovalInstance>().SingleAsync(i => i.DocumentType == ApprovalDocumentTypes.LeaveRequest && i.DocumentId == supervised.Id)).Status);
    }

    [Fact]
    public async Task Requests_pending_from_before_workflows_are_backfilled_once()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync(allowNegative: true);
        Guid legacyId;
        await using (var db = await kit.Factory.CreateAsync())
        {
            var legacy = new LeaveRequest { EmployeeId = kit.Fx.AnalystId, PolicyId = pto.Id, StartDate = Week1, EndDate = Week1, Hours = 8m, CreatedAtUtc = DateTime.UtcNow };
            legacy.Days.Add(new LeaveRequestDay { RequestId = legacy.Id, Date = Week1, Hours = 8m });
            db.Set<LeaveRequest>().Add(legacy);
            await db.SaveChangesAsync();
            legacyId = legacy.Id;
        }

        // Before the backfill the supervisor already sees it (previewed in memory).
        kit.As(Owner);
        Assert.Equal([legacyId], (await kit.Requests.ListPendingApprovalsAsync()).Select(r => r.Id));

        var backfill = kit.Get<ApprovalBackfill>();
        Assert.Equal(1, await backfill.RunAsync());
        Assert.Equal(0, await backfill.RunAsync());
        Assert.Equal(legacyId, Assert.Single(await kit.Get<IApprovalInboxService>().ListMineAsync()).DocumentId);
        await kit.Requests.RejectAsync(legacyId, new DecideLeaveRequest("Coverage"));
        Assert.Empty(await kit.Get<IApprovalInboxService>().ListMineAsync());
    }

    [Fact]
    public void Leave_requests_have_a_default_workflow_like_timesheets()
    {
        Assert.Contains(ApprovalDocumentTypes.LeaveRequest, ApprovalDocumentTypes.All);
        Assert.Equal(ApprovalDefaults.Serialize(ApprovalDefaults.For(ApprovalDocumentTypes.Timesheet)),
            ApprovalDefaults.Serialize(ApprovalDefaults.For(ApprovalDocumentTypes.LeaveRequest)));
    }

    // ---- Eligibility ----

    [Fact]
    public async Task Policies_apply_to_everyone_by_default()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync();
        Assert.True(pto.Eligibility!.IsEveryone);
        await using (var db = await kit.Factory.CreateAsync())
        {
            var stored = await db.Set<LeavePolicy>().SingleAsync(p => p.Id == pto.Id);
            Assert.True(LeaveEligibility.AppliesToEveryone(stored));
        }

        Assert.Equal(3, (await kit.Balances.RunAccrualAsync(kit.Period.Id)).EntriesAdded);
        kit.As(Editor);
        Assert.Contains(await kit.Policies.ListMineAsync(), p => p.Id == pto.Id);
    }

    [Fact]
    public async Task A_non_exempt_only_policy_accrues_shows_and_can_be_requested_only_by_non_exempt_employees()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync();
        await kit.UpdateEmployeeAsync(kit.Fx.ViewerId, e => e.IsExempt = false);
        kit.As(Admin);
        var saved = await kit.Policies.UpdateAsync(pto.Id, Request(pto, new LeaveEligibilityDto([], [], false, [])));
        Assert.Equal(false, saved.Eligibility!.Exempt);

        kit.As(Finance);
        var accrual = await kit.Balances.RunAccrualAsync(kit.Period.Id);
        Assert.Equal(1, accrual.EntriesAdded);
        Assert.Equal(4m, await kit.LedgerHoursAsync(kit.Fx.ViewerId, pto.Id));
        Assert.Equal(0m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, pto.Id));

        kit.As(Editor);
        Assert.DoesNotContain(await kit.Balances.GetMyBalancesAsync(), b => b.PolicyId == pto.Id);
        Assert.DoesNotContain(await kit.Policies.ListMineAsync(), p => p.Id == pto.Id);
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => kit.Requests.CreateAsync(new CreateLeaveRequest(pto.Id, Week1, Week1)));
        Assert.Contains("doesn't apply to you", refused.Message);

        kit.As(Viewer);
        Assert.Contains(await kit.Balances.GetMyBalancesAsync(), b => b.PolicyId == pto.Id && b.Balance == 4m);
        Assert.Equal(4m, (await kit.Requests.CreateAsync(new CreateLeaveRequest(pto.Id, Week1, Week1, 4m))).Hours);
    }

    [Fact]
    public async Task Org_units_include_sub_units_and_pay_groups_and_employment_types_come_from_the_payroll_profile()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync();
        var (parent, child) = (new OrgUnit { Code = "OPS", Name = "Operations" }, new OrgUnit { Code = "OPS-E", Name = "Engineering" });
        child.ParentId = parent.Id;
        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<OrgUnit>().AddRange(parent, child);
            await db.SaveChangesAsync();
        }

        await kit.UpdateEmployeeAsync(kit.Fx.AnalystId, e => e.OrgUnitId = child.Id);
        kit.As(Admin);
        await kit.Policies.UpdateAsync(pto.Id, Request(pto, new LeaveEligibilityDto([parent.Id], [], null, [])));
        kit.As(Finance);
        Assert.Equal(1, (await kit.Balances.RunAccrualAsync(kit.Period.Id)).EntriesAdded);
        Assert.Equal(4m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, pto.Id));

        // Part-time only: viewer@'s profile says part-time; employees without one count as full-time.
        await kit.Profiles.SaveAsync(kit.Fx.ViewerId, new SavePayProfileRequest(kit.Group.Id, "3003", PayTypeKind.Hourly, EmploymentType: EmploymentTypeKind.PartTime));
        Assert.Equal(EmploymentTypeKind.PartTime, (await kit.Profiles.GetAsync(kit.Fx.ViewerId)).EmploymentType);
        kit.As(Admin);
        var partTime = await kit.Policies.CreateAsync(new SaveLeavePolicyRequest("PT", "Part-time leave", LeaveTypeKind.Other, kit.PtoWbs, AccrualMethodKind.PerPayPeriod,
            [new(0, 2m)], Eligibility: new LeaveEligibilityDto([], [], null, [EmploymentTypeKind.PartTime])));
        // Another pay group only: nobody in BW.
        var other = await kit.Groups.CreateAsync(new SavePayGroupRequest("MO", "Monthly", PayFrequencyKind.Monthly, "paylocity", "W1234", new DateOnly(2026, 1, 1)));
        var monthly = await kit.Policies.CreateAsync(new SaveLeavePolicyRequest("MO", "Monthly leave", LeaveTypeKind.Other, kit.PtoWbs, AccrualMethodKind.PerPayPeriod,
            [new(0, 2m)], Eligibility: new LeaveEligibilityDto([], [other.Id], null, [])));
        kit.As(Finance);
        var next = (await kit.Groups.ListPeriodsAsync(kit.Group.Id, 2026)).Single(p => p.Start == Week1.AddDays(14));
        await kit.Balances.RunAccrualAsync(next.Id);
        Assert.Equal(2m, await kit.LedgerHoursAsync(kit.Fx.ViewerId, partTime.Id));
        Assert.Equal(0m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, partTime.Id));
        Assert.Equal(0m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, monthly.Id) + await kit.LedgerHoursAsync(kit.Fx.ViewerId, monthly.Id));

        // Round trip, and unknown ids are refused.
        var listed = (await kit.Policies.ListAsync()).Single(p => p.Id == monthly.Id);
        Assert.Equal([other.Id], listed.Eligibility!.PayGroupIds);
        kit.As(Admin);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Policies.UpdateAsync(pto.Id, Request(pto, new LeaveEligibilityDto([Guid.NewGuid()], [], null, []))));
        await using var check = await kit.Factory.CreateAsync();
        Assert.True(await check.Set<AuditEvent>().AnyAsync(a => a.EntityType == LeavePolicyService.AuditEntityType && a.Action == "Updated"
            && a.AfterJson!.Contains(parent.Id.ToString())));
    }

    [Fact]
    public async Task Hours_left_in_a_policy_that_stopped_applying_can_still_be_used_but_no_more()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var pto = await kit.PtoPolicyAsync();
        await kit.Balances.AdjustAsync(new AdjustLeaveRequest(kit.Fx.AnalystId, pto.Id, 8m, "Opening", new DateOnly(2026, 1, 1)));
        kit.As(Admin);
        await kit.Policies.UpdateAsync(pto.Id, Request(pto, new LeaveEligibilityDto([], [], false, []))); // analyst@ is exempt

        kit.As(Editor);
        Assert.Contains(await kit.Balances.GetMyBalancesAsync(), b => b.PolicyId == pto.Id && b.Balance == 8m); // still shown: it has history
        Assert.Contains(await kit.Policies.ListMineAsync(), p => p.Id == pto.Id); // still requestable for what's left
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Requests.CreateAsync(new CreateLeaveRequest(pto.Id, Week1, Week1.AddDays(1))));
        Assert.Equal(8m, (await kit.Requests.CreateAsync(new CreateLeaveRequest(pto.Id, Week1, Week1))).Hours);
    }

    private static SaveLeavePolicyRequest Request(LeavePolicyDto p, LeaveEligibilityDto eligibility) =>
        new(p.Code, p.Name, p.Type, p.WbsElementId, p.Method, p.Tiers, p.MaxBalance, p.CarryoverLimit, p.WaitingPeriodDays, p.AllowNegativeBalance, p.IsActive, eligibility);
}
