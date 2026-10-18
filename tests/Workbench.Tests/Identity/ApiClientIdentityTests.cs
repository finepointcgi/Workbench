using System.Net;
using System.Text;
using Duende.IdentityModel.OidcClient.Browser;
using Microsoft.Extensions.DependencyInjection;
using Workbench.ApiClient;
using Workbench.ApiClient.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Sheets;

namespace Workbench.Tests.Identity;

public sealed class ApiClientIdentityTests
{
    private static readonly Uri Server = new("https://workbench.example.test/");

    [Fact]
    public async Task Forbidden_responses_become_workbench_forbidden_exceptions()
    {
        var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("You need edit access to this sheet to do that."),
        })) { BaseAddress = Server };

        var error = await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            new HttpItemService(http).CreateAsync(Guid.NewGuid(), new CreateItemRequest("Task")));

        Assert.Contains("edit access", error.Message);
        Assert.IsAssignableFrom<UnauthorizedAccessException>(error);
    }

    [Fact]
    public async Task My_role_maps_404_to_null()
    {
        var http = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("my-role")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK))) { BaseAddress = Server };

        Assert.Null(await new HttpSheetPermissions(http).GetMyRoleAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Every_workbench_client_sends_the_bearer_token_but_only_to_the_server()
    {
        var seen = new List<(Uri Uri, string? Authorization)>();
        var services = new ServiceCollection();
        services.AddSingleton<IAccessTokenProvider>(new FixedTokens("token-123"));
        services.AddWorkbenchApiClient(Server);
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new StubHandler(request =>
        {
            seen.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
        })));
        services.AddHttpClient("other");
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<ISheetService>().ListAsync();
        await provider.GetRequiredService<IUserDirectory>().SearchAsync("a");
        await provider.GetRequiredService<IHttpClientFactory>().CreateClient("other").GetAsync("https://elsewhere.example.test/x");

        Assert.Equal("Bearer token-123", seen[0].Authorization);
        Assert.Equal("Bearer token-123", seen[1].Authorization);
        Assert.Null(seen[2].Authorization);
    }

    [Fact]
    public async Task Without_a_token_provider_requests_go_anonymously()
    {
        string? authorization = "unset";
        var handler = new BearerTokenHandler(new NoAccessTokenProvider(), new WorkbenchServer(Server))
        {
            InnerHandler = new StubHandler(request =>
            {
                authorization = request.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
        };

        await new HttpClient(handler).GetAsync(new Uri(Server, "api/v1/sheets"));

        Assert.Null(authorization);
    }

    [Fact]
    public async Task Loopback_browser_returns_the_redirect_and_ignores_other_requests()
    {
        var port = LoopbackBrowser.GetFreePort();
        Task? userAgent = null;
        var browser = new LoopbackBrowser(start =>
        {
            Assert.Equal("https://idp.example.test/authorize?x=1", start.ToString());

            // Like a real browser: opening returns at once, and the requests arrive while the listener waits.
            userAgent = Task.Run(async () =>
            {
                using var client = new HttpClient();
                using var favicon = await client.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
                Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
                using var callback = await client.GetAsync($"http://127.0.0.1:{port}/?code=abc&state=xyz");
                Assert.Contains("Sign-in complete", await callback.Content.ReadAsStringAsync());
            });
            return Task.CompletedTask;
        }, port);

        var result = await browser.InvokeAsync(new BrowserOptions("https://idp.example.test/authorize?x=1", browser.RedirectUri));
        await userAgent!;

        Assert.Equal(BrowserResultType.Success, result.ResultType);
        Assert.Equal($"http://127.0.0.1:{port}/?code=abc&state=xyz", result.Response);
    }

    [Fact]
    public async Task Loopback_browser_reports_cancellation()
    {
        using var cancel = new CancellationTokenSource();
        var browser = new LoopbackBrowser(_ =>
        {
            cancel.Cancel();
            return Task.CompletedTask;
        }, LoopbackBrowser.GetFreePort());

        var result = await browser.InvokeAsync(new BrowserOptions("https://idp.example.test/authorize", browser.RedirectUri), cancel.Token);

        Assert.Equal(BrowserResultType.UserCancel, result.ResultType);
    }

    private sealed class FixedTokens(string token) : IAccessTokenProvider
    {
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(token);

        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
