using Workbench.Application.Assistant;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Projects;
using Workbench.Contracts.Time;

namespace Workbench.Application.Projects;

/// <summary>
/// Things on a project that a license module watches and core only asks about. Core declares each interface and registers no implementation
/// (every one is read as a list of sources), so a host without the module behaves as if there were nothing to report.
/// </summary>
public interface IProjectAlertSource
{
    /// <summary>Alerts for the project page's Overview (for example a ceiling at its warning level). Called as the viewer, only for a viewer who may read the project's money.</summary>
    Task<IReadOnlyList<ProjectAlertDto>> AlertsAsync(Guid projectId, CancellationToken cancellationToken);
}

/// <summary>An hours ceiling on a contract's labor category, which project start turns into the first budget (see <see cref="IContractCeilingSource"/>).</summary>
/// <param name="ClinId">The CLIN the ceiling is on, when it is on one.</param>
public sealed record ContractHourCeiling(Guid? ClinId, Guid LaborCategoryId, decimal Hours);

/// <summary>
/// The project lifecycle's questions to the project controls module about a contract's labor category hour ceilings: project start seeds the
/// first budget from them and offers the capped categories for staffing. Without the module there are no ceilings.
/// </summary>
public interface IContractCeilingSource
{
    /// <summary>The active hour ceilings on the contract's labor categories that apply to the whole contract (not to one project or WBS element).</summary>
    Task<IReadOnlyList<ContractHourCeiling>> HourCeilingsAsync(Guid contractId, CancellationToken cancellationToken);

    /// <summary>The labor categories the contract caps, with any ceiling.</summary>
    Task<IReadOnlyList<Guid>> CappedCategoriesAsync(Guid contractId, CancellationToken cancellationToken);
}

/// <summary>The default: a host without project controls has no ceilings.</summary>
public sealed class NoContractCeilingSource : IContractCeilingSource
{
    public Task<IReadOnlyList<ContractHourCeiling>> HourCeilingsAsync(Guid contractId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContractHourCeiling>>([]);

    public Task<IReadOnlyList<Guid>> CappedCategoriesAsync(Guid contractId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);
}

/// <summary>
/// What the hours, cost and billing ceilings of the project controls module say about a charge code, for the timesheet pages ("hours left under the ceiling") and
/// the timesheet flags ("ceiling-limited code"). Core shows and flags nothing without the module. (The ceilings themselves are enforced where
/// the module decorates the time, expense, ODC, AP, purchase order, requisition and invoice services.)
/// </summary>
public interface ICeilingProximitySource
{
    /// <summary>For each WBS element the ceilings cover: the largest share of an hours or cost limit consumed.</summary>
    Task<IReadOnlyList<FlagCeiling>> ForWbsAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> wbsIds, DateTime asOfUtc, CancellationToken cancellationToken);

    /// <summary>For each charge code under an hours ceiling: the hours that can still be saved and submitted (the tightest ceiling, with any override).</summary>
    Task<IReadOnlyList<ChargeCeilingRoomDto>> HoursRoomAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> wbsIds, DateTime asOfUtc, CancellationToken cancellationToken);
}

/// <summary>
/// A step a license module takes when a project is started from the project start flow, after the project exists and before its labor categories are set
/// (the billing module sets up how the project earns revenue from its contract). Read as a list: a host without the module does nothing. An implementation
/// that the viewer may not use (setting revenue up is Finance's) returns without failing, so the project is still created.
/// </summary>
public interface IProjectStartHook
{
    Task OnProjectStartedAsync(ProjectDto project, CancellationToken cancellationToken);
}
