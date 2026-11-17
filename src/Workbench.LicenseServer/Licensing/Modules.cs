namespace Workbench.LicenseServer.Licensing;

/// <summary>The sellable modules. Core is always on and is never written into a key.</summary>
public static class Modules
{
    public static readonly string[] All = ["finance", "capture", "workforce", "controls", "govcon", "enterprise"];

    /// <summary>Normalizes to a sorted, de-duplicated list; throws <see cref="ArgumentException"/> for an unknown module.</summary>
    public static string[] Normalize(IEnumerable<string>? modules)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in modules ?? [])
        {
            var m = (raw ?? "").Trim().ToLowerInvariant();
            if (m.Length == 0 || m == "core") continue;
            if (!All.Contains(m)) throw new ArgumentException($"Unknown module '{raw}'. Modules: {string.Join(", ", All)}.");
            set.Add(m);
        }
        return [.. set];
    }

    public static string[] Parse(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? [] : Normalize(stored.Split(','));

    public static string Join(IEnumerable<string> modules) => string.Join(',', Normalize(modules));
}
