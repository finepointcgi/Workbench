using System.Text;

namespace Workbench.Ui.Help.Markdown;

/// <summary>
/// The small Markdown subset help articles use, parsed into a tree that <see cref="MarkdownRenderer"/> turns into
/// components. There is no HTML pass-through at all: anything that looks like a tag is plain text, and links are kept
/// only when their target is app-relative, <c>https:</c>/<c>http:</c>/<c>mailto:</c>, or one of the help schemes
/// (<c>term:eac</c> for a glossary tooltip, <c>help:slug</c> for another article).
/// <para>
/// Blocks: <c>#</c>–<c>####</c> headings, paragraphs, <c>-</c>/<c>*</c> and <c>1.</c> lists (one level), <c>&gt;</c>
/// quotes and <c>&gt; [!TIP]</c> / <c>[!NOTE]</c> / <c>[!WARNING]</c> callouts, fenced code, <c>---</c> rules and
/// pipe tables. Inline: <c>**strong**</c>, <c>*em*</c>/<c>_em_</c>, <c>`code`</c>, <c>[text](target)</c> and
/// <c>\</c> escapes.
/// </para>
/// </summary>
public sealed record MarkdownDocument(IReadOnlyDictionary<string, string> FrontMatter, IReadOnlyList<MdBlock> Blocks)
{
    public static MarkdownDocument Parse(string? markdown) => MarkdownParser.Parse(markdown ?? "");

    /// <summary>All the text, without markup (for search).</summary>
    public string PlainText()
    {
        var text = new StringBuilder();
        foreach (var block in Blocks)
        {
            block.AppendText(text);
            text.Append('\n');
        }

        return text.ToString();
    }
}

public abstract record MdBlock
{
    internal abstract void AppendText(StringBuilder text);

    internal static void Append(StringBuilder text, IEnumerable<MdInline> inlines)
    {
        foreach (var inline in inlines)
        {
            inline.AppendText(text);
        }
    }
}

public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Content, string Id) : MdBlock
{
    internal override void AppendText(StringBuilder text) => Append(text, Content);
}

public sealed record MdParagraph(IReadOnlyList<MdInline> Content) : MdBlock
{
    internal override void AppendText(StringBuilder text) => Append(text, Content);
}

public sealed record MdList(bool Ordered, IReadOnlyList<IReadOnlyList<MdInline>> Items, int Start = 1) : MdBlock
{
    internal override void AppendText(StringBuilder text)
    {
        foreach (var item in Items)
        {
            Append(text, item);
            text.Append('\n');
        }
    }
}

public enum MdCalloutKind
{
    Quote,
    Note,
    Tip,
    Warning,
}

public sealed record MdCallout(MdCalloutKind Kind, IReadOnlyList<IReadOnlyList<MdInline>> Paragraphs) : MdBlock
{
    internal override void AppendText(StringBuilder text)
    {
        foreach (var paragraph in Paragraphs)
        {
            Append(text, paragraph);
            text.Append('\n');
        }
    }
}

public sealed record MdCode(string Text) : MdBlock
{
    internal override void AppendText(StringBuilder text) => text.Append(Text);
}

public sealed record MdRule : MdBlock
{
    internal override void AppendText(StringBuilder text)
    {
    }
}

public sealed record MdTable(IReadOnlyList<IReadOnlyList<MdInline>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows) : MdBlock
{
    internal override void AppendText(StringBuilder text)
    {
        foreach (var cell in Header.Concat(Rows.SelectMany(r => r)))
        {
            Append(text, cell);
            text.Append(' ');
        }
    }
}

public abstract record MdInline
{
    internal abstract void AppendText(StringBuilder text);
}

public sealed record MdText(string Text) : MdInline
{
    internal override void AppendText(StringBuilder text) => text.Append(Text);
}

public sealed record MdStrong(IReadOnlyList<MdInline> Content) : MdInline
{
    internal override void AppendText(StringBuilder text) => MdBlock.Append(text, Content);
}

public sealed record MdEmphasis(IReadOnlyList<MdInline> Content) : MdInline
{
    internal override void AppendText(StringBuilder text) => MdBlock.Append(text, Content);
}

