using Workbench.Contracts.PerDiem;

namespace Workbench.Application.PerDiem;

/// <summary>Per diem policy and the GSA API (config section <c>PerDiem</c>).</summary>
public sealed class PerDiemOptions
{
    public const string Section = "PerDiem";

    /// <summary>Lodging above the maximum: needs a justification (default) or is unallowable outright.</summary>
    public LodgingOveragePolicy LodgingOveragePolicy { get; set; } = LodgingOveragePolicy.RequiresJustification;

    /// <summary>Justified lodging is allowable up to this percentage of the maximum (FTR 301-11.300: 300%).</summary>
    public decimal JustifiedLodgingCapPercent { get; set; } = 300m;

    public GsaApiOptions Gsa { get; set; } = new();
}

public sealed class GsaApiOptions
{
    /// <summary>An api.data.gov key. Supply it with an environment variable or a vault (<c>PerDiem__Gsa__ApiKey</c>), never appsettings.</summary>
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://api.gsa.gov/travel/perdiem/v2/";
}

/// <summary>The GSA M&amp;IE breakdown (FY2025–FY2026 tiers), used when an imported table has no breakdown of its own.</summary>
public static class DefaultMieTiers
{
    public static IReadOnlyList<ParsedMieTier> All { get; } =
    [
        new(68m, 16m, 19m, 28m, 5m, 51.00m),
        new(74m, 18m, 20m, 31m, 5m, 55.50m),
        new(80m, 20m, 22m, 33m, 5m, 60.00m),
        new(86m, 22m, 23m, 36m, 5m, 64.50m),
        new(92m, 23m, 26m, 38m, 5m, 69.00m),
    ];

    /// <summary>The standard CONUS rate when no table is loaded at all.</summary>
    public const decimal StandardLodging = 110m;

    public const decimal StandardMie = 68m;

    /// <summary>
    /// A breakdown for an M&amp;IE rate with no published tier (OCONUS rates): the nearest tier's meal shares scaled to the
    /// rate, with incidentals taking the rounding remainder.
    /// </summary>
    public static ParsedMieTier Scale(decimal total, IReadOnlyList<ParsedMieTier> tiers)
    {
        if (tiers.FirstOrDefault(t => t.Total == total) is { } exact)
        {
            return exact;
        }

        var nearest = (tiers.Count > 0 ? tiers : All).MinBy(t => Math.Abs(t.Total - total))!;
        var factor = total / nearest.Total;
        var breakfast = Math.Round(nearest.Breakfast * factor, 2, MidpointRounding.AwayFromZero);
        var lunch = Math.Round(nearest.Lunch * factor, 2, MidpointRounding.AwayFromZero);
        var dinner = Math.Round(nearest.Dinner * factor, 2, MidpointRounding.AwayFromZero);
        return new ParsedMieTier(total, breakfast, lunch, dinner, total - breakfast - lunch - dinner,
            Math.Round(total * 0.75m, 2, MidpointRounding.AwayFromZero));
    }
}
