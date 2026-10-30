using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Integrations.Jira;
using Workbench.Contracts;
using Workbench.Contracts.Integrations.Jira;
using Workbench.Domain.Integrations;
using Workbench.Domain.Integrations.Jira;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Integrations.Jira;

public sealed class JiraSyncTests
{
    private static readonly DateOnly Nov20 = new(2026, 11, 20);
    private static readonly DateOnly Nov25 = new(2026, 11, 25);
    private static readonly DateOnly Dec1 = new(2026, 12, 1);

    // ===================================================================== connecting

    [Fact]
    public async Task Saving_the_connection_tests_it_and_keeps_the_token_encrypted()
    {
        await using var kit = await JiraKit.CreateAsync();
        var dto = await kit.Connection.GetAsync();
        Assert.True(dto.IsConfigured);
        Assert.Equal(JiraKit.SiteUrl, dto.SiteUrl);
        Assert.Equal("Workbench Bot", dto.ConnectedAsName);
        Assert.Equal(FakeJira.Token[^4..], dto.TokenHint);

        await using var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var row = await db.Set<JiraConnection>().SingleAsync();
        Assert.DoesNotContain(FakeJira.Token, row.TokenProtected);
        Assert.Equal(FakeJira.Token, kit.Host.Get<JiraSecrets>().UnprotectToken(row.TokenProtected));
        Assert.True(await db.Set<IntegrationConnector>().AnyAsync(c => c.Kind == JiraOutbox.ConnectorKind && c.IsActive));
        Assert.DoesNotContain(await kit.Host.Get<Contracts.Integrations.IConnectorService>().ListKindsAsync(), k => k.Kind == JiraOutbox.ConnectorKind);
        Assert.DoesNotContain(FakeJira.Token, System.Text.Json.JsonSerializer.Serialize(dto));
    }

    [Fact]
    public async Task Test_connection_explains_a_wrong_token_and_a_bad_address_in_plain_words()
    {
        await using var kit = await JiraKit.CreateAsync(connect: false);
        var wrong = await kit.Connection.TestAsync(new SaveJiraConnectionRequest(JiraKit.SiteUrl, FakeJira.Email, "wrong-token-0000000"));
        Assert.False(wrong.Ok);
        Assert.Contains("didn't accept the account email and API token", wrong.Message);
        Assert.Contains("API token", wrong.Hint);

        var badAddress = await kit.Connection.TestAsync(new SaveJiraConnectionRequest("http://acme.atlassian.net", FakeJira.Email, FakeJira.Token));
        Assert.False(badAddress.Ok);
        Assert.Contains("https://", badAddress.Message);

        var ok = await kit.Connection.TestAsync(new SaveJiraConnectionRequest("acme.atlassian.net", FakeJira.Email, FakeJira.Token));
        Assert.True(ok.Ok);
        Assert.DoesNotContain(FakeJira.Token, ok.Message);
    }

