using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Security;

/// <summary>Invites, sign-in rules, deactivation, admin protections, employee links and access reviews.</summary>
public sealed class UserLifecycleTests
{
    [Fact]
    public async Task An_invite_is_accepted_on_first_sign_in_with_its_roles()
    {
        await using var seeded = await CreateAsync();
        var invite = await seeded.As(Admin).Host.Get<IInviteService>().CreateAsync(new CreateInviteRequest("New.Person@Example.com", "New Person", OrgRoles.Finance));

        var decision = await seeded.Host.Get<SignInGate>().EvaluateAsync("new.person@example.com", null, "oidc");
        var invites = await seeded.Host.Get<IInviteService>().ListAsync();
        var user = (await seeded.Host.Get<IUserAdminService>().ListUsersAsync("new.person")).Single();

        Assert.Equal(InviteStatus.Pending, invite.Status);
        Assert.Equal(invite.CreatedAtUtc.AddDays(7), invite.ExpiresAtUtc);
        Assert.True(decision.Allowed);
        Assert.Equal(InviteStatus.Accepted, invites.Single(i => i.Id == invite.Id).Status);
        Assert.Equal(OrgRoles.Finance, user.Roles);
        Assert.Equal("New Person", user.DisplayName);
        Assert.NotNull(user.LastSignInAtUtc);
        Assert.Contains(await EventsAsync(seeded), e => e.EventType == SecurityEventTypes.InviteCreated && e.Target == "new.person@example.com" && e.Actor == Admin);
        Assert.Contains(await EventsAsync(seeded), e => e.EventType == SecurityEventTypes.InviteAccepted);
        Assert.Contains(await EventsAsync(seeded), e => e.EventType == SecurityEventTypes.SignInSuccess && e.Actor == "new.person@example.com");
    }

    [Fact]
    public async Task Without_an_invite_new_people_are_refused_when_invites_are_required()
    {
        await using var seeded = await CreateAsync();
        var gate = seeded.Host.Get<SignInGate>();

        var stranger = await gate.EvaluateAsync("stranger@example.com", "Stranger", "oidc");
        var known = await gate.EvaluateAsync(Viewer, null, "oidc");

        Assert.False(stranger.Allowed);
        Assert.True(known.Allowed);
        Assert.Empty(await seeded.As(Admin).Host.Get<IUserAdminService>().ListUsersAsync("stranger"));
        Assert.Contains(await EventsAsync(seeded), e => e.EventType == SecurityEventTypes.SignInFailure && e.Target == "stranger@example.com" && e.Outcome == SecurityOutcomes.Failure);
    }

    [Fact]
    public async Task Expired_and_revoked_invites_are_not_accepted()
    {
        await using var seeded = await CreateAsync();
        var invites = seeded.As(Admin).Host.Get<IInviteService>();
        var expiring = await invites.CreateAsync(new CreateInviteRequest("late@example.com", "Late", OrgRoles.Employee));
        var revoked = await invites.CreateAsync(new CreateInviteRequest("revoked@example.com", "Revoked", OrgRoles.Admin));
        await invites.RevokeAsync(revoked.Id);
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            (await db.Set<UserInvite>().SingleAsync(i => i.Id == expiring.Id)).ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var gate = seeded.Host.Get<SignInGate>();
        Assert.False((await gate.EvaluateAsync("late@example.com", null, "oidc")).Allowed);
        Assert.False((await gate.EvaluateAsync("revoked@example.com", null, "oidc")).Allowed);
        var statuses = (await invites.ListAsync()).ToDictionary(i => i.Email, i => i.Status);
        Assert.Equal(InviteStatus.Expired, statuses["late@example.com"]);
        Assert.Equal(InviteStatus.Revoked, statuses["revoked@example.com"]);
        Assert.Contains(await EventsAsync(seeded), e => e.EventType == SecurityEventTypes.InviteRevoked);
    }

