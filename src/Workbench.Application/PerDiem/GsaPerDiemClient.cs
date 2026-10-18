using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Workbench.Application.PerDiem;

/// <summary>One CONUS destination from the GSA Per Diem API: the lodging maximum per month and the M&amp;IE rate.</summary>
public sealed record GsaLodgingRate(string State, string City, string? County, bool IsStandard, decimal Meals, IReadOnlyDictionary<int, decimal> LodgingByMonth);

/// <summary>The GSA Per Diem API (api.gsa.gov/travel/perdiem/v2). Tests use a fake; nothing calls GSA without a key.</summary>
public interface IGsaPerDiemClient
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<GsaLodgingRate>> GetConusLodgingAsync(int fiscalYear, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ParsedMieTier>> GetMieBreakdownAsync(int fiscalYear, CancellationToken cancellationToken = default);
}

/// <summary>Calls the GSA API with the api.data.gov key in the <c>X-API-KEY</c> header (never in the URL).</summary>
public sealed class GsaPerDiemHttpClient(HttpClient http, IOptions<PerDiemOptions> options) : IGsaPerDiemClient
{
    private static readonly string[] MonthKeys = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.Gsa.ApiKey);

    public async Task<IReadOnlyList<GsaLodgingRate>> GetConusLodgingAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        using var doc = await GetAsync($"rates/conus/lodging/{fiscalYear}", cancellationToken);
        return ParseLodging(doc.RootElement);
    }

    public async Task<IReadOnlyList<ParsedMieTier>> GetMieBreakdownAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        using var doc = await GetAsync($"rates/conus/mie/{fiscalYear}", cancellationToken);
        return ParseMie(doc.RootElement);
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new ArgumentException("The GSA Per Diem API isn't configured: set PerDiem:Gsa:ApiKey to an api.data.gov key.");
        }

        var baseUrl = options.Value.Gsa.BaseUrl.EndsWith('/') ? options.Value.Gsa.BaseUrl : options.Value.Gsa.BaseUrl + "/";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), path));
        request.Headers.Add("X-API-KEY", options.Value.Gsa.ApiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The GSA Per Diem API returned {(int)response.StatusCode}.", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>Parses the bulk lodging response: an array of destinations with one property per month (Jan…Dec) and "Meals".</summary>
    public static IReadOnlyList<GsaLodgingRate> ParseLodging(JsonElement root)
    {
        var items = root.ValueKind == JsonValueKind.Array ? root : Find(root, "rates") ?? root;
        var result = new List<GsaLodgingRate>();
        foreach (var item in items.EnumerateArray())
        {
            var months = new Dictionary<int, decimal>();
            for (var m = 0; m < 12; m++)
            {
                if (Number(Find(item, MonthKeys[m])) is { } value)
                {
                    months[m + 1] = value;
                }
            }

            var city = Text(Find(item, "City")) ?? "";
            var standardFlag = Text(Find(item, "StandardRate")) ?? Text(Find(item, "IsStandardRate"));
            var isStandard = string.Equals(standardFlag, "true", StringComparison.OrdinalIgnoreCase) || PerDiemFileParser.IsStandardName(city);
            if (months.Count == 0 || Number(Find(item, "Meals")) is not { } meals)
            {
                continue;
            }

            result.Add(new GsaLodgingRate(Text(Find(item, "State")) ?? "", city, Text(Find(item, "County")), isStandard, meals, months));
        }

        return result;
    }

    public static IReadOnlyList<ParsedMieTier> ParseMie(JsonElement root)
    {
        var result = new List<ParsedMieTier>();
        foreach (var item in root.EnumerateArray())
        {
            if (Number(Find(item, "total")) is not { } total)
            {
                continue;
            }

            result.Add(new ParsedMieTier(total, Number(Find(item, "breakfast")) ?? 0, Number(Find(item, "lunch")) ?? 0, Number(Find(item, "dinner")) ?? 0,
                Number(Find(item, "incidentals")) ?? 0, Number(Find(item, "FirstLastDay")) ?? Math.Round(total * 0.75m, 2)));
        }

        return result;
    }

    /// <summary>Turns monthly lodging maxima into seasons inside the fiscal year (consecutive months with the same rate).</summary>
    public static IEnumerable<ParsedPerDiemRate> ToSeasons(GsaLodgingRate rate, int fiscalYear)
    {
        var start = new DateOnly(fiscalYear - 1, 10, 1);
        var current = start;
        decimal? value = null;
        var seasonStart = start;
        for (var i = 0; i < 12; i++)
        {
            var month = start.AddMonths(i);
            var lodging = rate.LodgingByMonth.GetValueOrDefault(month.Month);
            if (value is not null && lodging != value)
            {
                yield return Season(seasonStart, month.AddDays(-1), value.Value);
                seasonStart = month;
            }

            value = lodging;
            current = month;
        }

        yield return Season(seasonStart, current.AddMonths(1).AddDays(-1), value ?? 0);

        ParsedPerDiemRate Season(DateOnly from, DateOnly to, decimal lodging) => rate.IsStandard
            ? new ParsedPerDiemRate("US", "", PerDiemFileParser.StandardDestination, null, from, to, lodging, rate.Meals, true)
            : new ParsedPerDiemRate("US", rate.State.ToUpperInvariant(), rate.City, rate.County, from, to, lodging, rate.Meals, false);
    }

    private static JsonElement? Find(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var p in item.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }

        return null;
    }

    private static decimal? Number(JsonElement? e) => e switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDecimal(),
        { ValueKind: JsonValueKind.String } s when decimal.TryParse(s.GetString()?.Replace("$", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    private static string? Text(JsonElement? e) => e switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        { ValueKind: JsonValueKind.Number } n => n.ToString(),
        _ => null,
    };
}
