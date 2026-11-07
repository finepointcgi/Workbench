using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Workbench.Contracts.Crm;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.Crm;

/// <summary>A requirement read from a solicitation: where it is, the sentence itself, what it is and which proposal section probably answers it.</summary>
public sealed record ParsedRequirement(string? Reference, string Text, D.RequirementKind Kind, string? SectionKey);

/// <summary>A key date read from a solicitation, e.g. questions due or proposals due.</summary>
public sealed record ParsedDate(MilestoneKind Kind, string Name, DateOnly Date);

/// <summary>
/// The rules half of the solicitation shredder. Splits the document into paragraphs, follows which Section it is in
/// (L instructions, M evaluation, C or the PWS for the work), and keeps every sentence that says a thing <i>shall</i>,
/// <i>must</i> or <i>will be evaluated</i>. Text is copied from the document, never written, so a line in the matrix is
/// always something the solicitation said. Dates come from lines that name what is due.
/// </summary>
public static partial class SolicitationParser
{
    public const int MaxRequirements = 300;
    private const int MaxTextLength = 600;

    public static (List<ParsedRequirement> Requirements, List<ParsedDate> Dates) Parse(string text)
    {
        var requirements = new List<ParsedRequirement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var section = Section.Other;
        string? sectionRef = null;
        foreach (var block in Blocks(text))
        {
            if (HeadingSection(block) is { } heading)
            {
                section = heading;
                sectionRef = null;
            }

            var reference = ReferenceOf(block) ?? sectionRef;
            if (ReferenceOf(block) is { } own && own.Length <= 3 && char.IsLetter(own[0]))
            {
                sectionRef = own;
            }

            // A table row (cells joined with " | " when a Word file is read) is one statement: the factor, its weight and what it is judged on.
            foreach (var sentence in block.Contains(" | ", StringComparison.Ordinal) ? [block.Replace(" | ", " · ", StringComparison.Ordinal)] : Sentences(block))
            {
                if (!Cue().IsMatch(sentence) || sentence.Length < 25)
                {
                    continue;
                }

                var clean = Clip(StripReference(sentence), MaxTextLength);
                if (!seen.Add(Normalize(clean)))
                {
                    continue;
                }

                var kind = KindOf(section, clean);
                requirements.Add(new ParsedRequirement(reference, clean, kind, SectionKeyOf(kind, clean)));
                if (requirements.Count >= MaxRequirements)
                {
                    return (requirements, Dates(text));
                }
            }
        }

        return (requirements, Dates(text));
    }

    public static List<ParsedDate> Dates(string text)
    {
        var found = new Dictionary<MilestoneKind, ParsedDate>();
        // A sentence at a time: "Questions are due Nov 30. Proposals are due Dec 21." holds two dates for two milestones.
        foreach (var raw in text.Split('\n').SelectMany(l => SentenceBreak().Split(l)))
        {
            var line = raw.Trim();
            if (line.Length is < 8 or > 400 || DateOf(line) is not { } date || date.Year is < 2000 or > 2100)
            {
                continue;
            }

            var kind = DateKind(line);
            if (kind is not null && !found.ContainsKey(kind.Value))
            {
                found[kind.Value] = new ParsedDate(kind.Value, CapturePlanLabels.Label(kind.Value), date);
            }
        }

        return found.Values.OrderBy(d => d.Date).ToList();
    }

    public static string Normalize(string text) => WhiteSpace().Replace(text.ToLowerInvariant(), " ").Trim();

    private enum Section { Other, Instructions, Evaluation, Work }

    private static MilestoneKind? DateKind(string line)
    {
        var l = line.ToLowerInvariant();
        if (l.Contains("question") && (l.Contains("due") || l.Contains("deadline") || l.Contains("submit") || l.Contains("by")))
        {
            return MilestoneKind.QuestionsDue;
        }

        if (l.Contains("industry day") || l.Contains("pre-proposal") || l.Contains("preproposal") || l.Contains("site visit"))
        {
            return MilestoneKind.IndustryDay;
        }

        if (l.Contains("oral"))
        {
            return MilestoneKind.Orals;
        }

        if (l.Contains("proposal") && (l.Contains("due") || l.Contains("submi")) || l.Contains("offers due") || l.Contains("offer due")
            || l.Contains("responses due") || l.Contains("response due") || l.Contains("closing date") || l.Contains("due date"))
        {
            return MilestoneKind.Submission;
        }

        if (l.Contains("issued") || l.Contains("release date") || l.Contains("rfp release"))
        {
            return MilestoneKind.RfpRelease;
        }

        return null;
    }

