using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Assistant;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Crm;
using Workbench.Domain.Assistant;
using Workbench.Domain.Identity;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.Crm;

/// <summary>
/// What a proposal draft is written from. Only what belongs in a proposal: win themes, discriminators, hot buttons and
/// strengths from the capture plan. Weaknesses, threats, pursuit risks, competitors' names and pricing stay out, because
/// they are internal and the draft is meant to be sent on.
/// </summary>
public sealed record ProposalFacts(
    string Name,
    string? Agency,
    string? SolicitationNumber,
    string? NaicsCode,
    string SetAside,
    string? ContractType,
    string? PlaceOfPerformance,
    int? PeriodOfPerformanceMonths,
    string? Description,
    bool IsRecompete,
    bool HasIncumbent,
    string? CaptureManager,
    string? ProposalManager,
    IReadOnlyList<string> WinThemes,
    IReadOnlyList<string> Discriminators,
    IReadOnlyList<string> HotButtons,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<(string Name, string Role, decimal Workshare)> Team,
    string? CompanyCapabilities);

/// <summary>The rules half of proposal drafting: a template with <c>[ADD: …]</c> placeholders wherever the facts run out.</summary>
public static class ProposalTemplate
{
    public static readonly IReadOnlyList<(string Key, string Title)> Sections =
    [
        ("summary", "Executive summary"),
        ("requirement", "Understanding of the requirement"),
        ("approach", "Technical approach"),
        ("management", "Management approach and staffing"),
        ("past", "Past performance"),
        ("risks", "Risk management"),
    ];

    public static string Build(string key, ProposalFacts f) => key switch
    {
        "summary" => Summary(f),
        "requirement" => Requirement(f),
        "approach" => Approach(f),
        "management" => Management(f),
        "past" => Past(f),
        "risks" => Risks(f),
        _ => "",
    };

    private static string Customer(ProposalFacts f) => f.Agency ?? "the customer";

    private static string Summary(ProposalFacts f)
    {
        var sb = new StringBuilder();
        sb.Append($"We propose to support {Customer(f)} on {f.Name}{(f.SolicitationNumber is { } s ? $" (solicitation {s})" : "")}.");
        if (f.Description is { Length: > 0 } d)
        {
            sb.Append(' ').Append(First(d, 300));
        }

        sb.AppendLine().AppendLine().AppendLine("Why we are the right choice:");
        Bullets(sb, f.WinThemes, "[ADD: your win themes (Capture plan tab)]");
        if (f.Discriminators.Count > 0)
        {
            sb.AppendLine().AppendLine("What sets us apart:");
            Bullets(sb, f.Discriminators, null);
        }

        return sb.ToString().TrimEnd();
    }

    private static string Requirement(ProposalFacts f)
    {
        var sb = new StringBuilder();
        sb.AppendLine(f.Description is { Length: > 0 } d
            ? $"{Customer(f)} needs the following: {First(d, 1200)}"
            : "[ADD: summarize the statement of work or PWS in your own words]");
        var facts = new List<string>();
        if (f.PlaceOfPerformance is { Length: > 0 } place) { facts.Add($"Place of performance: {place}"); }
        if (f.PeriodOfPerformanceMonths is { } months) { facts.Add($"Period of performance: {months} months"); }
        if (f.ContractType is { Length: > 0 } type) { facts.Add($"Contract type: {type}"); }
        if (f.SetAside != "None") { facts.Add($"Set-aside: {f.SetAside}"); }
        if (f.NaicsCode is { Length: > 0 } naics) { facts.Add($"NAICS: {naics}"); }
        if (facts.Count > 0)
        {
            sb.AppendLine().AppendLine("Key terms:");
            Bullets(sb, facts, null);
        }

        sb.AppendLine().AppendLine($"What matters most to {Customer(f)}:");
        Bullets(sb, f.HotButtons, "[ADD: the customer's hot buttons (Capture plan tab)]");
        return sb.ToString().TrimEnd();
    }

