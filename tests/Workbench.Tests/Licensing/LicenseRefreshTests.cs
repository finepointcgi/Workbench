using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Licensing;
using Workbench.Contracts.Licensing;
using Workbench.Domain.Licensing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Licensing;

/// <summary>The connected-install client: activate, refresh, reject what does not verify, surface the server's reason, back off, and stay silent offline.</summary>
public sealed class LicenseRefreshTests
{
    private static readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string PublicKey = Signer.ExportSubjectPublicKeyInfoPem();
    private static readonly Guid LicenseId = Guid.Parse("0f8b1c52-6e1a-4d0e-9a3b-5c1d2e3f4a5b");
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Server = "https://license.example.test";

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Issue(string issuedAt = "2026-11-01", string expires = "2026-11-15", Guid? id = null, ECDsa? signer = null, int seats = 25)
    {
        var payload = Encoding.UTF8.GetBytes($$"""{"v":2,"licenseId":"{{id ?? LicenseId}}","licensee":"Acme Federal","issuedAt":"{{issuedAt}}","expires":"{{expires}}","seats":{{seats}},"lightSeats":5,"modules":["finance"],"trial":false}""");
        return $"WB2.{B64(payload)}.{B64((signer ?? Signer).SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}";
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>A fake license server: records every request and answers from <see cref="Respond"/>.</summary>
    private sealed class FakeServer : HttpMessageHandler, IHttpClientFactory
    {
        public List<(string Path, JsonElement Body)> Calls { get; } = [];

        public Func<string, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            Calls.Add((request.RequestUri!.AbsolutePath, body));
            return Respond(request.RequestUri.AbsolutePath);
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        public static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status) { Content = JsonContent.Create(body) };

        public static HttpResponseMessage Ok(string key, params string[] warnings) =>
            Json(HttpStatusCode.OK, new { key, expires = "2026-11-15", refreshAfter = "2026-11-08", modules = new[] { "finance" }, seats = 25, lightSeats = 5, trial = false, warnings });
    }

    private sealed record Rig(Workbench.Tests.Identity.SeededHost Host, FakeServer Server, Clock Clock) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Host.DisposeAsync();

        public LicenseRefreshService Refresh => Host.Host.Get<LicenseRefreshService>();

