using Microsoft.Extensions.Configuration;
using Workbench.Domain.Licensing;

namespace Workbench.Application.Licensing;

/// <summary>
/// Which modules the host's license key includes, read at startup so a plugin module's assembly is loaded only when it is licensed.
/// The key is the install's: <c>Modules:LicenseKey</c>, else <c>License:Key</c> (the same configuration an operator already sets for a
/// hosted install). A key typed into Setup › License is per database and is not readable before the host builds, so a self-hosted install
/// with plugin modules puts its key in configuration too. <c>License:EditionOverride=Business</c> (demo and development) includes every module.
/// Whether a given <i>tenant</i> may use a loaded module stays the job of <see cref="EditionService"/>.
/// </summary>
public static class ModuleEntitlement
{
    public const string KeyKey = "Modules:LicenseKey";
    public const string FallbackKeyKey = "License:Key";

    /// <summary>The module ids the configured license includes, or null for all of them. <paramref name="why"/> says what was found (for the log).</summary>
    public static IReadOnlySet<string>? Licensed(IConfiguration configuration, TimeProvider clock, out string why)
    {
        var options = new LicenseOptions();
        configuration.GetSection(LicenseOptions.SectionName).Bind(options);
        if (string.Equals(options.EditionOverride?.Trim(), "business", StringComparison.OrdinalIgnoreCase))
        {
            why = "License:EditionOverride is Business: every module is included.";
            return null;
        }

        var key = configuration[KeyKey] is { Length: > 0 } k ? k : configuration[FallbackKeyKey];
        if (string.IsNullOrWhiteSpace(key))
        {
            why = $"No license key in configuration ({KeyKey} or {FallbackKeyKey}): no plugin module is licensed.";
            return new HashSet<string>();
        }

        if (!LicenseKeys.TryVerify(key, options.PublicKeyPem, out var claims, out var problem) || claims is null)
        {
            why = $"The configured license key is not valid ({problem}): no plugin module is licensed.";
            return new HashSet<string>();
        }

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var lastDay = claims.Trial ? claims.ExpiresOn : claims.ExpiresOn.AddDays(options.GraceDays); // The same grace period the edition has.
        if (today > lastDay)
        {
            why = $"The configured license key expired on {claims.ExpiresOn:yyyy-MM-dd}: no plugin module is licensed.";
            return new HashSet<string>();
        }

        why = $"License for {claims.Licensee}, expires {claims.ExpiresOn:yyyy-MM-dd}.";
        return claims.Modules; // Null for a WB1 key: every module.
    }

    /// <summary>The check <c>ModuleHost.Load</c> asks before it loads a module's assembly.</summary>
    public static Func<string, bool> Check(IConfiguration configuration, TimeProvider clock, out string why)
    {
        var licensed = Licensed(configuration, clock, out why);
        return id => id != ModuleIds.Core && (licensed is null || licensed.Contains(id));
    }
}
