namespace Workbench.Costpoint;

public sealed record CpProjectRecord(string ProjectId, string Name, string? OrganizationId, DateOnly? Start, DateOnly? End, bool Active);

public sealed record CpWbsRecord(string ProjectId, string WbsId, string? ParentWbsId, string Name, int Level, bool ChargeAllowed);

public sealed record CpEmployeeRecord(string EmployeeId, string Name, string? Email, string? OrganizationId, string? LaborCategoryId, bool Active);

public sealed record CpLaborCategoryRecord(string LaborCategoryId, string Name);

/// <summary>
/// A budget line. <paramref name="WbsId"/> null means a project-level budget;
/// <paramref name="FiscalPeriod"/> null means a total (whole-life) budget, otherwise <c>yyyy-MM</c>.
/// </summary>
public sealed record CpBudgetRecord(string ProjectId, string? WbsId, string? FiscalPeriod, decimal Hours, decimal Amount);

/// <summary>Labor actuals. <paramref name="FiscalPeriod"/> is <c>yyyy-MM</c>.</summary>
public sealed record CpActualRecord(string ProjectId, string WbsId, string FiscalPeriod, decimal Hours, decimal Cost);

/// <summary>
/// Read side of a Costpoint connection. Implementations: web services / REST,
/// file exchange (SFTP), and read-only database views. Costpoint stays the system of record,
/// so this interface has no write methods.
/// </summary>
public interface ICostpointSource
{
    string Name { get; }

    Task<IReadOnlyList<CpProjectRecord>> GetProjectsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CpWbsRecord>> GetWbsAsync(string projectId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CpEmployeeRecord>> GetEmployeesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CpLaborCategoryRecord>> GetLaborCategoriesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CpBudgetRecord>> GetBudgetsAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Actuals for fiscal periods <paramref name="fromPeriod"/> through <paramref name="toPeriod"/> (<c>yyyy-MM</c>, inclusive).</summary>
    Task<IReadOnlyList<CpActualRecord>> GetActualsAsync(string projectId, string fromPeriod, string toPeriod, CancellationToken cancellationToken = default);
}

/// <summary>Optional: sources that skip invalid rows report them here so the sync can record them.</summary>
public interface ICostpointSourceDiagnostics
{
    /// <summary>Returns and clears the warnings collected since the last call.</summary>
    IReadOnlyList<string> TakeWarnings();
}

/// <summary>Used when <c>Costpoint:Source</c> is <c>None</c>: returns no data.</summary>
public sealed class NullCostpointSource : ICostpointSource
{
    public string Name => "None";

    public Task<IReadOnlyList<CpProjectRecord>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CpProjectRecord>>([]);

    public Task<IReadOnlyList<CpWbsRecord>> GetWbsAsync(string projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CpWbsRecord>>([]);

    public Task<IReadOnlyList<CpEmployeeRecord>> GetEmployeesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CpEmployeeRecord>>([]);

    public Task<IReadOnlyList<CpLaborCategoryRecord>> GetLaborCategoriesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CpLaborCategoryRecord>>([]);

    public Task<IReadOnlyList<CpBudgetRecord>> GetBudgetsAsync(string projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CpBudgetRecord>>([]);

    public Task<IReadOnlyList<CpActualRecord>> GetActualsAsync(string projectId, string fromPeriod, string toPeriod, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CpActualRecord>>([]);
}
