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

public sealed record FeatureStatusDto(string Key, string Name, string Description, bool Included, string Module = "");

/// <param name="Id">The module id: <c>core</c>, <c>billing</c>, <c>accounting</c>, <c>capture</c>, <c>workforce</c>, <c>projectcontrols</c>, <c>materials</c>, <c>govcon</c>, <c>enterprise</c> or <c>ai</c>.</param>
/// <param name="Enforced">False when everyone has the module whatever the license says.</param>
/// <param name="Licensed">The license (or plan) includes the module.</param>
/// <param name="Available">The organization may use it: licensed, or not enforced. Menus and pages follow this.</param>
/// <param name="SeatsUsed">People whose roles grant any permission in this module (core has none: its seats are the full and light seats).</param>
/// <param name="WholeModule">False for an enforced module whose pages stay in the menu and whose services stay open, because only its named features need the license (govcon and enterprise today). The menu hides a module only when it is unavailable and this is true.</param>
/// <param name="SeatsAllowed">Seats the license gives the module; null when nothing limits it (not enforced, not licensed, or no key).</param>
public sealed record ModuleStatusDto(string Id, string Name, string Description, bool Enforced, bool Licensed, bool Available, int SeatsUsed = 0, int? SeatsAllowed = null, bool WholeModule = true);

/// <summary>
/// How a connected install's key refresh from the license server is going. Null in the status when this install is offline
/// (no <c>License:ServerUrl</c>), the default for defense and air-gapped sites.
/// </summary>
/// <param name="Outcome"><c>refreshed</c>, <c>refused</c> (the server says the subscription is not entitled), <c>offline</c> (an offline key, never refreshed), <c>failed</c> (no usable answer), or null before the first attempt.</param>
/// <param name="Message">The server's own reason when it refused (for example that the subscription lapsed), or what went wrong.</param>
/// <param name="Stale">No successful refresh for the configured number of days while attempts keep failing or being refused. Only a warning: the key's own expiry still decides what the install may do.</param>
public static class RefreshOutcomeNames
{
    public const string Refreshed = "refreshed";
    public const string Refused = "refused";
    public const string Offline = "offline";
    public const string Failed = "failed";
}

public sealed record LicenseRefreshDto(
    string ServerUrl,
    DateTime? LastAttemptUtc,
    DateTime? LastSuccessUtc,
    string? Outcome,
    string? Message,
    int ConsecutiveFailures,
    bool Stale,
    int? DaysSinceSuccess);

/// <param name="SeatLimit">Full seats the license covers. Null when seats aren't limited.</param>
/// <param name="SeatsUsed">Full seats in use: people with more than Employee access, plus light people once the light seats are full. API keys do not take a seat.</param>
/// <param name="LightSeatLimit">Light seats (people who only enter their own time, expenses and leave). Zero when the license has none.</param>
/// <param name="LightSeatsUsed">Light people in a light seat.</param>
/// <param name="Trial">The key is a time-boxed trial.</param>
/// <param name="DaysLeft">Days until the key expires (0 on its last day, negative once past); null without a key.</param>
public sealed record LicenseStatusDto(
    string Edition,
    EditionSource Source,
    string? Licensee,
    DateOnly? ExpiresOn,
    bool InGracePeriod,
    int? SeatLimit,
    int SeatsUsed,
    string? Problem,
    IReadOnlyList<FeatureStatusDto> Features,
    IReadOnlyList<ModuleStatusDto>? Modules = null,
    int LightSeatLimit = 0,
    int LightSeatsUsed = 0,
    bool Trial = false,
    int? DaysLeft = null,
    bool ReadOnly = false,
    LicenseRefreshDto? Refresh = null)
{
    /// <summary>True when the organization may use <paramref name="moduleId"/>; every module when the server didn't say.</summary>
    public bool HasModule(string moduleId) => Modules is null || Modules.FirstOrDefault(m => m.Id == moduleId)?.Available != false;
}

public sealed record ActivateLicenseRequest(string Key);

public interface ILicenseService
{
    /// <summary>Readable by everyone signed in, so menus can show what needs an upgrade.</summary>
    Task<LicenseStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<LicenseStatusDto> ActivateAsync(ActivateLicenseRequest request, CancellationToken cancellationToken = default);

    Task<LicenseStatusDto> RemoveAsync(CancellationToken cancellationToken = default);
}
