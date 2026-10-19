using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Workbench.Contracts.Crm;

namespace Workbench.Application.Crm;

/// <summary>A search of SAM.gov contract opportunities (notices posted in a date range).</summary>
public sealed record SamSearchQuery(
    DateOnly PostedFrom,
    DateOnly PostedTo,
    IReadOnlyList<string> NaicsCodes,
    IReadOnlyList<SetAside> SetAsides,
    IReadOnlyList<string> Agencies,
    string? Keywords);

/// <summary>One SAM.gov notice, as Workbench keeps it.</summary>
public sealed record SamNotice(
    string NoticeId,
    string Title,
    string? SolicitationNumber,
    string? Agency,
    string? Office,
    string? NaicsCode,
    SetAside SetAside,
    string? NoticeType,
    DateOnly? PostedOn,
    DateOnly? ResponseDueOn,
    string? PlaceOfPerformance,
    string? Url,
    string? Description,
    decimal? EstimatedValue);

/// <summary>
/// SAM.gov's Get Opportunities public API (v2). <see cref="SamGovOpportunitiesClient"/> calls it with the key in
/// <c>SamGov:ApiKey</c>; without a key (development, tests) <see cref="FakeSamGovClient"/> answers with sample notices.
/// </summary>
public interface ISamGovClient
{
    /// <summary>True for the built-in sample (no API key configured).</summary>
    bool IsSample { get; }

    Task<IReadOnlyList<SamNotice>> SearchAsync(SamSearchQuery query, CancellationToken cancellationToken);
}

/// <summary>SAM.gov's set-aside codes (<c>typeOfSetAside</c>).</summary>
public static class SamSetAsides
{
    public static string? Code(SetAside setAside) => setAside switch
    {
        SetAside.SmallBusiness => "SBA",
        SetAside.EightA => "8A",
        SetAside.HubZone => "HZC",
        SetAside.ServiceDisabledVeteranOwned => "SDVOSBC",
        SetAside.WomenOwned => "WOSB",
        SetAside.EconomicallyDisadvantagedWomenOwned => "EDWOSB",
        SetAside.VeteranOwned => "VSA",
        _ => null,
    };

    /// <summary>A SAM.gov code or description ("SBA", "Total Small Business Set-Aside (FAR 19.5)", "8(a)") as a set-aside.</summary>
    public static SetAside Parse(string? text)
    {
        var t = (text ?? "").Trim().ToUpperInvariant();
        if (t.Length == 0 || t is "NONE" || t.Contains("FULL AND OPEN"))
        {
            return SetAside.None;
        }

        return t switch
        {
            "SBA" or "SBP" or "SMALLBUSINESS" or "SMALL BUSINESS" => SetAside.SmallBusiness,
            "8A" or "8AN" or "8(A)" or "EIGHTA" => SetAside.EightA,
            "HZC" or "HZS" or "HUBZONE" => SetAside.HubZone,
            "SDVOSBC" or "SDVOSBS" or "SDVOSB" or "SERVICEDISABLEDVETERANOWNED" => SetAside.ServiceDisabledVeteranOwned,
            "WOSB" or "WOSBSS" or "WOMENOWNED" => SetAside.WomenOwned,
            "EDWOSB" or "EDWOSBSS" or "ECONOMICALLYDISADVANTAGEDWOMENOWNED" => SetAside.EconomicallyDisadvantagedWomenOwned,
            "VSA" or "VSS" or "VOSB" or "VETERANOWNED" => SetAside.VeteranOwned,
            _ when t.Contains("SERVICE-DISABLED") || t.Contains("SERVICE DISABLED") => SetAside.ServiceDisabledVeteranOwned,
            _ when t.Contains("ECONOMICALLY DISADVANTAGED") => SetAside.EconomicallyDisadvantagedWomenOwned,
            _ when t.Contains("WOMEN") => SetAside.WomenOwned,
            _ when t.Contains("HUBZONE") => SetAside.HubZone,
            _ when t.Contains("8(A)") => SetAside.EightA,
            _ when t.Contains("VETERAN") => SetAside.VeteranOwned,
            _ when t.Contains("SMALL BUSINESS") => SetAside.SmallBusiness,
            _ => SetAside.Other,
        };
    }
}

