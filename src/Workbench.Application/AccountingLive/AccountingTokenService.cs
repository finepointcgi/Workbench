using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Contracts.AccountingExport;
using Workbench.Contracts.AccountingLive;
using Workbench.Domain.AccountingExport;

namespace Workbench.Application.AccountingLive;

/// <summary>What a call needs: a token that is good now, and which company (QuickBooks realm, Xero tenant) it is for.</summary>
public sealed record ProviderSession(AccountingTarget Target, string AccessToken, string CompanyId, bool IsSandbox);

/// <summary>
/// Hands out a valid access token and renews it when it is about to lapse. Intuit and Xero both rotate the refresh token on every
/// refresh, so the new one is written to the connection row immediately, in one save guarded by the row's version: a second
/// request that refreshed at the same moment loses the race, re-reads the winner's tokens and uses them instead of overwriting them.
/// A refresh the provider refuses with <c>invalid_grant</c> marks the connection as needing a reconnect.
/// </summary>
public sealed class AccountingTokenService(
    IWorkbenchDbContextFactory dbFactory,
    IEnumerable<IAccountingOAuth> oauths,
    IOptions<AccountingProviderOptions> options,
    AccountingSecrets secrets,
    AccountingTokenLocks locks,
    TimeProvider clock)
{
    /// <summary>Refresh when the access token has less than this left, so a call never starts with a token that dies mid-flight.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private IAccountingOAuth OAuth(AccountingTarget target) => oauths.FirstOrDefault(o => o.Target == target) ?? throw new ArgumentException($"{target} can't be connected.");

    /// <param name="rejectedAccessToken">The token a call just got a 401 for. When the stored token is already a different one, someone refreshed meanwhile and no second refresh is made.</param>
    public async Task<ProviderSession> GetAsync(AccountingTarget target, string? rejectedAccessToken = null, CancellationToken ct = default)
    {
        var gate = locks.For(target);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var row = await db.Set<AccountingConnection>().FirstOrDefaultAsync(c => c.Target == target.ToString(), ct)
                ?? throw new AccountingNotConnectedException($"{OAuth(target).Name} isn't connected. Connect it under Admin › Integrations › Accounting export.");
            Ensure(row, target);
            var current = secrets.Unprotect(row.AccessTokenProtected!);
            var stillGood = row.AccessExpiresUtc - Now > Margin && (rejectedAccessToken is null || !string.Equals(current, rejectedAccessToken, StringComparison.Ordinal));
            if (stillGood)
            {
                return new ProviderSession(target, current, row.CompanyId!, row.IsSandbox);
            }

            return await RefreshAsync(db, row, target, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void Ensure(AccountingConnection row, AccountingTarget target)
    {
        if (row.Status == AccountingConnectionStatus.NeedsReconnect || string.IsNullOrEmpty(row.RefreshTokenProtected) || string.IsNullOrEmpty(row.AccessTokenProtected))
        {
            throw new AccountingNotConnectedException($"The {target} connection needs to be made again: {row.LastError ?? "the authorisation is no longer valid"}. Connect it under Admin › Integrations › Accounting export.");
        }

        if (row.Status == AccountingConnectionStatus.ChooseOrganisation || string.IsNullOrEmpty(row.CompanyId))
        {
            throw new AccountingNotConnectedException($"Choose which {target} organisation Workbench should post to, under Admin › Integrations › Accounting export.");
        }
    }

    private async Task<ProviderSession> RefreshAsync(IWorkbenchDbContext db, AccountingConnection row, AccountingTarget target, CancellationToken ct)
    {
        var app = options.Value.For(target);
        if (!app.IsConfigured)
        {
            throw new AccountingNotConnectedException($"{OAuth(target).Name} isn't set up on this server (Accounting:{target}:ClientId, ClientSecret and RedirectUri).");
        }

        var oldRefresh = secrets.Unprotect(row.RefreshTokenProtected!);
        TokenSet tokens;
        try
        {
            tokens = await OAuth(target).RefreshAsync(app, oldRefresh, ct);
        }
        catch (AccountingOAuthException ex) when (ex.IsInvalidGrant)
        {
            row.Status = AccountingConnectionStatus.NeedsReconnect;
            row.LastError = Clip(ex.Message, 1000);
            row.TokenVersion++;
            await TrySaveAsync(db, ct);
            throw new AccountingNotConnectedException($"{OAuth(target).Name}: {ex.Message}");
        }

        var now = Now;
        row.AccessTokenProtected = secrets.Protect(tokens.AccessToken);
        row.RefreshTokenProtected = secrets.Protect(tokens.RefreshToken);
        row.AccessExpiresUtc = now.AddSeconds(tokens.AccessExpiresInSeconds);
        row.RefreshExpiresUtc = now.AddSeconds(tokens.RefreshExpiresInSeconds ?? (target == AccountingTarget.Xero ? 60 * 86400 : 100 * 86400));
        row.LastRefreshedUtc = now;
        row.LastError = null;
        row.TokenVersion++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another node refreshed first and saved its rotated tokens; use those and drop ours (Xero keeps the previous refresh token valid for 30 minutes).
            await using var fresh = await dbFactory.CreateAsync(ct);
            var winner = await fresh.Set<AccountingConnection>().AsNoTracking().FirstAsync(c => c.Target == target.ToString(), ct);
            Ensure(winner, target);
            return new ProviderSession(target, secrets.Unprotect(winner.AccessTokenProtected!), winner.CompanyId!, winner.IsSandbox);
        }

        return new ProviderSession(target, tokens.AccessToken, row.CompanyId!, row.IsSandbox);
    }

    private static async Task TrySaveAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else already updated the connection; their state wins.
        }
    }

    private static string Clip(string text, int max) => text.Length > max ? text[..max] : text;
}

