using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Sheets;
using Workbench.Web.Identity;

namespace Workbench.Tests.Identity;

/// <summary>Authentication and authorization through the real web host and its HTTP pipeline.</summary>
public sealed class WebHostAuthTests
{
    [Theory]
    [InlineData("Development", null)]
    [InlineData("Production", "https://login.example.test/tenant/v2.0")]
    public async Task Anonymous_api_requests_get_401(string environment, string? authority)
    {
        await using var factory = new WorkbenchWebFactory(environment, oidcAuthority: authority);
        using var client = factory.CreateClientAs(null);

        using var response = await client.GetAsync("/api/v1/sheets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_pages_redirect_to_the_dev_sign_in_in_development()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        using var client = factory.CreateClientAs(null);

        using var page = await client.GetAsync("/sheets");
        using var signIn = await client.GetAsync(DevSignIn.Path);

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith($"http://localhost{DevSignIn.Path}", page.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        var html = await signIn.Content.ReadAsStringAsync();
        Assert.Contains("viewer@example.com", html);
        Assert.Contains("dev@workbench.local", html);
    }

    [Fact]
    public async Task Dev_sign_in_issues_the_cookie()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        var page = await client.GetStringAsync(DevSignIn.Path);
        var token = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;

        using var signIn = await client.PostAsync(DevSignIn.Path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = "viewer@example.com",
            ["returnUrl"] = "/sheets",
        }));
        var sheets = await client.GetFromJsonAsync<List<SheetSummary>>("/api/v1/sheets");

        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal("/sheets", signIn.Headers.Location!.ToString());
        Assert.Equal(3, sheets!.Count); // Program Office's two, and the viewer's seat on 1002's plan
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Dev_sign_in_does_not_exist_outside_development(string environment)
    {
        await using var factory = new WorkbenchWebFactory(environment, testAuth: true, oidcAuthority: "https://login.example.test/tenant/v2.0");
        using var client = factory.CreateClientAs("dev@workbench.local");

        using var get = await client.GetAsync(DevSignIn.Path);
        using var post = await client.PostAsync(DevSignIn.Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "dev@workbench.local" }));

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.True(post.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"POST returned {post.StatusCode}");
    }

    [Fact]
    public async Task Dev_sign_in_is_off_when_an_authority_is_configured_even_in_development()
    {
        var development = new FakeEnvironment(Environments.Development);

        Assert.True(DevSignIn.IsEnabled(development, new OidcSettings()));
        Assert.False(DevSignIn.IsEnabled(development, new OidcSettings { Authority = "https://login.example.test/" }));
        Assert.False(DevSignIn.IsEnabled(new FakeEnvironment(Environments.Production), new OidcSettings()));

        await using var factory = new WorkbenchWebFactory("Development", testAuth: true, oidcAuthority: "https://login.example.test/tenant/v2.0");
        using var client = factory.CreateClientAs("dev@workbench.local");
        using var response = await client.GetAsync(DevSignIn.Path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Production_without_an_authority_refuses_to_start()
    {
        using var factory = new WorkbenchWebFactory("Production");

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Authentication:Oidc", error.ToString());
    }

    [Fact]
    public async Task Forbidden_changes_map_to_403_and_reads_still_work()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var viewer = factory.CreateClientAs("viewer@example.com");
        using var analyst = factory.CreateClientAs("analyst@example.com");
        using var outsider = factory.CreateClientAs("outsider@example.com");
        var sheets = await viewer.GetFromJsonAsync<List<SheetSummary>>("/api/v1/sheets");
        var sheetId = sheets![0].Id;

        using var viewerCreate = await viewer.PostAsJsonAsync($"/api/v1/sheets/{sheetId}/items", new CreateItemRequest("From viewer"));
        using var analystCreate = await analyst.PostAsJsonAsync($"/api/v1/sheets/{sheetId}/items", new CreateItemRequest("From analyst"));
        using var outsiderGet = await outsider.GetAsync($"/api/v1/sheets/{sheetId}");
        var outsiderList = await outsider.GetFromJsonAsync<List<SheetSummary>>("/api/v1/sheets");

        Assert.Equal(HttpStatusCode.Forbidden, viewerCreate.StatusCode);
        Assert.Contains("edit access", await viewerCreate.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, analystCreate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsiderGet.StatusCode);
        Assert.Empty(outsiderList!);
    }

    [Fact]
    public async Task Seeded_dev_users_have_org_roles_and_employees()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var pm = factory.CreateClientAs("pm@example.com");
        using var analyst = factory.CreateClientAs("analyst@example.com");
        using var dev = factory.CreateClientAs("dev@workbench.local");

        var pmContext = await pm.GetFromJsonAsync<CurrentUserInfo>("/api/v1/users/me/context");
        var analystContext = await analyst.GetFromJsonAsync<CurrentUserInfo>("/api/v1/users/me/context");
        var devContext = await dev.GetFromJsonAsync<CurrentUserInfo>("/api/v1/users/me/context");
        using var analystRates = await analyst.GetAsync($"/api/v1/employees/{analystContext!.EmployeeId}/cost-rates");
        using var pmRates = await pm.GetAsync($"/api/v1/employees/{analystContext.EmployeeId}/cost-rates");
        using var analystAudit = await analyst.GetAsync("/api/v1/audit");

        Assert.Equal(OrgRoles.Finance | OrgRoles.ProjectManager, pmContext!.Roles);
        Assert.Equal(OrgRoles.Employee, analystContext.Roles);
        Assert.Equal(OrgRoles.Admin, devContext!.Roles);
        Assert.NotNull(pmContext.EmployeeId);
        Assert.NotNull(analystContext.EmployeeId);
        Assert.NotNull(devContext.EmployeeId);
        Assert.Equal(HttpStatusCode.Forbidden, analystRates.StatusCode);
        Assert.Equal(HttpStatusCode.OK, pmRates.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, analystAudit.StatusCode);
    }

    [Fact]
    public async Task Hub_requires_sign_in_and_sheet_access_to_join()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var viewer = factory.CreateClientAs("viewer@example.com");
        var sheetId = (await viewer.GetFromJsonAsync<List<SheetSummary>>("/api/v1/sheets"))![0].Id;

        await using var anonymous = Connect(factory, null);
        await using var outsider = Connect(factory, "outsider@example.com");
        await using var member = Connect(factory, "viewer@example.com");

        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
        await outsider.StartAsync();
        var refused = await Assert.ThrowsAsync<HubException>(() => outsider.InvokeAsync("JoinSheet", sheetId));
        await member.StartAsync();
        await member.InvokeAsync("JoinSheet", sheetId);

        Assert.Contains("was not found", refused.Message);
    }

    private static HubConnection Connect(WorkbenchWebFactory factory, string? email) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/sheets"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                if (email is not null)
                {
                    options.Headers[WorkbenchWebFactory.UserHeader] = email;
                }
            })
            .Build();

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Workbench.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