        public Task<LicenseStatusDto> StatusAsync() => Host.Host.Get<ILicenseService>().GetStatusAsync();
    }

    private static async Task<Rig> RigAsync(string? serverUrl = Server, string? key = null)
    {
        var server = new FakeServer();
        var clock = new Clock(Start);
        var seeded = await CreateAsync(s =>
        {
            s.Replace(ServiceDescriptor.Singleton(new LicenseOptions { PublicKeyPem = PublicKey, ServerUrl = serverUrl }));
            s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
            s.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(server));
        });
        if (key is not null)
        {
            await seeded.As(Admin).Host.Get<ILicenseService>().ActivateAsync(new ActivateLicenseRequest(key));
        }

        return new Rig(seeded, server, clock);
    }

    [Fact]
    public async Task With_no_server_url_nothing_ever_calls_out_and_the_page_has_no_refresh_state()
    {
        await using var rig = await RigAsync(serverUrl: null, key: Issue());
        rig.Server.Respond = _ => throw new InvalidOperationException("An offline install must not make a call.");

        Assert.Null(await rig.Refresh.RefreshAsync(default));
        Assert.Equal(TimeSpan.FromHours(24), await rig.Refresh.RunIfDueAsync(default));
        var service = new LicenseRefreshBackgroundService(rig.Host.Host.Get<IServiceScopeFactory>(), new Workbench.Application.Tenancy.SingleTenantJobRunner(), new LicenseOptions(), rig.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<LicenseRefreshBackgroundService>.Instance);
        await service.StartAsync(default);
        await service.StopAsync(default);

        Assert.Empty(rig.Server.Calls);
        var status = await rig.StatusAsync();
        Assert.Null(status.Refresh);
        Assert.Equal("Business", status.Edition);
    }

    [Fact]
    public async Task The_first_run_activates_with_a_stable_install_id_and_the_seat_counts_then_later_runs_refresh()
    {
        await using var rig = await RigAsync(key: Issue(expires: "2026-11-15"));
        var newer = Issue(issuedAt: "2026-11-02", expires: "2026-11-30");
        rig.Server.Respond = _ => FakeServer.Ok(newer);

        var first = await rig.Refresh.RefreshAsync(default);
        rig.Clock.Now = Start.AddDays(1);
        await rig.Refresh.RefreshAsync(default);

        Assert.Equal(["/v1/activate", "/v1/refresh"], rig.Server.Calls.Select(c => c.Path));
        var body = rig.Server.Calls[0].Body;
        Assert.Equal(LicenseId, body.GetProperty("licenseId").GetGuid());
        var installId = body.GetProperty("installId").GetString();
        Assert.Matches("^[0-9a-f]{32}$", installId);
        Assert.Equal(installId, rig.Server.Calls[1].Body.GetProperty("installId").GetString());
        var usage = await rig.Host.Host.Get<EditionService>().UsageAsync(await rig.Host.Host.Get<EditionService>().GetAsync(default), default);
        Assert.Equal(usage.FullSeatsInUse, body.GetProperty("seatsInUse").GetInt32());
        Assert.Equal(usage.LightSeatsInUse, body.GetProperty("lightSeatsInUse").GetInt32());
        Assert.True(body.GetProperty("seatsInUse").GetInt32() > 0);

        Assert.Equal("refreshed", first!.Outcome);
        var status = await rig.StatusAsync();
        Assert.Equal(new DateOnly(2026, 11, 30), status.ExpiresOn);
        Assert.Equal("refreshed", status.Refresh!.Outcome);
        Assert.Equal(Start.AddDays(1).UtcDateTime, status.Refresh.LastSuccessUtc);
        Assert.False(status.Refresh.Stale);
    }

    [Fact]
    public async Task A_key_that_was_not_signed_by_the_built_in_public_key_is_rejected_and_the_old_key_is_kept()
    {
        await using var rig = await RigAsync(key: Issue(expires: "2026-11-15"));
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        rig.Server.Respond = _ => FakeServer.Ok(Issue(issuedAt: "2026-11-02", expires: "2030-01-01", signer: attacker));

        var result = await rig.Refresh.RefreshAsync(default);

        Assert.Equal("failed", result!.Outcome);
        Assert.Equal(1, result.ConsecutiveFailures);
        Assert.Equal(new DateOnly(2026, 11, 15), (await rig.StatusAsync()).ExpiresOn);
    }

    [Theory]
    [InlineData("other-license")]
    [InlineData("older-key")]
    [InlineData("garbage")]
    public async Task A_validly_signed_key_for_another_license_or_an_older_one_or_no_key_at_all_is_ignored(string kind)
    {
        await using var rig = await RigAsync(key: Issue(issuedAt: "2026-11-01", expires: "2026-11-15"));
        rig.Server.Respond = _ => kind switch
        {
            "other-license" => FakeServer.Ok(Issue(id: Guid.NewGuid(), issuedAt: "2026-11-02", expires: "2030-01-01")),
            "older-key" => FakeServer.Ok(Issue(issuedAt: "2026-10-01", expires: "2030-01-01")),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>captive portal</html>") },
        };

        var result = await rig.Refresh.RefreshAsync(default);

        Assert.Equal("failed", result!.Outcome);
        Assert.Equal(new DateOnly(2026, 11, 15), (await rig.StatusAsync()).ExpiresOn);
    }

    [Fact]
    public async Task A_lapsed_subscription_shows_the_servers_reason_and_the_existing_grace_period_still_applies()
    {
        await using var rig = await RigAsync(key: Issue(expires: "2026-11-15"));
        rig.Server.Respond = _ => FakeServer.Json(HttpStatusCode.PaymentRequired, new { reason = "subscription_lapsed", message = "The subscription lapsed on 2026-11-10. Renew to keep it." });

        rig.Clock.Now = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero); // Five days past the key's expiry.
        var result = await rig.Refresh.RefreshAsync(default);
        var status = await rig.StatusAsync();

        Assert.Equal("refused", result!.Outcome);
        Assert.Contains("lapsed on 2026-11-10", status.Refresh!.Message);
        Assert.Equal(0, status.Refresh.ConsecutiveFailures); // The server answered; it is not a network problem.
        Assert.True(status.InGracePeriod);
        Assert.Equal("Business", status.Edition);

        rig.Clock.Now = new DateTimeOffset(2026, 12, 2, 8, 0, 0, TimeSpan.Zero); // Past the 14 days of grace.
        Assert.Equal("Community", (await rig.StatusAsync()).Edition);
    }

    [Fact]
    public async Task Network_errors_back_off_exponentially_and_never_change_what_anyone_may_do()
    {
        await using var rig = await RigAsync(key: Issue(expires: "2026-11-15"));
        rig.Server.Respond = _ => throw new HttpRequestException("no route to host");

        var waits = new List<TimeSpan> { await rig.Refresh.RunIfDueAsync(default) };
        var callsAfterFirst = rig.Server.Calls.Count;
        Assert.Equal(1, callsAfterFirst);

        // Asking again straight away waits out the backoff without calling.
        rig.Clock.Now = Start.AddMinutes(2);
        Assert.Equal(TimeSpan.FromMinutes(3), await rig.Refresh.RunIfDueAsync(default));
        Assert.Equal(1, rig.Server.Calls.Count);

        foreach (var minutes in new[] { 5, 15, 35 })
        {
            rig.Clock.Now = Start.AddMinutes(minutes);
            waits.Add(await rig.Refresh.RunIfDueAsync(default));
        }

        Assert.Equal([5, 10, 20, 40], waits.Select(w => (int)w.TotalMinutes));
        Assert.Equal(TimeSpan.FromHours(24), LicenseRefreshService.Backoff(30, TimeSpan.FromHours(24)));
        Assert.Equal(TimeSpan.FromHours(24), LicenseRefreshService.Backoff(0, TimeSpan.FromHours(24)));
        var status = await rig.StatusAsync();
        Assert.Equal("Business", status.Edition);
        Assert.Equal(new DateOnly(2026, 11, 15), status.ExpiresOn);
        Assert.Equal(4, status.Refresh!.ConsecutiveFailures);
    }

    [Fact]
    public async Task After_the_stale_period_without_a_refresh_administrators_get_a_banner_but_entitlements_do_not_change()
    {
        await using var rig = await RigAsync(key: Issue(expires: "2027-01-31"));
        rig.Server.Respond = _ => throw new HttpRequestException("offline");

        await rig.Refresh.RefreshAsync(default);
        Assert.False((await rig.StatusAsync()).Refresh!.Stale);

        rig.Clock.Now = Start.AddDays(8);
        await rig.Refresh.RefreshAsync(default);
        var status = await rig.StatusAsync();

        Assert.True(status.Refresh!.Stale);
        Assert.Equal("Business", status.Edition);
        Assert.False(status.InGracePeriod);
        Assert.True(status.HasModule("billing"));
    }

    [Fact]
    public async Task An_offline_key_on_a_connected_install_is_not_a_failure_or_a_banner()
    {
        await using var rig = await RigAsync(key: Issue(expires: "2027-01-31"));
        rig.Server.Respond = _ => FakeServer.Json(HttpStatusCode.Conflict, new { reason = "offline_license", message = "This is an offline key." });

        rig.Clock.Now = Start.AddDays(30);
        var result = await rig.Refresh.RefreshAsync(default);

        Assert.Equal("offline", result!.Outcome);
        Assert.Equal(0, result.ConsecutiveFailures);
        Assert.False(result.Stale);
    }

    [Fact]
    public async Task A_refresh_for_an_install_the_server_has_forgotten_activates_again()
    {
        await using var rig = await RigAsync(key: Issue(expires: "2026-11-15"));
        var newer = Issue(issuedAt: "2026-11-02", expires: "2026-11-30");
        rig.Server.Respond = _ => FakeServer.Ok(newer);
        await rig.Refresh.RefreshAsync(default);

        rig.Clock.Now = Start.AddDays(1);
        rig.Server.Respond = path => path.EndsWith("refresh", StringComparison.Ordinal)
            ? FakeServer.Json(HttpStatusCode.NotFound, new { reason = "not_activated", message = "Activate first." })
            : FakeServer.Ok(newer);
        var result = await rig.Refresh.RefreshAsync(default);

        Assert.Equal(["/v1/activate", "/v1/refresh", "/v1/activate"], rig.Server.Calls.Select(c => c.Path));
        Assert.Equal("refreshed", result!.Outcome);
    }

    /// <summary>
    /// <c>Samples/wb2-from-license-server.json</c> was produced by the license server's own <c>LicenseSigner.Sign</c>
    /// (copied from branch claude/license-server-d68057, run with a throwaway key). The app does not reference the server, so the
    /// sample is checked in and this proves the two sides agree on the format. Regenerate it if the server's key format changes.
    /// </summary>
    [Fact]
    public void A_key_issued_by_the_license_servers_own_signer_parses_in_the_app()
    {
        var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Licensing", "Samples", "wb2-from-license-server.json"))).RootElement;
        var key = sample.GetProperty("key").GetString();
        var publicKey = sample.GetProperty("publicKeyPem").GetString()!;

        Assert.True(LicenseKeys.TryVerify(key, publicKey, out var claims, out var problem), problem);

        Assert.Equal(("Golden Sample Federal", 25, 10, false, 2), (claims!.Licensee, claims.Seats, claims.LightSeats, claims.Trial, claims.Version));
        Assert.Equal(Guid.Parse("7c2f5d1e-3a64-4b8e-9d0a-1e2f3a4b5c6d"), claims.LicenseId);
        Assert.Equal(new DateOnly(2026, 11, 1), claims.IssuedOn);
        Assert.Equal(new DateOnly(2027, 11, 1), claims.ExpiresOn);
        Assert.Equal(new[] { "accounting", "billing", "core", "enterprise", "govcon" }, claims.Modules!.Order()); // the golden key predates the split: finance -> billing + accounting, controls -> enterprise
        Assert.False(LicenseKeys.TryVerify(key, PublicKey, out _, out _), "Another key pair's public key must not verify it.");
    }
}