    private static string Approach(ProposalFacts f)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[ADD: describe the solution in the order of the solicitation's evaluation factors]");
        if (f.HotButtons.Count > 0)
        {
            sb.AppendLine().AppendLine("How our approach answers what matters most:");
            foreach (var h in f.HotButtons)
            {
                sb.AppendLine($"- {h}: [ADD: how our solution answers this]");
            }
        }

        if (f.CompanyCapabilities is { Length: > 0 } capabilities)
        {
            sb.AppendLine().AppendLine("Capabilities we bring:").AppendLine(First(capabilities, 600));
        }

        if (f.Strengths.Count > 0)
        {
            sb.AppendLine().AppendLine("Strengths we build on:");
            Bullets(sb, f.Strengths, null);
        }

        return sb.ToString().TrimEnd();
    }

    private static string Management(ProposalFacts f)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Program leadership:");
        sb.AppendLine($"- Capture manager: {f.CaptureManager ?? "[ADD: name]"}");
        sb.AppendLine($"- Proposal manager: {f.ProposalManager ?? "[ADD: name]"}");
        sb.AppendLine("- Program manager: [ADD: name and qualifications]");
        sb.AppendLine().AppendLine("Team:");
        if (f.Team.Count == 0)
        {
            sb.AppendLine("- We propose to perform this work with our own staff. [ADD: key personnel and staffing plan]");
        }
        else
        {
            foreach (var t in f.Team)
            {
                sb.AppendLine($"- {t.Name} ({t.Role}), {t.Workshare:0.#}% of the work. [ADD: what they do and why they were chosen]");
            }
        }

        sb.AppendLine().AppendLine(f.IsRecompete || f.HasIncumbent
            ? "Transition: [ADD: how we will take over from the incumbent without a gap in service, including staff retention]"
            : "Startup: [ADD: how we will staff and start the work in the first 30 days]");
        return sb.ToString().TrimEnd();
    }

    private static string Past(ProposalFacts f)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Relevant past performance for {Customer(f)}:");
        sb.AppendLine("- [ADD: contract, customer, value, period of performance and why it is relevant]");
        sb.AppendLine("- [ADD: a second, recent and relevant contract]");
        sb.AppendLine("- [ADD: a third, if the solicitation asks for three]");
        if (f.Strengths.Count > 0)
        {
            sb.AppendLine().AppendLine("Strengths to support with evidence:");
            Bullets(sb, f.Strengths, null);
        }

        return sb.ToString().TrimEnd();
    }

    private static string Risks(ProposalFacts f)
    {
        var sb = new StringBuilder();
        sb.AppendLine("We manage delivery risk before it becomes a problem. The risks that matter most on this work, and how we handle them:");
        sb.AppendLine("- Staffing: [ADD: how we recruit, clear and retain the people this work needs]");
        sb.AppendLine("- Schedule: [ADD: how we protect delivery dates]");
        sb.AppendLine("- Security and compliance: [ADD: how we meet the solicitation's security requirements]");
        sb.AppendLine(f.IsRecompete || f.HasIncumbent
            ? "- Transition: [ADD: how we avoid a gap in service when the work changes hands]"
            : "- Startup: [ADD: how we reach full performance quickly]");
        return sb.ToString().TrimEnd();
    }

    private static void Bullets(StringBuilder sb, IReadOnlyList<string> items, string? whenEmpty)
    {
        if (items.Count == 0)
        {
            if (whenEmpty is not null)
            {
                sb.AppendLine($"- {whenEmpty}");
            }

            return;
        }

        foreach (var item in items)
        {
            sb.AppendLine($"- {item}");
        }
    }

    private static string First(string text, int max)
    {
        var t = text.Trim();
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }
}

