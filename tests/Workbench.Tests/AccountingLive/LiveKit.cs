using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.AccountingLive;
using Workbench.Contracts.AccountingExport;
using Workbench.Contracts.AccountingLive;
using Workbench.Domain.AccountingExport;
using Workbench.Tests.Purchasing;
using Microsoft.EntityFrameworkCore;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.AccountingLive;

/// <summary>One request the fake provider received.</summary>
internal sealed record Call(string Method, Uri Uri, string Body, string? Bearer, IReadOnlyDictionary<string, string> Headers)
{
    public string Url => Uri.ToString();

    public string? Query(string name) =>
        Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Where(p => p[0] == name).Select(p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : "")).FirstOrDefault();

    public JsonNode? Json => string.IsNullOrWhiteSpace(Body) || !Body.TrimStart().StartsWith('{') ? null : JsonNode.Parse(Body);
}

/// <summary>
/// Stands in for Intuit and Xero: nothing in these tests leaves the machine. Routes are checked newest first, so a test can override an
/// earlier answer; a call no route wants gets a 404 and is still recorded.
/// </summary>
internal sealed class FakeProvider : HttpMessageHandler
{
    private readonly List<(Func<Call, bool> When, Func<Call, HttpResponseMessage> Reply)> _routes = [];
    private readonly object _gate = new();

    public List<Call> Calls { get; } = [];

    public void On(Func<Call, bool> when, Func<Call, HttpResponseMessage> reply)
    {
        lock (_gate)
        {
            _routes.Insert(0, (when, reply));
        }
    }

    public IEnumerable<Call> To(string fragment) => Calls.Where(c => c.Url.Contains(fragment, StringComparison.Ordinal));

    public static HttpResponseMessage Json(HttpStatusCode status, string json, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.Select(h => (h.Key, Value: string.Join(",", h.Value))).ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
        var call = new Call(request.Method.Method, request.RequestUri!, body, request.Headers.Authorization?.Scheme == "Bearer" ? request.Headers.Authorization.Parameter : null, headers);
        Func<Call, HttpResponseMessage>? reply;
        lock (_gate)
        {
            Calls.Add(call);
            reply = _routes.FirstOrDefault(r => r.When(call)).Reply;
        }

        return reply?.Invoke(call) ?? Json(HttpStatusCode.NotFound, "{}");
    }
}

internal sealed class FakeAccountingHttp(FakeProvider handler) : IAccountingHttp
{
    public HttpClient Client { get; } = new(handler);
}

internal sealed class RecordingDelay : IAccountingDelay
{
    public List<TimeSpan> Waits { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Waits.Add(delay);
        return Task.CompletedTask;
    }
}

/// <summary>A seeded host with both providers configured (fake credentials) and the fake HTTP and delay registered.</summary>
internal sealed class LiveKit : IAsyncDisposable
{
    public const string Qbo = "https://sandbox-quickbooks.api.intuit.com";
    public const string XeroApi = "https://api.xero.com/api.xro/2.0";

    private LiveKit(PurchasingKit kit, FakeProvider provider, RecordingDelay delay)
    {
        Kit = kit;
        Provider = provider;
        Delay = delay;
    }

    public PurchasingKit Kit { get; }
    public FakeProvider Provider { get; }
    public RecordingDelay Delay { get; }

    public IAccountingConnectionService Connections => Kit.Get<IAccountingConnectionService>();
    public IAccountingPushService Push => Kit.Get<IAccountingPushService>();
    public IAccountingMappingService Mappings => Kit.Get<IAccountingMappingService>();

    public static async Task<LiveKit> CreateAsync(bool xeroPostAsDraft = true)
    {
        var provider = new FakeProvider();
        var delay = new RecordingDelay();
        var kit = await PurchasingKit.CreateAsync(services =>
        {
            services.AddSingleton<IAccountingHttp>(new FakeAccountingHttp(provider));
            services.AddSingleton<IAccountingDelay>(delay);
            services.Configure<AccountingProviderOptions>(o =>
            {
                o.QuickBooks = new ProviderApp { ClientId = "qb-client", ClientSecret = "qb-secret", RedirectUri = "https://wb.test/admin/integrations/accounting-live/callback/quickbooks", Sandbox = true };
                o.Xero = new ProviderApp { ClientId = "xero-client", ClientSecret = "xero-secret", RedirectUri = "https://wb.test/admin/integrations/accounting-live/callback/xero", PostAsDraft = xeroPostAsDraft };
            });
        });
        kit.As(Finance);
        return new LiveKit(kit, provider, delay);
    }

