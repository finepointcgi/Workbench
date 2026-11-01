namespace Workbench.Contracts.Licensing;

/// <summary>Where the edition comes from.</summary>
public enum EditionSource
{
    /// <summary>No license: the free Community edition.</summary>
    None,

    /// <summary>A license key entered under Setup › License.</summary>
    Key,

    /// <summary>The hosting service's plan for this organization.</summary>
    HostedPlan,

    /// <summary>The server's configuration turns Business on (demo, development, trials).</summary>
    Configuration,
}

public sealed record FeatureStatusDto(string Key, string Name, string Description, bool Included);

/// <param name="SeatLimit">Null when seats aren't limited.</param>
/// <param name="SeatsUsed">Active people. Everyone who can sign in takes a seat; API keys do not.</param>
public sealed record LicenseStatusDto(
    string Edition,
    EditionSource Source,
    string? Licensee,
    DateOnly? ExpiresOn,
    bool InGracePeriod,
    int? SeatLimit,
    int SeatsUsed,
    string? Problem,
    IReadOnlyList<FeatureStatusDto> Features);

public sealed record ActivateLicenseRequest(string Key);

public interface ILicenseService
{
    /// <summary>Readable by everyone signed in, so menus can show what needs an upgrade.</summary>
    Task<LicenseStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<LicenseStatusDto> ActivateAsync(ActivateLicenseRequest request, CancellationToken cancellationToken = default);

    Task<LicenseStatusDto> RemoveAsync(CancellationToken cancellationToken = default);
}
