using System.Net;
using Microsoft.Extensions.Options;
using Workbench.Application.Integrations;
using Workbench.Contracts.Integrations;

namespace Workbench.Tests.Integrations;

public sealed class SsrfGuardTests
{
    private static OutboundUrlGuard Guard(FakeDns? dns = null, bool allowHttp = false, bool allowPrivate = false) =>
        new(dns ?? new FakeDns(), Options.Create(new IntegrationOptions { AllowHttp = allowHttp, AllowPrivateNetworkTargets = allowPrivate }));

    [Theory]
    [InlineData("http://hooks.example.com/x", "https://")]
    [InlineData("ftp://hooks.example.com/x", "https://")]
    [InlineData("hooks.example.com/x", "full web address")]
    [InlineData("https://user:secret@hooks.example.com/x", "user name")]
    [InlineData("https://localhost/x", "internal")]
    [InlineData("https://app.localhost/x", "internal")]
    [InlineData("https://printer/x", "internal")]
    [InlineData("https://intranet.corp/x", "internal")]
    [InlineData("https://metadata.google.internal/computeMetadata", "internal")]
    [InlineData("https://127.0.0.1/x", "private")]
    [InlineData("https://10.1.2.3/x", "private")]
    [InlineData("https://172.20.0.1/x", "private")]
    [InlineData("https://192.168.1.10:8443/x", "private")]
    [InlineData("https://169.254.169.254/latest/meta-data/", "private")]
    [InlineData("https://100.64.0.1/x", "private")]
    [InlineData("https://0.0.0.0/x", "private")]
    [InlineData("https://[::1]/x", "private")]
    [InlineData("https://[fd12:3456::1]/x", "private")]
    [InlineData("https://[fe80::1]/x", "private")]
    [InlineData("https://[::ffff:10.0.0.1]/x", "private")]
    [InlineData("https://[64:ff9b::a9fe:a9fe]/x", "private")]
    public async Task Internal_and_unsafe_targets_are_refused(string url, string reason)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Guard().ValidateAsync(url, "The endpoint URL", CancellationToken.None));
        Assert.Contains(reason, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_public_name_that_resolves_to_a_private_address_is_refused()
    {
        var dns = new FakeDns();
        dns.Answers["sneaky.example.com"] = [IPAddress.Parse("52.1.2.3"), IPAddress.Parse("10.0.0.7")];
        dns.Answers["metadata.example.com"] = [IPAddress.Parse("169.254.169.254")];
        await Assert.ThrowsAsync<ArgumentException>(() => Guard(dns).ValidateAsync("https://sneaky.example.com/x", "URL", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => Guard(dns).ValidateAsync("https://metadata.example.com/x", "URL", CancellationToken.None));
    }

    [Fact]
    public async Task Public_https_targets_are_accepted_and_development_can_opt_in_to_local_ones()
    {
        var uri = await Guard().ValidateAsync("https://hooks.example.com/a?b=c", "URL", CancellationToken.None);
        Assert.Equal("hooks.example.com", uri.Host);
        await Guard().ValidateAsync("https://[2606:4700::1111]/x", "URL", CancellationToken.None);

        await Guard(allowHttp: true, allowPrivate: true).ValidateAsync("http://localhost:5000/hook", "URL", CancellationToken.None);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("52.1.2.3", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2002:0a00:0001::1", false)]
    [InlineData("ff02::1", false)]
    public void Address_ranges(string address, bool isPublic) => Assert.Equal(isPublic, OutboundUrlGuard.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task The_transport_rechecks_the_address_when_it_connects_dns_rebinding()
    {
        // The name passed the check once, then started resolving to loopback: the real transport refuses to connect.
        var dns = new FakeDns();
        dns.Answers["rebind.example.com"] = [IPAddress.Loopback];
        var options = Options.Create(new IntegrationOptions { RequestTimeoutSeconds = 2 });
        using var transport = new GuardedHttpTransport(new OutboundUrlGuard(dns, options), options);

        var response = await transport.SendAsync(new OutboundRequest(new Uri("https://rebind.example.com/hook"), "{}", "application/json", []), CancellationToken.None);
        Assert.Null(response.StatusCode);
        Assert.Contains("Refused", response.Error);
    }

    [Fact]
    public async Task Endpoints_and_chat_connectors_refuse_private_addresses_when_saved()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var webhooks = host.Get<IWebhookService>();
        await Assert.ThrowsAsync<ArgumentException>(() => webhooks.CreateEndpointAsync(new SaveWebhookEndpointRequest("Metadata", "https://169.254.169.254/", ["project.created"])));
        await Assert.ThrowsAsync<ArgumentException>(() => webhooks.CreateEndpointAsync(new SaveWebhookEndpointRequest("Plain", "http://hooks.example.com/", ["project.created"])));
        Assert.Empty(await webhooks.ListEndpointsAsync());

        await Assert.ThrowsAsync<ArgumentException>(() => host.Get<IConnectorService>().CreateAsync(new SaveConnectorRequest("slack", "Ops", null,
            new Dictionary<string, string?> { ["webhookUrl"] = "https://10.0.0.5/services/x" }, ["project.created"])));
    }
}
