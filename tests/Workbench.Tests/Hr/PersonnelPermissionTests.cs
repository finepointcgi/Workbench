using Microsoft.EntityFrameworkCore;
using Workbench.Application.Approvals;
using Workbench.Application.Hr;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Hr;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Hr;

/// <summary>The HR role and who sees which part of a personnel record: HR, the employee, their manager, Finance, others.</summary>
public sealed class PersonnelPermissionTests
{
    [Fact]
    public async Task The_hr_role_is_in_every_role_list_and_admin_is_the_fallback()
    {
        await using var kit = await HrKit.CreateAsync();
        Assert.Contains(OrgRoles.Hr, OrgRoleChecks.All);
        Assert.True(OrgRoleChecks.Everything.HasFlag(OrgRoles.Hr));
        Assert.Equal("HR", OrgRoleChecks.Label(OrgRoles.Hr));
        Assert.True(OrgRoles.Admin.IsHr());
        Assert.False(OrgRoles.Finance.IsHr());

        var context = await kit.As(HrKit.Hr).Get<ICurrentUserContext>().GetAsync();
        Assert.Equal(OrgRoles.Hr, context.Roles);
        Assert.True(context.IsHr);

        var dto = await kit.As(Admin).Get<IUserDirectory>().SetRolesAsync(Viewer, OrgRoles.Hr | OrgRoles.Timekeeper);
        Assert.Equal(OrgRoles.Hr | OrgRoles.Timekeeper, dto.Roles);
        await using var db = await kit.Factory.CreateAsync();
        var hrMembers = await ApprovalEngine.RoleMembersAsync(db, ApprovalOrgRole.Hr, CancellationToken.None);
        Assert.Contains(HrKit.Hr, hrMembers);
        Assert.Contains(Admin, hrMembers);
        Assert.DoesNotContain(Finance, hrMembers);
        Assert.True(ApprovalEngine.HasRole(OrgRoles.Hr, ApprovalOrgRole.Hr));
        Assert.False(ApprovalEngine.HasRole(OrgRoles.Finance, ApprovalOrgRole.Hr));
    }

