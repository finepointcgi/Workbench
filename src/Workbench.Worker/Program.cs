using Workbench.Costpoint;
using Workbench.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<ICostpointSource, NullCostpointSource>();
builder.Services.AddHostedService<CostpointSyncWorker>();

var host = builder.Build();
host.Run();
