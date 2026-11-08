using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Assistant;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Receipts;
using Workbench.Contracts;
using Workbench.Contracts.Crm;
using Workbench.Domain.Assistant;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.Crm;

/// <summary>Getting text out of an uploaded solicitation: a PDF with a text layer, a Word document, or plain text.</summary>
public static class SolicitationText
{
    public const int MaxBytes = 25 * 1024 * 1024;
    public const int MaxPdfPages = 400;
    public const int MaxCharacters = 1_500_000;

    public static string Read(string fileName, byte[] content)
    {
        if (content.Length == 0)
        {
            throw new ArgumentException("The file is empty.");
        }

        if (content.Length > MaxBytes)
        {
            throw new ArgumentException($"The file is larger than {MaxBytes / (1024 * 1024)} MB. Paste the sections that matter (L, M and the work statement) instead.");
        }

        string text;
        if (ReceiptText.IsPdf(fileName, content))
        {
            text = ReadPdf(content);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("The PDF has no text layer (it's probably a scan), or it's damaged or protected. Paste the text instead, or save a text copy of it.");
            }
        }
        else if (fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) || content.AsSpan().StartsWith("PK"u8))
        {
            text = ReadDocx(content);
        }
        else
        {
            text = Encoding.UTF8.GetString(content);
        }

        return text.Length <= MaxCharacters ? text : text[..MaxCharacters];
    }

    /// <summary>The PDF's text in reading order, a line per line of the page; empty when there is no text layer or it can't be read.</summary>
    private static string ReadPdf(byte[] content)
    {
        try
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(content);
            var pages = new List<string>();
            foreach (var page in document.GetPages().Take(MaxPdfPages))
            {
                pages.Add(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page));
            }

            return string.Join('\n', pages);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "";
        }
    }

    /// <summary>
    /// Word's automatic list numbers aren't in the paragraph text, so "1.1" and "(a)" would be lost. This walks the document
    /// in order, counts each list level the way Word does and returns the number a reader sees. A child like "(a)" gets its
    /// parent in front ("1.2(a)") so a matrix row says which item it is. Bullets get no number.
    /// </summary>
    private sealed class WordNumbering(MainDocumentPart? main)
    {
        private readonly Numbering? numbering = main?.NumberingDefinitionsPart?.Numbering;
        private readonly Styles? styles = main?.StyleDefinitionsPart?.Styles;
        private readonly Dictionary<int, int?[]> counters = [];
        private readonly Dictionary<int, string?[]> labels = [];

        public string? Label(Paragraph p)
        {
            if (numbering is null || NumberingOf(p) is not (int numId, int level) || level is < 0 or > 8)
            {
                return null;
            }

            var instance = numbering.Elements<NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
            var abstractId = instance?.AbstractNumId?.Val?.Value;
            var abstractNum = numbering.Elements<AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId);
            if (abstractNum is null)
            {
                return null;
            }

            var key = abstractId!.Value;
            var counts = counters.TryGetValue(key, out var c) ? c : counters[key] = new int?[9];
            var full = labels.TryGetValue(key, out var l) ? l : labels[key] = new string?[9];
            Level? Def(int i) => abstractNum.Elements<Level>().FirstOrDefault(x => x.LevelIndex?.Value == i);
            int Start(int i) => Def(i)?.StartNumberingValue?.Val?.Value ?? 1;

            counts[level] = counts[level] is { } n ? n + 1 : Start(level);
            for (var deeper = level + 1; deeper < 9; deeper++)
            {
                counts[deeper] = null;
                full[deeper] = null;
            }

            var def = Def(level);
            var format = def?.NumberingFormat?.Val?.Value;
            if (def is null || format == NumberFormatValues.Bullet || format == NumberFormatValues.None)
            {
                return null;
            }

            var pattern = def.LevelText?.Val?.Value ?? $"%{level + 1}.";
            var rendered = System.Text.RegularExpressions.Regex.Replace(pattern, "%([1-9])", m =>
            {
                var at = m.Groups[1].Value[0] - '1';
                return Format(counts[at] ?? Start(at), Def(at)?.NumberingFormat?.Val?.Value);
            });
            var bare = rendered.Trim().TrimEnd('.');
            var hasParents = Enumerable.Range(1, level).Any(i => pattern.Contains($"%{i}", StringComparison.Ordinal));
            var parent = level > 0 ? full[level - 1] : null;
            var label = !hasParents && parent is not null ? parent + bare : bare;
            full[level] = label;
            // A bare number reads as "1)" so it is kept as a reference; "1.1", "A" and "(a)" already are one.
            return label.All(char.IsDigit) ? label + ")" : label;
        }

        private (int NumId, int Level)? NumberingOf(Paragraph p)
        {
            var direct = p.ParagraphProperties?.NumberingProperties;
            var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            var fromStyle = styleId is null ? null : styles?.Elements<Style>().FirstOrDefault(s => s.StyleId?.Value == styleId)?.StyleParagraphProperties?.NumberingProperties;
            var id = direct?.NumberingId?.Val?.Value ?? fromStyle?.NumberingId?.Val?.Value;
            if (id is null or 0)
            {
                return null;
            }

            return (id.Value, direct?.NumberingLevelReference?.Val?.Value ?? fromStyle?.NumberingLevelReference?.Val?.Value ?? 0);
        }

        private static string Format(int n, NumberFormatValues? format)
        {
            if (format == NumberFormatValues.LowerLetter || format == NumberFormatValues.UpperLetter)
            {
                var letters = new string((char)('a' + ((n - 1) % 26)), (n - 1) / 26 + 1);
                return format == NumberFormatValues.UpperLetter ? letters.ToUpperInvariant() : letters;
            }

            if (format == NumberFormatValues.LowerRoman || format == NumberFormatValues.UpperRoman)
            {
                string[] symbols = ["m", "cm", "d", "cd", "c", "xc", "l", "xl", "x", "ix", "v", "iv", "i"];
                int[] values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
                var roman = new StringBuilder();
                for (var i = 0; i < values.Length; i++)
                {
                    while (n >= values[i])
                    {
                        roman.Append(symbols[i]);
                        n -= values[i];
                    }
                }

                return format == NumberFormatValues.UpperRoman ? roman.ToString().ToUpperInvariant() : roman.ToString();
            }

            return format == NumberFormatValues.DecimalZero ? n.ToString("00", CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// A Word document's text a block at a time, as pasted text would be: one block per paragraph, one per table row (cells
    /// joined with " | ", so a factor, its weight and what it is judged on stay together), blank lines between. Reading it as
    /// one line per paragraph with no gaps would run a heading and every item under it into a single "requirement".
    /// </summary>
    private static string WordBlocks(OpenXmlElement container, WordNumbering numbering)
    {
        var blocks = new List<string>();
        foreach (var element in container.ChildElements)
        {
            switch (element)
            {
                case Paragraph p:
                    blocks.Add(numbering.Label(p) is { } label ? $"{label} {p.InnerText}" : p.InnerText);
                    break;
                case Table table:
                    foreach (var row in table.Elements<TableRow>())
                    {
                        blocks.Add(string.Join(" | ", row.Elements<TableCell>().Select(c => string.Join(" ", c.Descendants<Paragraph>().Select(p => p.InnerText.Trim()).Where(t => t.Length > 0)))));
                    }

                    break;
                case SdtBlock or CustomXmlBlock:
                    blocks.Add(WordBlocks(element, numbering));
                    break;
            }
        }

        return string.Join("\n\n", blocks.Where(b => !string.IsNullOrWhiteSpace(b)));
    }

    private static string ReadDocx(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content);
            using var doc = WordprocessingDocument.Open(stream, false);
            var body = doc.MainDocumentPart?.Document?.Body ?? throw new ArgumentException("That Word document has no text.");
            return WordBlocks(body, new WordNumbering(doc.MainDocumentPart));
        }
        catch (Exception ex) when (ex is InvalidDataException or OpenXmlPackageException or IOException)
        {
            throw new ArgumentException("Couldn't read that file as a Word document. Save it as .docx or PDF, or paste the text.");
        }
    }
}