/// <summary>
/// Drafts, edits and exports an opportunity's proposal sections. Rules write the template; the assistant (when on for the
/// organization) rewrites the sections that need it. Permissions: <c>PermissionCheckingProposalService</c>.
/// </summary>
public sealed class ProposalService(IWorkbenchDbContextFactory dbFactory, CrmAccess access, AuditRecorder audit, AssistantRunner runner, TimeProvider clock) : IProposalService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<ProposalDto> GetAsync(Guid opportunityId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await LoadAsync(db, opportunityId, cancellationToken);
    }

    public async Task<ProposalDraftResultDto> DraftAsync(Guid opportunityId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var facts = await FactsAsync(db, opportunityId, cancellationToken);
        var existing = await db.Set<D.ProposalSection>().Where(s => s.OpportunityId == opportunityId).ToListAsync(cancellationToken);
        var keys = ProposalTemplate.Sections.Select(s => s.Key).Where(k => existing.FirstOrDefault(e => e.Key == k) is not { EditedByHand: true }).ToList();
        var (texts, assisted) = await WriteAsync(facts, keys, opportunityId, cancellationToken);

        int created = 0, rewritten = 0, kept = 0, byAssistant = 0;
        for (var i = 0; i < ProposalTemplate.Sections.Count; i++)
        {
            var (key, title) = ProposalTemplate.Sections[i];
            var section = existing.FirstOrDefault(e => e.Key == key);
            if (section is { EditedByHand: true })
            {
                kept++;
                continue;
            }

            var byAi = assisted.Contains(key);
            if (section is null)
            {
                section = new D.ProposalSection { OpportunityId = opportunityId, Key = key, Title = title, Order = i, UpdatedBy = access.Email };
                db.Set<D.ProposalSection>().Add(section);
                created++;
            }
            else
            {
                rewritten++;
            }

            section.Content = texts[key];
            section.ByAssistant = byAi;
            section.UpdatedBy = access.Email;
            section.UpdatedAtUtc = UtcNow;
            byAssistant += byAi ? 1 : 0;
        }

        audit.Record(db, "Opportunity", opportunityId, "ProposalDrafted", null, new { created, rewritten, kept, byAssistant });
        await db.SaveChangesAsync(cancellationToken);
        return new ProposalDraftResultDto(await LoadAsync(db, opportunityId, cancellationToken), created, rewritten, kept, byAssistant);
    }

    public async Task<ProposalSectionDto> RedraftSectionAsync(Guid sectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var section = await db.Set<D.ProposalSection>().FirstOrDefaultAsync(s => s.Id == sectionId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Proposal section", sectionId);
        var facts = await FactsAsync(db, section.OpportunityId, cancellationToken);
        var (texts, assisted) = await WriteAsync(facts, [section.Key], section.OpportunityId, cancellationToken);
        section.Content = texts[section.Key];
        section.ByAssistant = assisted.Contains(section.Key);
        section.EditedByHand = false;
        section.UpdatedBy = access.Email;
        section.UpdatedAtUtc = UtcNow;
        audit.Record(db, "ProposalSection", section.Id, "Redrafted", null, new { section.Key });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(section);
    }

    public async Task<ProposalSectionDto> SaveSectionAsync(Guid sectionId, SaveProposalSectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 200)
        {
            throw new ArgumentException("Give the section a title of up to 200 characters.");
        }

        var content = (request.Content ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        if (content.Length > 30_000)
        {
            throw new ArgumentException("A section can hold up to 30,000 characters.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var section = await db.Set<D.ProposalSection>().FirstOrDefaultAsync(s => s.Id == sectionId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Proposal section", sectionId);
        if (section.Title == title && section.Content == content)
        {
            return ToDto(section);
        }

        section.Title = title;
        section.Content = content;
        section.EditedByHand = true;
        section.UpdatedBy = access.Email;
        section.UpdatedAtUtc = UtcNow;
        audit.Record(db, "ProposalSection", section.Id, "Edited", null, new { section.Key, Characters = content.Length });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(section);
    }

    public async Task<ProposalFileDto> ExportAsync(Guid opportunityId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var opportunity = await db.Set<D.Opportunity>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == opportunityId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Opportunity", opportunityId);
        var proposal = await LoadAsync(db, opportunityId, cancellationToken);
        if (proposal.Sections.Count == 0)
        {
            throw new ArgumentException("Draft the proposal first.");
        }

        var safe = new string($"{opportunity.Number} proposal draft".Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' ? c : '_').ToArray());
        return new ProposalFileDto($"{safe}.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Docx($"{opportunity.Name}", $"{opportunity.Number} · draft {DateOnly.FromDateTime(UtcNow):yyyy-MM-dd}", proposal.Sections));
    }

    /// <summary>The Word document: a title, then each section as a heading and paragraphs ("- " lines keep their dash).</summary>
    public static byte[] Docx(string title, string subtitle, IReadOnlyList<ProposalSectionDto> sections)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);
            body.Append(Paragraph(title, bold: true, size: 40));
            body.Append(Paragraph(subtitle, bold: false, size: 20));
            foreach (var section in sections.OrderBy(s => s.Order))
            {
                body.Append(Paragraph(section.Title, bold: true, size: 30, spaceBefore: 360));
                foreach (var line in section.Content.Split('\n'))
                {
                    body.Append(Paragraph(line, bold: false, size: 22, indent: line.StartsWith("- ", StringComparison.Ordinal) ? 360 : 0));
                }
            }

            main.Document.Save();
        }

        return stream.ToArray();
    }

    private static Paragraph Paragraph(string text, bool bold, int size, int spaceBefore = 0, int indent = 0)
    {
        var props = new RunProperties(new FontSize { Val = size.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        if (bold)
        {
            props.Append(new Bold());
        }

        var paragraphProps = new ParagraphProperties(new SpacingBetweenLines { Before = spaceBefore.ToString(System.Globalization.CultureInfo.InvariantCulture), After = "80" });
        if (indent > 0)
        {
            paragraphProps.Append(new Indentation { Left = indent.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        return new Paragraph(paragraphProps, new Run(props, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private async Task<(Dictionary<string, string> Texts, HashSet<string> Assisted)> WriteAsync(ProposalFacts facts, IReadOnlyList<string> keys, Guid opportunityId, CancellationToken ct)
    {
        var texts = ProposalTemplate.Sections.ToDictionary(s => s.Key, s => ProposalTemplate.Build(s.Key, facts));
        var assisted = new HashSet<string>(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return (texts, assisted);
        }

        var draft = new { sections = keys.Select(k => new { key = k, title = ProposalTemplate.Sections.First(s => s.Key == k).Title, content = texts[k] }) };
        var completion = await runner.CompleteTextAsync(
            AssistantInteractionKind.ProposalDraft,
            $"Draft {keys.Count} proposal section{(keys.Count == 1 ? "" : "s")} for opportunity {opportunityId}",
            AssistantPrompts.ProposalSystem,
            $"<company-profile>\n{JsonSerializer.Serialize(facts.CompanyCapabilities, AssistantPrompts.DataJson)}\n</company-profile>\n"
                + $"<workbench-data source=\"opportunity and capture plan\" untrusted=\"true\">\n{JsonSerializer.Serialize(facts with { CompanyCapabilities = null }, AssistantPrompts.DataJson)}\n</workbench-data>\n"
                + $"<draft-json>\n{JsonSerializer.Serialize(draft, AssistantPrompts.DataJson)}\n</draft-json>",
            Schema(),
            $"bd/opportunities/{opportunityId}",
            "opportunity, capture plan and company profile",
            [$"crm/opportunities/{opportunityId}"],
            ct);
        if (completion is not null)
        {
            foreach (var (key, text) in TryParse(completion.Text, keys.ToHashSet(StringComparer.Ordinal)))
            {
                texts[key] = text;
                assisted.Add(key);
            }
        }

        return (texts, assisted);
    }

    public static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["sections"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["key"] = new JsonObject { ["type"] = "string" },
                        ["content"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("key", "content"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("sections"),
        ["additionalProperties"] = false,
    };

    /// <summary>The model's text for the sections that were asked for: unknown keys and empty text are dropped, long text clipped.</summary>
    public static Dictionary<string, string> TryParse(string json, IReadOnlySet<string> asked)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }

        foreach (var item in (node as JsonObject)?["sections"] as JsonArray ?? [])
        {
            if (item is JsonObject o && o["key"]?.ToString() is { } key && asked.Contains(key)
                && o["content"]?.ToString()?.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() is { Length: > 0 } content)
            {
                result[key] = content.Length <= 30_000 ? content : content[..29_999] + "…";
            }
        }

        return result;
    }

    private async Task<ProposalDto> LoadAsync(IWorkbenchDbContext db, Guid opportunityId, CancellationToken ct)
    {
        var opportunity = await db.Set<D.Opportunity>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == opportunityId, ct)
            ?? throw WorkbenchNotFoundException.For("Opportunity", opportunityId);
        var sections = await db.Set<D.ProposalSection>().AsNoTracking().Where(s => s.OpportunityId == opportunityId).OrderBy(s => s.Order).ToListAsync(ct);
        return new ProposalDto(opportunityId, sections.Select(ToDto).ToList(), CrmAccess.CanEdit(opportunity, await access.RolesAsync(ct), access.Email));
    }

    private async Task<ProposalFacts> FactsAsync(IWorkbenchDbContext db, Guid opportunityId, CancellationToken ct)
    {
        var o = await db.Set<D.Opportunity>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == opportunityId, ct) ?? throw WorkbenchNotFoundException.For("Opportunity", opportunityId);
        var items = await db.Set<D.OpportunityPlanItem>().AsNoTracking().Where(i => i.OpportunityId == opportunityId).OrderBy(i => i.Order).ToListAsync(ct);
        var team = await db.Set<D.OpportunityTeammate>().AsNoTracking().Where(t => t.OpportunityId == opportunityId).OrderByDescending(t => t.WorksharePercent).ToListAsync(ct);
        var hasIncumbent = o.IncumbentAccountId is not null || o.IncumbentName is not null
            || await db.Set<D.OpportunityCompetitor>().AsNoTracking().AnyAsync(c => c.OpportunityId == opportunityId && c.IsIncumbent, ct);
        var emails = new[] { o.CaptureManagerEmail, o.ProposalManagerEmail }.OfType<string>().ToList();
        var names = emails.Count == 0 ? [] : await db.Set<AppUser>().AsNoTracking().Where(u => emails.Contains(u.Email)).ToDictionaryAsync(u => u.Email, u => u.DisplayName, ct);
        string? NameOf(string? email) => email is null ? null : names.GetValueOrDefault(email) ?? email;

        List<string> Texts(D.CapturePlanItemKind kind) => items.Where(i => i.Kind == kind).Select(i => string.IsNullOrWhiteSpace(i.Detail) ? i.Text : $"{i.Text}: {i.Detail}").ToList();

        var profile = (await db.Set<D.CrmSettings>().AsNoTracking().FirstOrDefaultAsync(ct))?.CompanyProfile;
        // The profile's descriptive lines (not the NAICS and status lines, which are for scoring).
        var capabilities = profile is null ? null : string.Join('\n', profile.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("NAICS", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("Status", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("Set-aside", StringComparison.OrdinalIgnoreCase)));
        return new ProposalFacts(o.Name, o.AgencyName, o.SolicitationNumber, o.NaicsCode, o.SetAside.ToString(), o.ContractType?.ToString(), o.PlaceOfPerformance,
            o.PeriodOfPerformanceMonths, o.Description, o.Kind == D.OpportunityKind.Recompete, hasIncumbent, NameOf(o.CaptureManagerEmail), NameOf(o.ProposalManagerEmail),
            Texts(D.CapturePlanItemKind.WinTheme), Texts(D.CapturePlanItemKind.Discriminator), Texts(D.CapturePlanItemKind.HotButton), Texts(D.CapturePlanItemKind.Strength),
            team.Select(t => (t.Name, t.Role.ToString(), t.WorksharePercent)).ToList(), string.IsNullOrWhiteSpace(capabilities) ? null : capabilities);
    }

    private static ProposalSectionDto ToDto(D.ProposalSection s) => new(s.Id, s.Key, s.Title, s.Content, s.Order, s.ByAssistant, s.EditedByHand, s.UpdatedBy, s.UpdatedAtUtc);
}
