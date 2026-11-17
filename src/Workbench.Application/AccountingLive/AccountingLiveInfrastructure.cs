using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Workbench.Application.Tenancy;
using Workbench.Contracts.AccountingExport;

namespace Workbench.Application.AccountingLive;

/// <summary>
/// Configuration, bound from <c>Accounting</c>. Client ids and secrets come only from configuration (environment variables or a secret
/// store), never from the database or the source tree. Redirect URIs must match, character for character, the ones registered with the provider.
/// </summary>
public sealed class AccountingProviderOptions
{
    public const string Section = "Accounting";

    public ProviderApp QuickBooks { get; set; } = new();
    public ProviderApp Xero { get; set; } = new();

    public ProviderApp For(AccountingTarget target) => target switch
    {
        AccountingTarget.QuickBooks => QuickBooks,
        AccountingTarget.Xero => Xero,
        _ => throw new ArgumentException($"{target} has no live connection."),
    };
}

public sealed class ProviderApp
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>The address the provider sends the user back to: <c>https://your-host/admin/integrations/accounting-live/callback/quickbooks</c> (or <c>/xero</c>).</summary>
    public string? RedirectUri { get; set; }

    /// <summary>QuickBooks: use the sandbox API host and the development keys. Xero has no separate host; use its Demo Company (this only labels the connection).</summary>
    public bool Sandbox { get; set; }

    /// <summary>Overrides the scopes asked for (space separated). Empty uses the defaults for the provider.</summary>
    public string? Scopes { get; set; }

    /// <summary>Xero: send invoices, bills and journals as drafts for the accountant to approve in Xero (the default), or authorised and posted.</summary>
    public bool PostAsDraft { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(RedirectUri);
}

/// <summary>The HTTP client for provider calls. Real in production; tests register a fake handler so nothing leaves the machine.</summary>
public interface IAccountingHttp
{
    HttpClient Client { get; }
}

public sealed class AccountingHttp : IAccountingHttp, IDisposable
{
    public HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(100) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Workbench-Accounting/1.0");
        return http;
    }

    public void Dispose() => Client.Dispose();
}

/// <summary>Waits between retries. Tests register one that records the waits instead of sleeping.</summary>
public interface IAccountingDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class RealAccountingDelay : IAccountingDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

/// <summary>Access and refresh tokens are encrypted at rest (Data Protection, isolated per tenant by the tenant-aware provider).</summary>
public sealed class AccountingSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _tokens = provider.CreateProtector("Workbench.Accounting.Live.Tokens.v1");

    public string Protect(string token) => _tokens.Protect(token);

    public string Unprotect(string protectedToken) => _tokens.Unprotect(protectedToken);

    public static string NewState() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashState(string state) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
}

/// <summary>One lock per target per tenant, so two requests in this process never refresh the same refresh token at once.</summary>
public sealed class AccountingTokenLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public SemaphoreSlim For(AccountingTarget target) => _locks.GetOrAdd(TenantScope.Qualify($"accounting-oauth:{target}"), _ => new SemaphoreSlim(1, 1));
}

/// <summary>A provider call that failed. <see cref="StatusCode"/> is null when no answer came back.</summary>
public sealed class AccountingApiException(int? statusCode, string message, string? body = null, TimeSpan? retryAfter = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
    public string? Body { get; } = body;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>The connection is missing or must be redone. The message says what to do.</summary>
public sealed class AccountingNotConnectedException(string message) : InvalidOperationException(message);

/// <summary>The provider's token endpoint refused a request. <see cref="IsInvalidGrant"/>: the code or refresh token is no longer good.</summary>
public sealed class AccountingOAuthException(string message, bool isInvalidGrant, int? statusCode = null) : Exception(message)
{
    public bool IsInvalidGrant { get; } = isInvalidGrant;
    public int? StatusCode { get; } = statusCode;
}