public sealed record MdCodeSpan(string Text) : MdInline
{
    internal override void AppendText(StringBuilder text) => text.Append(Text);
}

public enum MdLinkKind
{
    /// <summary>A page inside the app (no scheme, no leading //).</summary>
    App,
    External,
    Mail,
    /// <summary><c>term:key</c>: a glossary tooltip.</summary>
    Term,
    /// <summary><c>help:slug</c>: another help article.</summary>
    Help,
}

/// <summary>A link with a target that passed <see cref="MarkdownLinks.Classify"/>. Unsafe targets never become links.</summary>
public sealed record MdLink(MdLinkKind Kind, string Target, IReadOnlyList<MdInline> Content) : MdInline
{
    internal override void AppendText(StringBuilder text) => MdBlock.Append(text, Content);
}

/// <summary>Decides which link targets are allowed and what they point at.</summary>
public static class MarkdownLinks
{
    /// <summary>
    /// The kind and cleaned target of a link, or null when it isn't allowed (javascript:, data:, protocol-relative
    /// //host, backslashes, whitespace or control characters, ...).
    /// </summary>
    public static (MdLinkKind Kind, string Target)? Classify(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        var value = target.Trim();
        if (value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\' || c == '<' || c == '>' || c == '"' || c == '\''))
        {
            return null;
        }

        var colon = value.IndexOf(':');
        var firstDelimiter = value.IndexOfAny(['/', '?', '#']);
        var hasScheme = colon >= 0 && (firstDelimiter < 0 || colon < firstDelimiter);
        if (!hasScheme)
        {
            if (value.StartsWith("//", StringComparison.Ordinal))
            {
                return null;
            }

            return (MdLinkKind.App, value.TrimStart('/'));
        }

        var scheme = value[..colon].ToLowerInvariant();
        var rest = value[(colon + 1)..];
        switch (scheme)
        {
            case "https" or "http" when Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length > 0:
                return (MdLinkKind.External, uri.AbsoluteUri);
            case "mailto" when rest.Length > 0 && rest.Contains('@'):
                return (MdLinkKind.Mail, value);
            case "term" when IsKey(rest):
                return (MdLinkKind.Term, rest.ToLowerInvariant());
            case "help" when IsKey(rest.Split('#')[0]):
                return (MdLinkKind.Help, rest.ToLowerInvariant());
            default:
                return null;
        }
    }

    private static bool IsKey(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '#');
}

internal static class MarkdownParser
{
    public static MarkdownDocument Parse(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var index = 0;
        var frontMatter = ReadFrontMatter(lines, ref index);
        var blocks = new List<MdBlock>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);

        while (index < lines.Length)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                index++;
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                index++;
                var code = new List<string>();
                while (index < lines.Length && !lines[index].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[index]);
                    index++;
                }