    [Fact]
    public async Task A_blank_token_keeps_the_saved_one_and_disconnect_is_refused_while_a_project_is_linked()
    {
        await using var kit = await JiraKit.CreateAsync();
        var again = await kit.Connection.SaveAsync(new SaveJiraConnectionRequest(JiraKit.SiteUrl, FakeJira.Email, "  "));
        Assert.True(again.LastTestOk);

        await kit.LinkAsync();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => kit.Connection.DeleteAsync());
        Assert.Contains("linked to Jira", ex.Message);
        await kit.Sync.UnlinkAsync(kit.ProjectId);
        await kit.Connection.DeleteAsync();
        Assert.False((await kit.Connection.GetAsync()).IsConfigured);
    }

    // ===================================================================== jira -> workbench

    [Fact]
    public async Task Linking_brings_issues_in_as_tasks_with_title_assignee_due_date_status_and_estimate()
    {
        await using var kit = await JiraKit.CreateAsync();
        var due = kit.Jira.Add("Write the interface spec", assignee: "acc-jordan", due: Nov20, estimateSeconds: 28_800);
        var done = kit.Jira.Add("Pick the vendor", assignee: "acc-hidden", done: true);
        kit.Jira.Add("Unassigned idea");

        var panel = await kit.LinkAsync();

        Assert.Equal(3, panel.Tasks.Count);
        var spec = await kit.ItemAsync("Write the interface spec");
        Assert.Equal(Nov20, spec.Finish);
        Assert.Equal([JiraKit.Jordan], await kit.AssigneesAsync(spec.Id));
        Assert.Equal(8m, (await kit.ItemLinkAsync(spec.Id)).EstimateHours);
        Assert.Equal("8", spec.Values.GetValueOrDefault(kit.EstimateColumn.ToString()));
        Assert.NotEqual("Done", kit.Status(spec));

        var vendor = await kit.ItemAsync("Pick the vendor");
        Assert.Equal("Done", kit.Status(vendor));
        Assert.Equal([JiraKit.Sam], await kit.AssigneesAsync(vendor.Id)); // Jira hides Sam's email: matched by name
        Assert.Empty(await kit.AssigneesAsync((await kit.ItemAsync("Unassigned idea")).Id));

        // The Jira key is a link on the task.
        var status = await kit.Sync.GetTaskStatusAsync(kit.SheetId, spec.Id);
        Assert.Equal(due.Key, status!.IssueKey);
        Assert.Equal($"{JiraKit.SiteUrl}/browse/{due.Key}", status.WebUrl);
        Assert.Equal("To Do", status.JiraStatus);
        Assert.Equal(done.Key, (await kit.Sync.GetTaskStatusAsync(kit.SheetId, vendor.Id))!.IssueKey);
        Assert.Null(await kit.Sync.GetTaskStatusAsync(kit.SheetId, kit.ExistingTask)); // a Workbench-only task isn't linked
    }

    [Fact]
    public async Task An_issue_linked_to_a_task_never_makes_a_duplicate_however_often_it_arrives()
    {
        await using var kit = await JiraKit.CreateAsync();
        var issue = kit.Jira.Add("Only once");
        await kit.LinkAsync();
        Assert.Single((await kit.ItemsAsync()).Where(i => i.Title == "Only once"));

        // Sync now, the poll, and webhooks (even different deliveries for the same change) change nothing.
        await kit.Sync.SyncNowAsync(kit.ProjectId);
        await kit.Engine.ReconcileAsync(await kit.LinkIdAsync(), everything: false, CancellationToken.None);
        await kit.HookAsync(issue);
        await kit.HookAsync(issue);
        await kit.DispatchAsync();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => kit.Host.Get<IServiceScopeFactory>().CreateAsyncScope()).Select(async scope =>
        {
            await using (scope)
            {
                await scope.ServiceProvider.GetRequiredService<JiraSyncEngine>().SyncIssueAsync(await kit.LinkIdAsync(), issue.Key, false, CancellationToken.None);
            }
        }));

        Assert.Single((await kit.ItemsAsync()).Where(i => i.Title == "Only once"));
        await using var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.Equal(1, await db.Set<JiraItemLink>().CountAsync());
    }

    [Fact]
    public async Task Jira_wins_the_title_and_the_assignee_and_done_comes_across_with_a_note_in_the_activity_log()
    {
        await using var kit = await JiraKit.CreateAsync();
        var issue = kit.Jira.Add("Draft title", assignee: "acc-jordan");
        await kit.LinkAsync();
        var task = await kit.ItemAsync("Draft title");

        issue.Summary = "Final title";
        issue.AssigneeAccountId = "acc-hidden";
        kit.Jira.SetDone(issue, true);
        Assert.Equal(200, (await kit.HookAsync(issue)).StatusCode);
        await kit.DispatchAsync();

        task = await kit.ItemAsync("Final title");
        Assert.Equal([JiraKit.Sam], await kit.AssigneesAsync(task.Id)); // Jordan was replaced, not added to
        Assert.Equal("Done", kit.Status(task));
        var log = await kit.ActivityAsync(task.Id);
        Assert.Contains(log, l => l.Contains("Title changed from Jira") && l.Contains("Draft title"));
        Assert.Contains(log, l => l.Contains("Marked Done because Jira"));
    }

    [Fact]
    public async Task An_assignee_workbench_does_not_know_is_a_note_not_a_failure()
    {
        await using var kit = await JiraKit.CreateAsync();
        kit.Jira.Users.Add(new FakeJiraUser("acc-casey", "Casey Outside", "casey@elsewhere.example"));
        kit.Jira.Add("Outsider's task", assignee: "acc-casey");
        var panel = await kit.LinkAsync();
        Assert.Single(panel.Tasks);
        Assert.Contains(panel.Notes, n => n.Field == "assignee" && n.Message.Contains("Casey Outside") && !n.IsError);

        var dismissed = await kit.Sync.DismissNoteAsync(kit.ProjectId, panel.Notes[0].Id);
        Assert.Empty(dismissed.Notes);
    }

    [Fact]
    public async Task The_epic_and_the_jql_filter_narrow_which_issues_become_tasks()
    {
        await using var kit = await JiraKit.CreateAsync();
        kit.Jira.Add("In the epic", parent: "RAD-1");
        kit.Jira.Add("Outside the epic");
        var panel = await kit.LinkAsync(epic: "RAD-1");
        Assert.Equal(["In the epic"], panel.Tasks.Select(t => t.Title));

        await using var other = await JiraKit.CreateAsync();
        other.Jira.Add("Labelled").Labels.Add("workbench");
        other.Jira.Add("Not labelled");
        var filtered = await other.LinkAsync(jql: "labels = \"workbench\" ORDER BY created");
        Assert.Equal(["Labelled"], filtered.Tasks.Select(t => t.Title));
        Assert.Contains(other.Jira.Requests, r => r.Path.Contains("labels") && !r.Path.Contains("ORDER", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Linking_refuses_a_second_plan_for_the_same_issues_and_an_epic_outside_the_project()
    {
        await using var kit = await JiraKit.CreateAsync();
        var bad = await Assert.ThrowsAsync<ArgumentException>(() => kit.Sync.LinkAsync(kit.ProjectId, new LinkJiraProjectRequest("RAD", "OTHER-5")));
        Assert.Contains("epic must be an issue key in RAD", bad.Message);
        var missing = await Assert.ThrowsAsync<ArgumentException>(() => kit.Sync.LinkAsync(kit.ProjectId, new LinkJiraProjectRequest("NOPE")));
        Assert.Contains("no project NOPE", missing.Message);

        await kit.LinkAsync();
        var again = await Assert.ThrowsAsync<ArgumentException>(() => kit.Sync.LinkAsync(kit.ProjectId, new LinkJiraProjectRequest("RAD")));
        Assert.Contains("already linked", again.Message);
    }

    // ===================================================================== webhook and poll

    [Fact]
    public async Task The_webhook_checks_the_secret_ignores_replays_and_other_projects_and_never_trusts_the_payload()
    {
        await using var kit = await JiraKit.CreateAsync();
        var issue = kit.Jira.Add("Hooked");
        await kit.LinkAsync();

        Assert.Equal(401, (await kit.HookAsync(issue, badSecret: "wrong")).StatusCode);
        Assert.Equal(401, (await kit.HookAsync(issue, sign: false)).StatusCode);
        Assert.Equal(404, (await kit.Host.Get<JiraWebhookReceiver>().ReceiveAsync(Guid.NewGuid(), null, "x", null, "{}", CancellationToken.None)).StatusCode);

        issue.Summary = "Hooked and renamed";
        var first = await kit.HookAsync(issue, identifier: "hook-1");
        Assert.Equal("Queued.", first.Message);
        Assert.Equal("Already received.", (await kit.HookAsync(issue, identifier: "hook-1")).Message);
        await kit.DispatchAsync();
        Assert.Contains(await kit.ItemsAsync(), i => i.Title == "Hooked and renamed");

        var secret = (await kit.Sync.GetWebhookSetupAsync(kit.ProjectId)).Secret;
        var byToken = await kit.Host.Get<JiraWebhookReceiver>().ReceiveAsync(await kit.LinkIdAsync(), null, secret, "hook-2",
            """{"webhookEvent":"jira:issue_updated","issue":{"key":"RAD-100","fields":{"project":{"key":"RAD"}}}}""", CancellationToken.None);
        Assert.Equal("Queued.", byToken.Message);

        var otherProject = kit.Jira.Add("Elsewhere", project: "OPS");
        Assert.StartsWith("Ignored", (await kit.HookAsync(otherProject)).Message);
        Assert.StartsWith("Ignored", (await kit.HookAsync(issue, eventName: "comment_created")).Message);

        // A payload that lies about the title changes nothing: the issue is read from Jira.
        var lie = """{"webhookEvent":"jira:issue_updated","issue":{"key":"RAD-100","fields":{"project":{"key":"RAD"},"summary":"HACKED"}}}""";
        await kit.Host.Get<JiraWebhookReceiver>().ReceiveAsync(await kit.LinkIdAsync(), JiraWebhookReceiver.Sign(secret, lie), null, "hook-3", lie, CancellationToken.None);
        await kit.DispatchAsync();
        Assert.DoesNotContain(await kit.ItemsAsync(), i => i.Title == "HACKED");
    }

    [Fact]
    public async Task The_poll_picks_up_what_no_webhook_reported_and_skips_what_did_not_change()
    {
        await using var kit = await JiraKit.CreateAsync();
        var quiet = kit.Jira.Add("Quiet issue");
        var busy = kit.Jira.Add("Busy issue");
        await kit.LinkAsync();
        var linkId = await kit.LinkIdAsync();
        await kit.Engine.ReconcileAsync(linkId, everything: true, CancellationToken.None);

        kit.Clock.Advance(TimeSpan.FromMinutes(30));
        busy.Summary = "Busy issue, renamed";
        busy.Updated = kit.Clock.Now.UtcDateTime;
        while (kit.Jira.Requests.TryDequeue(out _))
        {
        }

        var report = await kit.Engine.ReconcileAsync(linkId, everything: false, CancellationToken.None);

        Assert.Equal(1, report.Synced);
        Assert.Contains(await kit.ItemsAsync(), i => i.Title == "Busy issue, renamed");
        Assert.Contains(await kit.ItemsAsync(), i => i.Title == "Quiet issue");
        Assert.Contains(kit.Jira.Requests, r => r.Path.Contains("updated%20%3E%3D%20-", StringComparison.Ordinal) || r.Path.Contains("updated >= -"));
        Assert.Equal(quiet.Summary, (await kit.ItemAsync("Quiet issue")).Title);
    }

    // ===================================================================== workbench -> jira

    [Fact]
    public async Task Marking_a_linked_task_done_moves_the_issue_to_done_and_the_echo_changes_nothing()
    {
        await using var kit = await JiraKit.CreateAsync();
        var issue = kit.Jira.Add("Finish me");
        await kit.LinkAsync();
        var task = await kit.ItemAsync("Finish me");

        await kit.EditAsync(task.Id, i => i.Values[kit.StatusColumn.ToString()] = "Done");

        Assert.Equal("done", issue.Category);
        Assert.Contains(await kit.ActivityAsync(task.Id), l => l.Contains($"Moved Jira {issue.Key} to Done"));
        var link = await kit.ItemLinkAsync(task.Id);
        Assert.True(link.BaseJiraDone);
        Assert.True(link.BaseWorkbenchDone);

        while (kit.Jira.Requests.TryDequeue(out _))
        {
        }

        await kit.HookAsync(issue); // Jira's echo of our own change
        await kit.DispatchAsync();
        Assert.Equal("Done", kit.Status(await kit.ItemAsync("Finish me")));
        Assert.DoesNotContain(kit.Jira.Requests, r => r.Method is "POST" or "PUT");
    }

    [Fact]
    public async Task A_changed_due_date_goes_to_jira_and_an_unrelated_edit_makes_no_jira_calls()
    {
        await using var kit = await JiraKit.CreateAsync();
        var issue = kit.Jira.Add("Date me", due: Nov20);
        await kit.LinkAsync();
        var task = await kit.ItemAsync("Date me");
        Assert.Equal(Nov20, task.Finish);

        while (kit.Jira.Requests.TryDequeue(out _))
        {
        }

        await kit.EditAsync(task.Id, i => i.Title = "Date me (renamed here)");
        Assert.Empty(kit.Jira.Requests);

        await kit.EditAsync(task.Id, i => i.Finish = Nov25);
        Assert.Equal(Nov25, issue.DueDate);
        Assert.Contains(await kit.ActivityAsync(task.Id), l => l.Contains("sent to Jira"));
    }

    [Fact]
    public async Task Reopening_a_task_is_not_sent_to_jira_and_says_so()
    {
        await using var kit = await JiraKit.CreateAsync();
        var issue = kit.Jira.Add("Already done", done: true);
        var panel = await kit.LinkAsync();
        var task = await kit.ItemAsync("Already done");
        Assert.Equal("Done", kit.Status(task));

        await kit.EditAsync(task.Id, i => i.Values[kit.StatusColumn.ToString()] = "In progress");

        Assert.Equal("done", issue.Category);
        panel = await kit.Sync.GetAsync(kit.ProjectId);
        Assert.Contains(panel.Notes, n => n.Field == "status" && n.Message.Contains("reopening isn't sent to Jira"));
        Assert.Equal("In progress", kit.Status(await kit.ItemAsync("Already done"))); // and Workbench keeps what the person set
    }

    [Fact]
    public async Task A_workflow_with_no_way_to_done_is_a_note_with_what_to_do_not_a_failure()
    {
        await using var kit = await JiraKit.CreateAsync();
        kit.Jira.NoDoneTransition = true;
        kit.Jira.Add("Stuck");
        await kit.LinkAsync();
        var task = await kit.ItemAsync("Stuck");

        await kit.EditAsync(task.Id, i => i.Values[kit.StatusColumn.ToString()] = "Done");

        var panel = await kit.Sync.GetAsync(kit.ProjectId);
        var note = Assert.Single(panel.Notes, n => n.Field == "status");
        Assert.True(note.IsError);
        Assert.Contains("no way to Done", note.Message);
        Assert.Contains("Jira admin", note.Hint);
        Assert.Equal("Done", kit.Status(await kit.ItemAsync("Stuck")));
    }

    // ===================================================================== conflicts

    [Theory]
    [InlineData(JiraDateWinnerChoice.Workbench, true)]
    [InlineData(JiraDateWinnerChoice.Jira, false)]
    public async Task When_both_sides_changed_a_date_the_setting_decides_and_the_panel_says_which_won(JiraDateWinnerChoice winner, bool workbenchWins)
    {
        await using var kit = await JiraKit.CreateAsync();
        var issue = kit.Jira.Add("Contested", due: Nov20);
        await kit.LinkAsync(winner: winner);
        var task = await kit.ItemAsync("Contested");

        issue.DueDate = Dec1;
        issue.Updated = kit.Clock.Now.UtcDateTime;
        await kit.EditAsync(task.Id, i => i.Finish = Nov25);

        var after = await kit.ItemAsync("Contested");
        Assert.Equal(workbenchWins ? Nov25 : Dec1, after.Finish);
        Assert.Equal(workbenchWins ? Nov25 : Dec1, issue.DueDate);
        var panel = await kit.Sync.GetAsync(kit.ProjectId);
        var note = Assert.Single(panel.Notes, n => n.Field == "due");
        Assert.Contains(workbenchWins ? "Workbench's was kept" : "Jira's was kept", note.Message);
    }

    [Fact]
    public async Task A_date_changed_on_one_side_only_just_flows_across_whatever_the_setting()
    {
        await using var kit = await JiraKit.CreateAsync();
        var issue = kit.Jira.Add("One-sided", due: Nov20);
        await kit.LinkAsync();
        var task = await kit.ItemAsync("One-sided");
        issue.DueDate = Dec1;
        issue.Updated = kit.Clock.Now.UtcDateTime;
        await kit.HookAsync(issue);
        await kit.DispatchAsync();
        Assert.Equal(Dec1, (await kit.ItemAsync("One-sided")).Finish);
        Assert.Empty((await kit.Sync.GetAsync(kit.ProjectId)).Notes);
        Assert.Equal(task.Id, (await kit.ItemAsync("One-sided")).Id);
    }

    [Fact]
    public async Task The_rules_can_be_changed_after_linking_and_say_who_wins_in_plain_words()
    {
        await using var kit = await JiraKit.CreateAsync();
        var panel = await kit.LinkAsync();
        Assert.Contains("Jira wins the title and assignee", panel.Link!.Rules);
        Assert.Contains("Workbench wins when both changed", panel.Link.Rules);
        Assert.Contains("Time stays in Workbench", panel.Link.Rules);

        panel = await kit.Sync.UpdateLinkAsync(kit.ProjectId, new UpdateJiraLinkRequest(JiraDateWinnerChoice.Jira, true));
        Assert.Contains("Jira wins when both changed", panel.Link!.Rules);
        Assert.Contains("create Jira issues", panel.Link.Rules);
    }

    // ===================================================================== workbench-created tasks

    [Fact]
    public async Task A_task_added_here_creates_an_issue_only_when_asked_and_only_for_tasks_added_after_linking()
    {
        await using var off = await JiraKit.CreateAsync();
        await off.LinkAsync();
        var quiet = await off.AddTaskAsync("Not pushed");
        Assert.Empty(off.Jira.Issues);
        Assert.Null(await off.Sync.GetTaskStatusAsync(off.SheetId, quiet));

        await using var kit = await JiraKit.CreateAsync();
        await kit.LinkAsync(creates: true);
        var id = await kit.AddTaskAsync("Pushed to Jira", Nov20);

        var issue = Assert.Single(kit.Jira.Issues);
        Assert.Equal("Pushed to Jira", issue.Summary);
        Assert.Equal(Nov20, issue.DueDate);
        Assert.Contains(JiraRules.TaskLabel(id), issue.Labels);
        Assert.Equal(issue.Key, (await kit.Sync.GetTaskStatusAsync(kit.SheetId, id))!.IssueKey);
        Assert.Null(await kit.Sync.GetTaskStatusAsync(kit.SheetId, kit.ExistingTask)); // the plan's older tasks are left alone

        // Everything running again makes no second issue or task.
        await kit.Sync.SyncNowAsync(kit.ProjectId);
        await kit.Engine.SyncTaskAsync(await kit.LinkIdAsync(), id, CancellationToken.None);
        Assert.Single(kit.Jira.Issues);
        Assert.Single((await kit.ItemsAsync()).Where(i => i.Title == "Pushed to Jira"));
    }

    [Fact]
    public async Task A_create_that_crashed_before_saving_its_link_finds_its_issue_again_instead_of_duplicating()
    {
        await using var kit = await JiraKit.CreateAsync();
        await kit.LinkAsync(creates: true);
        var id = await kit.AddTaskAsync("Crash safe");
        var issue = Assert.Single(kit.Jira.Issues);

        await using (var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            db.Set<JiraItemLink>().RemoveRange(db.Set<JiraItemLink>().Where(l => l.ItemId == id)); // the link row was lost
            await db.SaveChangesAsync();
        }

        await kit.Sync.SyncNowAsync(kit.ProjectId);
        await kit.Engine.SyncTaskAsync(await kit.LinkIdAsync(), id, CancellationToken.None);

        Assert.Single(kit.Jira.Issues);
        Assert.Single((await kit.ItemsAsync()).Where(i => i.Title == "Crash safe"));
        Assert.Equal(issue.Key, (await kit.ItemLinkAsync(id)).IssueKey);
    }

    // ===================================================================== fails soft

    [Fact]
    public async Task A_down_jira_never_blocks_anyone_shows_an_error_and_recovers_on_its_own()
    {
        await using var kit = await JiraKit.CreateAsync();
        var issue = kit.Jira.Add("Survivor");
        await kit.LinkAsync();
        var task = await kit.ItemAsync("Survivor");

        kit.Jira.Down = true;
        await kit.EditAsync(task.Id, i => i.Finish = Nov25); // a person edits the plan: nothing throws
        Assert.Equal(Nov25, (await kit.ItemAsync("Survivor")).Finish);
        var report = await kit.Engine.ReconcileAsync(await kit.LinkIdAsync(), everything: true, CancellationToken.None);
        Assert.NotNull(report.Error);
        Assert.Contains("Couldn't reach Jira", report.Error);

        var panel = await kit.Sync.GetAsync(kit.ProjectId);
        Assert.Equal("Jira isn't answering", panel.Link!.State);
        Assert.Contains("Couldn't reach Jira", panel.Link.LastError);
        Assert.NotNull(panel.Link.LastSyncedAtUtc);
        Assert.Equal("Survivor", (await kit.ItemAsync("Survivor")).Title);

        kit.Jira.Down = false;
        kit.Clock.Advance(TimeSpan.FromMinutes(15));
        await kit.Engine.ReconcileAsync(await kit.LinkIdAsync(), everything: false, CancellationToken.None);
        await kit.DispatchAsync();
        panel = await kit.Sync.GetAsync(kit.ProjectId);
        Assert.Equal("Up to date", panel.Link!.State);
        Assert.Null(panel.Link.LastError);
        Assert.Equal(Nov25, issue.DueDate); // the edit made while Jira was down arrived afterwards
    }

    [Fact]
    public async Task A_revoked_token_needs_attention_with_a_hint_and_the_connection_page_agrees()
    {
        await using var kit = await JiraKit.CreateAsync();
        kit.Jira.Add("Anything");
        await kit.LinkAsync();
        kit.Jira.Fail("GET", "search/jql", HttpStatusCode.Unauthorized);

        var report = await kit.Engine.ReconcileAsync(await kit.LinkIdAsync(), everything: true, CancellationToken.None);

        Assert.Contains("didn't accept", report.Error);
        var panel = await kit.Sync.GetAsync(kit.ProjectId);
        Assert.Equal("Needs attention", panel.Link!.State);
        Assert.Contains("API token", panel.Link.LastErrorHint);
    }

    [Fact]
    public async Task A_rate_limit_is_retried_by_the_outbox_not_dropped()
    {
        await using var kit = await JiraKit.CreateAsync();
        var issue = kit.Jira.Add("Throttled");
        await kit.LinkAsync();
        var task = await kit.ItemAsync("Throttled");
        kit.Jira.Fail("POST", "transitions", HttpStatusCode.TooManyRequests);

        await kit.EditAsync(task.Id, i => i.Values[kit.StatusColumn.ToString()] = "Done");
        Assert.Equal("new", issue.Category);

        kit.Clock.Advance(TimeSpan.FromMinutes(5));
        await kit.DispatchAsync();
        Assert.Equal("done", issue.Category);
    }

    [Fact]
    public async Task A_deleted_issue_keeps_its_task_and_says_so()
    {
        await using var kit = await JiraKit.CreateAsync();
        var issue = kit.Jira.Add("Short lived");
        await kit.LinkAsync();
        var task = await kit.ItemAsync("Short lived");
        kit.Jira.Issues.Remove(issue);

        await kit.HookAsync(issue, eventName: "jira:issue_deleted");
        await kit.DispatchAsync();

        Assert.Contains(await kit.ItemsAsync(), i => i.Id == task.Id);
        var status = await kit.Sync.GetTaskStatusAsync(kit.SheetId, task.Id);
        Assert.True(status!.IsRemoved);
        Assert.Contains("deleted in Jira", status.LastError);
    }

    // ===================================================================== permissions and unlinking

    [Fact]
    public async Task Only_project_setup_can_link_and_only_people_who_see_the_plan_can_read_it()
    {
        await using var kit = await JiraKit.CreateAsync();
        await kit.LinkAsync();

        kit.Host.As(SeededHost.Outsider);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Sync.GetAsync(kit.ProjectId)); // a project you can't see doesn't exist for you
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Connection.GetAsync());

        kit.Host.As(SeededHost.Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Sync.SyncNowAsync(kit.ProjectId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Sync.UnlinkAsync(kit.ProjectId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Sync.GetWebhookSetupAsync(kit.ProjectId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Connection.SaveAsync(new SaveJiraConnectionRequest(JiraKit.SiteUrl, FakeJira.Email, FakeJira.Token)));

        kit.Host.As(SeededHost.Admin);
        await kit.Sync.UnlinkAsync(kit.ProjectId);
    }

    [Fact]
    public async Task Unlinking_stops_the_sync_and_leaves_the_tasks_and_the_issues_alone()
    {
        await using var kit = await JiraKit.CreateAsync();
        var issue = kit.Jira.Add("Stays");
        await kit.LinkAsync();
        await kit.Sync.UnlinkAsync(kit.ProjectId);

        Assert.Contains(await kit.ItemsAsync(), i => i.Title == "Stays");
        Assert.Contains(kit.Jira.Issues, i => i.Key == issue.Key);
        Assert.Null((await kit.Sync.GetAsync(kit.ProjectId)).Link);
        await using var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.Equal(0, await db.Set<JiraItemLink>().CountAsync());
    }

    [Fact]
    public async Task Linking_a_plan_with_no_way_to_say_done_adds_a_status_column()
    {
        await using var kit = await JiraKit.CreateAsync();
        await using (var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            db.Columns.RemoveRange(db.Columns.Where(c => c.SheetId == kit.SheetId));
            await db.SaveChangesAsync();
        }

        await kit.LinkAsync();
        await using var check = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.Contains(await check.Columns.Where(c => c.SheetId == kit.SheetId).ToListAsync(), c => c.Name == "Status" && c.Options.Contains("Done"));
    }

    // ===================================================================== time stays in workbench

    [Fact]
    public async Task Jira_worklogs_are_never_asked_for_and_no_time_entries_are_made()
    {
        await using var kit = await JiraKit.CreateAsync();
        kit.Jira.Add("Has hours", estimateSeconds: 7200);
        await kit.LinkAsync();
        Assert.DoesNotContain(kit.Jira.Requests, r => r.Path.Contains("worklog", StringComparison.OrdinalIgnoreCase));
        await using var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.Equal(0, await db.Set<Domain.ProjectAccounting.TimeEntry>().CountAsync());
    }
}

public sealed class JiraClientTests
{
    [Fact]
    public void Addresses_and_project_keys_are_normalized_and_refused_when_unsafe()
    {
        Assert.Equal("https://acme.atlassian.net/", JiraUrls.NormalizeSite("acme.atlassian.net/jira/software", false).ToString());
        Assert.Throws<ArgumentException>(() => JiraUrls.NormalizeSite("http://acme.atlassian.net", false));
        Assert.Throws<ArgumentException>(() => JiraUrls.NormalizeSite("https://user:pw@acme.atlassian.net", false));
        Assert.Equal("RAD", JiraUrls.ProjectKey("rad"));
        Assert.Equal("RAD", JiraUrls.ProjectKey("https://acme.atlassian.net/browse/RAD-12"));
        Assert.Equal("RAD", JiraUrls.ProjectKey("https://acme.atlassian.net/jira/software/projects/RAD/boards/1"));
        Assert.Throws<ArgumentException>(() => JiraUrls.ProjectKey("rad; drop"));
        Assert.True(JiraUrls.IsIssueKey("RAD-12"));
        Assert.False(JiraUrls.IsIssueKey("RAD-"));
        Assert.Equal("\"a\\\"b\"", JiraUrls.Quote("a\"b"));
    }

    [Fact]
    public async Task The_client_speaks_basic_auth_never_leaks_the_token_and_turns_failures_into_plain_words()
    {
        var fake = new FakeJira(() => DateTime.UtcNow);
        using var good = new JiraCloudClient(fake, JiraKit.SiteUrl, FakeJira.Email, FakeJira.Token, new JiraOptions());
        Assert.Equal("Workbench Bot", (await good.GetMyselfAsync(default)).DisplayName);
        fake.Add("Estimated", estimateSeconds: 5400, due: new DateOnly(2026, 11, 20));
        var issue = (await good.SearchIssuesAsync("project = \"RAD\"", 1, default)).Single();
        Assert.Equal(1.5m, issue.EstimateHours);
        Assert.Equal(new DateOnly(2026, 11, 20), issue.DueDate);
        Assert.False(issue.IsDone);

        using var bad = new JiraCloudClient(fake, JiraKit.SiteUrl, FakeJira.Email, "not-the-token-123456", new JiraOptions());
        var ex = await Assert.ThrowsAsync<JiraApiException>(() => bad.GetMyselfAsync(default));
        Assert.Equal(401, ex.StatusCode);
        Assert.False(ex.IsRetryable);
        Assert.DoesNotContain("not-the-token", ex.Message + ex.Hint);

        fake.Down = true;
        var down = await Assert.ThrowsAsync<JiraApiException>(() => good.GetMyselfAsync(default));
        Assert.Null(down.StatusCode);
        Assert.True(down.IsRetryable);
        fake.Down = false;
        fake.Fail("GET", "myself", HttpStatusCode.ServiceUnavailable);
        Assert.True((await Assert.ThrowsAsync<JiraApiException>(() => good.GetMyselfAsync(default))).IsRetryable);
        Assert.Null(await good.GetIssueAsync("RAD-9999", default));
    }
}
