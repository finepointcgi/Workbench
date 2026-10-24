using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Integrations.GitLab;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Integrations.GitLab;
using Workbench.Domain.Integrations;
using Workbench.Domain.Integrations.GitLab;
using Workbench.Domain.Tasks;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Integrations.GitLab;

public sealed class GitLabSyncTests
{
    private static readonly DateOnly Nov6 = new(2026, 11, 6);

    // ===================================================================== connecting

    [Fact]
    public async Task Saving_the_connection_tests_it_and_keeps_the_token_encrypted()
    {
        await using var kit = await GitLabKit.CreateAsync();
        var dto = await kit.Connection.GetAsync();
        Assert.True(dto.IsConfigured);
        Assert.Equal(GitLabKit.BaseUrl, dto.BaseUrl);
        Assert.Equal("workbench-bot", dto.ConnectedAsUsername);
        Assert.Equal("17.5.1-ee", dto.ServerVersion);
        Assert.Equal(FakeGitLab.Token[^4..], dto.TokenHint);

        await using var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var row = await db.Set<GitLabConnection>().SingleAsync();
        Assert.DoesNotContain(FakeGitLab.Token, row.TokenProtected);
        Assert.Equal(FakeGitLab.Token, kit.Host.Get<GitLabSecrets>().UnprotectToken(row.TokenProtected));

        // The hidden "GitLab sync" connector the outbox delivers to exists and isn't offered under "Add connector".
        Assert.True(await db.Set<IntegrationConnector>().AnyAsync(c => c.Kind == GitLabOutbox.ConnectorKind && c.IsActive));
        Assert.DoesNotContain(await kit.Host.Get<Contracts.Integrations.IConnectorService>().ListKindsAsync(), k => k.Kind == GitLabOutbox.ConnectorKind);
    }

