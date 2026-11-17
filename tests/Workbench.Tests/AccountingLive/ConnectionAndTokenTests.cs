using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.AccountingLive;
using Workbench.Contracts.AccountingExport;
using Workbench.Contracts.AccountingLive;
using Workbench.Domain.AccountingExport;
using Workbench.Domain.Security;

namespace Workbench.Tests.AccountingLive;

/// <summary>OAuth connections, token rotation and the 401 / 429 / 5xx behaviour, all against a fake provider.</summary>
public sealed class ConnectionAndTokenTests
{
    [Fact]
    public async Task QuickBooks_connects_with_a_one_time_state_and_keeps_the_tokens_encrypted()
    {
        await using var live = await LiveKit.CreateAsync();
        live.TokenEndpoints();
        var start = await live.Connections.StartAsync(AccountingTarget.QuickBooks);
        var url = new Uri(start.Url);
        Assert.Equal("appcenter.intuit.com", url.Host);
        Assert.Contains("client_id=qb-client", url.Query);
        Assert.Contains("response_type=code", url.Query);
        Assert.Contains("scope=com.intuit.quickbooks.accounting", url.Query);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("https://wb.test/admin/integrations/accounting-live/callback/quickbooks"), url.Query);
        var state = LiveKit.StateOf(start);

        // A forged or unknown state is refused and nothing is exchanged.
        await Assert.ThrowsAsync<ArgumentException>(() => live.Connections.CompleteAsync(new AccountingOAuthCallback(AccountingTarget.QuickBooks, "code", "forged", "realm-42", null)));
        Assert.Empty(live.Provider.To("oauth2/v1/tokens/bearer"));

        // The state belongs to the provider it was made for.
        await Assert.ThrowsAsync<ArgumentException>(() => live.Connections.CompleteAsync(new AccountingOAuthCallback(AccountingTarget.Xero, "code", state, null, null)));

        var connected = await live.Connections.CompleteAsync(new AccountingOAuthCallback(AccountingTarget.QuickBooks, "auth-code", state, "realm-42", null));
        Assert.True(connected.Connected);
        Assert.Equal("realm-42", connected.CompanyId);
        Assert.True(connected.IsSandbox);

        var exchange = Assert.Single(live.Provider.To("oauth2/v1/tokens/bearer"));
        Assert.Equal("Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("qb-client:qb-secret")), exchange.Headers["Authorization"]);
        Assert.Contains("grant_type=authorization_code", exchange.Body);
        Assert.Contains("code=auth-code", exchange.Body);

        var row = await live.ConnectionRowAsync(AccountingTarget.QuickBooks);
        Assert.DoesNotContain("access-1", row.AccessTokenProtected);
        Assert.DoesNotContain("refresh-1", row.RefreshTokenProtected);
        var secrets = live.Kit.Seeded.Host.Get<AccountingSecrets>();
        Assert.Equal("refresh-1", secrets.Unprotect(row.RefreshTokenProtected!));

        // The state works once.
        await Assert.ThrowsAsync<ArgumentException>(() => live.Connections.CompleteAsync(new AccountingOAuthCallback(AccountingTarget.QuickBooks, "auth-code", state, "realm-42", null)));
        Assert.Single(live.Provider.To("oauth2/v1/tokens/bearer"));
    }

