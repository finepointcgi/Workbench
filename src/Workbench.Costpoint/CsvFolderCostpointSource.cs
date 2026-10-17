using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Workbench.Costpoint;

/// <summary>
/// File-exchange adapter (<c>Costpoint:Source = Csv</c>): reads the CSV exports described in README.md from
/// a folder, typically dropped there by a scheduled Costpoint export over SFTP. Files are re-read on every call.
/// A missing file fails the call (so a half-delivered export never empties the mirror); bad rows are skipped,
/// logged and reported through <see cref="ICostpointSourceDiagnostics"/>.
/// </summary>
public sealed class CsvFolderCostpointSource(string folder, ILogger<CsvFolderCostpointSource> logger)
    : ICostpointSource, ICostpointSourceDiagnostics
{
    private readonly ConcurrentQueue<string> _warnings = new();

    public string Name => "Csv";

    public string Folder => folder;

    public Task<IReadOnlyList<CpProjectRecord>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync("projects.csv", CostpointCsv.ParseProjects, cancellationToken);

    public async Task<IReadOnlyList<CpWbsRecord>> GetWbsAsync(string projectId, CancellationToken cancellationToken = default) =>
        (await ReadAsync("wbs.csv", CostpointCsv.ParseWbs, cancellationToken)).Where(w => w.ProjectId == projectId).ToList();

    public Task<IReadOnlyList<CpEmployeeRecord>> GetEmployeesAsync(CancellationToken cancellationToken = default) =>
        ReadAsync("employees.csv", CostpointCsv.ParseEmployees, cancellationToken);

    public Task<IReadOnlyList<CpLaborCategoryRecord>> GetLaborCategoriesAsync(CancellationToken cancellationToken = default) =>
        ReadAsync("labor_categories.csv", CostpointCsv.ParseLaborCategories, cancellationToken);

    public async Task<IReadOnlyList<CpBudgetRecord>> GetBudgetsAsync(string projectId, CancellationToken cancellationToken = default) =>
        (await ReadAsync("budgets.csv", CostpointCsv.ParseBudgets, cancellationToken)).Where(b => b.ProjectId == projectId).ToList();

    public async Task<IReadOnlyList<CpActualRecord>> GetActualsAsync(string projectId, string fromPeriod, string toPeriod, CancellationToken cancellationToken = default) =>
        (await ReadAsync("actuals.csv", CostpointCsv.ParseActuals, cancellationToken))
            .Where(a => a.ProjectId == projectId
                && string.CompareOrdinal(a.FiscalPeriod, fromPeriod) >= 0
                && string.CompareOrdinal(a.FiscalPeriod, toPeriod) <= 0)
            .ToList();

    public IReadOnlyList<string> TakeWarnings()
    {
        var taken = new List<string>();
        while (_warnings.TryDequeue(out var warning))
        {
            taken.Add(warning);
        }

        // Per-project calls re-read the same file, so the same bad line is reported more than once.
        return taken.Distinct().ToList();
    }

    /// <summary>
    /// Resolves a configured folder. Relative paths are tried against the current directory and each of its
    /// parents, so <c>samples/costpoint</c> works whether the host runs from the repo root or a project folder.
    /// </summary>
    public static string ResolveFolder(string configured)
    {
        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, configured);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(configured);
    }

    private async Task<IReadOnlyList<T>> ReadAsync<T>(string fileName, Func<TextReader, string, CsvParseResult<T>> parse, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Costpoint export {fileName} was not found in {folder}.", path);
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var result = parse(new StringReader(text), fileName);
        foreach (var error in result.Errors)
        {
            logger.LogWarning("Skipped Costpoint CSV row: {Error}", error);
            _warnings.Enqueue(error);
        }

        return result.Rows;
    }
}
