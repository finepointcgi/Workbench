using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Time;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Time;

namespace Workbench.Tests.Approvals;

/// <summary>Bulk decisions in the inbox, Approve and Reject links in emails, and project managers approving time on their projects.</summary>
public sealed partial class BulkAndEmailApprovalTests
{
    private sealed class Kit
    {
        public required TimeTestHost Host { get; init; }
        public required ApprovalInboxService Inbox { get; init; }
        public required ApprovalEmailLinkService Links { get; init; }
        public required ApprovalEmailActions Emails { get; init; }
        public required EmployeeDto Kim { get; init; }
        public required WbsElementDto Help { get; init; }

        public static async Task<Kit> CreateAsync()
        {
            var host = await TimeTestHost.CreateAsync();
            var lcat = await host.People.CreateLaborCategoryAsync(new SaveLaborCategoryRequest("SE3", "Systems Engineer III"));
            var kim = await host.People.CreateEmployeeAsync(new SaveEmployeeRequest("E4", "kim@example.com", "Kim Ray", null, host.Pat.Id, lcat.Id, new DateOnly(2024, 1, 1)));
            // Sam manages a second project Dana also charges.
            var sustainment = await host.Projects.CreateProjectAsync(new SaveProjectRequest("2002", "Sustainment", null, host.Sam.Id, ProjectState.Active, null, null));
            var help = await host.Projects.CreateWbsElementAsync(sustainment.Id, new SaveWbsElementRequest(null, "2002.01", "Help desk"));
            await host.Projects.AddAssignmentAsync(help.Id, new AddChargeAssignmentRequest(host.Dana.Id));

            var services = new ServiceCollection().AddSingleton<ITimesheetApprovalService>(host.Approvals).BuildServiceProvider();
            var engine = new ApprovalEngine(host.Clock);
            var access = new AccessControl(host.Factory, host.User);
            var inbox = new ApprovalInboxService(host.Factory, engine, access, [new TimesheetApprovalHandler(services)]);
            var tokens = new ApprovalEmailTokens(new EphemeralDataProtectionProvider());
            return new Kit
            {
                Host = host,
                Inbox = inbox,
                Links = new ApprovalEmailLinkService(host.Factory, engine, access, new AuditRecorder(host.User, host.Clock), tokens, inbox),
                Emails = new ApprovalEmailActions(host.Factory, engine, tokens, new NotificationOptions { PublicBaseUrl = "https://wb.example" }),
                Kim = kim,
                Help = help,
            };
        }

        /// <summary>A submitted week for an employee, charged to the given codes (overhead when none).</summary>
        public async Task<Guid> SubmitWeekAsync(EmployeeDto who, params Guid[] wbs)
        {
            Host.SignInAs(who);
            var sheet = await Host.Timesheets.GetOrCreateMyTimesheetAsync(TimeTestHost.Monday);
            var codes = wbs.Length == 0 ? [Host.Overhead.Id] : wbs;
            // One day per code from Monday (the clock is Wednesday, so at most three).
            var entries = codes.Select((code, i) => new TimeEntryInput(code, TimeTestHost.Monday.AddDays(i), 8m)).ToList();
            await Host.Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, new SaveTimeEntriesRequest(entries));
            await Host.Timesheets.SubmitAsync(sheet.Timesheet.Id, new SubmitTimesheetRequest(true));
            return sheet.Timesheet.Id;
        }

        public async Task<TimesheetStatus> StatusAsync(Guid id)
        {
            await using var db = await Host.Factory.CreateAsync();
            return (await db.Set<Timesheet>().AsNoTracking().SingleAsync(t => t.Id == id)).Status;
        }

        public async Task<List<Workbench.Domain.ProjectAccounting.AuditEvent>> LinkAuditAsync()
        {
            await using var db = await Host.Factory.CreateAsync();
            return await db.Set<Workbench.Domain.ProjectAccounting.AuditEvent>().AsNoTracking()
                .Where(a => a.EntityType == ApprovalEmailLinkService.AuditEntityType).ToListAsync();
        }