/// <summary>
/// Reads a solicitation into an opportunity's compliance matrix. <see cref="SolicitationParser"/> finds the requirements and
/// dates; when the assistant is on it then decides which lines are real requirements, their kind and the proposal section
/// that answers each (it never rewrites a line). New dates become milestones on the schedule, and lines already in the
/// matrix are skipped, so reading an amendment adds only what changed. Permissions are in the identity decorator.
/// </summary>
public sealed class RequirementService(IWorkbenchDbContextFactory dbFactory, CrmAccess access, AuditRecorder audit, AssistantRunner runner, TimeProvider clock) : IRequirementService
{
    private const int BatchSize = 40;
    private const int MaxBatches = 4;
    private static readonly HashSet<string> SectionKeys = new(ProposalTemplate.Sections.Select(s => s.Key), StringComparer.Ordinal);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<RequirementsDto> GetAsync(Guid opportunityId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await LoadAsync(db, opportunityId, cancellationToken);
    }

    public async Task<ShredResultDto> ShredAsync(Guid opportunityId, ShredRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = request.Content is { Length: > 0 } content ? SolicitationText.Read(request.FileName ?? "", content) : request.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Paste the solicitation text or choose a file.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await RequireOpportunityAsync(db, opportunityId, cancellationToken);
        var (parsed, parsedDates) = SolicitationParser.Parse(text);
        var notes = new List<string>();
        var existing = await db.Set<D.OpportunityRequirement>().Where(r => r.OpportunityId == opportunityId).ToListAsync(cancellationToken);
        var known = existing.Select(r => SolicitationParser.Normalize(r.Text)).ToHashSet(StringComparer.Ordinal);
        var fresh = parsed.Where(p => !known.Contains(SolicitationParser.Normalize(p.Text))).ToList();
        var duplicates = parsed.Count - fresh.Count;
        if (parsed.Count == 0)
        {
            notes.Add("No \"shall\", \"must\" or \"will be evaluated\" statements found. If this is a scan or a summary, paste the text of Sections L, M and the work statement.");
        }

        if (parsed.Count >= SolicitationParser.MaxRequirements)
        {
            notes.Add($"Stopped at {SolicitationParser.MaxRequirements} lines. Read the document in parts (Section L and M first) to get the rest.");
        }

        var (kept, assisted, dropped) = await RefineAsync(fresh, opportunityId, cancellationToken);
        if (dropped > 0)
        {
            notes.Add($"The assistant left out {dropped} line{(dropped == 1 ? "" : "s")} that weren't requirements.");
        }

        var order = existing.Count == 0 ? 0 : existing.Max(r => r.Order) + 1;
        foreach (var line in kept)
        {
            db.Set<D.OpportunityRequirement>().Add(new D.OpportunityRequirement
            {
                OpportunityId = opportunityId, Reference = line.Reference, Text = line.Text, Kind = line.Kind, Status = D.RequirementStatus.Open,
                ProposalSectionKey = line.SectionKey, Order = order++, ByAssistant = assisted.Contains(line), CreatedBy = access.Email, CreatedAtUtc = UtcNow,
            });
        }

        var dates = await AddDatesAsync(db, opportunityId, parsedDates, cancellationToken);
        audit.Record(db, OpportunityService.AuditEntityType, opportunityId, "Solicitation read", null,
            new { added = kept.Count, duplicates, dates = dates.Count(d => d.Added), byAssistant = assisted.Count });
        await db.SaveChangesAsync(cancellationToken);
        return new ShredResultDto(await LoadAsync(db, opportunityId, cancellationToken), kept.Count, duplicates, assisted.Count, dates, notes);
    }

