using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Workbench.Contracts.Integrations.Jira;
using Workbench.Tests.Accessibility;
using Workbench.Tests.Navigation;
using Workbench.Ui.Integrations.Jira;

namespace Workbench.Tests.Integrations.Jira;

/// <summary>The Jira panel and the admin tab over stub services: plain words, who wins, last synced, errors that block nobody.</summary>
public sealed class JiraComponentTests
{
    private static readonly Guid ProjectId = Guid.CreateVersion7();

    private static BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static JiraLinkDto Link(string state = "Up to date", string? error = null, string? hint = null) => new(
        Guid.CreateVersion7(), "RAD", "Radar", "https://acme.atlassian.net/browse/RAD", null, null, JiraDateWinnerChoice.Workbench, false, "Task", state,
        "Jira wins the title and assignee. Dates: Workbench wins when both changed. Marking a task Done moves its issue to Done. Time stays in Workbench.",
        new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc), error, hint, DateTime.UtcNow, "pm@example.com");

    private static IJiraProjectSyncService Sync(JiraProjectSyncDto dto, List<string>? calls = null) => Stub<IJiraProjectSyncService>.Create(
        ("GetAsync", _ => Task.FromResult(dto)),
        ("SearchJiraProjectsAsync", _ => Task.FromResult<IReadOnlyList<JiraProjectOptionDto>>([new("RAD", "Radar", "https://acme.atlassian.net/browse/RAD")])),
        ("LinkAsync", args =>
        {
            calls?.Add($"link {((LinkJiraProjectRequest)args![1]!).Project}");
            return Task.FromResult(dto with { Link = Link() });
        }),
        ("SyncNowAsync", _ =>
        {
            calls?.Add("sync");
            return Task.FromResult(new JiraSyncNowResult(1, 0, 0, "1 updated."));
        }),
        ("DismissNoteAsync", args =>
        {
            calls?.Add("dismiss");
            return Task.FromResult(dto with { Notes = [] });
        }));

    [Fact]
    public async Task Unlinked_with_no_connection_says_who_connects_it_and_with_one_offers_the_link_form()
    {
        await using var ctx = Context();
        ctx.Services.AddSingleton(Sync(new JiraProjectSyncDto(ProjectId, false, "Jira isn't connected yet. An administrator connects it under Admin › Integrations › Jira.", true, null, [], [], [], 0, 0)));
        var rendered = ctx.Render<JiraProjectPanel>(p => p.Add(x => x.ProjectId, ProjectId));
        rendered.WaitForAssertion(() => Assert.Contains("An administrator connects it", rendered.Find("[data-testid=jira-not-connected]").TextContent));

        await using var ready = Context();
        var calls = new List<string>();
        ready.Services.AddSingleton(Sync(new JiraProjectSyncDto(ProjectId, true, null, true, null, [], [], [], 0, 0), calls));
        var form = ready.Render<JiraProjectPanel>(p => p.Add(x => x.ProjectId, ProjectId));
        form.WaitForAssertion(() => Assert.NotNull(form.Find("[data-testid=jira-link-form]")));
        Assert.Contains("Dates: Workbench wins when both changed", form.Find("[data-testid=jira-winner]").TextContent);
        Assert.Contains("Time is never imported", form.Markup);
        Assert.True(form.Find("[data-testid=jira-link]").HasAttribute("disabled")); // nothing chosen yet
        A11yAssert.Check(form);
    }

    [Fact]
    public async Task A_linked_project_shows_state_last_synced_who_wins_and_the_task_keys_as_links()
    {
        var tasks = new[] { new JiraTaskLinkDto(Guid.CreateVersion7(), "Interface spec", "2001.01", "RAD-100", "https://acme.atlassian.net/browse/RAD-100", "In Review", 8m, DateTime.UtcNow.AddMinutes(-3), null, false) };
        var dto = new JiraProjectSyncDto(ProjectId, true, null, true, Link(), tasks, [], [], 0, 0);
        await using var ctx = Context();
        ctx.Services.AddSingleton(Sync(dto));
        var rendered = ctx.Render<JiraProjectPanel>(p => p.Add(x => x.ProjectId, ProjectId));

        rendered.WaitForAssertion(() => Assert.Contains("Up to date", rendered.Find("[data-testid=jira-state]").TextContent));
        Assert.Contains("Last synced", rendered.Find("[data-testid=jira-last-synced]").TextContent);
        Assert.Contains("Jira wins the title and assignee", rendered.Find("[data-testid=jira-rules]").TextContent);
        var row = rendered.Find("[data-testid=jira-tasks] tbody tr");
        Assert.Contains("RAD-100", row.TextContent);
        Assert.Contains("In Review", row.TextContent);
        Assert.Contains("8 h", row.TextContent);
        Assert.Equal("https://acme.atlassian.net/browse/RAD-100", row.QuerySelector("a")!.GetAttribute("href"));
        A11yAssert.Check(rendered);
    }

    [Fact]
    public async Task A_jira_outage_is_an_error_with_what_to_do_that_says_nobody_is_blocked_and_notes_can_be_dismissed()
    {
        var note = new JiraNoteDto(Guid.CreateVersion7(), null, "Interface spec", "RAD-100", "due", "The due date changed on both sides; Workbench's was kept.", "Change who wins on this project's Jira panel.", false, DateTime.UtcNow);
        var dto = new JiraProjectSyncDto(ProjectId, true, null, true, Link("Jira isn't answering", "Couldn't reach Jira: connection refused", "Nothing to do: Workbench tries again shortly."), [], [note], [], 0, 0);
        await using var ctx = Context();
        var calls = new List<string>();
        ctx.Services.AddSingleton(Sync(dto, calls));
        var rendered = ctx.Render<JiraProjectPanel>(p => p.Add(x => x.ProjectId, ProjectId));

        rendered.WaitForAssertion(() => Assert.Contains("Couldn't reach Jira", rendered.Find("[data-testid=jira-error]").TextContent));
        Assert.Contains("Nobody is blocked", rendered.Find("[data-testid=jira-error]").TextContent);
        Assert.Contains("Nothing to do", rendered.Find("[data-testid=jira-error]").TextContent);
        Assert.Contains("Workbench's was kept", rendered.Find("[data-testid=jira-notes]").TextContent);

        rendered.Find("[data-testid=jira-sync-now]").Click();
        rendered.WaitForAssertion(() => Assert.Contains("sync", calls));
        rendered.FindAll("[data-testid=jira-notes] button").Single(b => b.TextContent.Contains("OK, dismiss")).Click();
        rendered.WaitForAssertion(() => Assert.Contains("dismiss", calls));
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll("[data-testid=jira-notes]")));
    }

    [Fact]
    public async Task A_reader_who_cannot_set_up_the_project_sees_the_state_but_no_buttons()
    {
        var dto = new JiraProjectSyncDto(ProjectId, true, null, false, Link(), [], [], [], 0, 0);
        await using var ctx = Context();
        ctx.Services.AddSingleton(Sync(dto));
        var rendered = ctx.Render<JiraProjectPanel>(p => p.Add(x => x.ProjectId, ProjectId));
        rendered.WaitForAssertion(() => Assert.NotNull(rendered.Find("[data-testid=jira-state]")));
        Assert.Empty(rendered.FindAll("[data-testid=jira-sync-now]"));
        Assert.Empty(rendered.FindAll("[data-testid=jira-unlink]"));
    }

    [Fact]
    public async Task The_admin_tab_tests_the_connection_and_says_what_to_do_when_it_fails()
    {
        var tested = new List<SaveJiraConnectionRequest>();
        var connection = Stub<IJiraConnectionService>.Create(
            ("GetAsync", _ => Task.FromResult(new JiraConnectionDto(false, null, null, null, false, null, null, null, null, 0, null, null))),
            ("TestAsync", args =>
            {
                tested.Add((SaveJiraConnectionRequest)args![0]!);
                return Task.FromResult(new JiraTestResult(false, "Jira didn't accept the account email and API token.", "Create a new API token in your Atlassian account.", null));
            }));
        await using var ctx = Context();
        ctx.Services.AddSingleton(connection);
        ctx.Render<MudPopoverProvider>();
        var rendered = ctx.Render<JiraTab>();

        rendered.WaitForAssertion(() => Assert.NotNull(rendered.Find("[data-testid=jira-connection-form]")));
        rendered.Find("input[data-testid=jira-url]").Change("https://acme.atlassian.net");
        rendered.Find("input[data-testid=jira-email]").Change("bot@example.com");
        rendered.Find("input[data-testid=jira-token]").Change("secret-token-12345");
        rendered.Find("[data-testid=jira-test]").Click();

        rendered.WaitForAssertion(() => Assert.Contains("What to do:", rendered.Find("[data-testid=jira-test-result]").TextContent));
        var request = Assert.Single(tested);
        Assert.Equal(("https://acme.atlassian.net", "bot@example.com", "secret-token-12345"), (request.SiteUrl, request.AccountEmail, request.Token));
        Assert.Equal("password", rendered.Find("input[data-testid=jira-token]").GetAttribute("type")); // typed in a password field, never shown
        A11yAssert.Check(rendered);
    }
}
