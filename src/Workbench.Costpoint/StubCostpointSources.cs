namespace Workbench.Costpoint;

/// <summary>
/// STUB. Costpoint web services / REST adapter (<c>Costpoint:Source = Rest</c>).
/// Not implemented until we have Deltek's API documentation; every call throws.
/// </summary>
public sealed class RestCostpointSource : ICostpointSource
{
    private const string Message =
        "The Costpoint REST adapter is not implemented yet (waiting on Deltek API documentation). Use Costpoint:Source=Csv.";

    public string Name => "Rest";

    public Task<IReadOnlyList<CpProjectRecord>> GetProjectsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

    public Task<IReadOnlyList<CpWbsRecord>> GetWbsAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

    public Task<IReadOnlyList<CpEmployeeRecord>> GetEmployeesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

    public Task<IReadOnlyList<CpLaborCategoryRecord>> GetLaborCategoriesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

    public Task<IReadOnlyList<CpBudgetRecord>> GetBudgetsAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

    public Task<IReadOnlyList<CpActualRecord>> GetActualsAsync(string projectId, string fromPeriod, string toPeriod, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Message);
}

/// <summary>
/// STUB. Read-only database view adapter (<c>Costpoint:Source = Database</c>).
/// Not implemented until we have the Costpoint view definitions; every call throws.
/// </summary>
public sealed class DatabaseViewCostpointSource : ICostpointSource
{
    private const string Message =
        "The Costpoint database-view adapter is not implemented yet (waiting on Deltek view definitions). Use Costpoint:Source=Csv.";

    public string Name => "Database";

    public Task<IReadOnlyList<CpProjectRecord>> GetProjectsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

    public Task<IReadOnlyList<CpWbsRecord>> GetWbsAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

    public Task<IReadOnlyList<CpEmployeeRecord>> GetEmployeesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

    public Task<IReadOnlyList<CpLaborCategoryRecord>> GetLaborCategoriesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

    public Task<IReadOnlyList<CpBudgetRecord>> GetBudgetsAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

    public Task<IReadOnlyList<CpActualRecord>> GetActualsAsync(string projectId, string fromPeriod, string toPeriod, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Message);
}