    [Fact]
    public async Task Each_role_sees_its_part_of_a_record()
    {
        await using var kit = await HrKit.CreateAsync();
        kit.As(HrKit.Hr);
        await kit.Personnel.SavePersonalAsync(kit.Fx.AnalystId, new SavePersonalInfoRequest(null, "alex@home.example", "703-555-0100", "1 Main St", null, "Reston", "va", "20190", null));
        await kit.Personnel.SaveEmergencyContactsAsync(kit.Fx.AnalystId, [new SaveEmergencyContact("Jordan", "Spouse", "703-555-0142", null, false)]);
        await kit.Personnel.SaveCredentialAsync(kit.Fx.AnalystId, null, new SaveCredentialRequest(CredentialKind.SecurityClearance, "Secret", "DCSA", null, ClearanceLevel.Secret, null, HrKit.Today.AddDays(30), null));

        var asHr = await kit.Personnel.GetAsync(kit.Fx.AnalystId);
        Assert.Equal(HrRecordAccess.Full, asHr.Access);
        Assert.Equal("VA", asHr.Personal!.State);
        Assert.True(Assert.Single(asHr.EmergencyContacts!).IsPrimary);

        var self = await kit.As(Editor).Personnel.GetAsync(kit.Fx.AnalystId);
        Assert.Equal(HrRecordAccess.Self, self.Access);
        Assert.Equal("alex@home.example", self.Personal!.PersonalEmail);
        Assert.Equal(self.Summary.EmployeeId, (await kit.Personnel.GetMineAsync()).Summary.EmployeeId);

        // The manager sees the job and credentials, nothing personal.
        var manager = await kit.As(Owner).Personnel.GetAsync(kit.Fx.AnalystId);
        Assert.Equal(HrRecordAccess.Job, manager.Access);
        Assert.NotNull(manager.Job);
        Assert.Single(manager.Credentials!);
        Assert.Null(manager.Personal);
        Assert.Null(manager.EmergencyContacts);
        Assert.Null(manager.Documents);

        var finance = await kit.As(Finance).Personnel.GetAsync(kit.Fx.AnalystId);
        Assert.Equal(HrRecordAccess.Job, finance.Access);
        Assert.Null(finance.Personal);

        // A peer sees the directory only.
        var peer = await kit.As(Viewer).Personnel.GetAsync(kit.Fx.AnalystId);
        Assert.Equal(HrRecordAccess.Directory, peer.Access);
        Assert.Null(peer.Job);
        Assert.Null(peer.Credentials);

        // Only HR changes records.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Personnel.SavePersonalAsync(kit.Fx.AnalystId, new SavePersonalInfoRequest(null, null, null, null, null, null, null, null, null)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Editor).Personnel.SaveJobAsync(kit.Fx.AnalystId,
            new SaveJobInfoRequest("Boss", null, null, null, EmploymentTypeKind.FullTime, EmploymentStatus.Active, true, null, null)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Finance).Personnel.SaveCredentialAsync(kit.Fx.AnalystId, null,
            new SaveCredentialRequest(CredentialKind.Certification, "CISSP", null, null, null, null, null, null)));

        // Audit rows for personal details name the fields, never the values (Finance reads the audit log).
        await using var db = await kit.Factory.CreateAsync();
        var audit = await db.Set<AuditEvent>().Where(a => a.EntityType == HrPersonnelService.ProfileAudit).Select(a => a.AfterJson).ToListAsync();
        Assert.DoesNotContain(audit, a => a != null && (a.Contains("703-555") || a.Contains("alex@home")));
    }

    [Fact]
    public async Task Hr_sets_the_job_on_the_employee_and_keeps_payroll_in_step()
    {
        await using var kit = await HrKit.CreateAsync();
        kit.As(HrKit.Hr);
        var position = await kit.Positions.CreateAsync(new SavePositionRequest("eng-se2", "Systems Engineer II", null, null, null, EeoJobCategory.Professionals, true,
            EmploymentTypeKind.FullTime, 90_000m, 120_000m, 2, PositionStatus.Approved));
        Assert.Equal("ENG-SE2", position.Code);
        var record = await kit.Personnel.SaveJobAsync(kit.Fx.AnalystId, new SaveJobInfoRequest("Systems Engineer", position.Id, null, kit.Fx.PmId,
            EmploymentTypeKind.PartTime, EmploymentStatus.Active, false, "Reston, VA", "va"));
        Assert.Equal("Systems Engineer", record.Job!.JobTitle);
        Assert.False(record.Job.IsExempt);
        Assert.Equal("VA", record.Job.WorkState);

        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.False((await db.Set<Employee>().FirstAsync(e => e.Id == kit.Fx.AnalystId)).IsExempt);
            Assert.Equal(EmploymentType.PartTime, (await db.Set<EmployeePayProfile>().FirstAsync(p => p.EmployeeId == kit.Fx.AnalystId)).EmploymentType);
        }

        var filled = Assert.Single(await kit.Positions.ListAsync(), p => p.Id == position.Id);
        Assert.Equal((1, 1), (filled.Filled, filled.Open));
        Assert.Contains("Alex Rivera", filled.Incumbents);

        // Salary ranges: HR, Finance and administrators.
        Assert.Null((await kit.As(Viewer).Positions.GetAsync(position.Id)).SalaryMin);
        Assert.Equal(90_000m, (await kit.As(Finance).Positions.GetAsync(position.Id)).SalaryMin);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Finance).Positions.CreateAsync(new SavePositionRequest("X", "X", null, null, null, null, true,
            EmploymentTypeKind.FullTime, null, null, 1, PositionStatus.Approved)));

        // A reporting loop is refused.
        kit.As(HrKit.Hr);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Personnel.SaveJobAsync(kit.Fx.PmId, new SaveJobInfoRequest(null, null, null, kit.Fx.AnalystId,
            EmploymentTypeKind.FullTime, EmploymentStatus.Active, true, null, null)));
    }

    [Fact]
    public async Task Eeo_data_is_hr_admin_and_self_only_and_reading_it_is_logged()
    {
        await using var kit = await HrKit.CreateAsync();
        var answers = new SaveEeoRequest(EeoGender.Female, EeoRace.Asian, DisabilityStatus.No, true, false, false, false, true, null, EeoJobCategory.Technicians);
        var mine = await kit.As(Viewer).Personnel.SaveEeoAsync(kit.Fx.ViewerId, answers);
        Assert.True(mine.IsProtectedVeteran);
        Assert.Null(mine.JobCategory); // only HR sets the job category

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Personnel.GetEeoAsync(kit.Fx.ViewerId)); // the manager
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Finance).Personnel.GetEeoAsync(kit.Fx.ViewerId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Editor).Personnel.SaveEeoAsync(kit.Fx.ViewerId, answers));

        var before = await kit.SecurityEventsAsync(HrSecurityEvents.EeoViewed);
        var seen = await kit.As(HrKit.Hr).Personnel.GetEeoAsync(kit.Fx.ViewerId);
        Assert.Equal(EeoRace.Asian, seen.Race);
        Assert.Equal(before + 1, await kit.SecurityEventsAsync(HrSecurityEvents.EeoViewed));
        Assert.Equal(EeoRace.Asian, (await kit.As(Admin).Personnel.GetEeoAsync(kit.Fx.ViewerId)).Race);

        // Reading your own isn't logged as a view; audit rows never carry the answers.
        var self = await kit.SecurityEventsAsync(HrSecurityEvents.EeoViewed);
        await kit.As(Viewer).Personnel.GetEeoAsync(kit.Fx.ViewerId);
        Assert.Equal(self, await kit.SecurityEventsAsync(HrSecurityEvents.EeoViewed));
        await using var db = await kit.Factory.CreateAsync();
        Assert.All(await db.Set<AuditEvent>().Where(a => a.EntityType == HrPersonnelService.EeoAudit).ToListAsync(), a => Assert.Null(a.AfterJson));

        await Assert.ThrowsAsync<ArgumentException>(() => kit.As(Viewer).Personnel.SaveEeoAsync(kit.Fx.ViewerId,
            new SaveEeoRequest(EeoGender.Female, EeoRace.Asian, DisabilityStatus.No, false, true, false, false, false, null)));
    }

    [Fact]
    public async Task An_employee_asks_for_a_change_and_hr_approves_it_through_the_approval_engine()
    {
        await using var kit = await HrKit.CreateAsync();
        var request = await kit.As(Editor).Personnel.RequestChangeAsync(new SubmitHrChangeRequest(
            new SavePersonalInfoRequest(null, null, "571-555-0199", "2 Oak St", null, "Herndon", "VA", "20170", null),
            [new SaveEmergencyContact("Jordan Rivera", "Spouse", "703-555-0142", null, true)], "Moved"));
        Assert.Equal(HrRequestStatus.Pending, request.Status);
        Assert.Contains("Phone", request.Summary);
        Assert.Contains("Emergency contacts", request.Summary);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Personnel.RequestChangeAsync(new SubmitHrChangeRequest(null, [], null))); // one at a time

        // It's in HR's approvals inbox, not the manager's.
        var inbox = await kit.As(HrKit.Hr).Get<IApprovalInboxService>().ListMineAsync();
        Assert.Contains(inbox, i => i.DocumentType == ApprovalDocumentTypes.HrChange && i.DocumentId == request.Id);
        Assert.DoesNotContain(await kit.As(Owner).Get<IApprovalInboxService>().ListMineAsync(), i => i.DocumentType == ApprovalDocumentTypes.HrChange);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Personnel.ApproveChangeAsync(request.Id, new HrDecisionRequest(null)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Editor).Personnel.ApproveChangeAsync(request.Id, new HrDecisionRequest(null)));

        // Approving from the inbox applies the change.
        await kit.As(HrKit.Hr).Get<IApprovalInboxService>().DecideAsync(ApprovalDocumentTypes.HrChange, request.Id, new ApprovalDecisionRequest(ApprovalDecisionKind.Approve, "Thanks"));
        var record = await kit.As(Editor).Personnel.GetMineAsync();
        Assert.Equal("571-555-0199", record.Personal!.PersonalPhone);
        Assert.Equal("Herndon", record.Personal.City);
        Assert.Equal("Jordan Rivera", Assert.Single(record.EmergencyContacts!).Name);
        Assert.Equal(HrRequestStatus.Approved, Assert.Single(await kit.Personnel.ListChangeRequestsAsync(pendingOnly: false)).Status);
        Assert.Empty(await kit.As(Viewer).Personnel.ListChangeRequestsAsync(pendingOnly: false)); // others don't see it
    }

    [Fact]
    public async Task Documents_are_hr_only_unless_marked_visible_to_the_employee()
    {
        await using var kit = await HrKit.CreateAsync();
        kit.As(HrKit.Hr);
        var hidden = await kit.Personnel.UploadDocumentAsync(kit.Fx.AnalystId, "I-9", "I-9", false, "i9.pdf", new MemoryStream([1, 2, 3]));
        var visible = await kit.Personnel.UploadDocumentAsync(kit.Fx.AnalystId, "Offer letter", "Offer", true, "offer.pdf", new MemoryStream([4, 5]));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Editor).Personnel.UploadDocumentAsync(kit.Fx.AnalystId, "x", null, true, "x.pdf", new MemoryStream([1])));

        var mine = await kit.Personnel.GetMineAsync();
        Assert.Equal("Offer letter", Assert.Single(mine.Documents!).Title);
        await using (var content = (await kit.Personnel.OpenDocumentAsync(visible.Id)).Content)
        {
            Assert.Equal(2, content.Length);
        }

        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Personnel.OpenDocumentAsync(hidden.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.As(Owner).Personnel.OpenDocumentAsync(visible.Id));
        Assert.Equal(2, (await kit.As(HrKit.Hr).Personnel.GetAsync(kit.Fx.AnalystId)).Documents!.Count);
    }

    [Fact]
    public async Task The_org_chart_and_expiring_credentials_are_scoped()
    {
        await using var kit = await HrKit.CreateAsync();
        kit.As(HrKit.Hr);
        await kit.Personnel.SaveCredentialAsync(kit.Fx.AnalystId, null, new SaveCredentialRequest(CredentialKind.SecurityClearance, "Secret", null, null, ClearanceLevel.Secret, null, HrKit.Today.AddDays(20), null));
        await kit.Personnel.SaveCredentialAsync(kit.Fx.OutsiderId, null, new SaveCredentialRequest(CredentialKind.Certification, "CISSP", null, null, null, null, HrKit.Today.AddDays(-3), null));
        await kit.Personnel.SaveCredentialAsync(kit.Fx.PmId, null, new SaveCredentialRequest(CredentialKind.Certification, "PMP", null, null, null, null, HrKit.Today.AddYears(2), null));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Personnel.SaveCredentialAsync(kit.Fx.PmId, null,
            new SaveCredentialRequest(CredentialKind.SecurityClearance, "TS", null, null, null, null, null, null)));

        Assert.Equal(["CISSP", "Secret"], (await kit.Personnel.ListExpiringCredentialsAsync(60)).Select(c => c.Name));
        Assert.Equal(-3, (await kit.Personnel.ListExpiringCredentialsAsync(60)).First().DaysToExpiry);
        Assert.Equal(["Secret"], (await kit.As(Owner).Personnel.ListExpiringCredentialsAsync(60)).Select(c => c.Name)); // the manager: reports only
        Assert.Equal(["Secret"], (await kit.As(Editor).Personnel.ListExpiringCredentialsAsync(60)).Select(c => c.Name)); // their own
        Assert.Empty(await kit.As(Viewer).Personnel.ListExpiringCredentialsAsync(60));

        var chart = await kit.As(Viewer).Personnel.GetOrgChartAsync();
        Assert.Equal(2, chart.Single(n => n.EmployeeId == kit.Fx.PmId).DirectReports);
        Assert.Equal(kit.Fx.PmId, chart.Single(n => n.EmployeeId == kit.Fx.AnalystId).SupervisorId);
    }
}