                index++;
                blocks.Add(new MdCode(string.Join('\n', code)));
                continue;
            }

            if (HeadingLevel(trimmed) is { } level)
            {
                var text = trimmed[level..].Trim().TrimEnd('#').Trim();
                var content = Inline.Parse(text);
                blocks.Add(new MdHeading(level, content, UniqueId(Slug(PlainOf(content)), usedIds)));
                index++;
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                blocks.Add(new MdRule());
                index++;
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                blocks.Add(ReadQuote(lines, ref index));
                continue;
            }

            if (trimmed.StartsWith('|') && index + 1 < lines.Length && IsTableSeparator(lines[index + 1]))
            {
                blocks.Add(ReadTable(lines, ref index));
                continue;
            }

            if (ListMarker(line) is not null)
            {
                blocks.Add(ReadList(lines, ref index));
                continue;
            }

            blocks.Add(ReadParagraph(lines, ref index));
        }

        return new MarkdownDocument(frontMatter, blocks);
    }

    private static Dictionary<string, string> ReadFrontMatter(string[] lines, ref int index)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return values;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                index = i + 1;
                return values;
            }

            var colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                values[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim().Trim('"');
            }
        }

        // No closing marker: it wasn't front matter.
        values.Clear();
        return values;
    }

    private static int? HeadingLevel(string trimmed)
    {
        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        return level is >= 1 and <= 4 && trimmed.Length > level && trimmed[level] == ' ' ? level : null;
    }

    /// <summary>The marker of a list item line: (ordered, number, content start), or null.</summary>
    private static (bool Ordered, int Number, int ContentStart)? ListMarker(string line)
    {
        var start = 0;
        while (start < line.Length && line[start] == ' ')
        {
            start++;
        }

        if (start > 3 || start >= line.Length)
        {
            return null;
        }

        if ((line[start] == '-' || line[start] == '*' || line[start] == '+') && start + 1 < line.Length && line[start + 1] == ' ')
        {
            return (false, 0, start + 2);
        }

        var digits = start;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]) && digits - start < 4)
        {
            digits++;
        }

        if (digits > start && digits + 1 < line.Length && line[digits] is '.' or ')' && line[digits + 1] == ' ')
        {
            return (true, int.Parse(line[start..digits], System.Globalization.CultureInfo.InvariantCulture), digits + 2);
        }

        return null;
    }

    private static MdList ReadList(string[] lines, ref int index)
    {
        var first = ListMarker(lines[index])!.Value;
        var items = new List<StringBuilder>();
        while (index < lines.Length)
        {
            var line = lines[index];
            if (line.Trim().Length == 0)
            {
                // A blank line ends the list unless the next line carries on with another item.
                if (index + 1 < lines.Length && ListMarker(lines[index + 1]) is { } next && next.Ordered == first.Ordered)
                {
                    index++;
                    continue;
                }

                break;
            }

            if (ListMarker(line) is { } marker)
            {
                if (marker.Ordered != first.Ordered)
                {
                    break;
                }

                items.Add(new StringBuilder(line[marker.ContentStart..].Trim()));
            }
            else if (line.StartsWith("  ", StringComparison.Ordinal) && items.Count > 0)
            {
                items[^1].Append(' ').Append(line.Trim());
            }
            else
            {
                break;
            }

            index++;
        }

        return new MdList(first.Ordered, items.Select(i => Inline.Parse(i.ToString())).ToList(), first.Ordered ? Math.Max(1, first.Number) : 1);
    }

    private static MdCallout ReadQuote(string[] lines, ref int index)
    {
        var content = new List<string>();
        while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
        {
            var text = lines[index].TrimStart()[1..];
            content.Add(text.StartsWith(' ') ? text[1..] : text);
            index++;
        }

        var kind = MdCalloutKind.Quote;
        if (content.Count > 0)
        {
            var head = content[0].Trim();
            var marker = head.ToUpperInvariant() switch
            {
                var h when h.StartsWith("[!NOTE]", StringComparison.Ordinal) => (MdCalloutKind?)MdCalloutKind.Note,
                var h when h.StartsWith("[!TIP]", StringComparison.Ordinal) => MdCalloutKind.Tip,
                var h when h.StartsWith("[!WARNING]", StringComparison.Ordinal) || h.StartsWith("[!IMPORTANT]", StringComparison.Ordinal) => MdCalloutKind.Warning,
                _ => null,
            };
            if (marker is { } found)
            {
                kind = found;
                content[0] = head[(head.IndexOf(']') + 1)..].Trim();
            }
        }

        var paragraphs = new List<IReadOnlyList<MdInline>>();
        var current = new StringBuilder();
        foreach (var line in content.Append(""))
        {
            if (line.Trim().Length == 0)
            {
                if (current.Length > 0)
                {
                    paragraphs.Add(Inline.Parse(current.ToString()));
                    current.Clear();
                }

                continue;
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(line.Trim());
        }

        return new MdCallout(kind, paragraphs);
    }

    private static bool IsTableSeparator(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith('|') && trimmed.Contains('-') && trimmed.All(c => c is '|' or '-' or ':' or ' ');
    }

    private static MdTable ReadTable(string[] lines, ref int index)
    {
        var header = Cells(lines[index]);
        index += 2;
        var rows = new List<IReadOnlyList<IReadOnlyList<MdInline>>>();
        while (index < lines.Length && lines[index].Trim().StartsWith('|'))
        {
            var cells = Cells(lines[index]);
            while (cells.Count < header.Count)
            {
                cells.Add([]);
            }

            rows.Add(cells.Take(header.Count).ToList());
            index++;
        }

        return new MdTable(header, rows);
    }

    private static List<IReadOnlyList<MdInline>> Cells(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^1];
        }

        var cells = new List<string>();
        var cell = new StringBuilder();
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                cell.Append("\\|");
                i++;
            }
            else if (trimmed[i] == '|')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(trimmed[i]);
            }
        }

        cells.Add(cell.ToString());
        return cells.Select(c => (IReadOnlyList<MdInline>)Inline.Parse(c.Trim())).ToList();
    }

    private static MdParagraph ReadParagraph(string[] lines, ref int index)
    {
        var text = new StringBuilder();
        while (index < lines.Length)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || HeadingLevel(trimmed) is not null || trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith('>') || (text.Length > 0 && ListMarker(line) is not null) || trimmed is "---" or "***" or "___")
            {
                break;
            }

            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(trimmed);
            index++;
        }

        return new MdParagraph(Inline.Parse(text.ToString()));
    }

    internal static string PlainOf(IEnumerable<MdInline> inlines)
    {
        var text = new StringBuilder();
        MdBlock.Append(text, inlines);
        return text.ToString();
    }

    internal static string Slug(string text)
    {
        var slug = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if ((c == ' ' || c == '-') && slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().Trim('-') is { Length: > 0 } s ? s : "section";
    }

    private static string UniqueId(string id, HashSet<string> used)
    {
        var candidate = id;
        for (var n = 2; !used.Add(candidate); n++)
        {
            candidate = $"{id}-{n}";
        }

        return candidate;
    }
}