    public async Task<RequirementDto> AddAsync(Guid opportunityId, SaveRequirementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await RequireOpportunityAsync(db, opportunityId, cancellationToken);
        var order = await db.Set<D.OpportunityRequirement>().Where(r => r.OpportunityId == opportunityId).Select(r => (int?)r.Order).MaxAsync(cancellationToken) ?? -1;
        var row = new D.OpportunityRequirement { OpportunityId = opportunityId, Text = "", Order = order + 1, CreatedBy = access.Email, CreatedAtUtc = UtcNow };
        Apply(row, request);
        db.Set<D.OpportunityRequirement>().Add(row);
        audit.Record(db, OpportunityService.AuditEntityType, opportunityId, "Requirement added", after: new { row.Reference, row.Text });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(row);
    }

    public async Task<RequirementDto> UpdateAsync(Guid requirementId, SaveRequirementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await LoadRowAsync(db, requirementId, cancellationToken);
        var before = new { row.Reference, row.Text, row.Kind, row.Status, row.ProposalSectionKey };
        Apply(row, request);
        audit.Record(db, OpportunityService.AuditEntityType, row.OpportunityId, "Requirement changed", before, new { row.Reference, row.Text, row.Kind, row.Status, row.ProposalSectionKey });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(row);
    }

    public async Task DeleteAsync(Guid requirementId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await LoadRowAsync(db, requirementId, cancellationToken);
        db.Set<D.OpportunityRequirement>().Remove(row);
        audit.Record(db, OpportunityService.AuditEntityType, row.OpportunityId, "Requirement removed", new { row.Reference, row.Text });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<RequirementFileDto> ExportAsync(Guid opportunityId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var opportunity = await db.Set<D.Opportunity>().AsNoTracking().Where(o => o.Id == opportunityId).Select(o => new { o.Name, o.SolicitationNumber }).FirstOrDefaultAsync(cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Opportunity", opportunityId);
        var items = (await LoadAsync(db, opportunityId, cancellationToken)).Items;
        var titles = ProposalTemplate.Sections.ToDictionary(s => s.Key, s => s.Title);
        var sb = new StringBuilder("Reference,Requirement,Type,Proposal section,Status,Notes\r\n");
        foreach (var r in items)
        {
            sb.Append(Csv(r.Reference)).Append(',').Append(Csv(r.Text)).Append(',').Append(Csv(RequirementLabels.Label(r.Kind))).Append(',')
                .Append(Csv(r.ProposalSectionKey is { } k && titles.TryGetValue(k, out var t) ? t : "")).Append(',').Append(Csv(RequirementLabels.Label(r.Status))).Append(',').Append(Csv(r.Notes)).Append("\r\n");
        }

        var slug = new string((opportunity.SolicitationNumber ?? opportunity.Name).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return new RequirementFileDto($"compliance-matrix-{(slug.Length == 0 ? "opportunity" : slug)}.csv", "text/csv", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray());
    }

    /// <summary>A CSV cell, quoted, with formula-looking text defanged so a spreadsheet won't run it.</summary>
    internal static string Csv(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private async Task<(List<ParsedRequirement> Kept, HashSet<ParsedRequirement> Assisted, int Dropped)> RefineAsync(List<ParsedRequirement> fresh, Guid opportunityId, CancellationToken ct)
    {
        var kept = new List<ParsedRequirement>();
        var assisted = new HashSet<ParsedRequirement>();
        var dropped = 0;
        var batches = fresh.Chunk(BatchSize).ToList();
        for (var i = 0; i < batches.Count; i++)
        {
            var batch = batches[i];
            var decisions = i < MaxBatches ? await AskAsync(batch, opportunityId, ct) : null;
            for (var n = 0; n < batch.Length; n++)
            {
                var line = batch[n];
                if (decisions is not null && decisions.TryGetValue(n, out var d))
                {
                    if (!d.Keep)
                    {
                        dropped++;
                        continue;
                    }

                    var refined = line with { Kind = d.Kind ?? line.Kind, SectionKey = d.Section ?? line.SectionKey };
                    kept.Add(refined);
                    assisted.Add(refined);
                }
                else
                {
                    kept.Add(line);
                }
            }
        }

        return (kept, assisted, dropped);
    }

    private async Task<Dictionary<int, Decision>?> AskAsync(ParsedRequirement[] batch, Guid opportunityId, CancellationToken ct)
    {
        var lines = batch.Select((r, n) => new { id = n.ToString(CultureInfo.InvariantCulture), reference = r.Reference, text = r.Text, draftKind = r.Kind.ToString(), draftSection = r.SectionKey }).ToList();
        var draft = new { items = batch.Select((r, n) => new { id = n.ToString(CultureInfo.InvariantCulture), keep = true, kind = r.Kind.ToString(), section = r.SectionKey ?? "" }) };
        var completion = await runner.CompleteTextAsync(
            AssistantInteractionKind.RequirementReading,
            $"Sort {batch.Length} solicitation line{(batch.Length == 1 ? "" : "s")} into a compliance matrix",
            AssistantPrompts.RequirementSystem,
            $"<workbench-data source=\"solicitation sentences\" untrusted=\"true\">\n{JsonSerializer.Serialize(lines, AssistantPrompts.DataJson)}\n</workbench-data>\n"
                + $"<draft-json>\n{JsonSerializer.Serialize(draft, AssistantPrompts.DataJson)}\n</draft-json>",
            Schema(),
            "bd/requirements",
            "sentences from the solicitation",
            [$"crm/opportunities/{opportunityId}"],
            ct);
        return completion is null ? null : TryParse(completion.Text, batch.Length);
    }

    internal sealed record Decision(bool Keep, D.RequirementKind? Kind, string? Section);

    public static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["items"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["keep"] = new JsonObject { ["type"] = "boolean" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Instruction", "Evaluation", "Technical", "Administrative") },
                        ["section"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("id", "keep", "kind", "section"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("items"),
        ["additionalProperties"] = false,
    };

    /// <summary>The model's decisions for known lines only: unknown ids are dropped, kinds and sections it invented fall back to the rules'. Null when nothing usable.</summary>
    internal static Dictionary<int, Decision>? TryParse(string json, int count)
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

        var decisions = new Dictionary<int, Decision>();
        foreach (var item in (node as JsonObject)?["items"] as JsonArray ?? [])
        {
            if (item is JsonObject o && int.TryParse(o["id"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 0 && id < count
                && o["keep"] is JsonValue keep && keep.TryGetValue<bool>(out var keepIt))
            {
                D.RequirementKind? kind = Enum.TryParse<D.RequirementKind>(o["kind"]?.ToString(), true, out var k) && Enum.IsDefined(k) ? k : null;
                var section = o["section"]?.ToString()?.Trim().ToLowerInvariant();
                decisions[id] = new Decision(keepIt, kind, section is not null && SectionKeys.Contains(section) ? section : null);
            }
        }

        return decisions.Count == 0 ? null : decisions;
    }

    private async Task<IReadOnlyList<ShredDateDto>> AddDatesAsync(IWorkbenchDbContext db, Guid opportunityId, List<ParsedDate> found, CancellationToken ct)
    {
        var present = (await db.Set<D.OpportunityMilestone>().AsNoTracking().Where(m => m.OpportunityId == opportunityId).Select(m => m.Kind).ToListAsync(ct)).ToHashSet();
        var result = new List<ShredDateDto>();
        foreach (var date in found)
        {
            var kind = (D.MilestoneKind)(int)date.Kind;
            var add = !present.Contains(kind);
            if (add)
            {
                db.Set<D.OpportunityMilestone>().Add(new D.OpportunityMilestone { OpportunityId = opportunityId, Kind = kind, Name = date.Name, DueOn = date.Date, Notes = "Read from the solicitation.", CreatedBy = access.Email, CreatedAtUtc = UtcNow });
                present.Add(kind);
            }

            result.Add(new ShredDateDto(date.Kind, date.Name, date.Date, add));
        }

        return result;
    }

    private static void Apply(D.OpportunityRequirement row, SaveRequirementRequest request)
    {
        var text = request.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            throw new ArgumentException("A requirement needs text.");
        }

        if (text.Length > 1200)
        {
            throw new ArgumentException("A requirement can be at most 1,200 characters. Split it into separate lines.");
        }

        if (!Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Status))
        {
            throw new ArgumentException("Unknown type or status.");
        }

        var section = string.IsNullOrWhiteSpace(request.ProposalSectionKey) ? null : request.ProposalSectionKey.Trim();
        if (section is not null && !SectionKeys.Contains(section))
        {
            throw new ArgumentException("Unknown proposal section.");
        }

        row.Text = text;
        row.Kind = (D.RequirementKind)(int)request.Kind;
        row.Status = (D.RequirementStatus)(int)request.Status;
        row.Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : Clip(request.Reference.Trim(), 64);
        row.ProposalSectionKey = section;
        row.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : Clip(request.Notes.Trim(), 2000);
        row.ByAssistant = false;
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    private static async Task RequireOpportunityAsync(IWorkbenchDbContext db, Guid opportunityId, CancellationToken ct)
    {
        if (!await db.Set<D.Opportunity>().AnyAsync(o => o.Id == opportunityId, ct))
        {
            throw WorkbenchNotFoundException.For("Opportunity", opportunityId);
        }
    }

    private static async Task<D.OpportunityRequirement> LoadRowAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<D.OpportunityRequirement>().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Requirement", id);

    private async Task<RequirementsDto> LoadAsync(IWorkbenchDbContext db, Guid opportunityId, CancellationToken ct)
    {
        var opportunity = await db.Set<D.Opportunity>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == opportunityId, ct)
            ?? throw WorkbenchNotFoundException.For("Opportunity", opportunityId);
        var items = await db.Set<D.OpportunityRequirement>().AsNoTracking().Where(r => r.OpportunityId == opportunityId).OrderBy(r => r.Order).ToListAsync(ct);
        return new RequirementsDto(opportunityId, items.Select(ToDto).ToList(), CrmAccess.CanEdit(opportunity, await access.RolesAsync(ct), access.Email));
    }

    private static RequirementDto ToDto(D.OpportunityRequirement r) =>
        new(r.Id, r.Reference, r.Text, (RequirementKind)(int)r.Kind, (RequirementStatus)(int)r.Status, r.ProposalSectionKey, r.Notes, r.Order, r.ByAssistant);
}