    [Fact]
    public async Task A_denied_authorisation_connects_nothing()
    {
        await using var live = await LiveKit.CreateAsync();
        var start = await live.Connections.StartAsync(AccountingTarget.QuickBooks);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => live.Connections.CompleteAsync(new AccountingOAuthCallback(AccountingTarget.QuickBooks, null, LiveKit.StateOf(start), null, "access_denied")));
        Assert.Contains("denied", ex.Message);
        Assert.All(await live.Connections.ListAsync(), c => Assert.False(c.Connected));
    }

    [Fact]
    public async Task A_provider_that_is_not_configured_cannot_be_connected()
    {
        await using var live = await LiveKit.CreateAsync();
        live.Kit.Seeded.Host.Get<Microsoft.Extensions.Options.IOptions<AccountingProviderOptions>>().Value.Xero.ClientSecret = null;
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => live.Connections.StartAsync(AccountingTarget.Xero));
        Assert.Contains("Accounting:Xero", ex.Message);
        Assert.False((await live.Connections.ListAsync()).First(c => c.Target == AccountingTarget.Xero).Configured);
    }

    [Fact]
    public async Task Xero_with_several_organisations_asks_which_one_and_refuses_to_push_until_chosen()
    {
        await using var live = await LiveKit.CreateAsync();
        var dto = await live.ConnectXeroAsync(("t-1", "Maple Florist"), ("t-2", "Demo Company (US)"));
        Assert.Equal(AccountingConnectionStatus.ChooseOrganisation, dto.Status);
        Assert.False(dto.Connected);
        Assert.Equal(2, dto.Choices.Count);

        var tokens = live.Kit.Get<AccountingTokenService>();
        await Assert.ThrowsAsync<AccountingNotConnectedException>(() => tokens.GetAsync(AccountingTarget.Xero));
        await Assert.ThrowsAsync<ArgumentException>(() => live.Connections.ChooseOrganisationAsync(AccountingTarget.Xero, "t-9"));

        var chosen = await live.Connections.ChooseOrganisationAsync(AccountingTarget.Xero, "t-2");
        Assert.True(chosen.Connected);
        Assert.Equal("Demo Company (US)", chosen.CompanyName);
        var session = await tokens.GetAsync(AccountingTarget.Xero);
        Assert.Equal("t-2", session.CompanyId);
        var authorize = live.Provider.Calls.Count; // the organisation list was read once, with the new access token
        Assert.Equal("access-1", live.Provider.To("api.xero.com/connections").Single().Bearer);
        Assert.True(authorize > 0);
    }

    [Fact]
    public async Task Disconnecting_revokes_the_token_at_the_provider_and_forgets_it()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        await live.Connections.DisconnectAsync(AccountingTarget.QuickBooks);

        var revoke = Assert.Single(live.Provider.To("/oauth2/tokens/revoke"));
        Assert.Contains("refresh-1", revoke.Body);
        Assert.False((await live.Connections.ListAsync()).First(c => c.Target == AccountingTarget.QuickBooks).Connected);
        await using var db = await live.Kit.Factory.CreateAsync();
        Assert.False(await db.Set<AccountingConnection>().AnyAsync());
        Assert.True(await db.Set<SecurityEvent>().AnyAsync(e => e.EventType == "integration.connector" && e.Detail!.StartsWith("Disconnected")));
    }

    [Fact]
    public async Task Refreshing_rotates_the_refresh_token_and_the_new_one_is_the_one_used_next_time()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        var tokens = live.Kit.Get<AccountingTokenService>();
        var secrets = live.Kit.Seeded.Host.Get<AccountingSecrets>();

        var fresh = await tokens.GetAsync(AccountingTarget.QuickBooks);
        Assert.Equal("access-1", fresh.AccessToken); // still good: no refresh
        Assert.Single(live.Provider.To("oauth2/v1/tokens/bearer"));

        await live.ExpireAccessTokenAsync(AccountingTarget.QuickBooks);
        var renewed = await tokens.GetAsync(AccountingTarget.QuickBooks);
        Assert.Equal("access-2", renewed.AccessToken);
        var refresh1 = live.Provider.To("oauth2/v1/tokens/bearer").Last();
        Assert.Contains("grant_type=refresh_token", refresh1.Body);
        Assert.Contains("refresh_token=refresh-1", refresh1.Body);
        Assert.Equal("refresh-2", secrets.Unprotect((await live.ConnectionRowAsync(AccountingTarget.QuickBooks)).RefreshTokenProtected!));

        await live.ExpireAccessTokenAsync(AccountingTarget.QuickBooks);
        await tokens.GetAsync(AccountingTarget.QuickBooks);
        Assert.Contains("refresh_token=refresh-2", live.Provider.To("oauth2/v1/tokens/bearer").Last().Body);
        Assert.Equal("refresh-3", secrets.Unprotect((await live.ConnectionRowAsync(AccountingTarget.QuickBooks)).RefreshTokenProtected!));
    }

    [Fact]
    public async Task Two_requests_that_find_an_expired_token_at_once_refresh_it_once()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        await live.ExpireAccessTokenAsync(AccountingTarget.QuickBooks);
        var tokens = live.Kit.Get<AccountingTokenService>();

        var sessions = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => tokens.GetAsync(AccountingTarget.QuickBooks)));

        Assert.All(sessions, s => Assert.Equal("access-2", s.AccessToken));
        Assert.Equal(2, live.Provider.To("oauth2/v1/tokens/bearer").Count()); // the connect, and one refresh
    }

    [Fact]
    public async Task A_refresh_the_provider_refuses_marks_the_connection_as_needing_a_reconnect()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectXeroAsync(("t-1", "Maple Florist"));
        await live.ExpireAccessTokenAsync(AccountingTarget.Xero);
        live.Provider.On(c => c.Url.Contains("identity.xero.com/connect/token"), _ => FakeProvider.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        var tokens = live.Kit.Get<AccountingTokenService>();
        var ex = await Assert.ThrowsAsync<AccountingNotConnectedException>(() => tokens.GetAsync(AccountingTarget.Xero));
        Assert.Contains("Connect again", ex.Message);

        var dto = (await live.Connections.ListAsync()).First(c => c.Target == AccountingTarget.Xero);
        Assert.Equal(AccountingConnectionStatus.NeedsReconnect, dto.Status);
        Assert.False(dto.Connected);
        await Assert.ThrowsAsync<AccountingNotConnectedException>(() => tokens.GetAsync(AccountingTarget.Xero));
    }

    [Fact]
    public async Task A_401_renews_the_token_and_retries_the_call_once()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        var tokens = live.Kit.Get<AccountingTokenService>();
        var api = new AccountingApi(AccountingTarget.QuickBooks, live.Kit.Get<IAccountingHttp>(), tokens, live.Delay);
        live.Provider.On(c => c.Url.Contains("/query"), c => c.Bearer == "access-1" ? FakeProvider.Json(HttpStatusCode.Unauthorized, """{"fault":{"error":[{"message":"AuthenticationFailed"}]}}""") : FakeProvider.Json(HttpStatusCode.OK, """{"QueryResponse":{}}"""));

        var response = await api.SendAsync(s => new HttpRequestMessage(HttpMethod.Get, $"{LiveKit.Qbo}/v3/company/{s.CompanyId}/query?query=x"), default);

        Assert.Equal(200, response.Status);
        var calls = live.Provider.To("/query").ToList();
        Assert.Equal(["access-1", "access-2"], calls.Select(c => c.Bearer));
        Assert.Equal(2, live.Provider.To("oauth2/v1/tokens/bearer").Count());
    }

    [Fact]
    public async Task A_401_that_survives_a_renewal_means_the_connection_is_gone()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        var api = new AccountingApi(AccountingTarget.QuickBooks, live.Kit.Get<IAccountingHttp>(), live.Kit.Get<AccountingTokenService>(), live.Delay);
        live.Provider.On(c => c.Url.Contains("/query"), _ => FakeProvider.Json(HttpStatusCode.Unauthorized, "{}"));

        await Assert.ThrowsAsync<AccountingNotConnectedException>(() => api.SendAsync(s => new HttpRequestMessage(HttpMethod.Get, $"{LiveKit.Qbo}/v3/company/{s.CompanyId}/query?query=x"), default));
        Assert.Equal(2, live.Provider.To("/query").Count());
    }

    [Fact]
    public async Task A_429_waits_for_Retry_After_and_tries_again_then_gives_up_after_the_limit()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        var api = new AccountingApi(AccountingTarget.QuickBooks, live.Kit.Get<IAccountingHttp>(), live.Kit.Get<AccountingTokenService>(), live.Delay);
        var served = 0;
        live.Provider.On(c => c.Url.Contains("/query"), _ => ++served <= 2 ? FakeProvider.Json((HttpStatusCode)429, "{}", ("Retry-After", "7")) : FakeProvider.Json(HttpStatusCode.OK, "{}"));

        var response = await api.SendAsync(s => new HttpRequestMessage(HttpMethod.Get, $"{LiveKit.Qbo}/v3/company/{s.CompanyId}/query?query=x"), default);

        Assert.Equal(200, response.Status);
        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)], live.Delay.Waits);

        live.Delay.Waits.Clear();
        live.Provider.On(c => c.Url.Contains("/query"), _ => FakeProvider.Json((HttpStatusCode)429, "{}"));
        var ex = await Assert.ThrowsAsync<AccountingApiException>(() => api.SendAsync(s => new HttpRequestMessage(HttpMethod.Get, $"{LiveKit.Qbo}/v3/company/{s.CompanyId}/query?query=x"), default));
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(AccountingApi.MaxRetries, live.Delay.Waits.Count);
        Assert.All(live.Delay.Waits, w => Assert.Equal(TimeSpan.FromSeconds(60), w)); // no Retry-After: Intuit's advice, a minute
    }

    [Fact]
    public async Task A_server_error_backs_off_exponentially_and_then_succeeds()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        var api = new AccountingApi(AccountingTarget.QuickBooks, live.Kit.Get<IAccountingHttp>(), live.Kit.Get<AccountingTokenService>(), live.Delay);
        var served = 0;
        live.Provider.On(c => c.Url.Contains("/query"), _ => ++served <= 2 ? FakeProvider.Json(HttpStatusCode.ServiceUnavailable, "{}") : FakeProvider.Json(HttpStatusCode.OK, "{}"));

        var response = await api.SendAsync(s => new HttpRequestMessage(HttpMethod.Get, $"{LiveKit.Qbo}/v3/company/{s.CompanyId}/query?query=x"), default);

        Assert.Equal(200, response.Status);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], live.Delay.Waits);
    }

    [Fact]
    public async Task Connecting_needs_the_export_permission()
    {
        await using var live = await LiveKit.CreateAsync();
        live.Kit.As("viewer@workbench.local");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => live.Connections.ListAsync());
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => live.Connections.StartAsync(AccountingTarget.Xero));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => live.Push.ListRunsAsync());
    }
}
