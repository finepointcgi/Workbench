namespace Workbench.Costpoint;

public sealed record CpProjectRecord(string ProjectId, string Name, string? OrganizationId, DateOnly? Start, DateOnly? End, bool Active);

public sealed record CpWbsRecord(string ProjectId, string WbsId, string? ParentWbsId, string Name, int Level, bool ChargeAllowed);

public sealed record CpActualRecord(string ProjectId, string WbsId, string FiscalPeriod, decimal Hours, decimal Cost);

/// <summary>
/// Read side of a Costpoint connection. Implementations: web services / REST,
/// file exchange (SFTP), and read-only database views. Costpoint stays the system of record.
/// </summary>
public interface ICostpointSource
{
    string Name { get; }

    Task<IReadOnlyList<CpProjectRecord>> GetProjectsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CpWbsRecord>> GetWbsAsync(string projectId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CpActualRecord>> GetActualsAsync(string projectId, string fromPeriod, string toPeriod, CancellationToken cancellationToken = default);
}

/// <summary>Placeholder used until a real adapter is configured.</summary>
public sealed class NullCostpointSource : ICostpointSource
{
    public string Name => "None";

    public Task<IReadOnlyList<CpProjectRecord>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CpProjectRecord>>([]);

    public Task<IReadOnlyList<CpWbsRecord>> GetWbsAsync(string projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CpWbsRecord>>([]);

    public Task<IReadOnlyList<CpActualRecord>> GetActualsAsync(string projectId, string fromPeriod, string toPeriod, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CpActualRecord>>([]);
}
