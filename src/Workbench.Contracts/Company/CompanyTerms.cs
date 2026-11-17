namespace Workbench.Contracts.Company;

/// <summary>Looks up the word the product uses for a concept. Unknown keys come back as the key itself.</summary>
public interface ITerms
{
    CompanyType Type { get; }

    /// <summary>The word for <paramref name="key"/> ("contract", "contracts", "funded value"...). A capitalised key gives a capitalised word.</summary>
    string Get(string key);

    /// <summary>
    /// Rewords a finished sentence or title ("Contract C-100 has no funded value") by swapping every known phrase for this
    /// company type's word. Government contractors get the text back unchanged.
    /// </summary>
    string ReplaceTerms(string text);
}

/// <summary>
/// Every company-type word in one place. Keys are lower case; the first entry is the government contractor wording
/// (the default and the fallback), then professional services, then engineering and construction. Add a row to
/// reword a concept; a type that has no entry of its own uses the government wording. Plurals are their own rows.
/// </summary>
public sealed class CompanyTerms : ITerms
{
    private static readonly Dictionary<string, (string Gov, string Prof, string Eng)> Table = new(StringComparer.Ordinal)
    {
        ["contract"] = ("contract", "agreement", "contract"),
        ["contracts"] = ("contracts", "agreements", "contracts"),
        ["contract number"] = ("contract number", "agreement number", "contract number"),
        ["new contract"] = ("new contract", "new agreement", "new contract"),
        ["prime contract"] = ("prime contract", "client agreement", "prime contract"),
        ["contract value"] = ("contract value", "agreement value", "contract value"),
        ["contracts and milestones"] = ("contracts and milestones", "agreements and milestones", "contracts and milestones"),
        ["clin"] = ("CLIN", "billing line", "pay item"),
        ["clins"] = ("CLINs", "billing lines", "pay items"),
        ["contract line"] = ("contract line (CLIN)", "billing line", "pay item"),
        ["funding"] = ("funding", "authorized budget", "authorized budget"),
        ["funded value"] = ("funded value", "authorized budget", "authorized budget"),
        ["funded"] = ("funded", "authorized", "authorized"),
        ["unfunded"] = ("unfunded", "not yet authorized", "not yet authorized"),
        ["ceilings"] = ("ceilings", "not-to-exceed limits", "not-to-exceed limits"),
        ["ceiling"] = ("ceiling", "not-to-exceed", "not-to-exceed"),
        ["contract ceiling"] = ("contract ceiling", "not-to-exceed amount", "not-to-exceed amount"),
        ["period of performance"] = ("period of performance", "term", "contract duration"),
        ["pop"] = ("PoP", "term", "duration"),
        ["contracting officer"] = ("contracting officer", "client contact", "owner's representative"),
        ["contracting officer's representative"] = ("contracting officer's representative", "client contact", "owner's representative"),
        ["customer"] = ("customer", "customer", "customer"),
        ["customers"] = ("customers", "customers", "customers"),
        ["proposal"] = ("proposal", "proposal", "proposal"),
        ["award"] = ("award", "signed agreement", "award"),
        ["awarded"] = ("awarded", "signed", "awarded"),
        ["win"] = ("win", "win", "win"),
        ["modification"] = ("modification", "change order", "change order"),
        ["modifications"] = ("modifications", "change orders", "change orders"),
        ["task order"] = ("task order", "work order", "work order"),
        ["subcontract"] = ("subcontract", "subcontract", "subcontract"),
        ["option year"] = ("option year", "renewal term", "option period"),
        ["funding status"] = ("funding status", "budget status", "budget status"),
        ["contract funding"] = ("contract funding", "agreement budget", "contract budget"),
        ["contract setup"] = ("contract setup", "agreement setup", "contract setup"),
    };

    // Left out of Reword: words that also mean something else in ordinary sentences.
    private static readonly HashSet<string> NotReworded = new(StringComparer.Ordinal) { "award", "awarded", "win", "pop", "customer", "customers", "proposal", "subcontract" };