        /// <summary>The Approve and Reject tokens in the email a notification to <paramref name="recipient"/> would get now.</summary>
        public async Task<(string Approve, string Reject)> TokensForAsync(string recipient)
        {
            var notification = new Notification { RecipientEmail = recipient, Kind = "timesheet.submitted", Title = "Submitted", Body = "", CreatedAtUtc = Host.Clock.UtcNow };
            var extra = await Emails.ForSingleAsync(notification) ?? throw new InvalidOperationException("No approval links in the email.");
            var approve = ApproveLink().Match(extra.Text);
            var reject = RejectLink().Match(extra.Text);
            return (approve.Groups[1].Value, reject.Groups[1].Value);
        }
    }

    [GeneratedRegex(@"Approve: https://wb\.example/approvals/email/(\S+)")]
    private static partial Regex ApproveLink();

    [GeneratedRegex(@"Reject: https://wb\.example/approvals/email/(\S+)")]
    private static partial Regex RejectLink();

    private static BulkApprovalRequest Bulk(ApprovalDecisionKind kind, string? comment, params Guid[] timesheets) =>
        new(timesheets.Select(id => new ApprovalItemRef(ApprovalDocumentTypes.Timesheet, id)).ToList(), kind, comment);

    // ---- Bulk ----

    [Fact]
    public async Task Bulk_approve_decides_each_item_and_reports_the_ones_it_could_not()
    {
        var kit = await Kit.CreateAsync();
        var dana = await kit.SubmitWeekAsync(kit.Host.Dana, kit.Host.Direct.Id);
        var kim = await kit.SubmitWeekAsync(kit.Kim);
        var pats = await kit.SubmitWeekAsync(kit.Host.Pat); // Pat has no supervisor: it goes to the administrators.

        kit.Host.SignInAs(kit.Host.Pat);
        Assert.Equal(2, (await kit.Inbox.ListMineAsync()).Count);
        // Pat's own timesheet in the request (a crafted call): segregation of duties still refuses it, the others go through.
        var result = await kit.Inbox.DecideManyAsync(Bulk(ApprovalDecisionKind.Approve, "Looks right", dana, kim, pats));

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal("2 approved, 1 couldn't be: you entered it yourself", result.Summary);
        var refused = Assert.Single(result.Items, r => !r.Succeeded);
        Assert.Equal(pats, refused.DocumentId);
        Assert.Contains("Pat Morgan", refused.Title);
        Assert.Equal(TimesheetStatus.Approved, await kit.StatusAsync(dana));
        Assert.Equal(TimesheetStatus.Approved, await kit.StatusAsync(kim));
        Assert.Equal(TimesheetStatus.Submitted, await kit.StatusAsync(pats));
        Assert.Empty(await kit.Inbox.ListMineAsync());

        // Doing it again: each is already decided, and nothing changes.
        var again = await kit.Inbox.DecideManyAsync(Bulk(ApprovalDecisionKind.Approve, null, dana, kim));
        Assert.Equal(0, again.Succeeded);
        Assert.Equal("0 approved, 2 couldn't be: it isn't waiting for approval any more", again.Summary);
    }

    [Fact]
    public async Task Bulk_decisions_by_someone_who_is_not_the_approver_change_nothing()
    {
        var kit = await Kit.CreateAsync();
        var dana = await kit.SubmitWeekAsync(kit.Host.Dana, kit.Host.Direct.Id);
        var kim = await kit.SubmitWeekAsync(kit.Kim);

        kit.Host.SignInAs(kit.Host.Sam);
        var result = await kit.Inbox.DecideManyAsync(Bulk(ApprovalDecisionKind.Approve, null, dana, kim));
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(2, result.Failed);
        Assert.All(result.Items, r => Assert.Contains("supervisor", r.Problem));
        Assert.Equal(TimesheetStatus.Submitted, await kit.StatusAsync(dana));

        // The submitter can't bulk-approve their own either.
        kit.Host.SignInAs(kit.Host.Dana);
        Assert.Equal("0 approved, 1 couldn't be: you entered it yourself", (await kit.Inbox.DecideManyAsync(Bulk(ApprovalDecisionKind.Approve, null, dana))).Summary);
    }

    [Fact]
    public async Task Bulk_reject_needs_one_reason_and_gives_it_to_every_item()
    {
        var kit = await Kit.CreateAsync();
        var dana = await kit.SubmitWeekAsync(kit.Host.Dana, kit.Host.Direct.Id);
        var kim = await kit.SubmitWeekAsync(kit.Kim);
        kit.Host.SignInAs(kit.Host.Pat);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Inbox.DecideManyAsync(Bulk(ApprovalDecisionKind.Reject, "  ", dana, kim)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Inbox.DecideManyAsync(Bulk(ApprovalDecisionKind.Return, "Fix it", dana)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Inbox.DecideManyAsync(new BulkApprovalRequest([], ApprovalDecisionKind.Approve)));
        Assert.Equal(TimesheetStatus.Submitted, await kit.StatusAsync(dana));

        var result = await kit.Inbox.DecideManyAsync(Bulk(ApprovalDecisionKind.Reject, "Use the new charge codes", dana, kim, dana));
        Assert.Equal("2 rejected", result.Summary);
        await using var db = await kit.Host.Factory.CreateAsync();
        var sheets = await db.Set<Timesheet>().AsNoTracking().Where(t => t.Id == dana || t.Id == kim).ToListAsync();
        Assert.All(sheets, s =>
        {
            Assert.Equal(TimesheetStatus.Rejected, s.Status);
            Assert.Equal("Use the new charge codes", s.RejectionReason);
        });
    }

    [Fact]
    public void The_summary_lists_each_reason_with_its_count()
    {
        var results = new List<BulkApprovalItemResult>
        {
            new("timesheet", Guid.NewGuid(), "A", true, null),
            new("timesheet", Guid.NewGuid(), "B", false, "you entered it yourself"),
            new("timesheet", Guid.NewGuid(), "C", false, "you entered it yourself"),
            new("timesheet", Guid.NewGuid(), "D", false, "The period is closed."),
        };
        Assert.Equal("1 approved, 3 couldn't be: 2 because you entered it yourself; 1 because The period is closed",
            ApprovalInboxService.Summarize(ApprovalDecisionKind.Approve, results));
    }

    // ---- Project managers approve time on their projects ----

    [Fact]
    public async Task A_project_manager_approves_the_time_charged_to_their_project_from_the_inbox()
    {
        var kit = await Kit.CreateAsync();
        kit.Host.User.Email = "admin@example.com";
        var admin = new TestUser("admin@example.com");
        await new ApprovalWorkflowService(kit.Host.Factory, new ApprovalEngine(kit.Host.Clock), new AuditRecorder(admin, kit.Host.Clock), new AccessControl(kit.Host.Factory, admin))
            .SaveAsync(ApprovalDocumentTypes.Timesheet, new SaveApprovalWorkflowRequest("Supervisor and project managers", new ApprovalWorkflowDefinition([
                new ApprovalStepDefinition("Supervisor", 1, new ApproverRule(ApproverRuleKind.Supervisor), Fallback: new ApproverRule(ApproverRuleKind.OrgRole, ApprovalOrgRole.Admin),
                    WhenSubmitterIsApprover: SelfApprovalPolicy.Unassigned),
                new ApprovalStepDefinition("Project managers", 1, new ApproverRule(ApproverRuleKind.ProjectManager), WhenSubmitterIsApprover: SelfApprovalPolicy.Skip),
            ])));
        var dana = await kit.SubmitWeekAsync(kit.Host.Dana, kit.Host.Direct.Id, kit.Help.Id, kit.Host.Overhead.Id);
        var kim = await kit.SubmitWeekAsync(kit.Kim); // Overhead only: no project manager step.

        kit.Host.SignInAs(kit.Host.Sam);
        var item = Assert.Single(await kit.Inbox.ListMineAsync());
        Assert.Equal(dana, item.DocumentId);
        Assert.Equal("Project managers", item.StepName);
        Assert.Equal("2002 Sustainment", item.ScopeLabel);
        Assert.NotNull(item.ProjectId);
        var result = await kit.Inbox.DecideManyAsync(Bulk(ApprovalDecisionKind.Approve, null, item.DocumentId, kim));
        Assert.Equal("1 approved, 1 couldn't be: Only the employee's supervisor (or an administrator when there's no supervisor) can review this timesheet", result.Summary);
        Assert.Equal(TimesheetStatus.Submitted, await kit.StatusAsync(dana)); // The supervisor (and 1001's manager) still decide.
        Assert.Empty(await kit.Inbox.ListMineAsync());

        // Pat is Dana's supervisor and manages 1001: one inbox row, one approval for both tasks.
        kit.Host.SignInAs(kit.Host.Pat);
        var pats = (await kit.Inbox.ListMineAsync()).Single(i => i.DocumentId == dana);
        Assert.NotNull(pats.ProjectId);
        Assert.Equal("2 approved", (await kit.Inbox.DecideManyAsync(Bulk(ApprovalDecisionKind.Approve, null, dana, kim))).Summary);
        Assert.Equal(TimesheetStatus.Approved, await kit.StatusAsync(dana));
        var history = Assert.Single(await kit.Inbox.GetHistoryAsync(ApprovalDocumentTypes.Timesheet, dana));
        Assert.Contains(history.Tasks, t => t.ScopeLabel == "2002 Sustainment" && t.DecidedBy == "sam@example.com");
    }

    // ---- Approve from email ----

    [Fact]
    public async Task An_email_link_approves_only_after_a_signed_in_confirm_and_only_once()
    {
        var kit = await Kit.CreateAsync();
        var dana = await kit.SubmitWeekAsync(kit.Host.Dana, kit.Host.Direct.Id);
        var (approve, reject) = await kit.TokensForAsync("pat@example.com");

        kit.Host.SignInAs(kit.Host.Pat);
        var opened = await kit.Links.OpenAsync(approve);
        Assert.Equal(ApprovalEmailLinkState.Ready, opened.State);
        Assert.Equal(ApprovalDecisionKind.Approve, opened.Decision);
        Assert.Contains("Dana Lee", opened.Title);
        Assert.Equal(TimesheetStatus.Submitted, await kit.StatusAsync(dana)); // Opening never decides.

        var done = await kit.Links.ConfirmAsync(new ConfirmApprovalEmailLinkRequest(approve, "Thanks"));
        Assert.Equal(ApprovalEmailLinkState.Done, done.State);
        Assert.Equal(TimesheetStatus.Approved, await kit.StatusAsync(dana));
        Assert.Contains(await kit.LinkAuditAsync(), a => a.Action == "Approved" && a.Actor == "pat@example.com");

        // Single use, and its sibling Reject link is dead too.
        Assert.Equal(ApprovalEmailLinkState.Used, (await kit.Links.ConfirmAsync(new ConfirmApprovalEmailLinkRequest(approve))).State);
        Assert.Equal(ApprovalEmailLinkState.NoLongerWaiting, (await kit.Links.ConfirmAsync(new ConfirmApprovalEmailLinkRequest(reject, "Too late"))).State);
        Assert.Equal(ApprovalEmailLinkState.Invalid, (await kit.Links.OpenAsync(approve[..^4] + "AAAA")).State);
        Assert.Equal(2, (await kit.LinkAuditAsync()).Count(a => a.Action == ApprovalEmailLinkService.RefusedAction));
    }

    [Fact]
    public async Task An_email_link_rejects_with_a_reason()
    {
        var kit = await Kit.CreateAsync();
        var dana = await kit.SubmitWeekAsync(kit.Host.Dana, kit.Host.Direct.Id);
        var (_, reject) = await kit.TokensForAsync("pat@example.com");

        kit.Host.SignInAs(kit.Host.Pat);
        Assert.Equal(ApprovalDecisionKind.Reject, (await kit.Links.OpenAsync(reject)).Decision);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Links.ConfirmAsync(new ConfirmApprovalEmailLinkRequest(reject)));
        Assert.Equal(TimesheetStatus.Submitted, await kit.StatusAsync(dana));

        Assert.Equal(ApprovalEmailLinkState.Done, (await kit.Links.ConfirmAsync(new ConfirmApprovalEmailLinkRequest(reject, "Wrong week"))).State);
        Assert.Equal(TimesheetStatus.Rejected, await kit.StatusAsync(dana));
        Assert.Contains(await kit.LinkAuditAsync(), a => a.Action == "Rejected" && a.Reason == "Wrong week");
    }

    [Fact]
    public async Task An_email_link_opened_by_someone_else_is_refused_and_audited()
    {
        var kit = await Kit.CreateAsync();
        var dana = await kit.SubmitWeekAsync(kit.Host.Dana, kit.Host.Direct.Id);
        var (approve, _) = await kit.TokensForAsync("pat@example.com");

        foreach (var who in new[] { kit.Host.Sam, kit.Host.Dana })
        {
            kit.Host.SignInAs(who);
            var opened = await kit.Links.OpenAsync(approve);
            Assert.Equal(ApprovalEmailLinkState.WrongUser, opened.State);
            Assert.Null(opened.Title); // Nothing about the document.
            Assert.Equal(ApprovalEmailLinkState.WrongUser, (await kit.Links.ConfirmAsync(new ConfirmApprovalEmailLinkRequest(approve))).State);
        }

        Assert.Equal(TimesheetStatus.Submitted, await kit.StatusAsync(dana));
        var refusals = (await kit.LinkAuditAsync()).Where(a => a.Action == ApprovalEmailLinkService.RefusedAction).ToList();
        Assert.Equal(2, refusals.Count); // One per person: opening then confirming within a minute is the same refusal.
        Assert.Contains(refusals, a => a.Actor == "sam@example.com" && a.Reason!.Contains("sent to pat@example.com"));

        // Still good for Pat.
        kit.Host.SignInAs(kit.Host.Pat);
        Assert.Equal(ApprovalEmailLinkState.Done, (await kit.Links.ConfirmAsync(new ConfirmApprovalEmailLinkRequest(approve))).State);
    }

    [Fact]
    public async Task An_email_link_expires()
    {
        var kit = await Kit.CreateAsync();
        var dana = await kit.SubmitWeekAsync(kit.Host.Dana, kit.Host.Direct.Id);
        var (approve, _) = await kit.TokensForAsync("pat@example.com");

        kit.Host.Clock.UtcNow += ApprovalEmailTokens.Lifetime + TimeSpan.FromMinutes(1);
        kit.Host.SignInAs(kit.Host.Pat);
        Assert.Equal(ApprovalEmailLinkState.Expired, (await kit.Links.OpenAsync(approve)).State);
        Assert.Equal(ApprovalEmailLinkState.Expired, (await kit.Links.ConfirmAsync(new ConfirmApprovalEmailLinkRequest(approve))).State);
        Assert.Equal(TimesheetStatus.Submitted, await kit.StatusAsync(dana));
        Assert.Contains(await kit.LinkAuditAsync(), a => a.Reason == "Expired");
    }

    [Fact]
    public async Task Approval_emails_carry_links_for_what_became_waiting_and_digests_for_everything()
    {
        var kit = await Kit.CreateAsync();
        await kit.SubmitWeekAsync(kit.Host.Dana, kit.Host.Direct.Id);
        var notification = new Notification { RecipientEmail = "pat@example.com", Kind = "timesheet.submitted", Title = "Dana Lee submitted a timesheet", Body = "", CreatedAtUtc = kit.Host.Clock.UtcNow };

        var email = NotificationEmails.Single(notification, new NotificationOptions(), await kit.Emails.ForSingleAsync(notification));
        Assert.Contains("Waiting on you", email.HtmlBody);
        Assert.Contains(">Approve</a>", email.HtmlBody);
        Assert.Contains(">Reject</a>", email.HtmlBody);
        Assert.Contains("Timesheet: Dana Lee: timesheet", email.TextBody);

        // Nothing waiting on Sam; and a notification long after the task started (a comment, say) gets no links.
        Assert.Null(await kit.Emails.ForSingleAsync(new Notification { RecipientEmail = "sam@example.com", Kind = "timesheet.submitted", Title = "x", Body = "", CreatedAtUtc = kit.Host.Clock.UtcNow }));
        Assert.Null(await kit.Emails.ForSingleAsync(new Notification { RecipientEmail = "pat@example.com", Kind = "mention", Title = "x", Body = "", CreatedAtUtc = kit.Host.Clock.UtcNow.AddHours(3) }));

        await kit.SubmitWeekAsync(kit.Kim);
        var digest = await kit.Emails.ForDigestAsync("pat@example.com", [notification]);
        Assert.Equal(2, Regex.Matches(digest!.Text, "Approve: ").Count);
    }
}