/// <summary>Sends a call to a provider with the current token, renews the token and retries once on 401, and backs off on 429 and 5xx.</summary>
public sealed class AccountingApi(AccountingTarget target, IAccountingHttp http, AccountingTokenService tokens, IAccountingDelay delay)
{
    public const int MaxRetries = 4;
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);

    public sealed record Response(int Status, string Body);

    public ProviderSession? Session { get; private set; }

    /// <summary>
    /// Returns the response for any status except the ones that are retried: 401 (token renewed once, then <see cref="AccountingNotConnectedException"/>),
    /// 429 (waits Retry-After, or 60 s when absent; Intuit advises a minute) and 5xx or no answer (2, 4, 8, 16 s). After <see cref="MaxRetries"/> it throws
    /// <see cref="AccountingApiException"/>. Other 4xx answers are returned for the caller to read the provider's validation messages.
    /// </summary>
    public async Task<Response> SendAsync(Func<ProviderSession, HttpRequestMessage> build, CancellationToken ct)
    {
        var session = Session ??= await tokens.GetAsync(target, null, ct);
        var refreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            string body = "";
            try
            {
                using var request = build(session);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
                request.Headers.Accept.ParseAdd("application/json");
                response = await http.Client.SendAsync(request, ct);
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= MaxRetries)
                {
                    throw new AccountingApiException(null, $"Couldn't reach {target}: {ex.Message}");
                }

                await delay.DelayAsync(Backoff(attempt), ct);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= MaxRetries)
                {
                    throw new AccountingApiException(null, $"{target} didn't answer in time.");
                }

                await delay.DelayAsync(Backoff(attempt), ct);
                continue;
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status == 401)
                {
                    if (refreshed)
                    {
                        throw new AccountingNotConnectedException($"{target} rejected the connection even after renewing it. Connect it again.");
                    }

                    refreshed = true;
                    session = Session = await tokens.GetAsync(target, session.AccessToken, ct);
                    attempt--; // the renewal doesn't use up a retry
                    continue;
                }

                if (status == 429 || status >= 500)
                {
                    var wait = status == 429 ? RetryAfter(response) ?? TimeSpan.FromSeconds(60) : Backoff(attempt);
                    if (attempt >= MaxRetries)
                    {
                        throw new AccountingApiException(status, status == 429 ? $"{target} is rate limiting Workbench; try again in a few minutes." : $"{target} had a server error ({status}).", body, wait);
                    }

                    await delay.DelayAsync(wait > MaxWait ? MaxWait : wait, ct);
                    continue;
                }

                return new Response(status, body);
            }
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } delta)
            {
                return delta;
            }

            if (ra.Date is { } date)
            {
                var d = date - DateTimeOffset.UtcNow;
                return d > TimeSpan.Zero ? d : TimeSpan.FromSeconds(1);
            }
        }

        return null;
    }
}
