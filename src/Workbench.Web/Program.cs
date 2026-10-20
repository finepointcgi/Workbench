using MudBlazor.Services;
using Workbench.Api;
using Workbench.Application;
using Workbench.Costpoint;
using Workbench.Infrastructure;
using Workbench.Infrastructure.Persistence;
using Workbench.Web.Components;
using Workbench.Web.Diagnostics;
using Workbench.Web.Hubs;
using Workbench.Web.Identity;
using Workbench.Web.Notifications;
using Workbench.Web.Security;
using Workbench.Web.Supplier;
using Workbench.Web.Tenancy;

// `Workbench.Web --health-check`: the container HEALTHCHECK probe (GET /health/live), without starting a host.
if (args.Contains("--health-check", StringComparer.OrdinalIgnoreCase))
{
    return await Observability.RunContainerHealthCheckAsync(args);
}

// `Workbench.Web --migrate` applies pending migrations (the catalog and every tenant in MultiTenant mode) and exits:
// the production deploy step. Normal startup only migrates when Database:MigrateOnStartup is true (Development default).
// `--rewrap-data-protection-keys` re-encrypts the stored key ring with the configured key encryption and exits (after
// rotating the certificate or vault key; docs/operations/data-protection-keys.md).
// `--reset-demo` deletes the public demo's data and exits; the next start seeds it afresh (docs/demo-hosting.md).
var migrateOnly = args.Contains("--migrate", StringComparer.OrdinalIgnoreCase);
var rewrapKeys = args.Contains("--rewrap-data-protection-keys", StringComparer.OrdinalIgnoreCase);
var resetDemo = args.Contains("--reset-demo", StringComparer.OrdinalIgnoreCase);
string[] commands = ["--migrate", "--rewrap-data-protection-keys", "--reset-demo"];
args = args.Where(a => !commands.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();

var builder = WebApplication.CreateBuilder(args);
builder.AddWorkbenchHosting();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddProblemDetails();

builder.Services.AddWorkbenchApplication();
builder.Services.AddWorkbenchInfrastructure(builder.Configuration);

// Authentication registers ICurrentUser, which the notifications/automation hosting decorates, so it comes first.
builder.AddWorkbenchAuthentication();
builder.AddWorkbenchSupplierPortal(); // The supplier portal's own sign-in (cookie scheme SupplierPortal, /supplier only).
builder.AddWorkbenchSecurity();
builder.AddWorkbenchDemoReset();
builder.AddWorkbenchTenancyHosting();
builder.Services.AddWorkbenchLiveUpdatesHub();
builder.Services.AddWorkbenchNotificationsHub();
builder.Services.AddCostpointSource(builder.Configuration);

var app = builder.Build();
if (migrateOnly)
{
    await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
    return 0;
}

if (rewrapKeys)
{
    var count = await DataProtectionKeyRewrap.RunAsync(app.Services);
    app.Logger.LogInformation("Re-encrypted {Count} Data Protection keys. Restart every web host.", count);
    return 0;
}

if (resetDemo)
{
    await DemoReset.DeleteDemoDataAsync(app.Services);
    app.Logger.LogInformation("Deleted the demo data. Restart the web host to seed it again.");
    return 0;
}

await app.Services.InitializeWorkbenchDatabaseAsync();
app.UseWorkbenchServerTiming();
app.UseWorkbenchSecurityHeaders();
app.UseWorkbenchTenancy();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}

// API and hub callers get bare status codes (401, 403, 404), not the not-found page.
app.UseWhen(
    context => !AuthenticationSetup.IsApiRequest(context.Request),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseRateLimiter();
app.UseWorkbenchDenialLogging();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
// Browsers ask for /favicon.ico on their own; without this it would fall through to a sign-in redirect.
app.MapGet("/favicon.ico", () => Results.Redirect("/favicon.png", permanent: true)).AllowAnonymous().ExcludeFromDescription();
app.MapWorkbenchAccountEndpoints();
app.MapWorkbenchHealthChecks();
app.MapWorkbenchScim();
app.MapWorkbenchApi();
app.MapWorkbenchSupplierPortal();
app.MapWorkbenchLiveUpdatesHub();
app.MapWorkbenchNotificationsHub();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Workbench.Ui.Routes).Assembly);

app.Run();
return 0;

/// <summary>Visible to the integration tests (WebApplicationFactory).</summary>
public partial class Program;
