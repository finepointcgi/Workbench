using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Contracts.Projects;

/// <summary>A project's cost health: how spending and the forecast compare with the budget and the contract's funding.</summary>
public enum ProjectHealth { NoBudget, OnTrack, AtRisk, Over }

/// <summary>
/// Money on a project, for Finance, administrators and the project's manager only. Figures are direct cost (labor at cost
/// plus allowable ODCs), the same as the Financials tab's Direct view; funding is the contract's (shared by its projects).
/// </summary>
public sealed record ProjectMoneyDto(
    decimal Budget,
    decimal Spent,
    decimal EstimateToComplete,
    decimal ForecastAtCompletion,

    /// <summary>Spent / budget (0.5 = 50%); null without a budget.</summary>
    decimal? PercentSpent,

    /// <summary>Forecast at completion / budget; null without a budget.</summary>
    decimal? ForecastVsBudget,
    decimal? ContractValue,
    decimal? FundedValue,
    decimal? FundingLeft,

    /// <summary>Funded value / contract value; null off contract or with no contract value.</summary>
    decimal? PercentFunded,

    /// <summary>Contract spend / funded value; null off contract or with nothing funded.</summary>
    decimal? PercentFundingUsed,

    /// <summary>How much of the project's period has passed (0–1); null without start and finish dates.</summary>
    decimal? PercentTimeElapsed,
    ProjectHealth Health,
    IReadOnlyList<string> HealthReasons);

/// <summary>One project in the Projects list. <see cref="Money"/> and <see cref="OpenItems"/> are null for people who may not see them.</summary>
public sealed record ProjectHubRowDto(
    Guid Id,
    string Code,
    string Name,
    Guid? ProjectManagerId,
    string? ProjectManagerName,
    Guid? ContractId,
    string? ContractNumber,
    ProjectState Status,
    DateOnly? Start,
    DateOnly? Finish,
    Guid? SheetId,

    /// <summary>The signed-in user manages this project.</summary>
    bool IsManager,

    /// <summary>The signed-in user charges to it (time, an assignment or an allocation).</summary>
    bool ChargesTo,

    /// <summary>Plan sheet tasks (leaves) that aren't Done; null without a plan sheet with a Status column, or when hidden.</summary>
    int? OpenItems,
    ProjectMoneyDto? Money)
{
    public bool IsMine => IsManager || ChargesTo;
}

/// <summary>
/// The Projects list. <see cref="SeesAll"/> is true for Finance and administrators; everyone else gets the projects they
/// manage or charge to. <see cref="ShowsMoney"/> is true when at least one row carries money (otherwise the UI shows the
/// simpler "my projects" view).
/// </summary>
public sealed record ProjectHubListDto(bool SeesAll, IReadOnlyList<ProjectHubRowDto> Projects)
{
    public bool ShowsMoney => Projects.Any(p => p.Money is not null);
}

/// <summary>Which tabs of the project page the user may open (the server enforces the same rules on each tab's data).</summary>
public sealed record ProjectHubTabsDto(
    bool Plan,
    bool Financials,
    bool Budgets,
    bool Revenue,
    bool EarnedValue,
    bool Ceilings,
    bool Costs,
    bool Team,
    bool Setup);

public sealed record ProjectMilestoneDto(Guid ItemId, string Title, DateOnly Date, bool IsLate);

/// <summary>Schedule health from the plan sheet: its leaf tasks by Status (Done / Blocked) and Finish date.</summary>
public sealed record ProjectScheduleDto(
    Guid SheetId,
    int TotalTasks,
    int DoneTasks,
    int OpenTasks,
    int LateTasks,
    int BlockedTasks,
    int DueSoonTasks,
    bool HasStatusColumn,
    IReadOnlyList<ProjectMilestoneDto> UpcomingMilestones);

public enum ProjectAlertLevel { Info, Warning, Critical }

/// <summary>Something on the project that needs attention. <see cref="Tab"/> is the hub tab that explains it (e.g. "ceilings").</summary>
public sealed record ProjectAlertDto(ProjectAlertLevel Level, string Kind, string Message, string? Tab);