    public ValueTask DisposeAsync() => Kit.DisposeAsync();

    // ---- Provider scripts ----

    public static string TokenJson(string access, string refresh, int expiresIn = 3600) =>
        new JsonObject { ["token_type"] = "bearer", ["expires_in"] = expiresIn, ["access_token"] = access, ["refresh_token"] = refresh, ["x_refresh_token_expires_in"] = 8640000 }.ToJsonString();

    /// <summary>The token endpoints answer with a numbered pair each time, so a test can see which token was used when.</summary>
    public void TokenEndpoints()
    {
        var n = 0;
        Provider.On(c => c.Url.Contains("/oauth2/v1/tokens/bearer") || c.Url.Contains("identity.xero.com/connect/token"),
            _ => FakeProvider.Json(HttpStatusCode.OK, TokenJson($"access-{++n}", $"refresh-{n}")));
        Provider.On(c => c.Url.Contains("/oauth2/tokens/revoke") || c.Url.Contains("/connect/revocation"), _ => FakeProvider.Json(HttpStatusCode.OK, "{}"));
    }

    public static string StateOf(AccountingAuthorizeStart start) => new Uri(start.Url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).First(p => p[0] == "state")[1];

    public async Task<AccountingConnectionDto> ConnectQuickBooksAsync()
    {
        TokenEndpoints();
        var start = await Connections.StartAsync(AccountingTarget.QuickBooks);
        return await Connections.CompleteAsync(new AccountingOAuthCallback(AccountingTarget.QuickBooks, "auth-code", Uri.UnescapeDataString(StateOf(start)), "realm-42", null));
    }

    public async Task<AccountingConnectionDto> ConnectXeroAsync(params (string Id, string Name)[] orgs)
    {
        TokenEndpoints();
        var tenants = new JsonArray(orgs.Select(o => (JsonNode)new JsonObject { ["tenantId"] = o.Id, ["tenantType"] = "ORGANISATION", ["tenantName"] = o.Name }).ToArray());
        Provider.On(c => c.Url == "https://api.xero.com/connections", _ => FakeProvider.Json(HttpStatusCode.OK, tenants.ToJsonString()));
        var start = await Connections.StartAsync(AccountingTarget.Xero);
        return await Connections.CompleteAsync(new AccountingOAuthCallback(AccountingTarget.Xero, "auth-code", Uri.UnescapeDataString(StateOf(start)), null, null));
    }

    /// <summary>Saves a mapping for every account, customer, vendor, item and project, with the Workbench name as the destination name (and the account number as the Xero code).</summary>
    public async Task<IReadOnlyList<AccountingMappingRowDto>> MapEverythingAsync(AccountingTarget target)
    {
        var set = await Mappings.GetAsync(target);
        var rows = set.Rows.Where(r => r.Kind is AccountingMapKind.Account or AccountingMapKind.Customer or AccountingMapKind.Vendor or AccountingMapKind.Item or AccountingMapKind.Class).ToList();
        await Mappings.SaveAsync(target, new SaveAccountingMappingsRequest(rows.Select(r => new AccountingMappingEntry(r.Kind, r.SourceKey, r.SourceName, r.Kind == AccountingMapKind.Account ? r.SourceKey : r.Kind == AccountingMapKind.Class ? "Region" : null)).ToList()));
        return rows;
    }

    public async Task<AccountingConnection> ConnectionRowAsync(AccountingTarget target)
    {
        await using var db = await Kit.Factory.CreateAsync();
        return await db.Set<AccountingConnection>().AsNoTracking().FirstAsync(c => c.Target == target.ToString());
    }

    public async Task ExpireAccessTokenAsync(AccountingTarget target)
    {
        await using var db = await Kit.Factory.CreateAsync();
        var row = await db.Set<AccountingConnection>().FirstAsync(c => c.Target == target.ToString());
        row.AccessExpiresUtc = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();
    }
}
