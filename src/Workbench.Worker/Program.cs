using Workbench.Application;
using Workbench.Costpoint;
using Workbench.Infrastructure;
using Workbench.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWorkbenchApplication();
builder.Services.AddWorkbenchInfrastructure(builder.Configuration);
builder.Services.AddCostpointSource(builder.Configuration);
builder.Services.AddHostedService<CostpointSyncWorker>();

var host = builder.Build();
host.Run();
