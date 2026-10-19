using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Workbench.Application;
using Workbench.Contracts.Collaboration;
using Workbench.Infrastructure;
using Workbench.Infrastructure.Persistence;
using Workbench.LoadData;

// Fills a Workbench database with a mid-size government contractor for load testing:
//
//   dotnet run -c Release --project tools/Workbench.LoadData -- --reset true
//
// Options (all optional): --connection "Host=...;Database=workbench_load;..." --reset true (drop and recreate the database)
// --employees 500 --years 5 --today 2026-10-18 --seed 42 --services true (submit last week through the real services).
// The database is migrated first. See docs/operations/performance.md for what it creates and how to run the load tests.
var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Connection"] = "Host=localhost;Port=5444;Database=workbench_load;Username=workbench;Password=workbench;Include Error Detail=true",
        ["Employees"] = "500",
        ["Years"] = "5",
        ["Seed"] = "42",
        ["Reset"] = "false",
        ["Services"] = "true",
    })
    .AddEnvironmentVariables("LOADDATA_")
    .AddCommandLine(args)
    .Build();

var connection = configuration["Connection"]!;
var options = new GeneratorOptions(
    Employees: configuration.GetValue<int>("Employees"),
    Years: configuration.GetValue<int>("Years"),
    Seed: configuration.GetValue<int>("Seed"),
    Today: configuration["Today"] is { Length: > 0 } t ? DateOnly.Parse(t) : DateOnly.FromDateTime(DateTime.UtcNow));

if (configuration.GetValue<bool>("Reset"))
{
    var builder = new NpgsqlConnectionStringBuilder(connection);
    var database = builder.Database!;
    builder.Database = "postgres";
    await using var admin = new NpgsqlConnection(builder.ConnectionString);
    await admin.OpenAsync();
    await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin))
    {
        await drop.ExecuteNonQueryAsync();
    }

    await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin))
    {
        await create.ExecuteNonQueryAsync();
    }

    Console.WriteLine($"Recreated database {database}.");
}

var hostConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Database:Provider"] = "PostgreSQL",
    ["ConnectionStrings:Workbench"] = connection,
    ["Tenancy:Mode"] = "SingleTenant",
    ["Email:Mode"] = "Pickup",
    ["Email:PickupFolder"] = Path.Combine(Path.GetTempPath(), "workbench-loaddata-mail"),
    ["Storage:Root"] = Path.Combine(Path.GetTempPath(), "workbench-loaddata-blobs"),
}).Build();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(hostConfig);
services.AddLogging(l => l.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning).AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None));
var user = new SwitchableUser();
services.AddSingleton<ICurrentUser>(user);
services.AddWorkbenchApplication();
services.AddWorkbenchInfrastructure(hostConfig);
services.Replace(ServiceDescriptor.Singleton<ICurrentUser>(user));
await using var provider = services.BuildServiceProvider();

var clock = Stopwatch.StartNew();
await provider.GetRequiredService<DatabaseMigrator>().MigrateAsync();
Console.WriteLine($"Migrated in {clock.Elapsed.TotalSeconds:0.0}s.");

var factory = provider.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>();
await using (var check = await factory.CreateDbContextAsync())
{
    if (await check.Set<Workbench.Domain.ProjectAccounting.Employee>().AnyAsync())
    {
        Console.Error.WriteLine("The database already has employees. Pass --reset true to start over.");
        return 1;
    }
}

var generator = new LoadGenerator(options, factory, provider, user);
await generator.RunAsync(includeServices: configuration.GetValue<bool>("Services"));
Console.WriteLine($"Done in {clock.Elapsed.TotalMinutes:0.0} min.");
return 0;

namespace Workbench.LoadData
{
    public sealed record GeneratorOptions(int Employees, int Years, int Seed, DateOnly Today);

    /// <summary>The signed-in user for the real-service phases (each employee submits their own timesheet).</summary>
    public sealed class SwitchableUser : ICurrentUser
    {
        private readonly AsyncLocal<string?> _email = new();

        public string Email
        {
            get => _email.Value ?? "loaddata@workbench.local";
            set => _email.Value = value;
        }
    }
}
