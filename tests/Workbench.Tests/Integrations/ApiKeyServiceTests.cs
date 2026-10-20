using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Integrations;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.Integrations;
using Workbench.Domain.Security;

namespace Workbench.Tests.Integrations;

public sealed class ApiKeyServiceTests
{
    private static Task<ApiKeyCreatedDto> CreateAsync(IntegrationHost host, OrgRoles roles = OrgRoles.Employee, DateTime? expires = null, params string[] scopes) =>
        host.Get<IApiKeyService>().CreateAsync(new CreateApiKeyRequest("Warehouse sync", roles, scopes.Length == 0 ? ["time:read"] : scopes, expires));

    [Fact]
    public async Task A_key_is_shown_once_stored_as_a_hash_and_backed_by_its_own_service_user()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var created = await CreateAsync(host, OrgRoles.Finance, null, "time:read", "time:write", "billing:read");

        Assert.StartsWith("wbk_", created.Key);
        Assert.Equal(["time:write", "billing:read"], created.ApiKey.Scopes);
        Assert.StartsWith(created.ApiKey.Prefix, created.Key);
        Assert.EndsWith("@api-keys.workbench.invalid", created.ApiKey.ServiceUserEmail);

        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var row = await db.Set<ApiKey>().AsNoTracking().SingleAsync();
        Assert.Equal(IntegrationSecrets.HashApiKey(created.Key), row.KeyHash);
        Assert.DoesNotContain(created.Key, row.KeyHash + row.Prefix + row.Scopes);
        var user = await db.Set<AppUser>().AsNoTracking().SingleAsync(u => u.Email == row.ServiceUserEmail);
        Assert.True(user is { IsActive: true, IsFinance: true, IsAdmin: false });
        var events = await db.Set<SecurityEvent>().AsNoTracking().Where(e => e.EventType == SecurityEventTypes.ApiKeyCreated).ToListAsync();
        Assert.DoesNotContain(created.Key, Assert.Single(events).Detail);

        // Listing never returns the key again.
        Assert.Equal(created.ApiKey.Id, Assert.Single(await host.Get<IApiKeyService>().ListAsync()).Id);
    }

    [Fact]
    public async Task Scopes_and_expiry_are_validated()
    {
        await using var host = await IntegrationHost.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => CreateAsync(host, scopes: "time:delete"));
        await Assert.ThrowsAsync<ArgumentException>(() => CreateAsync(host, scopes: "integrations:write"));
        await Assert.ThrowsAsync<ArgumentException>(() => CreateAsync(host, scopes: " "));
        await Assert.ThrowsAsync<ArgumentException>(() => CreateAsync(host, expires: host.Clock.GetUtcNow().UtcDateTime.AddDays(-1)));
    }

    [Fact]
    public async Task Revoked_expired_and_deactivated_keys_stop_working_and_are_recorded()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var auth = host.Get<ApiKeyAuthenticator>();
        var key = await CreateAsync(host);
        var expiring = await CreateAsync(host, expires: host.Clock.GetUtcNow().UtcDateTime.AddHours(2));

        var (identity, _) = await auth.AuthenticateAsync(key.Key, "203.0.113.9", CancellationToken.None);
        Assert.Equal(key.ApiKey.ServiceUserEmail, identity!.Email);
        Assert.Equal(["time:read"], identity.Scopes);
        Assert.NotNull((await host.Get<IApiKeyService>().ListAsync()).Single(k => k.Id == key.ApiKey.Id).LastUsedAtUtc);

        Assert.Null((await auth.AuthenticateAsync(key.Key + "x", null, CancellationToken.None)).Identity);
        Assert.Null((await auth.AuthenticateAsync("wbk_" + new string('a', 43), null, CancellationToken.None)).Identity);

        var revoked = await host.Get<IApiKeyService>().RevokeAsync(key.ApiKey.Id, new RevokeApiKeyRequest("Rotated"));
        Assert.False(revoked.IsActive);
        Assert.Equal("This API key was revoked.", (await auth.AuthenticateAsync(key.Key, null, CancellationToken.None)).Failure);

        host.Clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal("This API key has expired.", (await auth.AuthenticateAsync(expiring.Key, null, CancellationToken.None)).Failure);

        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.False((await db.Set<AppUser>().AsNoTracking().SingleAsync(u => u.Email == key.ApiKey.ServiceUserEmail)).IsActive);
        var types = await db.Set<SecurityEvent>().AsNoTracking().Select(e => e.EventType).ToListAsync();
        Assert.Contains(SecurityEventTypes.ApiKeyRevoked, types);
        Assert.True(types.Count(t => t == SecurityEventTypes.ApiKeyRejected) >= 4);
    }

    [Fact]
    public async Task A_key_always_acts_with_the_roles_it_was_issued()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var key = await CreateAsync(host);
        await using (var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var user = await db.Set<AppUser>().SingleAsync(u => u.Email == key.ApiKey.ServiceUserEmail);
            user.IsAdmin = true; // someone promoted the service user elsewhere
            await db.SaveChangesAsync();
        }

        await host.Get<ApiKeyAuthenticator>().AuthenticateAsync(key.Key, null, CancellationToken.None);
        await using (var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            Assert.False((await db.Set<AppUser>().AsNoTracking().SingleAsync(u => u.Email == key.ApiKey.ServiceUserEmail)).IsAdmin);
        }
    }

    [Fact]
    public async Task Only_administrators_issue_keys_and_a_key_can_never_manage_integrations()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var adminKey = await CreateAsync(host, OrgRoles.Admin);

        host.As(Identity.SeededHost.Finance);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => CreateAsync(host));

        // Even an Admin-role key's service user is refused.
        host.As(adminKey.ApiKey.ServiceUserEmail);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => CreateAsync(host));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IWebhookService>().ListEndpointsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IConnectorService>().ListKindsAsync());
    }

    [Theory]
    [InlineData("GET", "/api/v1/time/timesheets", "time:read", true)]
    [InlineData("POST", "/api/v1/time/timesheets", "time:read", false)]
    [InlineData("POST", "/api/v1/time/timesheets", "time:write", true)]
    [InlineData("GET", "/api/v1/billing/invoices", "time:write", false)]
    [InlineData("GET", "/api/v1/integrations/api-keys", "setup:write", false)]
    [InlineData("GET", "/api/v1/admin/users", "setup:write", false)]
    [InlineData("GET", "/api/v1/users/me/context", "setup:write", false)]
    [InlineData("GET", "/api/v1/host/tenants", "setup:write", false)]
    [InlineData("GET", "/api/v1/", "setup:write", false)]
    [InlineData("GET", "/scim/v2/Users", "setup:write", false)]
    public void Scopes_allow_only_their_areas_and_methods(string method, string path, string scope, bool allowed) =>
        Assert.Equal(allowed, ApiKeyScopes.Check([scope], path, method).Allowed);
}
