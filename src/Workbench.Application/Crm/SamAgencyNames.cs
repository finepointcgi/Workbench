namespace Workbench.Application.Crm;

/// <summary>
/// Matches the agency or office text on a SAM.gov notice to an account name. SAM writes departments inverted ("VETERANS AFFAIRS,
/// DEPARTMENT OF", "ARMY, DEPARTMENT OF THE"), while accounts are named the way people say it ("Department of Veterans Affairs").
/// </summary>
public static class SamAgencyNames
{
    /// <summary>True when <paramref name="text"/> contains the account's name or acronym as whole words (case and punctuation ignored).</summary>
    public static bool Matches(string accountName, string? acronym, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var words = Words(Uninvert(text));
        return words.Contains(Words(accountName), StringComparison.Ordinal)
            || (acronym is { Length: > 1 } && words.Contains(Words(acronym), StringComparison.Ordinal));
    }

    /// <summary>"VETERANS AFFAIRS, DEPARTMENT OF" becomes "DEPARTMENT OF VETERANS AFFAIRS"; anything else is returned as it came.</summary>
    internal static string Uninvert(string text)
    {
        var comma = text.IndexOf(',');
        if (comma <= 0)
        {
            return text;
        }

        var tail = text[(comma + 1)..].Trim();
        return tail.Equals("DEPARTMENT OF", StringComparison.OrdinalIgnoreCase) || tail.Equals("DEPARTMENT OF THE", StringComparison.OrdinalIgnoreCase)
            ? $"{tail} {text[..comma].Trim()}"
            : text;
    }

    /// <summary>Upper-cased words with the usual short forms spelled out ("DEPT" is "DEPARTMENT"), each wrapped in spaces for whole-word matching.</summary>
    private static string Words(string text) =>
        " " + string.Join(' ', text.ToUpperInvariant().Split([' ', '/', ',', '.', '-', '(', ')', '&'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w is "DEPT" or "DEPT." ? "DEPARTMENT" : w)) + " ";
}
