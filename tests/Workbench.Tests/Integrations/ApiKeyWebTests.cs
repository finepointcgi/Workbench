using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Integrations;

/// <summary>API keys against the real web host: authentication, scopes, the permission decorators and revocation.</summary>
public sealed class ApiKeyWebTests
{
    private const string Admin = "dev@workbench.local";

    private static async Task<ApiKeyCreatedDto> CreateKeyAsync(HttpClient admin, OrgRoles roles, params string[] scopes)
    {
        var response = await admin.PostAsJsonAsync("api/v1/integrations/api-keys", new CreateApiKeyRequest($"Test {Guid.NewGuid():N}", roles, scopes));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiKeyCreatedDto>())!;
    }

    private static HttpClient KeyClient(WorkbenchWebFactory factory, string key, bool header = false)
    {
        var client = factory.CreateClientAs(null);
        if (header)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }
        else
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return client;
    }

    [Fact]
    public async Task Keys_are_limited_to_their_scopes_and_still_pass_every_permission_check()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        var admin = factory.CreateClientAs(Admin);
        var employee = await CreateKeyAsync(admin, OrgRoles.Employee, "sheets:read", "accounting:read");
        var client = KeyClient(factory, employee.Key);

        // In scope: allowed, as the key's own service user.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("api/v1/sheets")).StatusCode);
        var me = await KeyClient(factory, employee.Key, header: true).GetAsync("api/v1/sheets");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // Out of scope: refused before the endpoint runs.
        var write = await client.PostAsJsonAsync("api/v1/sheets", new CreateSheetRequest("From a key", null, false));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Contains("sheets:write", await write.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("api/v1/billing/invoices")).StatusCode);

        // In scope but not allowed for the key's roles: the permission decorators still say no (Finance only).
        var accounts = await client.GetAsync("api/v1/accounting/accounts");
        Assert.Equal(HttpStatusCode.Forbidden, accounts.StatusCode);
        Assert.DoesNotContain("scope", await accounts.Content.ReadAsStringAsync());
        var finance = await CreateKeyAsync(admin, OrgRoles.Finance, "accounting:read");
        Assert.Equal(HttpStatusCode.OK, (await KeyClient(factory, finance.Key).GetAsync("api/v1/accounting/accounts")).StatusCode);

        // Even an Admin key with every scope can't manage integrations or users.
        var everything = await CreateKeyAsync(admin, OrgRoles.Admin, ApiKeyScopes.Areas.Select(a => $"{a.Key}:write").ToArray());
        var root = KeyClient(factory, everything.Key);
        Assert.Equal(HttpStatusCode.Forbidden, (await root.GetAsync("api/v1/integrations/api-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await root.PostAsJsonAsync("api/v1/integrations/api-keys", new CreateApiKeyRequest("Escalate", OrgRoles.Admin, ["sheets:read"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await root.GetAsync("api/v1/admin/security-events")).StatusCode);

        // Keys don't sign anyone in to the web pages.
        var page = await root.GetAsync("admin/integrations");
        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);
    }

    [Fact]
    public async Task Revoked_and_unknown_keys_get_401()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        var admin = factory.CreateClientAs(Admin);
        var key = await CreateKeyAsync(admin, OrgRoles.Employee, "sheets:read");
        var client = KeyClient(factory, key.Key);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("api/v1/sheets")).StatusCode);

        (await admin.PostAsJsonAsync($"api/v1/integrations/api-keys/{key.ApiKey.Id}/revoke", new RevokeApiKeyRequest("Leaked"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/v1/sheets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await KeyClient(factory, "wbk_" + new string('z', 43)).GetAsync("api/v1/sheets")).StatusCode);

        var listed = await (await admin.GetAsync("api/v1/integrations/api-keys")).Content.ReadFromJsonAsync<List<ApiKeyDto>>();
        var row = listed!.Single(k => k.Id == key.ApiKey.Id);
        Assert.NotNull(row.RevokedAtUtc);
        Assert.NotNull(row.LastUsedAtUtc);
    }

    // Top-level API paths no key may call: user, sign-in and host administration, integrations themselves, imports, the
    // assistant, notifications and support for people, and the Costpoint mirror. Add a new path to an area in ApiKeyScopes,
    // or here if keys must never reach it.
    private static readonly string[] ClosedToKeys =
        ["admin", "host", "users", "workspaces-admin", "integrations", "imports", "migration", "assistant", "me", "support", "help", "costpoint", "timesheet-flags",
         "vendor-bank", "bank-files", "compliance"]; // bank details, payment files and CPSR/CAS stay out of reach of integration keys

    [Fact]
    public async Task Every_api_path_belongs_to_a_key_area_or_is_deliberately_closed()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        _ = factory.Server;
        var segments = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => "/" + (e.RoutePattern.RawText ?? "").TrimStart('/'))
            .Select(ApiKeyScopes.FirstSegment)
            .Where(s => s is not null && !s.StartsWith('{'))
            .Distinct()
            .ToList();

        Assert.NotEmpty(segments);
        var unassigned = segments.Where(s => ApiKeyScopes.AreaFor(s!) is null && !ClosedToKeys.Contains(s)).ToList();
        Assert.True(unassigned.Count == 0, $"Add these API paths to an area in ApiKeyScopes (or to ClosedToKeys): {string.Join(", ", unassigned)}");
    }
}
