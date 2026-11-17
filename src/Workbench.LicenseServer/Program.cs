using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.LicenseServer;
using Workbench.LicenseServer.Data;
using Workbench.LicenseServer.Endpoints;
using Workbench.LicenseServer.Licensing;
using Workbench.LicenseServer.Mail;
using Workbench.LicenseServer.Stripe;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var services = builder.Services;

services.Configure<AdminOptions>(config.GetSection(AdminOptions.SectionName));
services.Configure<SigningOptions>(config.GetSection(SigningOptions.SectionName));
services.Configure<KeyOptions>(config.GetSection(KeyOptions.SectionName));
services.Configure<LimitOptions>(config.GetSection(LimitOptions.SectionName));
services.Configure<PortalOptions>(config.GetSection(PortalOptions.SectionName));
services.Configure<StripeOptions>(config.GetSection(StripeOptions.SectionName));
services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.SectionName));
services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// One database, two providers (each with its own migrations). Services depend on LicenseDb. The provider is read from the options
// when a context is built, not here, so test hosts and late configuration sources are honored.
services.AddDbContext<PostgresLicenseDb>((sp, o) => o.UseNpgsql(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));
services.AddDbContext<SqliteLicenseDb>((sp, o) => o.UseSqlite(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));
services.AddScoped<LicenseDb>(sp => sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase)
    ? sp.GetRequiredService<PostgresLicenseDb>()
    : sp.GetRequiredService<SqliteLicenseDb>());

services.AddSingleton(TimeProvider.System);
services.AddSingleton<ILicenseSigner, LicenseSigner>();
services.AddScoped<LicenseService>();
services.AddScoped<StripeWebhookHandler>();
services.AddSingleton<IMailer, LogMailer>();
services.AddHttpClient<IStripeApi, HttpStripeApi>();

var dp = services.AddDataProtection().SetApplicationName("Workbench.LicenseServer");
if (config["Portal:DataProtectionPath"] is { Length: > 0 } dpPath) dp.PersistKeysToFileSystem(new DirectoryInfo(dpPath));

// The server may sit behind a reverse proxy; trust X-Forwarded-* only from the proxies you list (or all, when run in a private network).
services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    if (config.GetValue<bool>("ForwardedHeaders:TrustAll")) { o.KnownIPNetworks.Clear(); o.KnownProxies.Clear(); }
});

services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    o.AddPolicy("connected", c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new FixedWindowRateLimiterOptions
        { PermitLimit = c.RequestServices.GetRequiredService<IOptions<LimitOptions>>().Value.ConnectedPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new FixedWindowRateLimiterOptions
        { PermitLimit = c.RequestServices.GetRequiredService<IOptions<LimitOptions>>().Value.LoginPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// Fail fast on a missing or malformed signing key rather than at the first customer.
app.Services.GetRequiredService<ILicenseSigner>();

var db = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
if (db.MigrateOnStartup)
{
    using var scope = app.Services.CreateScope();
    if (!db.Provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
    {
        var path = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(db.ConnectionString).DataSource;
        if (!string.IsNullOrEmpty(path) && path != ":memory:" && Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir) Directory.CreateDirectory(dir);
    }
    await scope.ServiceProvider.GetRequiredService<LicenseDb>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapAdminEndpoints();
app.MapConnectedEndpoints();
app.MapStripeEndpoints();
app.MapPortalEndpoints();
app.MapGet("/", () => Results.Redirect("/portal"));

app.Run();

/// <summary>Lets the test project host the server.</summary>
public partial class Program;
