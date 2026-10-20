using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Integrations;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Domain.Integrations;
using Workbench.Tests.Tenancy;

namespace Workbench.Tests.Integrations;

/// <summary>MultiTenant: keys, endpoints, events and deliveries stay in the tenant that owns them.</summary>
public sealed class IntegrationTenancyTests
{
    [Fact]
    public async Task Keys_endpoints_and_events_never_cross_tenants()
    {
        await using var web = await MultiTenantWeb.DevelopmentAsync();
        var acme = web.Client("acme");
        var key = await MultiTenantWeb.ReadAsync<ApiKeyCreatedDto>(await acme.PostAsJsonAsync("api/v1/integrations/api-keys",
            new CreateApiKeyRequest("Acme sync", OrgRoles.Employee, ["sheets:read"])));
        await MultiTenantWeb.ReadAsync<WebhookSecretDto>(await acme.PostAsJsonAsync("api/v1/integrations/webhooks",
            new SaveWebhookEndpointRequest("Acme warehouse", "https://hooks.acme.example/wb", ["project.created"])));

        // The key works at acme only.
        Client(web, "acme", key.Key, out var atAcme);
        Client(web, "beta", key.Key, out var atBeta);
        Assert.Equal(HttpStatusCode.OK, (await atAcme.GetAsync("api/v1/sheets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await atBeta.GetAsync("api/v1/sheets")).StatusCode);

        // Beta's admin sees none of acme's integrations.
        var beta = web.Client("beta");
        Assert.Empty(await MultiTenantWeb.ReadAsync<List<WebhookEndpointDto>>(await beta.GetAsync("api/v1/integrations/webhooks")));
        Assert.Empty(await MultiTenantWeb.ReadAsync<List<ApiKeyDto>>(await beta.GetAsync("api/v1/integrations/api-keys")));

        // An acme event is delivered by acme's dispatcher run only, to acme's endpoint.
        await web.InTenantAsync("acme", async sp =>
        {
            await using var db = await sp.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
            IntegrationOutbox.Raise(db, IntegrationEventType.ProjectCreated,
                new ProjectEventData(Guid.NewGuid(), "A-1", "Acme only", Guid.NewGuid(), null, null, "Active", null, null), "dev@workbench.local");
            await db.SaveChangesAsync();
            return 0;
        });

        var transport = new FakeTransport();
        var sent = new Dictionary<string, int>();
        foreach (var slug in new[] { "acme", "beta" })
        {
            sent[slug] = await web.InTenantAsync(slug, async sp =>
            {
                var dispatcher = ActivatorUtilities.CreateInstance<IntegrationDispatcher>(sp, (IOutboundTransport)transport);
                return (await dispatcher.RunOnceAsync(CancellationToken.None)).Attempts;
            });
        }

        // (The sample data seeding raises events of its own in each tenant; beta has no endpoint, so nothing of beta's is sent.)
        Assert.True(sent["acme"] >= 1);
        Assert.Equal(0, sent["beta"]);
        Assert.All(transport.Sent, r => Assert.Equal("hooks.acme.example", r.Url.Host));
        Assert.Contains(transport.Sent, r => r.Body.Contains("\"Acme only\""));
        var betaHasIt = await web.InTenantAsync("beta", async sp =>
        {
            await using var db = await sp.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
            return await db.Set<IntegrationOutboxEvent>().AnyAsync(e => e.PayloadJson.Contains("Acme only"));
        });
        Assert.False(betaHasIt);
    }

    private static void Client(MultiTenantWeb web, string tenant, string key, out HttpClient client)
    {
        client = web.Client(tenant, email: null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }
}