/// <summary>Chooses the real client when <c>SamGov:ApiKey</c> is set, else the sample.</summary>
public static class SamGovClients
{
    public const string HttpClientName = "SamGov";
    public const string DefaultBaseUrl = "https://api.sam.gov/opportunities/v2/search";

    public static ISamGovClient Create(IServiceProvider services)
    {
        var configuration = (IConfiguration?)services.GetService(typeof(IConfiguration));
        var key = configuration?["SamGov:ApiKey"];
        if (string.IsNullOrWhiteSpace(key) || string.Equals(configuration?["SamGov:UseSample"], "true", StringComparison.OrdinalIgnoreCase))
        {
            return new FakeSamGovClient();
        }

        var factory = (IHttpClientFactory)services.GetService(typeof(IHttpClientFactory))!;
        return new SamGovOpportunitiesClient(factory.CreateClient(HttpClientName), key, configuration?["SamGov:BaseUrl"] ?? DefaultBaseUrl);
    }
}

/// <summary>
/// The public SAM.gov Opportunities API: <c>GET /opportunities/v2/search?api_key=…&amp;postedFrom=MM/dd/yyyy&amp;postedTo=…</c>
/// with <c>ncode</c> (one NAICS per call), <c>typeOfSetAside</c>, <c>title</c> and paging (<c>limit</c>, <c>offset</c>).
/// Agencies are matched against each notice's <c>fullParentPathName</c>.
/// </summary>
public sealed class SamGovOpportunitiesClient(HttpClient http, string apiKey, string baseUrl = SamGovClients.DefaultBaseUrl) : ISamGovClient
{
    private const int PageSize = 500;
    private const int MaxPages = 10;

    public bool IsSample => false;

    public async Task<IReadOnlyList<SamNotice>> SearchAsync(SamSearchQuery query, CancellationToken cancellationToken)
    {
        if (query.PostedTo.DayNumber - query.PostedFrom.DayNumber > 364)
        {
            throw new ArgumentException("SAM.gov searches cover at most a year of posted notices.");
        }

        var results = new Dictionary<string, SamNotice>();
        var naics = query.NaicsCodes.Count == 0 ? [null] : query.NaicsCodes.Select(n => (string?)n).ToList();
        var setAsides = query.SetAsides.Count == 0 ? [null] : query.SetAsides.Select(SamSetAsides.Code).Distinct().ToList();
        foreach (var code in naics)
        {
            foreach (var setAside in setAsides)
            {
                for (var page = 0; page < MaxPages; page++)
                {
                    var url = BuildUrl(query, code, setAside, page * PageSize);
                    SamResponse? response;
                    try
                    {
                        using var reply = await http.GetAsync(url, cancellationToken);
                        if (!reply.IsSuccessStatusCode)
                        {
                            throw new ArgumentException($"SAM.gov answered {(int)reply.StatusCode}. Check SamGov:ApiKey and try again later.");
                        }

                        response = await reply.Content.ReadFromJsonAsync<SamResponse>(Json, cancellationToken);
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new ArgumentException($"SAM.gov couldn't be reached: {ex.Message}");
                    }

                    var batch = response?.OpportunitiesData ?? [];
                    foreach (var notice in batch.Select(ToNotice).Where(n => n is not null && MatchesAgency(n, query.Agencies)))
                    {
                        results.TryAdd(notice!.NoticeId, notice);
                    }

                    if (batch.Count < PageSize)
                    {
                        break;
                    }
                }
            }
        }

        return results.Values.ToList();
    }