/// <summary>The project page: the list row, the contract, which tabs the user may open, the schedule and alerts.</summary>
public sealed record ProjectHubDto(
    ProjectHubRowDto Project,
    string? ContractName,
    string? CustomerName,
    ProjectHubTabsDto Tabs,

    /// <summary>Null without a plan sheet or when the user can't see it.</summary>
    ProjectScheduleDto? Schedule,
    IReadOnlyList<ProjectAlertDto> Alerts,
    DateOnly AsOf);

/// <summary>Someone who charges to (or is allocated to, or assigned on) the project.</summary>
public sealed record ProjectTeamMemberDto(
    Guid EmployeeId,
    string Name,
    string Email,
    string? LaborCategory,
    bool IsManager,
    bool IsAssigned,
    decimal ApprovedHours,
    decimal PendingHours,
    DateOnly? LastCharged,

    /// <summary>Allocated hours from this week on.</summary>
    decimal UpcomingAllocatedHours,
    DateOnly? AllocatedThrough);

/// <summary>
/// The Projects hub: the list of the user's projects with headline numbers, and one project's overview.
/// Finance and administrators see every project; a project manager sees the projects they manage (with money); everyone
/// sees the projects they charge to (without money). Team is for Finance, administrators and the project's manager.
/// </summary>
public interface IProjectHubService
{
    Task<ProjectHubListDto> ListProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>Not found when the project doesn't exist or the user isn't on it (manager, charging, plan sheet member).</summary>
    Task<ProjectHubDto> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectTeamMemberDto>> GetTeamAsync(Guid projectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The health chip's thresholds, shared by the server (which sets <see cref="ProjectMoneyDto.Health"/>) and the UI (which
/// explains them).
/// </summary>
public static class ProjectHealthRules
{
    /// <summary>A forecast more than this far over budget is Over (5%).</summary>
    public const decimal OverrunTolerance = 0.05m;

    /// <summary>Spending this far ahead of the time elapsed is At risk (10 points).</summary>
    public const decimal BurnAheadTolerance = 0.10m;

    /// <summary>Funding this used up is At risk (90%, the critical funding notice).</summary>
    public const decimal FundingAtRisk = 0.90m;

    public static readonly IReadOnlyList<string> Explanation =
    [
        "Over: spent more than the budget, forecast at completion more than 5% over budget, or the contract's funding is used up.",
        "At risk: forecast over budget (up to 5%), spending more than 10 points ahead of the time elapsed, or 90% of funding used.",
        "On track: none of these.",
    ];

    public static (ProjectHealth Health, IReadOnlyList<string> Reasons) Evaluate(
        decimal budget, decimal spent, decimal forecastAtCompletion, decimal? percentFundingUsed, decimal? percentTimeElapsed)
    {
        var over = new List<string>();
        var risk = new List<string>();
        if (percentFundingUsed >= 1m)
        {
            over.Add("The contract's funding is used up.");
        }
        else if (percentFundingUsed >= FundingAtRisk)
        {
            risk.Add($"{percentFundingUsed:P0} of the contract's funding is used.");
        }

        if (budget <= 0)
        {
            if (over.Count > 0)
            {
                return (ProjectHealth.Over, over);
            }

            return risk.Count > 0 ? (ProjectHealth.AtRisk, risk) : (ProjectHealth.NoBudget, ["There's no budget to compare with."]);
        }

        if (spent > budget)
        {
            over.Add("Spent so far is more than the budget.");
        }

        var forecastRatio = forecastAtCompletion / budget;
        if (forecastRatio > 1m + OverrunTolerance)
        {
            over.Add($"The forecast at completion is {forecastRatio - 1m:P0} over budget.");
        }
        else if (forecastRatio > 1m)
        {
            risk.Add($"The forecast at completion is {forecastRatio - 1m:P0} over budget.");
        }

        if (percentTimeElapsed is { } elapsed && spent / budget - elapsed > BurnAheadTolerance)
        {
            risk.Add($"{spent / budget:P0} of the budget is spent with {elapsed:P0} of the time gone.");
        }

        return over.Count > 0 ? (ProjectHealth.Over, over)
            : risk.Count > 0 ? (ProjectHealth.AtRisk, risk)
            : (ProjectHealth.OnTrack, []);
    }

    public static string Label(ProjectHealth health) => health switch
    {
        ProjectHealth.OnTrack => "On track",
        ProjectHealth.AtRisk => "At risk",
        ProjectHealth.Over => "Over",
        _ => "No budget",
    };
}
