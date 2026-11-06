using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Assistant;
using Workbench.Contracts.Crm;
using Workbench.Domain.Assistant;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.Crm;

/// <summary>A lead's fit with the company profile: 0–100 and a one-line reason.</summary>
public sealed record LeadFit(int Score, string Reason);

/// <summary>
/// The rules half of lead scoring. They read the company profile as free text: NAICS codes in it, the set-asides it
/// claims, and the words it uses. Points: NAICS 35, set-aside 20, keywords 35, time left to respond 10. Deterministic, so
/// scoring works with the assistant off and the assistant has a baseline to refine.
/// </summary>
public static partial class LeadFitRules
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "above", "after", "agency", "services", "service", "support", "contract", "contractor", "provide", "providing", "requirements",
        "requirement", "solicitation", "notice", "government", "federal", "department", "united", "states", "their", "which", "shall", "will", "with",
        "this", "that", "these", "those", "from", "have", "other", "under", "within", "including", "include", "general", "services,", "office",
    };

    public static LeadFit? Score(string? profile, D.OpportunityLead lead, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return null;
        }

        var reasons = new List<string>();
        var score = 0;

        // NAICS: exact code in the profile, else the same 4-digit group.
        var profileNaics = NaicsPattern().Matches(profile).Select(m => m.Value).Distinct().ToList();
        var naics = new string((lead.NaicsCode ?? "").Where(char.IsDigit).ToArray());
        if (profileNaics.Count == 0)
        {
            score += 15;
        }
        else if (naics.Length == 0)
        {
            score += 10;
            reasons.Add("no NAICS code on the notice");
        }
        else if (profileNaics.Contains(naics))
        {
            score += 35;
            reasons.Add($"NAICS {naics} is one of yours");
        }
        else if (naics.Length >= 4 && profileNaics.Any(p => p.StartsWith(naics[..4], StringComparison.Ordinal)))
        {
            score += 20;
            reasons.Add($"NAICS {naics} is close to one of yours");
        }
        else
        {
            reasons.Add($"NAICS {naics} isn't in your profile");
        }

        // Set-aside: open competition always fits; a restricted one fits only when the profile claims the status.
        if (lead.SetAside == D.SetAside.None)
        {
            score += 20;
        }
        else if (ClaimsSetAside(profile, lead.SetAside))
        {
            score += 20;
            reasons.Add($"{Label(lead.SetAside)} set-aside matches your status");
        }
        else
        {
            reasons.Add($"{Label(lead.SetAside)} set-aside, which your profile doesn't claim");
        }

        // Keywords: significant words the notice and the profile share.
        var profileWords = Words(profile);
        var noticeWords = Words($"{lead.Title} {lead.Description}");
        var shared = noticeWords.Where(profileWords.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        score += Math.Min(35, shared.Count * 7);
        if (shared.Count > 0)
        {
            reasons.Add("matches " + string.Join(", ", shared.Take(4)));
        }
        else
        {
            reasons.Add("nothing in the notice matches your capabilities");
        }

        // Time left.
        if (lead.ResponseDueOn is not { } due)
        {
            score += 5;
        }
        else if (due < today)
        {
            reasons.Add("response date has passed");
        }
        else if (due >= today.AddDays(14))
        {
            score += 10;
        }
        else
        {
            score += 5;
            reasons.Add($"only {due.DayNumber - today.DayNumber} days left to respond");
        }

        score = Math.Clamp(score, 0, 100);
        var text = string.Join("; ", reasons);
        return new LeadFit(score, text.Length == 0 ? "No details to go on." : char.ToUpperInvariant(text[0]) + text[1..] + ".");
    }

    private static HashSet<string> Words(string text) =>
        WordPattern().Matches(text).Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length >= 5 && !Stop.Contains(w)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool ClaimsSetAside(string profile, D.SetAside setAside)
    {
        var p = profile.ToLowerInvariant();
        return setAside switch
        {
            D.SetAside.SmallBusiness => p.Contains("small business") || p.Contains("small-business") || p.Contains("sba"),
            D.SetAside.EightA => p.Contains("8(a)") || p.Contains("8a"),
            D.SetAside.HubZone => p.Contains("hubzone"),
            D.SetAside.ServiceDisabledVeteranOwned => p.Contains("sdvosb") || p.Contains("service-disabled") || p.Contains("service disabled"),
            D.SetAside.WomenOwned or D.SetAside.EconomicallyDisadvantagedWomenOwned => p.Contains("wosb") || p.Contains("women-owned") || p.Contains("women owned"),
            D.SetAside.VeteranOwned => p.Contains("veteran-owned") || p.Contains("veteran owned") || p.Contains("vosb"),
            _ => false,
        };
    }

    private static string Label(D.SetAside setAside) => setAside switch
    {
        D.SetAside.SmallBusiness => "Small business",
        D.SetAside.EightA => "8(a)",
        D.SetAside.HubZone => "HUBZone",
        D.SetAside.ServiceDisabledVeteranOwned => "SDVOSB",
        D.SetAside.WomenOwned => "WOSB",
        D.SetAside.EconomicallyDisadvantagedWomenOwned => "EDWOSB",
        D.SetAside.VeteranOwned => "Veteran-owned",
        _ => "Restricted",
    };

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex NaicsPattern();

    [GeneratedRegex(@"[A-Za-z][A-Za-z\-]+")]
    private static partial Regex WordPattern();
}

