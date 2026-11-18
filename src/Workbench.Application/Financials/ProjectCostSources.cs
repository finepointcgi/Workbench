using Workbench.Contracts.Modules;

namespace Workbench.Application.Financials;

/// <summary>A cost a module charged to a project's WBS element on a date (positive), counted as a direct cost like an ODC.</summary>
public sealed record ProjectCostActual(Guid ProjectId, Guid WbsElementId, DateOnly Date, decimal Amount);

/// <summary>
/// <b>Cost of a license module that the financials and burdened cost count as direct cost</b> (material issued from inventory to a project, for one).
/// The module lists a source as a row (<see cref="ModuleContribution.Rows"/>); <see cref="ProjectCostSources.LoadAsync"/> asks every loaded
/// module's source. A host without such a module has none, so the financials count time and ODCs only.
/// </summary>
public sealed record ProjectCostSource(
    string Key,
    Func<IWorkbenchDbContext, IReadOnlyCollection<Guid>?, DateOnly, DateOnly, CancellationToken, Task<IReadOnlyList<ProjectCostActual>>> LoadAsync) : IModuleRow
{
    public string Catalog => ModuleCatalogs.ProjectCostSources;

    public string RowKey => Key;
}

public static class ProjectCostSources
{
    /// <summary>The costs the loaded modules charged to these projects (all projects when <paramref name="projectIds"/> is null) between the dates.</summary>
    public static async Task<IReadOnlyList<ProjectCostActual>> LoadAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid>? projectIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new List<ProjectCostActual>();
        foreach (var source in ModuleRegistry.Current.RowsOf<ProjectCostSource>())
        {
            result.AddRange(await source.LoadAsync(db, projectIds, from, to, ct));
        }

        return result;
    }
}