    [Fact]
    public async Task Test_connection_explains_a_wrong_token_and_a_token_without_the_api_scope()
    {
        await using var kit = await GitLabKit.CreateAsync(connect: false);
        var wrong = await kit.Connection.TestAsync(new SaveGitLabConnectionRequest(GitLabKit.BaseUrl, "glpat-wrong-token-000000", null));
        Assert.False(wrong.Ok);
        Assert.Contains("refused the access token", wrong.Message);
        Assert.Contains("api scope", wrong.Hint);

        kit.GitLab.Scopes = ["read_api"];
        var readOnly = await kit.Connection.TestAsync(new SaveGitLabConnectionRequest(GitLabKit.BaseUrl, FakeGitLab.Token, null));
        Assert.False(readOnly.Ok);
        Assert.Contains("read_api", readOnly.Message);

        kit.GitLab.Scopes = ["api"];
        var ok = await kit.Connection.TestAsync(new SaveGitLabConnectionRequest(GitLabKit.BaseUrl, FakeGitLab.Token, null));
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal("Connected as @workbench-bot (GitLab 17.5.1-ee).", ok.Message);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Connection.SaveAsync(new SaveGitLabConnectionRequest(GitLabKit.BaseUrl, null, null)));
    }

    [Fact]
    public async Task Self_managed_servers_keep_their_path_and_private_ones_need_the_operator()
    {
        Assert.Equal("https://git.example.mil/gitlab", GitLabUrls.NormalizeBase("https://git.example.mil/gitlab/api/v4/", false).ToString().TrimEnd('/'));
        Assert.Equal("https://gitlab.com", GitLabUrls.NormalizeBase("gitlab.com", false).ToString().TrimEnd('/'));
        Assert.Equal("https://git.example.mil:8443/gitlab", GitLabUrls.NormalizeBase("https://git.example.mil:8443/gitlab/", false).ToString().TrimEnd('/'));
        Assert.Throws<ArgumentException>(() => GitLabUrls.NormalizeBase("http://gitlab.example.com", false));
        Assert.Throws<ArgumentException>(() => GitLabUrls.NormalizeBase("https://user:pass@gitlab.example.com", false));
        Assert.Equal("group/sub/project", GitLabUrls.ProjectRef("https://git.example.mil/gitlab", "https://git.example.mil/gitlab/group/sub/project/-/issues"));
        Assert.Equal("group/project", GitLabUrls.ProjectRef(GitLabKit.BaseUrl, "group/project.git"));
        Assert.Equal("42", GitLabUrls.ProjectRef(GitLabKit.BaseUrl, " 42 "));

        await using (var refused = await GitLabKit.CreateAsync(connect: false))
        {
            refused.Host.Dns.Answers["git.example.mil"] = [IPAddress.Parse("10.20.0.5")];
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                refused.Connection.SaveAsync(new SaveGitLabConnectionRequest("https://git.example.mil/gitlab", FakeGitLab.Token, null)));
            Assert.Contains("GitLab:AllowedPrivateHosts", ex.Message);
        }

        await using var allowed = await GitLabKit.CreateAsync(connect: false, gitlab: o => o.AllowedPrivateHosts = ["git.example.mil"]);
        allowed.Host.Dns.Answers["git.example.mil"] = [IPAddress.Parse("10.20.0.5")];
        var saved = await allowed.Connection.SaveAsync(new SaveGitLabConnectionRequest("https://git.example.mil/gitlab/api/v4", FakeGitLab.Token, null));
        Assert.True(saved.LastTestOk, saved.LastTestMessage);
        Assert.Equal("https://git.example.mil/gitlab", saved.BaseUrl);
        Assert.False(saved.IsGitLabCom);
        Assert.Equal("https://git.example.mil/gitlab", allowed.GitLab.BaseUrlSeen);
        Assert.Contains(allowed.GitLab.Requests, r => r.Path == "api/v4/version");
    }

    [Fact]
    public async Task A_private_certificate_authority_is_trusted_for_its_server_only()
    {
        using var ca = GitLabCertificates.Authority("Example Root CA");
        using var other = GitLabCertificates.Authority("Someone Else CA");
        using var leaf = GitLabCertificates.Issue(ca, "git.example.mil");
        var roots = GitLabTls.ParseCa(GitLabCertificates.Pem(ca));

        Assert.True(GitLabTls.Validate(leaf, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, roots));
        Assert.False(GitLabTls.Validate(leaf, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, GitLabTls.ParseCa(GitLabCertificates.Pem(other))));
        Assert.False(GitLabTls.Validate(leaf, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, null));
        Assert.False(GitLabTls.Validate(leaf, null, System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch | System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, roots));
        Assert.True(GitLabTls.Validate(leaf, null, System.Net.Security.SslPolicyErrors.None, null));
        Assert.Throws<ArgumentException>(() => GitLabTls.ParseCa("not a certificate"));

        await using var kit = await GitLabKit.CreateAsync(connect: false);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Connection.SaveAsync(new SaveGitLabConnectionRequest(GitLabKit.BaseUrl, FakeGitLab.Token, "-----BEGIN CERTIFICATE-----\nnope\n-----END CERTIFICATE-----")));
        var saved = await kit.Connection.SaveAsync(new SaveGitLabConnectionRequest(GitLabKit.BaseUrl, FakeGitLab.Token, GitLabCertificates.Pem(ca)));
        Assert.True(saved.HasCustomCa);
        Assert.Contains("Example Root CA", saved.CaSummary);

        // The real handler factory keeps one pooled handler per server and CA.
        var factory = new GuardedGitLabHandlerFactory(kit.Host.Get<Application.Integrations.OutboundUrlGuard>(), Options.Create(new GitLabOptions()));
        Assert.Same(factory.Get(new Uri("https://git.example.mil/gitlab"), GitLabCertificates.Pem(ca)), factory.Get(new Uri("https://git.example.mil/other"), GitLabCertificates.Pem(ca)));
        Assert.NotSame(factory.Get(new Uri("https://git.example.mil"), null), factory.Get(new Uri("https://git.example.mil"), GitLabCertificates.Pem(ca)));
        factory.Dispose();
    }

    // ===================================================================== linking: epics and the milestone fallback

    [Fact]
    public async Task Linking_sends_phases_as_epics_and_tasks_as_issues_with_wbs_people_hours_and_dependencies()
    {
        await using var kit = await GitLabKit.CreateAsync(epics: true);
        var panel = await kit.LinkAsync();

        Assert.Equal("epics", panel.Link!.PhaseMode);
        Assert.Equal("Up to date", panel.Link.State);
        Assert.True(panel.Link.WebhookRegistered);
        Assert.Equal(2, kit.GitLab.Hooks.Count); // project + group (epic events)
        Assert.All(kit.GitLab.Hooks, h => Assert.EndsWith($"/webhooks/gitlab/{panel.Link.Id}", h.Url));

        var design = kit.GitLab.Epics.Single(e => e.Title == "Design");
        Assert.Equal(["WBS::1001.01"], design.Labels);
        Assert.Equal(new DateOnly(2026, 11, 2), design.Start);
        Assert.Equal(new DateOnly(2026, 11, 13), design.Due);
        Assert.Single(kit.GitLab.Epics, e => e.Title == "Build");

        Assert.Equal(3, kit.GitLab.Issues.Count);
        var requirements = kit.GitLab.IssueTitled("Requirements");
        Assert.Equal(design.Id, requirements.EpicId);
        Assert.Equal(["WBS::1001.01.01"], requirements.Labels);
        Assert.Equal([202L], requirements.AssigneeIds); // Jordan, matched by email
        Assert.Equal(Nov6, requirements.DueDate);
        Assert.Equal(40 * 3600, requirements.TimeEstimateSeconds);
        Assert.Equal("Gather the radar requirements.", requirements.Notes);
        Assert.Contains("WBS 1001.01.01", requirements.Description);
        Assert.Contains("planned Nov 2, 2026 to Nov 6, 2026", requirements.Description);
        Assert.Contains(GitLabRules.Marker(kit.Tasks["Requirements"]), requirements.Description);

        var mockups = kit.GitLab.IssueTitled("Mockups");
        Assert.Equal([203L], mockups.AssigneeIds); // Sam, matched by user name and name
        Assert.Contains(kit.GitLab.Links, l => l.SourceIid == requirements.Iid && l.TargetIid == mockups.Iid && l.Type == "blocks");
        Assert.Empty(kit.GitLab.IssueTitled("Code").AssigneeIds); // Riley has no GitLab user

        Assert.Equal(new[] { "Build", "Code", "Design", "Mockups", "Requirements" }, panel.Tasks.Select(t => t.Title).Order());
        Assert.Contains(panel.Tasks, t => t.Title == "Requirements" && t.Label == $"issue #{requirements.Iid}");
        Assert.Contains(panel.Tasks, t => t.Title == "Design" && t.Label == $"epic &{design.Iid}");
        var riley = Assert.Single(panel.PeopleToMatch);
        Assert.Equal(("workbench", GitLabKit.Riley), (riley.Side, riley.Email));
        Assert.Contains("Created GitLab issue", string.Join("|", await kit.ActivityAsync("Requirements")));

        await using var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var maps = await db.Set<GitLabUserMap>().AsNoTracking().ToDictionaryAsync(m => m.Email);
        Assert.Equal("email", maps[GitLabKit.Jordan].MatchedBy);
        Assert.Equal("username", maps[GitLabKit.Sam].MatchedBy);
        Assert.False(maps.ContainsKey(GitLabKit.Riley));
    }

    [Fact]
    public async Task Without_epics_phases_become_milestones_and_issues_get_one_assignee_and_related_links()
    {
        await using var kit = await GitLabKit.CreateAsync(epics: false);
        var panel = await kit.LinkAsync();

        Assert.Equal("milestones", panel.Link!.PhaseMode);
        Assert.Empty(kit.GitLab.Epics);
        var design = kit.GitLab.Milestones.Single(m => m.Title == "1001.01 Design");
        Assert.Equal(new DateOnly(2026, 11, 2), design.Start);
        Assert.Equal(new DateOnly(2026, 11, 13), design.Due);
        Assert.Single(kit.GitLab.Milestones, m => m.Title == "1001.02 Build");
        Assert.Equal(design.Id, kit.GitLab.IssueTitled("Requirements").MilestoneId);
        Assert.Contains(kit.GitLab.Links, l => l.Type == "relates_to");
        Assert.Contains(panel.Tasks, t => t.Title == "Design" && t.Label == $"milestone %{design.Iid}");

        // Epics asked for on a Free group: refused in plain words.
        await kit.Sync.UnlinkAsync(kit.ProjectId);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => kit.Sync.LinkAsync(kit.ProjectId, new LinkGitLabProjectRequest(FakeGitLab.ProjectPath, GitLabPhaseChoice.Epics)));
        Assert.Contains("Premium or Ultimate", ex.Message);
    }

    [Fact]
    public async Task A_task_already_done_when_linked_starts_closed_in_gitlab_without_a_note()
    {
        await using var kit = await GitLabKit.CreateAsync();
        await kit.EditAsync("Code", i => i.Values[kit.StatusColumn.ToString()] = "Done", raise: false);
        var panel = await kit.LinkAsync();

        Assert.Equal("closed", kit.GitLab.IssueTitled("Code").State);
        Assert.Equal("opened", kit.GitLab.IssueTitled("Requirements").State);
        Assert.Equal("Done", kit.Status(await kit.ItemAsync("Code")));
        Assert.Equal("100", kit.Percent(await kit.ItemAsync("Code")));
        Assert.Empty(panel.Conflicts);
        Assert.Contains(await kit.ActivityAsync("Code"), a => a.Contains("the task was already done"));
    }

    [Fact]
    public async Task Relinking_finds_issues_by_their_marker_instead_of_making_duplicates()
    {
        await using var kit = await GitLabKit.CreateAsync(epics: false);
        await kit.LinkAsync();
        var before = kit.GitLab.Issues.Count;
        await kit.Sync.UnlinkAsync(kit.ProjectId);
        Assert.Empty(kit.GitLab.Hooks);
        Assert.Equal(before, kit.GitLab.Issues.Count); // unlinking keeps the issues

        await kit.LinkAsync();
        Assert.Equal(before, kit.GitLab.Issues.Count);
        Assert.Equal(2, kit.GitLab.Milestones.Count); // milestones found by their title
        Assert.Contains(await kit.ActivityAsync("Requirements"), a => a.Contains("found by its Workbench marker"));
    }

    // ===================================================================== Workbench -> GitLab

    [Fact]
    public async Task Workbench_edits_go_to_gitlab_through_the_outbox()
    {
        await using var kit = await GitLabKit.CreateAsync();
        await kit.LinkAsync();
        kit.Clock.Advance(TimeSpan.FromMinutes(5));

        await kit.EditAsync("Requirements", i =>
        {
            i.Title = "Requirements v2";
            i.Finish = new DateOnly(2026, 11, 10);
            i.Values[kit.NotesColumn.ToString()] = "Updated in Workbench.";
        });
        await kit.DispatchAsync();

        var issue = await kit.IssueAsync("Requirements");
        Assert.Equal("Requirements v2", issue.Title);
        Assert.Equal(new DateOnly(2026, 11, 10), issue.DueDate);
        Assert.Equal("Updated in Workbench.", issue.Notes);
        Assert.Contains(await kit.ActivityAsync("Requirements"), a => a.StartsWith($"Sent to GitLab issue #{issue.Iid}: title, description, due date", StringComparison.Ordinal));

        await using var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var delivery = await db.Set<IntegrationDelivery>().AsNoTracking().OrderByDescending(d => d.CreatedAtUtc).FirstAsync(d => d.EventType == GitLabOutbox.TaskChanged);
        Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);

        // A change the sync itself made isn't sent back.
        await kit.Host.Get<GitLabChangeListener>().OnChangeAsync(new SheetChange(kit.SheetId, SheetChangeKind.ItemUpdated, kit.Tasks["Mockups"], null, GitLabSyncEngine.Actor, kit.Clock.Now.UtcDateTime));
        Assert.False(await db.Set<IntegrationOutboxEvent>().AnyAsync(e => e.DispatchedAtUtc == null));
    }

    [Fact]
    public async Task Deleting_a_task_closes_its_issue_with_a_note()
    {
        await using var kit = await GitLabKit.CreateAsync();
        await kit.LinkAsync();
        var issue = await kit.IssueAsync("Code");
        await using (var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            db.Items.Remove(await db.Items.SingleAsync(i => i.Id == kit.Tasks["Code"]));
            await db.SaveChangesAsync();
        }

        await kit.Host.Get<GitLabChangeListener>().OnChangeAsync(new SheetChange(kit.SheetId, SheetChangeKind.ItemDeleted, kit.Tasks["Code"], null, SeededHost.Owner, kit.Clock.Now.UtcDateTime));
        await kit.DispatchAsync();

        Assert.Equal("closed", issue.State);
        Assert.Contains(kit.GitLab.Notes, n => n.Iid == issue.Iid && n.Body.Contains("deleted from the project's plan"));
        Assert.True((await kit.ItemLinkAsync("Code")).IsRemoved);
    }

    [Fact]
    public async Task Failed_pushes_are_retried_with_backoff_and_rate_limits_are_respected()
    {
        await using var kit = await GitLabKit.CreateAsync();
        await kit.LinkAsync();
        var issue = await kit.IssueAsync("Requirements");

        kit.GitLab.Fail("PUT", @"projects/42/issues/\d+$", HttpStatusCode.ServiceUnavailable);
        await kit.EditAsync("Requirements", i => i.Title = "Retried title");
        await kit.DispatchAsync();
        Assert.Equal("Requirements", issue.Title);

        await using var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var delivery = await db.Set<IntegrationDelivery>().AsNoTracking().SingleAsync(d => d.Status == DeliveryStatus.Pending);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.True(delivery.NextAttemptAtUtc > kit.Clock.Now.UtcDateTime);

        kit.Clock.Advance(TimeSpan.FromMinutes(1));
        await kit.DispatchAsync();
        Assert.Equal("Retried title", issue.Title);
        var done = await db.Set<IntegrationDelivery>().AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        Assert.Equal((DeliveryStatus.Succeeded, 2), (done.Status, done.AttemptCount));

        // 429 with Retry-After: GitLab's window is waited out, then the change goes.
        kit.GitLab.Fail("PUT", @"projects/42/issues/\d+$", HttpStatusCode.TooManyRequests, retryAfter: "120");
        await kit.EditAsync("Requirements", i => i.Title = "After the rate limit");
        await kit.DispatchAsync();
        Assert.NotNull(kit.Host.Get<GitLabRateGate>().PausedUntil("gitlab.example.com"));
        kit.Clock.Advance(TimeSpan.FromSeconds(45));
        await kit.DispatchAsync();
        Assert.Equal("Retried title", issue.Title); // still waiting for the window
        kit.Clock.Advance(TimeSpan.FromMinutes(5));
        await kit.DispatchAsync();
        Assert.Equal("After the rate limit", issue.Title);
    }

    [Fact]
    public async Task A_revoked_token_stops_retrying_and_says_what_to_do()
    {
        await using var kit = await GitLabKit.CreateAsync();
        await kit.LinkAsync();
        kit.GitLab.Fail("GET", @"projects/42/issues/\d+$", HttpStatusCode.Unauthorized, times: 5);
        await kit.EditAsync("Requirements", i => i.Title = "Never sent");
        await kit.DispatchAsync();

        await using var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.Contains(await db.Set<IntegrationDelivery>().AsNoTracking().ToListAsync(), d => d.Status == DeliveryStatus.Failed);
        var panel = await kit.Sync.GetAsync(kit.ProjectId);
        Assert.Equal("Needs attention", panel.Link!.State);
        Assert.Contains("api scope", panel.Link.LastErrorHint);
        Assert.Contains(panel.Conflicts, c => c.Kind == "Couldn't sync" && c.TaskTitle == "Never sent");
    }

    // ===================================================================== GitLab -> Workbench: progress

    [Fact]
    public async Task Closing_an_issue_makes_the_task_100_percent_and_reopening_restores_progress()
    {
        await using var kit = await GitLabKit.CreateAsync();
        await kit.LinkAsync();
        var issue = await kit.IssueAsync("Code");

        kit.Clock.Advance(TimeSpan.FromMinutes(10));
        issue.State = "closed";
        issue.UpdatedAt = kit.Clock.Now.UtcDateTime;
        Assert.Equal(200, (await kit.IssueHookAsync(issue)).StatusCode);
        await kit.DispatchAsync();

        var code = await kit.ItemAsync("Code");
        Assert.Equal("100", kit.Percent(code));
        Assert.Equal("Done", kit.Status(code));
        Assert.Equal("100", kit.Percent(await kit.ItemAsync("Build"))); // the summary rolls up
        Assert.Contains(await kit.ActivityAsync("Code"), a => a.Contains($"issue #{issue.Iid} closed in GitLab, so 100% complete"));

        kit.Clock.Advance(TimeSpan.FromMinutes(10));
        issue.State = "opened";
        issue.ChecklistCount = 4;
        issue.ChecklistDone = 1;
        issue.UpdatedAt = kit.Clock.Now.UtcDateTime;
        await kit.IssueHookAsync(issue);
        await kit.DispatchAsync();

        code = await kit.ItemAsync("Code");
        Assert.Equal("25", kit.Percent(code));
        Assert.Equal("In progress", kit.Status(code));
    }

    [Fact]
    public async Task Progress_comes_from_weighted_child_tasks_then_the_checklist_else_workbench_keeps_its_value()
    {
        Assert.Equal(100m, GitLabRules.Progress(true, null, null));
        Assert.Equal(75m, GitLabRules.Progress(false, [new(true, 3), new(false, 1)], new GitLabTaskCompletion(4, 0)));
        Assert.Equal(50m, GitLabRules.Progress(false, [new(true, null), new(false, null)], null));
        Assert.Equal(33m, GitLabRules.Progress(false, [], new GitLabTaskCompletion(3, 1)));
        Assert.Null(GitLabRules.Progress(false, [], new GitLabTaskCompletion(0, 0)));
        Assert.Null(GitLabRules.Progress(false, null, null));

        await using var kit = await GitLabKit.CreateAsync();
        await kit.LinkAsync();
        await kit.EditAsync("Mockups", i => i.Values[kit.PercentColumn.ToString()] = "40");
        await kit.DispatchAsync();
        Assert.Equal("40", kit.Percent(await kit.ItemAsync("Mockups"))); // GitLab has nothing to say yet

        var issue = await kit.IssueAsync("Mockups");
        kit.GitLab.ChildTasks[issue.Id] = [(true, 3), (false, 1)];
        kit.Clock.Advance(TimeSpan.FromMinutes(1));
        issue.UpdatedAt = kit.Clock.Now.UtcDateTime;
        await kit.IssueHookAsync(issue);
        await kit.DispatchAsync();
        Assert.Equal("75", kit.Percent(await kit.ItemAsync("Mockups")));
    }

    // ===================================================================== ownership

    [Fact]
    public async Task A_due_date_changed_in_gitlab_is_put_back_and_noted_and_can_be_taken_on_purpose()
    {
        await using var kit = await GitLabKit.CreateAsync();
        await kit.LinkAsync();
        var issue = await kit.IssueAsync("Requirements");

        kit.Clock.Advance(TimeSpan.FromMinutes(5));
        issue.DueDate = new DateOnly(2026, 11, 20);
        issue.UpdatedAt = kit.Clock.Now.UtcDateTime;
        await kit.IssueHookAsync(issue);
        await kit.DispatchAsync();

        Assert.Equal(Nov6, issue.DueDate); // Workbench owns dates
        Assert.Equal(Nov6, (await kit.ItemAsync("Requirements")).Finish);
        var panel = await kit.Sync.GetAsync(kit.ProjectId);
        var note = Assert.Single(panel.Conflicts, c => c.Field == "due");
        Assert.Equal("Workbench owns this", note.Kind);
        Assert.Equal(("Nov 6, 2026", "Nov 20, 2026"), (note.WorkbenchValue, note.GitLabValue));
        Assert.Contains("Workbench owns the schedule", note.Outcome);
        Assert.True(note.CanUseGitLab);

        await kit.Sync.ResolveConflictAsync(kit.ProjectId, note.Id, new ResolveGitLabConflictRequest(GitLabConflictChoice.UseGitLab));
        Assert.Equal(new DateOnly(2026, 11, 20), (await kit.ItemAsync("Requirements")).Finish);
        Assert.Equal(new DateOnly(2026, 11, 20), issue.DueDate);
        Assert.DoesNotContain((await kit.Sync.GetAsync(kit.ProjectId)).Conflicts, c => c.Id == note.Id);
    }

    [Fact]
    public async Task Percent_complete_edited_in_workbench_goes_back_to_gitlabs()
    {
        await using var kit = await GitLabKit.CreateAsync();
        await kit.LinkAsync();
        var issue = await kit.IssueAsync("Mockups");
        kit.Clock.Advance(TimeSpan.FromMinutes(1));
        (issue.ChecklistCount, issue.ChecklistDone, issue.UpdatedAt) = (2, 1, kit.Clock.Now.UtcDateTime);
        await kit.IssueHookAsync(issue);
        await kit.DispatchAsync();
        Assert.Equal("50", kit.Percent(await kit.ItemAsync("Mockups")));

        kit.Clock.Advance(TimeSpan.FromMinutes(1));
        await kit.EditAsync("Mockups", i => i.Values[kit.PercentColumn.ToString()] = "80");
        await kit.DispatchAsync();

        Assert.Equal("50", kit.Percent(await kit.ItemAsync("Mockups")));
        var note = Assert.Single((await kit.Sync.GetAsync(kit.ProjectId)).Conflicts, c => c.Field == "percent");
        Assert.Equal("GitLab owns this", note.Kind);
        Assert.False(note.CanUseWorkbench);
    }

    [Fact]
    public async Task A_title_changed_on_both_sides_keeps_the_later_edit_and_notes_the_other()
    {
        await using var kit = await GitLabKit.CreateAsync();
        await kit.LinkAsync();
        var issue = await kit.IssueAsync("Requirements");

        kit.Clock.Advance(TimeSpan.FromMinutes(1));
        await kit.EditAsync("Requirements", i => i.Title = "Reqs (Workbench)", raise: false);
        issue.Title = "Reqs (GitLab)";
        issue.UpdatedAt = DateTime.UtcNow.AddMinutes(1); // the row's stamp is the real clock (SheetRowStamps); GitLab's edit is later
        await kit.IssueHookAsync(issue);
        await kit.DispatchAsync();

        Assert.Equal("Reqs (GitLab)", (await kit.ItemAsync("Requirements")).Title);
        var note = Assert.Single((await kit.Sync.GetAsync(kit.ProjectId)).Conflicts, c => c.Field == "title");
        Assert.Equal(("Changed in both", "Reqs (Workbench)", "Reqs (GitLab)"), (note.Kind, note.WorkbenchValue, note.GitLabValue));
        Assert.Contains("GitLab's edit was later", note.Outcome);

        await kit.Sync.ResolveConflictAsync(kit.ProjectId, note.Id, new ResolveGitLabConflictRequest(GitLabConflictChoice.UseWorkbench));
        Assert.Equal("Reqs (Workbench)", (await kit.ItemAsync("Requirements")).Title);
        Assert.Equal("Reqs (Workbench)", issue.Title);

        // A GitLab-only edit just comes across, with no note.
        kit.Clock.Advance(TimeSpan.FromMinutes(1));
        issue.Title = "Requirements (final)";
        issue.UpdatedAt = kit.Clock.Now.UtcDateTime;
        await kit.IssueHookAsync(issue);
        await kit.DispatchAsync();
        Assert.Equal("Requirements (final)", (await kit.ItemAsync("Requirements")).Title);
        Assert.Empty((await kit.Sync.GetAsync(kit.ProjectId)).Conflicts.Where(c => c.Field == "title"));
        Assert.Contains(await kit.ActivityAsync("Requirements"), a => a.Contains("title changed to \"Requirements (final)\""));
    }

    // ===================================================================== people

    [Fact]
    public async Task Gitlab_assignees_come_across_unmatched_ones_stay_and_are_noted_and_staffed_people_stay()
    {
        await using var kit = await GitLabKit.CreateAsync();
        await kit.LinkAsync();
        var code = await kit.IssueAsync("Code");

        kit.Clock.Advance(TimeSpan.FromMinutes(1));
        code.AssigneeIds = [202, 204]; // Jordan and casey (nobody in Workbench)
        code.UpdatedAt = kit.Clock.Now.UtcDateTime;
        await kit.IssueHookAsync(code);
        await kit.DispatchAsync();

        await using (var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            Assert.Equal([GitLabKit.Jordan], await db.Set<ItemAssignee>().Where(a => a.ItemId == kit.Tasks["Code"]).Select(a => a.Email).ToListAsync());
        }

        Assert.Equal([202L, 204L], code.AssigneeIds.Order()); // casey isn't dropped from GitLab
        var panel = await kit.Sync.GetAsync(kit.ProjectId);
        Assert.Contains(panel.Conflicts, c => c.Kind == "Person not matched" && c.GitLabValue == "@casey");
        Assert.Contains(panel.PeopleToMatch, p => p.Side == "gitlab" && p.GitLabUsername == "casey");

        // Matching by hand settles the note and sends the person to their tasks.
        panel = await kit.Sync.MatchPersonAsync(kit.ProjectId, new MatchGitLabPersonRequest(GitLabKit.Riley, 204));
        await kit.DispatchAsync();
        panel = await kit.Sync.GetAsync(kit.ProjectId);
        Assert.DoesNotContain(panel.Conflicts, c => c.Kind == "Person not matched");
        Assert.DoesNotContain(panel.PeopleToMatch, p => p.Email == GitLabKit.Riley);
        Assert.Equal([202L, 204L], code.AssigneeIds.Order());

        // Jordan is staffed on Requirements: unassigning them in GitLab doesn't stick.
        var requirements = await kit.IssueAsync("Requirements");
        kit.Clock.Advance(TimeSpan.FromMinutes(1));
        requirements.AssigneeIds = [];
        requirements.UpdatedAt = kit.Clock.Now.UtcDateTime;
        await kit.IssueHookAsync(requirements);
        await kit.DispatchAsync();
        Assert.Equal([202L], requirements.AssigneeIds);
        Assert.Contains((await kit.Sync.GetAsync(kit.ProjectId)).Conflicts, c => c.Field == "assignees" && c.Outcome.Contains("staffed on this task"));
    }

    // ===================================================================== new issues under a phase

    [Fact]
    public async Task An_issue_added_under_a_linked_epic_waits_for_approval_then_becomes_a_task()
    {
        await using var kit = await GitLabKit.CreateAsync();
        await kit.LinkAsync();
        var design = kit.GitLab.Epics.Single(e => e.Title == "Design");
        var spike = kit.GitLab.AddIssue("Spike: antenna options", epicId: design.Id, description: "Try two antennas.");
        spike.DueDate = new DateOnly(2026, 11, 12);
        spike.AssigneeIds = [203];
        var outside = kit.GitLab.AddIssue("Unrelated chore"); // not under a linked phase: ignored

        await kit.IssueHookAsync(spike);
        await kit.IssueHookAsync(outside);
        await kit.DispatchAsync();
        var panel = await kit.Sync.GetAsync(kit.ProjectId);
        var pending = Assert.Single(panel.Pending);
        Assert.Equal(("Spike: antenna options", "Design"), (pending.Title, pending.PhaseTitle));

        panel = await kit.Sync.DecidePendingAsync(kit.ProjectId, pending.Id, approve: true);
        Assert.Empty(panel.Pending);
        await using var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var task = await db.Items.AsNoTracking().SingleAsync(i => i.Title == "Spike: antenna options");
        Assert.Equal(kit.Tasks["Design"], task.ParentId);
        Assert.Equal(new DateOnly(2026, 11, 12), task.Finish);
        Assert.Equal("Try two antennas.", task.Values.GetValueOrDefault(kit.NotesColumn.ToString()));
        Assert.StartsWith("1001.01.", task.CostpointWbsId);
        Assert.Contains($"WBS::{task.CostpointWbsId}", spike.Labels);
        Assert.Equal([GitLabKit.Sam], await db.Set<ItemAssignee>().Where(a => a.ItemId == task.Id).Select(a => a.Email).ToListAsync());
        Assert.Contains(panel.Tasks, t => t.ItemId == task.Id && t.Label == $"issue #{spike.Iid}");

        // A rejected one isn't offered again.
        var second = kit.GitLab.AddIssue("Another idea", epicId: design.Id);
        await kit.IssueHookAsync(second);
        await kit.DispatchAsync();
        var again = Assert.Single((await kit.Sync.GetAsync(kit.ProjectId)).Pending);
        await kit.Sync.DecidePendingAsync(kit.ProjectId, again.Id, approve: false);
        second.UpdatedAt = kit.Clock.Now.UtcDateTime.AddMinutes(1);
        await kit.IssueHookAsync(second);
        await kit.DispatchAsync();
        Assert.Empty((await kit.Sync.GetAsync(kit.ProjectId)).Pending);
    }

    // ===================================================================== webhooks

    [Fact]
    public async Task Webhooks_need_the_secret_and_replays_change_nothing()
    {
        await using var kit = await GitLabKit.CreateAsync();
        await kit.LinkAsync();
        var issue = await kit.IssueAsync("Code");
        var receiver = kit.Host.Get<GitLabWebhookReceiver>();
        var linkId = await kit.LinkIdAsync();
        var body = FakeGitLab.Payload("issue-hook.json", ("object_attributes.iid", issue.Iid), ("object_attributes.id", issue.Id));

        Assert.Equal(401, (await receiver.ReceiveAsync(linkId, "wrong", "k1", null, body, CancellationToken.None)).StatusCode);
        Assert.Equal(401, (await receiver.ReceiveAsync(linkId, null, "k1", null, body, CancellationToken.None)).StatusCode);
        Assert.Equal(404, (await receiver.ReceiveAsync(Guid.NewGuid(), "x", "k1", null, body, CancellationToken.None)).StatusCode);

        var secret = (await kit.Sync.GetWebhookSetupAsync(kit.ProjectId)).SecretToken;
        Assert.Equal(kit.GitLab.Hooks.First().Token, secret);
        Assert.Equal("Queued.", (await receiver.ReceiveAsync(linkId, secret, "k1", null, body, CancellationToken.None)).Message);
        Assert.Equal("Already received.", (await receiver.ReceiveAsync(linkId, secret, "k1", null, body, CancellationToken.None)).Message);
        Assert.Equal("Queued.", (await receiver.ReceiveAsync(linkId, secret, null, "uuid-1", body, CancellationToken.None)).Message);
        Assert.Equal("Already received.", (await receiver.ReceiveAsync(linkId, secret, null, "uuid-1", body, CancellationToken.None)).Message);
        Assert.Equal("Queued.", (await receiver.ReceiveAsync(linkId, secret, null, null, body, CancellationToken.None)).Message);
        Assert.Equal("Already received.", (await receiver.ReceiveAsync(linkId, secret, null, null, body, CancellationToken.None)).Message); // same body hash

        var otherProject = FakeGitLab.Payload("issue-hook.json", ("project.id", 999));
        Assert.StartsWith("Ignored", (await receiver.ReceiveAsync(linkId, secret, "k2", null, otherProject, CancellationToken.None)).Message);
        var push = """{"object_kind":"push","ref":"refs/heads/main"}""";
        Assert.StartsWith("Ignored", (await receiver.ReceiveAsync(linkId, secret, "k3", null, push, CancellationToken.None)).Message);
        Assert.Equal(400, (await receiver.ReceiveAsync(linkId, secret, "k4", null, "not json", CancellationToken.None)).StatusCode);

        await using var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.Equal(3, await db.Set<IntegrationOutboxEvent>().CountAsync(e => e.EventType == GitLabOutbox.Pull));

        // Epic and milestone payloads are recognized too.
        var epicHook = GitLabWebhookReceiver.Parse(await db.Set<GitLabProjectLink>().SingleAsync(), File.ReadAllText(Path.Combine(FakeGitLab.FixtureFolder, "epic-hook.json")));
        Assert.Equal(("epic", 1L), (epicHook.Pull!.ObjectKind, epicHook.Pull.Iid));
        var milestoneHook = GitLabWebhookReceiver.Parse(await db.Set<GitLabProjectLink>().SingleAsync(), File.ReadAllText(Path.Combine(FakeGitLab.FixtureFolder, "milestone-hook.json")));
        Assert.Equal(("milestone", 5001L), (milestoneHook.Pull!.ObjectKind, milestoneHook.Pull.Id));
    }

    [Fact]
    public async Task The_webhook_route_is_anonymous_and_unknown_links_get_404()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsync($"/webhooks/gitlab/{Guid.NewGuid()}", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Not found.", await response.Content.ReadAsStringAsync());
    }

    // ===================================================================== reconcile and permissions

    [Fact]
    public async Task The_reconcile_catches_missed_webhooks_and_changes_that_raise_no_event()
    {
        await using var kit = await GitLabKit.CreateAsync();
        await kit.LinkAsync();
        var issue = await kit.IssueAsync("Code");
        kit.Clock.Advance(TimeSpan.FromMinutes(15));
        issue.State = "closed"; // no webhook
        issue.UpdatedAt = kit.Clock.Now.UtcDateTime;
        await using (var db = await kit.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            // Staffing changed (raises no sheet change): Riley now has 30 hours on Code.
            var assignment = await db.Set<Domain.Resourcing.StaffingAssignment>().SingleAsync(a => a.TotalHours == 16);
            assignment.TotalHours = 30;
            await db.SaveChangesAsync();
        }

        var ran = await GitLabReconcileBackgroundService.RunOnceAsync(kit.Host.Seeded.Host.Get<IServiceProvider>(), new GitLabOptions(), kit.Clock, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(1, ran);
        Assert.Equal("100", kit.Percent(await kit.ItemAsync("Code")));
        Assert.Equal(30 * 3600, issue.TimeEstimateSeconds);

        var now = await kit.Sync.SyncNowAsync(kit.ProjectId);
        Assert.Equal("Everything was already up to date.", now.Message);
    }

    [Fact]
    public async Task Only_admins_connect_gitlab_and_only_project_setup_links_projects()
    {
        await using var kit = await GitLabKit.CreateAsync();
        kit.Host.As(SeededHost.Owner);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Connection.GetAsync());
        await Assert.ThrowsAnyAsync<WorkbenchForbiddenException>(() => kit.Sync.LinkAsync(kit.ProjectId, new LinkGitLabProjectRequest(FakeGitLab.ProjectPath)));
        kit.Host.As(SeededHost.Outsider);
        await Assert.ThrowsAnyAsync<Exception>(() => kit.Sync.GetAsync(kit.ProjectId));
        kit.Host.As(SeededHost.Admin);
        Assert.True((await kit.Sync.GetAsync(kit.ProjectId)).CanManage);
    }
}
