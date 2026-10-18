using MudBlazor.Services;
using Workbench.Api;
using Workbench.Application;
using Workbench.Costpoint;
using Workbench.Infrastructure;
using Workbench.Infrastructure.Persistence;
using Workbench.Web.Components;
using Workbench.Web.Hubs;
using Workbench.Web.Identity;
using Workbench.Web.Notifications;
using Workbench.Web.Security;
using Workbench.Web.Tenancy;

// `Workbench.Web --migrate` applies pending migrations (the catalog and every tenant in MultiTenant mode) and exits:
// the production deploy step. Normal startup only migrates when Database:MigrateOnStartup is true (Development default).
var migrateOnly = args.Contains("--migrate", StringComparer.OrdinalIgnoreCase);
args = args.Where(a => !a.Equals("--migrate", StringComparison.OrdinalIgnoreCase)).ToArray();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddProblemDetails();

builder.Services.AddWorkbenchApplication();
builder.Services.AddWorkbenchInfrastructure(builder.Configuration);

// Authentication registers ICurrentUser, which the notifications/automation hosting decorates, so it comes first.
builder.AddWorkbenchAuthentication();
builder.AddWorkbenchSecurity();
builder.AddWorkbenchTenancyHosting();
builder.Services.AddWorkbenchLiveUpdatesHub();
builder.Services.AddWorkbenchNotificationsHub();
builder.Services.AddCostpointSource(builder.Configuration);

var app = builder.Build();
if (migrateOnly)
{
    await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
    return;
}

await app.Services.InitializeWorkbenchDatabaseAsync();
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
app.MapWorkbenchAccountEndpoints();
app.MapWorkbenchHealthChecks();
app.MapWorkbenchScim();
app.MapWorkbenchApi();
app.MapWorkbenchLiveUpdatesHub();
app.MapWorkbenchNotificationsHub();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Workbench.Ui.Routes).Assembly);

app.Run();

/// <summary>Visible to the integration tests (WebApplicationFactory).</summary>
public partial class Program;
