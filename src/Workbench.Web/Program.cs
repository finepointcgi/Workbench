using MudBlazor.Services;
using Workbench.Api;
using Workbench.Application;
using Workbench.Costpoint;
using Workbench.Infrastructure;
using Workbench.Infrastructure.Persistence;
using Workbench.Web.Components;
using Workbench.Web.Hubs;
using Workbench.Web.Notifications;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddProblemDetails();

builder.Services.AddWorkbenchApplication();
builder.Services.AddWorkbenchInfrastructure(builder.Configuration);
builder.Services.AddWorkbenchLiveUpdatesHub();
builder.Services.AddWorkbenchNotificationsHub();
builder.Services.AddCostpointSource(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await app.Services.InitializeWorkbenchDatabaseAsync();
}
else
{
    app.UseExceptionHandler();
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapWorkbenchApi();
app.MapWorkbenchLiveUpdatesHub();
app.MapWorkbenchNotificationsHub();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Workbench.Ui.Routes).Assembly);

app.Run();