/// <summary>What administrators see on Setup &gt; License and in the banner.</summary>
public sealed class LicenseRefreshPageTests
{
    private static LicenseStatusDto Status(LicenseRefreshDto refresh) => new("Business", EditionSource.Key, "Acme", new DateOnly(2026, 11, 15), false, 5, 1, null, [], null, 0, 0, false, 10, false, refresh);

    private static Bunit.BunitContext Context(LicenseStatusDto status, Workbench.Contracts.Identity.OrgRoles roles)
    {
        var ctx = Navigation.NavTestKit.NewContext(new Navigation.NavUser(roles));
        ctx.Services.AddSingleton(Navigation.Stub<ILicenseService>.Create(("GetStatusAsync", _ => Task.FromResult(status))));
        return ctx;
    }

    [Fact]
    public void The_banner_tells_administrators_the_install_could_not_refresh_and_nothing_changed()
    {
        var refresh = new LicenseRefreshDto("https://x", DateTime.UtcNow, DateTime.UtcNow.AddDays(-9), "failed", "Could not reach the license server.", 12, true, 9);
        using var ctx = Context(Status(refresh), Workbench.Contracts.Identity.OrgRoles.Admin);

        var banner = ctx.Render<Workbench.Ui.Layout.LicenseBanner>(p => p.Add(c => c.Access, Workbench.Ui.Navigation.NavAccess.LoadAsync(ctx.Services).GetAwaiter().GetResult()));

        var text = banner.Find("[data-testid=license-banner]").TextContent;
        Assert.Contains("not been able to refresh", text);
        Assert.Contains("keeps working until it expires", text);
    }