    private string BuildUrl(SamSearchQuery q, string? naics, string? setAside, int offset)
    {
        var parts = new List<string>
        {
            $"api_key={Uri.EscapeDataString(apiKey)}",
            $"postedFrom={q.PostedFrom.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)}",
            $"postedTo={q.PostedTo.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)}",
            $"limit={PageSize}",
            $"offset={offset}",
        };
        if (naics is not null)
        {
            parts.Add($"ncode={Uri.EscapeDataString(naics)}");
        }

        if (setAside is not null)
        {
            parts.Add($"typeOfSetAside={Uri.EscapeDataString(setAside)}");
        }

        if (!string.IsNullOrWhiteSpace(q.Keywords))
        {
            parts.Add($"title={Uri.EscapeDataString(q.Keywords.Trim())}");
        }

        return $"{baseUrl}?{string.Join("&", parts)}";
    }

    internal static bool MatchesAgency(SamNotice notice, IReadOnlyList<string> agencies) =>
        agencies.Count == 0 || agencies.Any(a => ($"{notice.Agency}.{notice.Office}").Contains(a.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static SamNotice? ToNotice(SamOpportunity o)
    {
        if (string.IsNullOrWhiteSpace(o.NoticeId) || string.IsNullOrWhiteSpace(o.Title))
        {
            return null;
        }

        var path = (o.FullParentPathName ?? o.Department ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var place = o.PlaceOfPerformance is { } p ? string.Join(", ", new[] { p.City?.Name, p.State?.Code ?? p.State?.Name, p.Country?.Code }.Where(x => !string.IsNullOrWhiteSpace(x))) : null;
        return new SamNotice(o.NoticeId.Trim(), o.Title.Trim(), Blank(o.SolicitationNumber), path.Length > 0 ? path[0] : null,
            path.Length > 1 ? string.Join(" / ", path[1..]) : null, Blank(o.NaicsCode), SamSetAsides.Parse(o.TypeOfSetAside ?? o.TypeOfSetAsideDescription),
            Blank(o.Type), ParseDate(o.PostedDate), ParseDate(o.ResponseDeadLine), string.IsNullOrWhiteSpace(place) ? null : place, Blank(o.UiLink),
            null, o.Award?.Amount is { } amount && decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static DateOnly? ParseDate(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null
        : DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? DateOnly.FromDateTime(d.Date)
        : null;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal sealed record SamResponse([property: JsonPropertyName("totalRecords")] int TotalRecords, [property: JsonPropertyName("opportunitiesData")] List<SamOpportunity>? OpportunitiesData);

    internal sealed record SamOpportunity(
        string? NoticeId, string? Title, string? SolicitationNumber, string? FullParentPathName, string? Department, string? PostedDate, string? Type,
        string? TypeOfSetAside, string? TypeOfSetAsideDescription, string? ResponseDeadLine, string? NaicsCode, string? UiLink, SamPlace? PlaceOfPerformance, SamAward? Award);

    internal sealed record SamPlace(SamNamed? City, SamNamed? State, SamNamed? Country);

    internal sealed record SamNamed(string? Code, string? Name);

    internal sealed record SamAward(string? Amount);
}

/// <summary>
/// Sample SAM.gov notices for development and tests (no API key). Deterministic: posted relative to the query's end date and
/// filtered by its NAICS codes, set-asides, agencies and keywords like the real API.
/// </summary>
public sealed class FakeSamGovClient : ISamGovClient
{
    public bool IsSample => true;

    /// <summary>The sample notices posted <c>DaysAgo</c> before the query's end date.</summary>
    public static IReadOnlyList<(int DaysAgo, SamNotice Notice)> Samples { get; } =
    [
        (2, Notice("f3a91c0e01", "Enterprise Cloud Migration Support Services", "W91QUZ-27-R-0012", "DEPT OF DEFENSE", "DEPT OF THE ARMY / PEO EIS", "541512", SetAside.SmallBusiness, "Solicitation", 45, "Fort Belvoir, VA", 48_000_000m)),
        (4, Notice("f3a91c0e02", "Cybersecurity Operations Center (CSOC) Support", "70RCSA27R00000005", "HOMELAND SECURITY, DEPARTMENT OF", "CISA", "541519", SetAside.None, "Presolicitation", 60, "Arlington, VA", 92_000_000m)),
        (6, Notice("f3a91c0e03", "Data Analytics Platform Modernization", "75N98027R00011", "HEALTH AND HUMAN SERVICES, DEPARTMENT OF", "NIH / NCI", "541511", SetAside.EightA, "Sources Sought", 21, "Bethesda, MD", 12_500_000m)),
        (9, Notice("f3a91c0e04", "Agile Software Development for Logistics Systems", "FA8771-27-R-1004", "DEPT OF DEFENSE", "DEPT OF THE AIR FORCE / AFLCMC", "541511", SetAside.ServiceDisabledVeteranOwned, "Solicitation", 30, "Wright-Patterson AFB, OH", 27_000_000m)),
        (12, Notice("f3a91c0e05", "Help Desk and End User Support", "36C10B27Q0042", "VETERANS AFFAIRS, DEPARTMENT OF", "TAC", "541513", SetAside.ServiceDisabledVeteranOwned, "Combined Synopsis/Solicitation", 14, "Austin, TX", 6_200_000m)),
        (15, Notice("f3a91c0e06", "Financial Management Systems Support", "W56KGU-26-R-0042", "DEPT OF DEFENSE", "DEPT OF THE ARMY / PEO EIS", "541611", SetAside.SmallBusiness, "Solicitation", 25, "Fort Belvoir, VA", 18_000_000m)),
        (20, Notice("f3a91c0e07", "Facilities Maintenance, Building 12", "47PB0027R0003", "GENERAL SERVICES ADMINISTRATION", "PBS REGION 3", "561210", SetAside.HubZone, "Solicitation", 30, "Philadelphia, PA", 3_100_000m)),
        (25, Notice("f3a91c0e08", "AI/ML Research Prototyping (BAA)", null, "DEPT OF DEFENSE", "DARPA", "541715", SetAside.None, "Special Notice", 90, "Arlington, VA", null)),
    ];

    public Task<IReadOnlyList<SamNotice>> SearchAsync(SamSearchQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<SamNotice> result = Samples
            .Select(s => s.Notice with
            {
                PostedOn = query.PostedTo.AddDays(-s.DaysAgo),
                ResponseDueOn = s.Notice.ResponseDueOn is { } due ? query.PostedTo.AddDays(due.DayNumber - DateOnly.MinValue.DayNumber - s.DaysAgo) : null,
            })
            .Where(n => n.PostedOn >= query.PostedFrom && n.PostedOn <= query.PostedTo)
            .Where(n => query.NaicsCodes.Count == 0 || query.NaicsCodes.Any(c => n.NaicsCode?.StartsWith(c.Trim(), StringComparison.Ordinal) == true))
            .Where(n => query.SetAsides.Count == 0 || query.SetAsides.Contains(n.SetAside))
            .Where(n => SamGovOpportunitiesClient.MatchesAgency(n, query.Agencies))
            .Where(n => string.IsNullOrWhiteSpace(query.Keywords)
                        || query.Keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(k => n.Title.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return Task.FromResult(result);
    }

    // ResponseDueOn carries "days to respond" as an offset from DateOnly.MinValue until the query dates are known.
    private static SamNotice Notice(string id, string title, string? solicitation, string agency, string office, string naics, SetAside setAside, string type,
        int respondInDays, string place, decimal? value) =>
        new(id, title, solicitation, agency, office, naics, setAside, type, null, DateOnly.MinValue.AddDays(respondInDays), place,
            $"https://sam.gov/opp/{id}/view", $"Sample notice: {title}.", value);
}
