using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Integrations;
using Workbench.Contracts;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Integrations;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Tests.Integrations;

public sealed class WebhookDeliveryTests
{
    private const string Url = "https://hooks.example.com/workbench";

    private static async Task<WebhookSecretDto> EndpointAsync(IntegrationHost host, params string[] events) =>
        await host.Get<IWebhookService>().CreateEndpointAsync(new SaveWebhookEndpointRequest("Warehouse", Url, events.Length == 0 ? ["project.created"] : events));

    private static Task<ProjectDto> ProjectAsync(IntegrationHost host, string code = "3003") =>
        host.Get<IProjectService>().CreateProjectAsync(new SaveProjectRequest(code, $"Project {code}", null, null, ProjectState.Active, null, null));

    private static async Task<List<IntegrationOutboxEvent>> OutboxAsync(IntegrationHost host)
    {
        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Set<IntegrationOutboxEvent>().AsNoTracking().OrderBy(e => e.OccurredAtUtc).ToListAsync();
    }

    [Fact]
    public async Task The_outbox_row_is_saved_with_the_change_and_never_without_it()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var project = await ProjectAsync(host);

        var row = Assert.Single(await OutboxAsync(host));
        Assert.Equal("project.created", row.EventType);
        Assert.Equal(project.Id, row.SubjectId);
        Assert.Null(row.DispatchedAtUtc);
        using (var doc = JsonDocument.Parse(row.PayloadJson))
        {
            Assert.Equal(row.Id, doc.RootElement.GetProperty("id").GetGuid());
            Assert.Equal("3003", doc.RootElement.GetProperty("data").GetProperty("code").GetString());
            Assert.Equal(SeededHostAdmin, doc.RootElement.GetProperty("actor").GetString());
        }