    [Fact]
    public async Task Only_admins_invite_and_existing_users_cant_be_invited()
    {
        await using var seeded = await CreateAsync();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Owner).Host.Get<IInviteService>().CreateAsync(new CreateInviteRequest("x@example.com", "X", OrgRoles.Admin)));
        await Assert.ThrowsAsync<ArgumentException>(() => seeded.As(Admin).Host.Get<IInviteService>().CreateAsync(new CreateInviteRequest(Viewer, "Val", OrgRoles.Employee)));
        await Assert.ThrowsAsync<ArgumentException>(() => seeded.Host.Get<IInviteService>().CreateAsync(new CreateInviteRequest("not-an-email", "X", OrgRoles.Employee)));
        Assert.Contains(await EventsAsync(seeded), e => e.EventType == SecurityEventTypes.PermissionDenied && e.Actor == Owner);
    }

    [Fact]
    public async Task Deactivated_users_are_refused_and_keep_their_history()
    {
        await using var seeded = await CreateAsync();
        var admin = seeded.As(Admin).Host.Get<IUserAdminService>();
        var status = seeded.Host.Get<UserStatusCache>();
        Assert.True((await status.GetAsync(Viewer)).IsActive);

        var deactivated = await admin.DeactivateAsync(Viewer, "Left the company");
        var refused = await seeded.Host.Get<SignInGate>().EvaluateAsync(Viewer, null, "oidc");
        var bearer = await seeded.Host.Get<SignInGate>().EvaluateAsync(Viewer, null, "bearer", interactive: false);
        var state = await status.GetAsync(Viewer);

        Assert.False(deactivated.IsActive);
        Assert.Equal(Admin, deactivated.DeactivatedBy);
        Assert.False(refused.Allowed);
        Assert.False(bearer.Allowed);
        Assert.False(state.IsActive);
        Assert.False(state.Accepts(DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(OrgRoles.Employee, await seeded.As(Viewer).Host.Get<AccessControl>().GetOrgRolesAsync());
        Assert.DoesNotContain(await seeded.As(Admin).Host.Get<IUserDirectory>().SearchAsync(null, 100), u => u.Email == Viewer);

        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            Assert.True(await db.Set<AppUser>().AnyAsync(u => u.Email == Viewer));
            Assert.True(await db.Set<WorkspaceMember>().AnyAsync(m => m.UserEmail == Viewer));
        }

        var reactivated = await admin.ReactivateAsync(Viewer);
        Assert.True(reactivated.IsActive);
        Assert.True((await seeded.Host.Get<SignInGate>().EvaluateAsync(Viewer, null, "oidc")).Allowed);
        var events = await EventsAsync(seeded);
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.UserDeactivated && e.Target == Viewer && e.Detail == "Left the company");
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.UserReactivated && e.Target == Viewer);
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.SignInFailure && e.Target == Viewer);
    }

    [Fact]
    public async Task Sign_out_everywhere_rejects_sessions_issued_before_it()
    {
        await using var seeded = await CreateAsync();
        var before = DateTime.UtcNow.AddSeconds(-1);

        await seeded.As(Admin).Host.Get<IUserAdminService>().SignOutEverywhereAsync(Owner);
        var state = await seeded.Host.Get<UserStatusCache>().GetAsync(Owner);

        Assert.True(state.IsActive);
        Assert.False(state.Accepts(before));
        Assert.False(state.Accepts(null));
        Assert.True(state.Accepts(DateTime.UtcNow.AddSeconds(1)));
        Assert.Contains(await EventsAsync(seeded), e => e.EventType == SecurityEventTypes.SessionsRevoked && e.Target == Owner);
    }

    [Fact]
    public async Task Admins_cant_demote_or_deactivate_themselves_and_the_last_active_admin_stays()
    {
        await using var seeded = await CreateAsync();
        var admin = seeded.As(Admin).Host.Get<IUserAdminService>();

        var selfDemote = await Assert.ThrowsAsync<ArgumentException>(() => admin.SetRolesAsync(Admin, OrgRoles.Finance));
        var selfDeactivate = await Assert.ThrowsAsync<ArgumentException>(() => admin.DeactivateAsync(Admin));
        Assert.Contains("own Admin", selfDemote.Message);
        Assert.Contains("yourself", selfDeactivate.Message);

        // With a second admin, one can demote the other; then the one left is the last admin.
        await admin.SetRolesAsync(Owner, OrgRoles.Admin);
        await seeded.As(Owner).Host.Get<IUserAdminService>().SetRolesAsync(Admin, OrgRoles.Finance);

        // SCIM (no self) still can't remove the last active admin.
        var lifecycle = seeded.Host.Get<UserLifecycle>();
        var lastAdmin = await Assert.ThrowsAsync<ArgumentException>(() => lifecycle.SetActiveAsync(Owner, false, UserLifecycle.ScimActor));
        Assert.Contains("at least one active administrator", lastAdmin.Message);

        // A deactivated admin doesn't count as the remaining one.
        await seeded.As(Owner).Host.Get<IUserAdminService>().SetRolesAsync(Admin, OrgRoles.Admin);
        await lifecycle.SetActiveAsync(Admin, false, UserLifecycle.ScimActor);
        await Assert.ThrowsAsync<ArgumentException>(() => lifecycle.SetRolesAsync(Owner, OrgRoles.Employee, UserLifecycle.ScimActor));
        Assert.Contains(await EventsAsync(seeded), e => e.EventType == SecurityEventTypes.RolesChanged && e.Target == Admin && e.Detail == "Admin → Finance");
    }

    [Fact]
    public async Task Employee_link_defaults_to_email_and_can_be_set_or_removed()
    {
        await using var seeded = await CreateAsync();
        Guid matching, other;
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var byEmail = new Employee { EmployeeNumber = "E1", Email = Viewer, DisplayName = "Val Chen" };
            var contractor = new Employee { EmployeeNumber = "E2", Email = "val.contractor@example.com", DisplayName = "Val (contract)" };
            db.Set<Employee>().AddRange(byEmail, contractor);
            await db.SaveChangesAsync();
            (matching, other) = (byEmail.Id, contractor.Id);
        }

        var admin = seeded.As(Admin).Host.Get<IUserAdminService>();
        Assert.Equal(matching, await seeded.As(Viewer).Host.Get<AccessControl>().GetEmployeeIdAsync());

        var linked = await seeded.As(Admin).Host.Get<IUserAdminService>().SetEmployeeLinkAsync(Viewer, EmployeeLinkMode.Explicit, other);
        Assert.Equal(EmployeeLinkMode.Explicit, linked.EmployeeLink);
        Assert.Equal(other, await seeded.As(Viewer).Host.Get<AccessControl>().GetEmployeeIdAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => seeded.As(Admin).Host.Get<IUserAdminService>().SetEmployeeLinkAsync(Owner, EmployeeLinkMode.Explicit, other));

        await seeded.As(Admin).Host.Get<IUserAdminService>().SetEmployeeLinkAsync(Viewer, EmployeeLinkMode.Unlinked);
        Assert.Null(await seeded.As(Viewer).Host.Get<AccessControl>().GetEmployeeIdAsync());

        await seeded.As(Admin).Host.Get<IUserAdminService>().SetEmployeeLinkAsync(Viewer, EmployeeLinkMode.ByEmail);
        Assert.Equal(matching, await seeded.As(Viewer).Host.Get<AccessControl>().GetEmployeeIdAsync());
        Assert.Equal(3, (await EventsAsync(seeded)).Count(e => e.EventType == SecurityEventTypes.EmployeeLinkChanged));
    }

    [Fact]
    public async Task Access_review_snapshots_users_applies_revocations_and_is_frozen_when_completed()
    {
        await using var seeded = await CreateAsync();
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            (await db.Set<AppUser>().SingleAsync(u => u.Email == Owner)).LastSignInAtUtc = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        var reviews = seeded.As(Admin).Host.Get<IAccessReviewService>();
        var review = await reviews.StartAsync("2026 Q4");
        await Assert.ThrowsAsync<ArgumentException>(() => reviews.StartAsync());

        var owner = review.Items.Single(i => i.UserEmail == Owner);
        var viewer = review.Items.Single(i => i.UserEmail == Viewer);
        Assert.Equal(5, review.Summary.UserCount);
        Assert.Equal("Program Office: Owner", owner.WorkspaceRoles);
        Assert.False(owner.Dormant);
        Assert.True(viewer.Dormant);
        Assert.Equal("All workspaces (Admin)", review.Items.Single(i => i.UserEmail == Admin).WorkspaceRoles);

        await Assert.ThrowsAsync<ArgumentException>(() => reviews.DecideAsync(review.Summary.Id, viewer.Id, new AccessReviewDecisionRequest(AccessReviewDecisionKind.Revoke, null)));
        await Assert.ThrowsAsync<ArgumentException>(() => reviews.CompleteAsync(review.Summary.Id));
        var revoked = await reviews.DecideAsync(review.Summary.Id, viewer.Id, new AccessReviewDecisionRequest(AccessReviewDecisionKind.Revoke, "No sign-in for 90 days"));
        Assert.NotNull(revoked.AppliedAtUtc);
        Assert.False((await seeded.Host.Get<UserStatusCache>().GetAsync(Viewer)).IsActive);

        // Revoking the reviewer themselves is refused by the lifecycle rules.
        var self = review.Items.Single(i => i.UserEmail == Admin);
        await Assert.ThrowsAsync<ArgumentException>(() => reviews.DecideAsync(review.Summary.Id, self.Id, new AccessReviewDecisionRequest(AccessReviewDecisionKind.Revoke, "test")));

        foreach (var item in review.Items.Where(i => i.UserEmail != Viewer))
        {
            await reviews.DecideAsync(review.Summary.Id, item.Id, new AccessReviewDecisionRequest(AccessReviewDecisionKind.Keep, null));
        }

        var completed = await reviews.CompleteAsync(review.Summary.Id, "Quarterly review");
        Assert.True(completed.Summary.IsCompleted);
        Assert.Equal(Admin, completed.Summary.CompletedBy);
        Assert.Equal(1, completed.Summary.Revoked);
        await Assert.ThrowsAsync<ArgumentException>(() => reviews.DecideAsync(review.Summary.Id, owner.Id, new AccessReviewDecisionRequest(AccessReviewDecisionKind.Revoke, "late")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Owner).Host.Get<IAccessReviewService>().ListAsync());

        var events = await EventsAsync(seeded);
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.AccessReviewStarted && e.Target == "2026 Q4");
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.AccessReviewDecision && e.Target == Viewer && e.Detail!.Contains("Revoke"));
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.AccessReviewCompleted && e.Detail == "5 users reviewed, 1 revoked");
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.UserDeactivated && e.Target == Viewer);
    }

    [Fact]
    public async Task Security_event_log_filters_exports_and_is_admin_only()
    {
        await using var seeded = await CreateAsync();
        await seeded.As(Admin).Host.Get<IUserAdminService>().SetRolesAsync(Viewer, OrgRoles.Timekeeper);
        await seeded.Host.Get<IInviteService>().CreateAsync(new CreateInviteRequest("=cmd@example.com", "=HYPERLINK(\"x\")", OrgRoles.Employee));
        var service = seeded.Host.Get<ISecurityEventService>();

        var roles = await service.SearchAsync(new SecurityEventQuery(EventType: SecurityEventTypes.RolesChanged));
        var export = await service.ExportCsvAsync(new SecurityEventQuery());
        var types = await service.ListEventTypesAsync();

        Assert.Single(roles);
        Assert.StartsWith("OccurredAtUtc,EventType,Outcome,Actor,Target", export.Content);
        Assert.Contains("'=cmd@example.com", export.Content);
        Assert.Contains(SecurityEventTypes.InviteCreated, types);
        Assert.Contains((await service.SearchAsync(new SecurityEventQuery())), e => e.EventType == SecurityEventTypes.ExportSecurityEvents);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Viewer).Host.Get<ISecurityEventService>().SearchAsync(new SecurityEventQuery()));
    }

    internal static async Task<List<SecurityEvent>> EventsAsync(SeededHost seeded)
    {
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Set<SecurityEvent>().AsNoTracking().OrderBy(e => e.OccurredAtUtc).ToListAsync();
    }
}