    private static readonly System.Text.RegularExpressions.Regex Articles = new(@"\b([Aa]) (agreement|authorized|owner's|not yet authorized)\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex?[] Patterns = new System.Text.RegularExpressions.Regex?[3];

    /// <summary>The government contractor wording: the default, and what an unknown company type gets.</summary>
    public static ITerms Default { get; } = new CompanyTerms(CompanyType.GovernmentContractor);

    private readonly int _column;

    public CompanyTerms(CompanyType type)
    {
        Type = type;
        _column = type switch { CompanyType.ProfessionalServices => 1, CompanyType.EngineeringConstruction => 2, _ => 0 };
    }

    public CompanyType Type { get; }

    public static ITerms For(CompanyType type) => type == CompanyType.GovernmentContractor ? Default : new CompanyTerms(type);

    /// <summary>Whether federal-only pages and fields show unless an admin says otherwise.</summary>
    public static bool ShowsGovernmentFeaturesByDefault(CompanyType type) => type == CompanyType.GovernmentContractor;

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return key;
        }

        var capital = char.IsUpper(key[0]);
        var lower = capital ? char.ToLowerInvariant(key[0]) + key[1..] : key;
        if (!Table.TryGetValue(lower, out var row))
        {
            return key;
        }

        var word = Pick(row);

        // "CLIN" and similar acronyms keep their case; ordinary words follow the key's capitalisation.
        if (capital && char.IsLower(word[0]))
        {
            return char.ToUpperInvariant(word[0]) + word[1..];
        }

        return word;
    }

    public string ReplaceTerms(string text)
    {
        if (_column == 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var pattern = Patterns[_column] ??= new System.Text.RegularExpressions.Regex(
            @"\b(" + string.Join("|", Table.Where(r => !NotReworded.Contains(r.Key) && !string.Equals(Pick(r.Value), r.Key, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Key).OrderByDescending(k => k.Length).Select(System.Text.RegularExpressions.Regex.Escape)) + @")\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var reworded = pattern.Replace(text, m =>
        {
            // Capitalise at the start of a sentence, or where the original was a Capitalised word; "CLINs" mid-sentence is an acronym, not a name.
            var acronym = m.Value.Length > 1 && char.IsUpper(m.Value[0]) && char.IsUpper(m.Value[1]);
            var before = text[..m.Index].TrimEnd();
            var capital = before.Length == 0 || before[^1] is '.' or '!' or '?' or ':' || (char.IsUpper(m.Value[0]) && !acronym);
            var lower = m.Value.ToLowerInvariant();
            return Get(capital ? char.ToUpperInvariant(lower[0]) + lower[1..] : lower);
        });

        // "a contract" became "a authorized budget": fix the article in front of the words that start with a vowel.
        return Articles.Replace(reworded, "$1n $2");
    }

    private string Pick((string Gov, string Prof, string Eng) row) => _column switch { 1 => row.Prof, 2 => row.Eng, _ => row.Gov };

    /// <summary>All keys the table knows (tests and docs).</summary>
    public static IReadOnlyCollection<string> Keys => Table.Keys;
}

public static class TermsExtensions
{
    /// <summary>
    /// For Razor: <c>[CascadingParameter] ITerms? Tm</c> then <c>@Tm.Term("contract")</c>. Null (no layout, as in tests)
    /// gives the government wording, so unchanged pages read exactly as before.
    /// </summary>
    public static string Term(this ITerms? terms, string key) => (terms ?? CompanyTerms.Default).Get(key);

    /// <summary>True for company types that are not government contractors (funding and ceiling inputs read as optional).</summary>
    public static bool IsCommercial(this ITerms? terms) => terms is not null && terms.Type != CompanyType.GovernmentContractor;

    /// <summary>Rewords a whole title or sentence (see <see cref="ITerms.ReplaceTerms"/>); null leaves it as written.</summary>
    public static string Reword(this ITerms? terms, string text) => terms is null ? text : terms.ReplaceTerms(text);
}
