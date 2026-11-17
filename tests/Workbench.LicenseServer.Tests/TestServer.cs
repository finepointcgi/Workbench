using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Workbench.LicenseServer.Mail;
using Workbench.LicenseServer.Stripe;

namespace Workbench.LicenseServer.Tests;

public sealed class CapturingMailer : IMailer
{
    public List<(string To, string Subject, string Body)> Sent { get; } = [];
    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default) { lock (Sent) Sent.Add((to, subject, body)); return Task.CompletedTask; }
}

public sealed class FakeStripeApi : IStripeApi
{
    public Dictionary<string, StripeSubscription> Subscriptions { get; } = [];
    public Dictionary<string, string> Emails { get; } = [];
    public Task<StripeSubscription?> GetSubscriptionAsync(string id, CancellationToken ct = default) => Task.FromResult(Subscriptions.GetValueOrDefault(id));
    public Task<string?> GetCustomerEmailAsync(string id, CancellationToken ct = default) => Task.FromResult(Emails.GetValueOrDefault(id));
    public Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken ct = default) =>
        Task.FromResult($"https://billing.stripe.test/session/{customerId}?return={Uri.EscapeDataString(returnUrl)}");
}

/// <summary>
/// The server on a throwaway database: SQLite by default, PostgreSQL when WORKBENCH_TEST_DB=PostgreSql (CI sets it, with WORKBENCH_TEST_POSTGRES),
/// the same switch the main suite uses. A fresh key pair, a capturing mailer and a fake Stripe API.
/// </summary>
public sealed class TestServer : WebApplicationFactory<Program>
{
    public const string AdminKey = "test-admin-key-0123456789abcdef";
    public const string WebhookSecret = "whsec_test_secret";
    public const string SeatPrice = "price_seat";
    public const string LightPrice = "price_light";
    public const string FinancePrice = "price_finance";
    public const string BundlePrice = "price_govcon_bundle";

    private readonly string? _sqlitePath;
    private readonly string? _pgAdmin, _pgDb;
    private readonly string _privatePem;
    private readonly Dictionary<string, string?> _extra;

    public string PublicKeyPem { get; }
    public CapturingMailer Mail { get; } = new();
    public FakeStripeApi Stripe { get; } = new();

    public TestServer(Dictionary<string, string?>? extra = null)
    {
        _extra = extra ?? [];
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _privatePem = key.ExportPkcs8PrivateKeyPem();
        PublicKeyPem = key.ExportSubjectPublicKeyInfoPem();
        if (string.Equals(Environment.GetEnvironmentVariable("WORKBENCH_TEST_DB"), "PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            _pgAdmin = Environment.GetEnvironmentVariable("WORKBENCH_TEST_POSTGRES") ?? "Host=localhost;Port=5432;Username=workbench;Password=workbench";
            _pgDb = "lstest_" + Guid.NewGuid().ToString("N");
            using var c = new NpgsqlConnection(_pgAdmin);
            c.Open();
            using var cmd = new NpgsqlCommand($"CREATE DATABASE {_pgDb}", c);
            cmd.ExecuteNonQuery();
        }
        else
        {
            _sqlitePath = Path.Combine(Path.GetTempPath(), $"lstest_{Guid.NewGuid():N}.db");
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        var settings = new Dictionary<string, string?>
        {
            ["Admin:ApiKey"] = AdminKey,
            ["Signing:PrivateKeyPem"] = _privatePem,
            ["Stripe:WebhookSecret"] = WebhookSecret,
            [$"Stripe:Prices:{SeatPrice}:Role"] = "Seats",
            [$"Stripe:Prices:{LightPrice}:Role"] = "LightSeats",
            [$"Stripe:Prices:{FinancePrice}:Role"] = "Modules",
            [$"Stripe:Prices:{FinancePrice}:Modules:0"] = "finance",
            [$"Stripe:Prices:{BundlePrice}:Role"] = "Modules",
            [$"Stripe:Prices:{BundlePrice}:Modules:0"] = "govcon",
            [$"Stripe:Prices:{BundlePrice}:Modules:1"] = "controls",
            [$"Stripe:Prices:{BundlePrice}:IncludedSeats"] = "5",
            ["Portal:PublicBaseUrl"] = "https://license.test",
            ["Database:Provider"] = _pgDb != null ? "PostgreSql" : "Sqlite",
            ["Database:ConnectionString"] = _pgDb != null ? new NpgsqlConnectionStringBuilder(_pgAdmin) { Database = _pgDb }.ConnectionString : $"Data Source={_sqlitePath}",
            ["RateLimits:ConnectedPerMinute"] = "1000",
            ["RateLimits:LoginPerMinute"] = "1000",
        };
        foreach (var kv in _extra) settings[kv.Key] = kv.Value;
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings));
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IMailer>();
            s.AddSingleton<IMailer>(Mail);
            s.RemoveAll<IStripeApi>();
            s.AddSingleton<IStripeApi>(Stripe);
        });
    }

    public HttpClient Admin()
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
        return c;
    }

    /// <summary>A client that keeps cookies off and doesn't follow redirects, for the portal.</summary>
    public HttpClient Plain() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    public async Task<JsonElement> PostAsync(HttpClient c, string url, object body)
    {
        var r = await c.PostAsJsonAsync(url, body);
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{url} -> {(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<(Guid CustomerId, Guid SubscriptionId, Guid LicenseId, string Key)> NewSubscriptionAsync(string[] modules, int seats = 10, int light = 0, int months = 12, string name = "Acme Defense", string email = "ops@acme.test")
    {
        var admin = Admin();
        var customer = await PostAsync(admin, "/admin/api/customers", new { name, email });
        var customerId = customer.GetProperty("id").GetGuid();
        var sub = await PostAsync(admin, $"/admin/api/customers/{customerId}/subscriptions", new { modules, seats, lightSeats = light, termMonths = months });
        return (customerId, sub.GetProperty("subscription").GetProperty("id").GetGuid(), sub.GetProperty("licenseId").GetGuid(), sub.GetProperty("key").GetString()!);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try
        {
            if (_sqlitePath != null) { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_sqlitePath)!, Path.GetFileName(_sqlitePath) + "*")) File.Delete(f); }
            if (_pgDb != null)
            {
                NpgsqlConnection.ClearAllPools();
                using var c = new NpgsqlConnection(_pgAdmin);
                c.Open();
                using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_pgDb} WITH (FORCE)", c);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception) { /* a leftover temp database is harmless */ }
    }
}
