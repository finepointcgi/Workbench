namespace Workbench.LicenseServer.Licensing;

/// <summary>The sellable modules. Core is always on and is never written into a key.</summary>
public static class Modules
{
    public static readonly string[] All = ["billing", "accounting", "capture", "workforce", "projectcontrols", "materials", "govcon", "enterprise", "ai"];

    /// <summary>Ids from before the catalog was split. The server no longer issues them; stored rows that still carry one are read as what they became.</summary>
    private static readonly Dictionary<string, string[]> Legacy = new() { ["finance"] = ["billing", "accounting"], ["controls"] = ["enterprise"] };

    /// <summary>Normalizes to a sorted, de-duplicated list; throws <see cref="ArgumentException"/> for an unknown (or retired) module.</summary>
    public static string[] Normalize(IEnumerable<string>? modules)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in modules ?? [])
        {
            var m = (raw ?? "").Trim().ToLowerInvariant();
            if (m.Length == 0 || m == "core") continue;
            if (Legacy.TryGetValue(m, out var now)) throw new ArgumentException($"The module id '{raw}' is retired; use {string.Join(" + ", now)}.");
            if (!All.Contains(m)) throw new ArgumentException($"Unknown module '{raw}'. Modules: {string.Join(", ", All)}.");
            set.Add(m);
        }
        return [.. set];
    }

    public static string[] Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return [];
        var ids = stored.Split(',').Select(s => s.Trim().ToLowerInvariant()).SelectMany(s => Legacy.TryGetValue(s, out var now) ? now : [s]);
        return Normalize(ids);
    }

    public static string Join(IEnumerable<string> modules) => string.Join(',', Normalize(modules));

    /// <summary>Validates a module-to-seats map (known modules, non-negative counts). Entries for modules not sold are kept; the key writer drops them.</summary>
    public static SortedDictionary<string, int> NormalizeSeats(IEnumerable<KeyValuePair<string, int>>? seats)
    {
        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (raw, count) in seats ?? [])
        {
            var id = Normalize([raw]).SingleOrDefault() ?? throw new ArgumentException("Module seats need a module id.");
            if (count < 0) throw new ArgumentException($"Seats for {id} can't be negative.");
            result[id] = count;
        }
        return result;
    }

    /// <summary>Stored as <c>accounting=10,billing=5</c>.</summary>
    public static string JoinSeats(IEnumerable<KeyValuePair<string, int>>? seats) =>
        string.Join(',', NormalizeSeats(seats).Select(kv => $"{kv.Key}={kv.Value}"));

    public static SortedDictionary<string, int> ParseSeats(string? stored)
    {
        var pairs = new List<KeyValuePair<string, int>>();
        foreach (var part in (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && int.TryParse(kv[1], out var n))
                foreach (var id in Parse(kv[0])) pairs.Add(new(id, n));
        }
        return NormalizeSeats(pairs);
    }
}