    [Fact]
    public void The_banner_is_absent_when_the_refresh_is_not_stale_or_the_install_is_offline()
    {
        var fresh = new LicenseRefreshDto("https://x", DateTime.UtcNow, DateTime.UtcNow, "refreshed", "ok", 0, false, 0);
        using var ctx = Context(Status(fresh), Workbench.Contracts.Identity.OrgRoles.Admin);

        var banner = ctx.Render<Workbench.Ui.Layout.LicenseBanner>(p => p.Add(c => c.Access, Workbench.Ui.Navigation.NavAccess.LoadAsync(ctx.Services).GetAwaiter().GetResult()));

        Assert.Empty(banner.FindAll("[data-testid=license-banner]"));
    }

    [Fact]
    public async Task The_license_page_shows_the_servers_reason_and_when_it_last_tried()
    {
        var refresh = new LicenseRefreshDto("https://x", new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc), "refused", "The subscription lapsed on 2026-11-10.", 0, false, 15);
        await using var ctx = Context(Status(refresh), Workbench.Contracts.Identity.OrgRoles.Admin);

        var page = ctx.Render<Workbench.Ui.Setup.LicensePage>();

        var text = page.Find("[data-testid=license-refresh]").TextContent;
        Assert.Contains("did not renew", text);
        Assert.Contains("lapsed on 2026-11-10", text);
        Assert.Contains("20 Nov 2026", text);
        Assert.Contains("5 Nov 2026", text);
    }
}