    private static DateOnly? DateOf(string line)
    {
        var m = LongDate().Match(line);
        if (m.Success && DateOnly.TryParse(m.Value.Replace("Sept", "Sep", StringComparison.OrdinalIgnoreCase).Replace(".", ""), CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var d))
        {
            return d;
        }

        m = SlashDate().Match(line);
        if (m.Success && DateOnly.TryParse(m.Value, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out d))
        {
            return d;
        }

        m = IsoDate().Match(line);
        return m.Success && DateOnly.TryParse(m.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : null;
    }

    private static Section? HeadingSection(string block)
    {
        var first = block.Split('\n')[0].Trim();
        if (first.Length > 140)
        {
            return null;
        }

        var l = first.ToLowerInvariant();
        if (SectionLetterOf(first) is { } letter)
        {
            return letter.ToUpperInvariant() switch
            {
                "L" => Section.Instructions,
                "M" => Section.Evaluation,
                "C" => Section.Work,
                _ => Section.Other,
            };
        }

        if (l.Contains("instructions, conditions") || l.Contains("instructions to offerors") || l.Contains("proposal instructions") || l.Contains("proposal preparation"))
        {
            return Section.Instructions;
        }

        if (l.Contains("evaluation factors") || l.Contains("evaluation criteria") || l.Contains("basis for award") || l.Contains("basis of award"))
        {
            return Section.Evaluation;
        }

        if (l.Contains("statement of work") || l.Contains("performance work statement") || l.Contains("scope of work") || l.StartsWith("pws", StringComparison.Ordinal) || l.StartsWith("sow", StringComparison.Ordinal))
        {
            return Section.Work;
        }

        return null;
    }

    private static D.RequirementKind KindOf(Section section, string text)
    {
        var l = text.ToLowerInvariant();
        if (section == Section.Evaluation || l.Contains("will be evaluated") || l.Contains("will evaluate") || l.Contains("evaluation factor") || l.Contains("will be rated") || l.Contains("will be scored"))
        {
            return D.RequirementKind.Evaluation;
        }

        if (section == Section.Instructions || l.Contains("offeror shall") || l.Contains("offerors shall") || l.Contains("proposal shall") || l.Contains("proposals shall") || l.Contains("page limit")
            || l.Contains("pages") && l.Contains("not exceed") || l.Contains("font") || l.Contains("volume"))
        {
            return D.RequirementKind.Instruction;
        }

        if (l.Contains("invoice") || l.Contains("payment") || l.Contains("far ") || l.Contains("dfars") || l.Contains("clause") || l.Contains("insurance") || l.Contains("registered in sam"))
        {
            return D.RequirementKind.Administrative;
        }

        return D.RequirementKind.Technical;
    }

    private static string? SectionKeyOf(D.RequirementKind kind, string text)
    {
        var l = text.ToLowerInvariant();
        if (l.Contains("past performance") || l.Contains("relevant experience") || l.Contains("references"))
        {
            return "past";
        }

        if (l.Contains("technical approach") || l.Contains("technical volume"))
        {
            return "approach";
        }

        if (l.Contains("risk management") || l.Contains("mitigat"))
        {
            return "risks";
        }

        if (l.Contains("staff") || l.Contains("personnel") || l.Contains("management") || l.Contains("transition") || l.Contains("quality") || l.Contains("organization"))
        {
            return "management";
        }

        if (l.Contains("understanding") || l.Contains("scope"))
        {
            return "requirement";
        }

        return kind == D.RequirementKind.Technical ? "approach" : null;
    }

    /// <summary>Paragraphs: blank-line separated, and a new one wherever a line starts with a section or paragraph number. Wrapped lines are joined.</summary>
    private static IEnumerable<string> Blocks(string text)
    {
        var current = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            var startsNew = line.Length == 0 || NumberedStart().IsMatch(line) || SectionLetterOf(line) is not null;
            if (startsNew && current.Length > 0)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }

            if (line.Length > 0)
            {
                current.Append(current.Length == 0 ? "" : " ").Append(line);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString().Trim();
        }
    }

    private static IEnumerable<string> Sentences(string block) =>
        SentenceSplit().Split(block).Select(s => s.Trim()).Where(s => s.Length > 0);

    private static string? ReferenceOf(string block)
    {
        var m = ReferenceStart().Match(block);
        return m.Success ? m.Groups[1].Value.TrimEnd('.', ')', ' ') : null;
    }

    private static string StripReference(string sentence)
    {
        var m = ReferenceStart().Match(sentence);
        return m.Success ? sentence[m.Length..].Trim() : sentence;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    [GeneratedRegex(@"(?<=[a-z0-9\)][.;])\s+(?=[A-Z])")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"\b(shall|must|is required to|are required to|will be evaluated|will evaluate|will be rated|will be scored|not exceed|page limit|should be submitted)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Cue();

    [GeneratedRegex(@"^\s*(?:\(?([A-M](?:\.\d+)+|\d+(?:\.\d+)+|[A-M]\.|\d+\)|\([a-z0-9]\)))\s+")]
    private static partial Regex ReferenceStart();

    [GeneratedRegex(@"^\s*(?:[A-M](?:\.\d+)+\s|\d+(?:\.\d+)+\s|\([a-z0-9]\)\s|[a-z]\.\s)")]
    private static partial Regex NumberedStart();

    [GeneratedRegex(@"^\s*(?:SECTION|Section)\s+([A-M])\b|^\s*([A-M])\.\s+[A-Z][A-Za-z,\- ]{4,}$")]
    private static partial Regex SectionLetterPattern();

    /// <summary>The letter of a "Section L" or "L. Instructions…" heading, or null.</summary>
    private static string? SectionLetterOf(string line)
    {
        var m = SectionLetterPattern().Match(line);
        return !m.Success ? null : m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    [GeneratedRegex(@"(?<=[.;])\s+(?=[A-Z(])")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"\b(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sept?(?:ember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\.?\s+\d{1,2},?\s+\d{4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex LongDate();

    [GeneratedRegex(@"\b\d{1,2}/\d{1,2}/\d{4}\b")]
    private static partial Regex SlashDate();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b")]
    private static partial Regex IsoDate();
}
