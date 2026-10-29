using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.ApiClient;
using Workbench.Application.Integrations.Jira;
using Workbench.Contracts.Integrations.Jira;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Integrations.Jira;

/// <summary>The Jira sync against the real web host on the Development sample data, with a fake Jira behind it: the API, permissions and the anonymous webhook route.</summary>
public sealed class JiraWebTests
{
    private const string Admin = "dev@workbench.local";

    [Fact]
    public async Task An_admin_connects_links_a_project_syncs_and_a_signed_webhook_is_accepted_while_a_wrong_one_is_not()
    {
        var jira = new FakeJira(() => DateTime.UtcNow);
        jira.Add("Radar interface spec", assignee: "acc-jordan", due: new DateOnly(2026, 11, 20));
        await using var seeded = new WorkbenchWebFactory("Development", testAuth: true);
        await using var web = seeded.WithWebHostBuilder(b =>
        {
            b.UseSetting("Integrations:AllowPrivateNetworkTargets", "true"); // the fake Jira's name isn't looked up
            b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IJiraHandlerFactory>(jira)));
        });
        using var admin = web.CreateClient();
        admin.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, Admin);
        using var analyst = web.CreateClient();
        analyst.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, "analyst@example.com");
        using var anonymous = web.CreateClient();
        var connection = new HttpJiraConnectionService(admin);
        var sync = new HttpJiraProjectSyncService(admin);

        var saved = await connection.SaveAsync(new SaveJiraConnectionRequest(JiraKit.SiteUrl, FakeJira.Email, FakeJira.Token));
        Assert.True(saved.LastTestOk, saved.LastTestMessage);
        Assert.DoesNotContain(FakeJira.Token, await (await admin.GetAsync("api/v1/integrations/jira/connection")).Content.ReadAsStringAsync());

        // An ordinary employee can neither see the connection nor link a project.
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.GetAsync("api/v1/integrations/jira/connection")).StatusCode);
        var projects = (await admin.GetFromJsonAsync<List<ProjectDto>>("api/v1/projects"))!;
        var project = projects.First(p => p.Code == "1001");
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsJsonAsync($"api/v1/integrations/jira/projects/{project.Id}/link", new LinkJiraProjectRequest("RAD"))).StatusCode);

        var linked = await sync.LinkAsync(project.Id, new LinkJiraProjectRequest("RAD"));
        Assert.Equal("RAD", linked.Link!.JiraProjectKey);
        Assert.Contains("Jira wins the title and assignee", linked.Link.Rules);
        var result = await sync.SyncNowAsync(project.Id);
        Assert.Contains("added", result.Message + "updated");
        var panel = await sync.GetAsync(project.Id);
        var task = Assert.Single(panel.Tasks, t => t.Title == "Radar interface spec");
        Assert.StartsWith($"{JiraKit.SiteUrl}/browse/RAD-", task.WebUrl);

        // The anonymous webhook route: wrong secret 401, unknown link 404, the right signature accepted.
        var hook = await sync.GetWebhookSetupAsync(project.Id);
        Assert.EndsWith($"/webhooks/jira/{panel.Link!.Id}", hook.Url);
        var body = """{"webhookEvent":"jira:issue_updated","issue":{"key":"RAD-100","fields":{"project":{"key":"RAD"}}}}""";
        Task<HttpResponseMessage> Post(Guid id, string? signature)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/jira/{id}") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            if (signature is not null)
            {
                request.Headers.Add("X-Hub-Signature", signature);
            }

            return anonymous.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(panel.Link.Id, JiraWebhookReceiver.Sign("wrong", body))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(panel.Link.Id, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(Guid.NewGuid(), null)).StatusCode);
        var accepted = await Post(panel.Link.Id, JiraWebhookReceiver.Sign(hook.Secret, body));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("Queued.", await accepted.Content.ReadAsStringAsync());

        await sync.UnlinkAsync(project.Id);
        Assert.Null((await sync.GetAsync(project.Id)).Link);
    }
}
