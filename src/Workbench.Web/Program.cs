using MudBlazor.Services;
using Workbench.Api;
using Workbench.Application;
using Workbench.Costpoint;
using Workbench.Infrastructure;
using Workbench.Infrastructure.Persistence;
using Workbench.Web.Calendar;
using Workbench.Web.Components;
using Workbench.Web.Diagnostics;
using Workbench.Web.Hubs;
using Workbench.Web.Identity;
using Workbench.Web.Integrations;
using Workbench.Web.Notifications;
using Workbench.Web.Security;
using Workbench.Web.Supplier;
using Workbench.Web.Forms;
using Workbench.Web.Customer;
using Workbench.Web.Tenancy;
using Workbench.Web.Storage;

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
// `--copy-blobs [--apply]` copies files from the local folder (Storage:Root) into the configured bucket, checksums
// verified; without --apply it's a dry run (docs/operations/object-storage.md).
var migrateOnly = args.Contains("--migrate", StringComparer.OrdinalIgnoreCase);
var rewrapKeys = args.Contains("--rewrap-data-protection-keys", StringComparer.OrdinalIgnoreCase);
var resetDemo = args.Contains("--reset-demo", StringComparer.OrdinalIgnoreCase);
var copyBlobs = args.Contains("--copy-blobs", StringComparer.OrdinalIgnoreCase);
var copyBlobsApply = copyBlobs && args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
string[] commands = ["--migrate", "--rewrap-data-protection-keys", "--reset-demo", "--copy-blobs", "--apply"];
args = args.Where(a => !commands.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();

// Dates, numbers and money read the US way everywhere. A container has no OS locale, so without this the server formats with the
// invariant culture and every currency written with "C" shows "¤960,000" instead of "$960,000".
Workbench.Web.WorkbenchCulture.Apply();

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

if (copyBlobs)
{
    var report = await BlobCopyCommand.RunAsync(app.Services, copyBlobsApply, Console.Out);
    return report.Applied ? (report.Complete ? 0 : 1) : (report.Conflicts.Count == 0 ? 0 : 1);
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

// API and hub callers, and scripts or images, get bare status codes (401, 403, 404); a person loading a page is sent to the not-found page.
// A redirect, not a re-execute: re-executing left the 404 on the response and Blazor then rendered nothing, a blank white page.
app.UseWhen(
    context => !AuthenticationSetup.IsApiRequest(context.Request) && AuthenticationSetup.IsPageNavigation(context.Request),
    branch => branch.UseStatusCodePagesWithRedirects("/not-found"));
app.UseHttpsRedirection();
app.UseRequestLocalization(new Microsoft.AspNetCore.Builder.RequestLocalizationOptions()
    .SetDefaultCulture("en-US").AddSupportedCultures("en-US").AddSupportedUICultures("en-US")); // the request's culture, which Blazor's circuit takes
app.UseAuthentication();
app.UseRateLimiter();
app.UseWorkbenchDenialLogging();
app.UseWorkbenchApiKeyScopes();
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
app.MapWorkbenchPublicForms(); // Public form links /f/{token} (wave E).
app.MapWorkbenchCustomerView(); // Customer links /c/{token}: one contract's funding and invoice status, read only.
app.MapWorkbenchLiveUpdatesHub();
app.MapWorkbenchNotificationsHub();
app.MapWorkbenchEmailEvents(); // Bounce and complaint webhooks from SendGrid and SES (docs/operations/email.md).
app.MapWorkbenchJiraWebhooks(); // Jira issue webhooks for linked projects (docs/integrations/jira.md).
app.MapWorkbenchGitLabWebhooks(); // GitLab issue, epic and milestone webhooks for linked projects (docs/integrations/gitlab.md).
app.MapWorkbenchCalendarFeed(); // Read-only per-person .ics feeds behind a private token.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Workbench.Ui.Routes).Assembly);

app.Run();
return 0;

/// <summary>Visible to the integration tests (WebApplicationFactory).</summary>
public partial class Program;