/// <summary>Inline markup: emphasis, code spans, links and escapes. Everything else is text.</summary>
internal static class Inline
{
    public static IReadOnlyList<MdInline> Parse(string text) => Parse(text, 0);

    private static List<MdInline> Parse(string text, int depth)
    {
        var result = new List<MdInline>();
        var buffer = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (buffer.Length > 0)
            {
                result.Add(new MdText(buffer.ToString()));
                buffer.Clear();
            }
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    result.Add(new MdCodeSpan(text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }

            if (depth < 4 && (c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                var marker = new string(c, 2);
                var end = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    result.Add(new MdStrong(Parse(text[(i + 2)..end], depth + 1)));
                    i = end + 2;
                    continue;
                }
            }

            if (depth < 4 && (c == '*' || c == '_') && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && (c == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                var end = FindClosing(text, c, i + 1);
                if (end > i + 1)
                {
                    Flush();
                    result.Add(new MdEmphasis(Parse(text[(i + 1)..end], depth + 1)));
                    i = end + 1;
                    continue;
                }
            }

            if (depth < 4 && c == '[')
            {
                var close = FindBracket(text, i);
                if (close > i && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var end = text.IndexOf(')', close + 2);
                    if (end > close)
                    {
                        Flush();
                        var label = Parse(text[(i + 1)..close], depth + 1);
                        if (MarkdownLinks.Classify(text[(close + 2)..end]) is { } link)
                        {
                            result.Add(new MdLink(link.Kind, link.Target, label));
                        }
                        else
                        {
                            // Not an allowed target: keep the words, drop the link.
                            result.AddRange(label);
                        }

                        i = end + 1;
                        continue;
                    }
                }
            }

            buffer.Append(c);
            i++;
        }

        Flush();
        return result;
    }

    private static bool IsEscapable(char c) => "\\`*_[](){}#+-.!|>".Contains(c);

    private static int FindClosing(string text, char marker, int from)
    {
        for (var j = from; j < text.Length; j++)
        {
            if (text[j] == '\\')
            {
                j++;
                continue;
            }

            if (text[j] == marker && !char.IsWhiteSpace(text[j - 1]) && (j + 1 >= text.Length || text[j + 1] != marker)
                && (marker == '*' || j + 1 >= text.Length || !char.IsLetterOrDigit(text[j + 1])))
            {
                return j;
            }
        }

        return -1;
    }

    private static int FindBracket(string text, int open)
    {
        var depth = 0;
        for (var j = open; j < text.Length; j++)
        {
            if (text[j] == '\\')
            {
                j++;
            }
            else if (text[j] == '[')
            {
                depth++;
            }
            else if (text[j] == ']' && --depth == 0)
            {
                return j;
            }
        }

        return -1;
    }
}