        // A change that fails validation saves nothing, so it raises nothing.
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectAsync(host, "3003"));
        Assert.Single(await OutboxAsync(host));

        // Same unit of work: when SaveChanges fails, the event is rolled back with the change.
        await using (var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var broken = new Project { Code = null!, Name = "No code" };
            db.Set<Project>().Add(broken);
            IntegrationOutbox.Raise(db, IntegrationEventType.ProjectCreated, IntegrationEventPayloads.Project(broken), "x@example.com");
            await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
        }

        Assert.Single(await OutboxAsync(host));
    }

    private const string SeededHostAdmin = Identity.SeededHost.Admin;

    [Fact]
    public void Raising_checks_the_event_carries_its_own_data_record()
    {
        var data = new PeriodEventData(Guid.NewGuid(), 2027, 1, "Oct", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), "Closed");
        Assert.Throws<ArgumentException>(() => IntegrationOutbox.Raise(null!, IntegrationEventType.ProjectCreated, data, null));
    }

    [Fact]
    public async Task Deliveries_are_signed_so_receivers_can_verify_them()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var endpoint = await EndpointAsync(host);
        await ProjectAsync(host);

        var result = await host.Get<IntegrationDispatcher>().RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, result.EventsFannedOut);
        Assert.Equal(1, result.Attempts);

        var sent = Assert.Single(host.Transport.Sent);
        Assert.Equal(new Uri(Url), sent.Url);
        Assert.Equal((await OutboxAsync(host))[0].PayloadJson, sent.Body);
        var headers = sent.Headers.ToDictionary(h => h.Key, h => h.Value);
        Assert.Equal("project.created", headers[WebhookSignature.EventHeader]);
        var signature = headers[WebhookSignature.SignatureHeader];

        var now = host.Clock.GetUtcNow();
        Assert.True(WebhookSignature.Verify(endpoint.SigningSecret, signature, sent.Body, now));
        Assert.False(WebhookSignature.Verify(endpoint.SigningSecret, signature, sent.Body.Replace("3003", "9999"), now));
        Assert.False(WebhookSignature.Verify("whsec_wrong", signature, sent.Body, now));
        Assert.False(WebhookSignature.Verify(endpoint.SigningSecret, signature, sent.Body, now.AddMinutes(6)));
        Assert.False(WebhookSignature.Verify(endpoint.SigningSecret, "v1=abc", sent.Body, now));

        // The secret is only ever returned once; at rest it's encrypted.
        await using (var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var row = await db.Set<WebhookEndpoint>().SingleAsync();
            Assert.DoesNotContain(endpoint.SigningSecret, row.SecretProtected);
            Assert.Equal(endpoint.SigningSecret[^4..], row.SecretHint);
        }

        var delivery = Assert.Single(await host.Get<IWebhookService>().ListDeliveriesAsync(new DeliveryQuery()));
        Assert.Equal(DeliveryState.Succeeded, delivery.Status);
        var detail = await host.Get<IWebhookService>().GetDeliveryAsync(delivery.Id);
        var attempt = Assert.Single(detail.Attempts);
        Assert.True(attempt.Succeeded);
        Assert.Equal(200, attempt.ResponseStatusCode);
        Assert.Contains(WebhookSignature.SignatureHeader, attempt.RequestHeaders);
        Assert.NotNull((await host.Get<IWebhookService>().GetEndpointAsync(endpoint.Endpoint.Id)).LastSuccessAtUtc);

        // Sent once: another run finds nothing to do.
        Assert.Equal(new DispatchResult(0, 0, 0), await host.Get<IntegrationDispatcher>().RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public void Retry_delays_double_from_30_seconds_up_to_six_hours()
    {
        var seed = Guid.NewGuid();
        var delays = Enumerable.Range(1, 12).Select(n => RetryPolicy.DelayAfter(n, seed).TotalSeconds).ToList();
        Assert.InRange(delays[0], 30, 33);
        Assert.InRange(delays[1], 60, 66);
        Assert.InRange(delays[4], 480, 528);
        Assert.All(delays.Zip(delays.Skip(1)), p => Assert.True(p.Second >= p.First));
        Assert.Equal(TimeSpan.FromHours(6).TotalSeconds, delays[^1]);
        Assert.True(RetryPolicy.IsRetryable(null));
        Assert.True(RetryPolicy.IsRetryable(503));
        Assert.True(RetryPolicy.IsRetryable(429));
        Assert.False(RetryPolicy.IsRetryable(400));
        Assert.False(RetryPolicy.IsRetryable(410));
    }

    [Fact]
    public async Task Failures_are_retried_with_backoff_until_the_attempts_run_out()
    {
        await using var host = await IntegrationHost.CreateAsync(o => o.MaxAttempts = 3);
        await EndpointAsync(host);
        await ProjectAsync(host);
        host.Transport.Default = FakeTransport.Status(500);
        var dispatcher = host.Get<IntegrationDispatcher>();

        await dispatcher.RunOnceAsync(CancellationToken.None);
        var first = Assert.Single(await host.Get<IWebhookService>().ListDeliveriesAsync(new DeliveryQuery()));
        Assert.Equal(DeliveryState.Pending, first.Status);
        Assert.Equal(1, first.AttemptCount);
        Assert.InRange((first.NextAttemptAtUtc!.Value - host.Clock.GetUtcNow().UtcDateTime).TotalSeconds, 30, 33);

        // Not due yet: nothing is sent.
        host.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(0, (await dispatcher.RunOnceAsync(CancellationToken.None)).Attempts);

        host.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(1, (await dispatcher.RunOnceAsync(CancellationToken.None)).Attempts);
        var second = (await host.Get<IWebhookService>().ListDeliveriesAsync(new DeliveryQuery())).Single();
        Assert.Equal(2, second.AttemptCount);
        Assert.InRange((second.NextAttemptAtUtc!.Value - host.Clock.GetUtcNow().UtcDateTime).TotalSeconds, 60, 66);

        host.Clock.Advance(TimeSpan.FromSeconds(70));
        await dispatcher.RunOnceAsync(CancellationToken.None);
        var last = (await host.Get<IWebhookService>().ListDeliveriesAsync(new DeliveryQuery())).Single();
        Assert.Equal(DeliveryState.Failed, last.Status);
        Assert.Equal(3, last.AttemptCount);
        Assert.Equal(500, last.LastStatusCode);
        Assert.Equal(3, (await host.Get<IWebhookService>().GetDeliveryAsync(last.Id)).Attempts.Count);
        Assert.Equal(3, host.Transport.Sent.Count);
    }

    [Fact]
    public async Task A_client_error_isnt_retried()
    {
        await using var host = await IntegrationHost.CreateAsync();
        await EndpointAsync(host);
        await ProjectAsync(host);
        host.Transport.Answer(FakeTransport.Status(401));

        await host.Get<IntegrationDispatcher>().RunOnceAsync(CancellationToken.None);
        var delivery = (await host.Get<IWebhookService>().ListDeliveriesAsync(new DeliveryQuery())).Single();
        Assert.Equal(DeliveryState.Failed, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
    }

    [Fact]
    public async Task Repeated_failures_mark_the_endpoint_unhealthy_and_it_is_probed_hourly_until_it_recovers()
    {
        await using var host = await IntegrationHost.CreateAsync(o => (o.UnhealthyAfterFailures, o.MaxAttempts) = (2, 20));
        var endpoint = (await EndpointAsync(host)).Endpoint;
        await ProjectAsync(host, "4001");
        host.Transport.Default = FakeTransport.Unreachable;
        var dispatcher = host.Get<IntegrationDispatcher>();

        await dispatcher.RunOnceAsync(CancellationToken.None);
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.RunOnceAsync(CancellationToken.None);
        var unhealthy = await host.Get<IWebhookService>().GetEndpointAsync(endpoint.Id);
        Assert.Equal(WebhookHealthState.Unhealthy, unhealthy.Health);
        Assert.Equal(2, unhealthy.ConsecutiveFailures);

        // The admin who set it up is told.
        await using (var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            Assert.Contains(await db.Set<Notification>().AsNoTracking().ToListAsync(), n => n.Kind == "integration.webhook_unhealthy" && n.RecipientEmail == Identity.SeededHost.Admin);
        }

        // New events wait for the hourly probe instead of hammering the receiver.
        await ProjectAsync(host, "4002");
        host.Clock.Advance(TimeSpan.FromMinutes(10));
        var sentBefore = host.Transport.Sent.Count;
        await dispatcher.RunOnceAsync(CancellationToken.None);
        Assert.Equal(sentBefore, host.Transport.Sent.Count);

        // The probe succeeds: healthy again, and the waiting deliveries follow.
        host.Transport.Default = FakeTransport.Status(204);
        host.Clock.Advance(TimeSpan.FromHours(1));
        await dispatcher.RunOnceAsync(CancellationToken.None);
        Assert.Equal(WebhookHealthState.Healthy, (await host.Get<IWebhookService>().GetEndpointAsync(endpoint.Id)).Health);
        host.Clock.Advance(TimeSpan.FromHours(1));
        await dispatcher.RunOnceAsync(CancellationToken.None);
        Assert.All(await host.Get<IWebhookService>().ListDeliveriesAsync(new DeliveryQuery()), d => Assert.Equal(DeliveryState.Succeeded, d.Status));
    }

    [Fact]
    public async Task Redeliver_sends_the_same_event_again_as_a_new_delivery()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var endpoint = await EndpointAsync(host);
        await ProjectAsync(host);
        var dispatcher = host.Get<IntegrationDispatcher>();
        await dispatcher.RunOnceAsync(CancellationToken.None);
        var original = (await host.Get<IWebhookService>().ListDeliveriesAsync(new DeliveryQuery())).Single();

        var again = await host.Get<IWebhookService>().RedeliverAsync(original.Id);
        Assert.Equal(original.EventId, again.EventId);
        Assert.Equal(original.Id, again.RedeliveryOfId);
        await dispatcher.RunOnceAsync(CancellationToken.None);

        var bodies = host.Transport.Sent.ToList();
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0].Body, bodies[1].Body);
        Assert.NotEqual(Header(bodies[0], WebhookSignature.DeliveryHeader), Header(bodies[1], WebhookSignature.DeliveryHeader));
        Assert.True(WebhookSignature.Verify(endpoint.SigningSecret, Header(bodies[1], WebhookSignature.SignatureHeader), bodies[1].Body, host.Clock.GetUtcNow()));

        // A pending delivery can't be redelivered (it's still being tried).
        host.Transport.Default = FakeTransport.Status(500);
        await host.Get<IWebhookService>().SendTestAsync(endpoint.Endpoint.Id);
        await dispatcher.RunOnceAsync(CancellationToken.None);
        var ping = (await host.Get<IWebhookService>().ListDeliveriesAsync(new DeliveryQuery(EventType: "ping"))).Single();
        Assert.Equal(DeliveryState.Pending, ping.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Get<IWebhookService>().RedeliverAsync(ping.Id));
    }

    [Fact]
    public async Task Only_subscribed_active_endpoints_get_an_event_and_turning_one_off_cancels_what_waits()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var projects = await EndpointAsync(host, "project.created");
        await EndpointAsync(host, "invoice.paid");
        await ProjectAsync(host);
        host.Transport.Default = FakeTransport.Status(500);
        var dispatcher = host.Get<IntegrationDispatcher>();
        await dispatcher.RunOnceAsync(CancellationToken.None);
        var delivery = (await host.Get<IWebhookService>().ListDeliveriesAsync(new DeliveryQuery())).Single();
        Assert.Equal(projects.Endpoint.Id, delivery.EndpointId);

        var e = projects.Endpoint;
        await host.Get<IWebhookService>().UpdateEndpointAsync(e.Id, new SaveWebhookEndpointRequest(e.Name, e.Url, e.EventTypes, IsActive: false));
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await dispatcher.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DeliveryState.Cancelled, (await host.Get<IWebhookService>().ListDeliveriesAsync(new DeliveryQuery())).Single().Status);
    }

    [Fact]
    public async Task Only_administrators_manage_webhooks()
    {
        await using var host = await IntegrationHost.CreateAsync();
        host.As(Identity.SeededHost.Finance);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IWebhookService>().ListEndpointsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => EndpointAsync(host));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IConnectorService>().ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IApiKeyService>().ListAsync());
    }

    private static string Header(OutboundRequest request, string name) => request.Headers.First(h => h.Key == name).Value;
}
