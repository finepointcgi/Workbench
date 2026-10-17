using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Workbench.Costpoint;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the <see cref="ICostpointSource"/> chosen by <c>Costpoint:Source</c>:
    /// <c>None</c> (default), <c>Csv</c> (folder in <c>Costpoint:Csv:Folder</c>), <c>Rest</c> or <c>Database</c> (stubs).
    /// </summary>
    public static IServiceCollection AddCostpointSource(this IServiceCollection services, IConfiguration configuration)
    {
        var source = configuration["Costpoint:Source"] ?? "None";
        switch (source.ToLowerInvariant())
        {
            case "none":
                services.AddSingleton<ICostpointSource, NullCostpointSource>();
                break;
            case "csv":
                var folder = configuration["Costpoint:Csv:Folder"];
                if (string.IsNullOrWhiteSpace(folder))
                {
                    throw new InvalidOperationException("Costpoint:Source is Csv but Costpoint:Csv:Folder is not set.");
                }

                services.AddSingleton<ICostpointSource>(sp => new CsvFolderCostpointSource(
                    CsvFolderCostpointSource.ResolveFolder(folder),
                    sp.GetService<ILogger<CsvFolderCostpointSource>>() ?? NullLogger<CsvFolderCostpointSource>.Instance));
                break;
            case "rest":
                services.AddSingleton<ICostpointSource, RestCostpointSource>();
                break;
            case "database":
                services.AddSingleton<ICostpointSource, DatabaseViewCostpointSource>();
                break;
            default:
                throw new InvalidOperationException($"Unsupported Costpoint:Source '{source}'. Use None, Csv, Rest or Database.");
        }

        return services;
    }
}