/// <summary>
/// Scores leads against <see cref="D.CrmSettings.CompanyProfile"/>: <see cref="LeadFitRules"/> first, then the assistant
/// refines the batch (it may move a score and rewrites the reason) when it's on for the organization. Scores are
/// advice for the person deciding Pursue or Dismiss; they never move a lead.
/// </summary>
public sealed class LeadFitScorer(AssistantRunner runner, TimeProvider clock)
{
    private const int BatchSize = 15;
    private const int MaxBatches = 3;

    public async Task<LeadFitResultDto> ScoreAsync(IWorkbenchDbContext db, bool rescoreAll, CancellationToken ct)
    {
        var profile = (await db.Set<D.CrmSettings>().AsNoTracking().FirstOrDefaultAsync(ct))?.CompanyProfile;
        if (string.IsNullOrWhiteSpace(profile))
        {
            return new LeadFitResultDto(0, 0);
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var query = db.Set<D.OpportunityLead>().Where(l => l.Status == D.LeadStatus.New);
        if (!rescoreAll)
        {
            query = query.Where(l => l.FitScoredAtUtc == null);
        }

        var leads = await query.OrderByDescending(l => l.CreatedAtUtc).Take(BatchSize * MaxBatches).ToListAsync(ct);
        var scored = 0;
        var refined = 0;
        foreach (var batch in leads.Chunk(BatchSize))
        {
            var baseline = batch.ToDictionary(l => l.Id, l => LeadFitRules.Score(profile, l, today)!);
            var refinedFits = await RefineAsync(profile, batch, baseline, ct);
            foreach (var lead in batch)
            {
                var fit = refinedFits?.GetValueOrDefault(lead.Id) ?? baseline[lead.Id];
                lead.FitScore = fit.Score;
                lead.FitReason = fit.Reason;
                lead.FitByAssistant = refinedFits?.ContainsKey(lead.Id) == true;
                lead.FitScoredAtUtc = clock.GetUtcNow().UtcDateTime;
                scored++;
                refined += lead.FitByAssistant ? 1 : 0;
            }
        }

        await db.SaveChangesAsync(ct);
        return new LeadFitResultDto(scored, refined);
    }

    private async Task<Dictionary<Guid, LeadFit>?> RefineAsync(string profile, D.OpportunityLead[] batch, Dictionary<Guid, LeadFit> baseline, CancellationToken ct)
    {
        var notices = batch.Select(l => new
        {
            id = l.Id,
            title = l.Title,
            agency = l.Agency,
            office = l.Office,
            naics = l.NaicsCode,
            setAside = l.SetAside.ToString(),
            noticeType = l.NoticeType,
            responseDue = l.ResponseDueOn,
            estimatedValue = l.EstimatedValue,
            description = Clip(l.Description, 800),
            rulesScore = baseline[l.Id].Score,
            rulesReason = baseline[l.Id].Reason,
        }).ToList();
        var draft = new { fits = batch.Select(l => new { id = l.Id, score = baseline[l.Id].Score, reason = baseline[l.Id].Reason }) };
        var completion = await runner.CompleteTextAsync(
            AssistantInteractionKind.LeadFit,
            $"Score {batch.Length} new lead{(batch.Length == 1 ? "" : "s")} against the company profile",
            AssistantPrompts.LeadFitSystem,
            $"<company-profile>\n{JsonSerializer.Serialize(profile, AssistantPrompts.DataJson)}\n</company-profile>\n"
                + $"<workbench-data source=\"lead notices\" untrusted=\"true\">\n{JsonSerializer.Serialize(notices, AssistantPrompts.DataJson)}\n</workbench-data>\n"
                + $"<draft-json>\n{JsonSerializer.Serialize(draft, AssistantPrompts.DataJson)}\n</draft-json>",
            Schema(),
            "bd/leads",
            "lead notices and the company profile",
            batch.Select(l => $"crm/leads/{l.Id}").ToList(),
            ct);
        return completion is null ? null : TryParse(completion.Text, baseline.Keys.ToHashSet());
    }

    public static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["fits"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["score"] = new JsonObject { ["type"] = "integer" },
                        ["reason"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("id", "score", "reason"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("fits"),
        ["additionalProperties"] = false,
    };

    /// <summary>The model's scores for known leads only: ids it invented are dropped, scores clamped to 0–100, reasons clipped.</summary>
    public static Dictionary<Guid, LeadFit>? TryParse(string json, IReadOnlySet<Guid> known)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        var fits = new Dictionary<Guid, LeadFit>();
        foreach (var item in (node as JsonObject)?["fits"] as JsonArray ?? [])
        {
            if (item is JsonObject o
                && o["id"]?.ToString() is { } idText && Guid.TryParse(idText, out var id) && known.Contains(id)
                && o["score"] is JsonValue sv && sv.TryGetValue<double>(out var raw)
                && o["reason"]?.ToString() is { Length: > 0 } reason)
            {
                fits[id] = new LeadFit((int)Math.Clamp(Math.Round(raw), 0, 100), Clip(reason.Trim(), 300)!);
            }
        }

        return fits.Count == 0 ? null : fits;
    }

    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..(max - 1)] + "…";
}
