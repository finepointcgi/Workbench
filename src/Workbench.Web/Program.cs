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
builder.Services.AddWorkbenchLiveUpdatesHub();
builder.Services.AddWorkbenchNotificationsHub();
builder.Services.AddCostpointSource(builder.Configuration);

var app = builder.Build();
app.UseWorkbenchSecurityHeaders();

if (app.Environment.IsDevelopment())
{
    await app.Services.InitializeWorkbenchDatabaseAsync();
}
else
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
