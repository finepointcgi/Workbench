namespace Workbench.Ui.Dashboards;

public enum MdInlineKind
{
    Text,
    Bold,
    Italic,
    Code,
    Link,
}

/// <summary>A run of text. <see cref="Href"/> is set only for links with a safe target.</summary>
public sealed record MdInline(MdInlineKind Kind, string Text, string? Href = null);

public abstract record MdBlock;

public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;

public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;

public sealed record MdList(bool Ordered, IReadOnlyList<IReadOnlyList<MdInline>> Items) : MdBlock;

public sealed record MdCode(string Text) : MdBlock;

/// <summary>
/// A deliberately small Markdown subset (headings, paragraphs, bullet and numbered lists, fenced code, bold, italic,
/// inline code and links) parsed into a model that components render as text. Nothing is ever emitted as raw markup,
/// so HTML in the source shows up literally, and links are limited to http(s), mailto and relative targets.
/// </summary>
public static class SafeMarkdown
{
    public static IReadOnlyList<MdBlock> Parse(string? source)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrWhiteSpace(source))
        {
            return blocks;
        }

        var lines = source.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        List<IReadOnlyList<MdInline>>? list = null;
        var ordered = false;

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new MdParagraph(ParseInline(string.Join(' ', paragraph))));
                paragraph.Clear();
            }
        }

        void FlushList()
        {
            if (list is not null)
            {
                blocks.Add(new MdList(ordered, list));
                list = null;
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushList();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i].TrimEnd());
                }

                blocks.Add(new MdCode(string.Join('\n', code)));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                FlushList();
                continue;
            }

            var level = trimmed.TakeWhile(c => c == '#').Count();
            if (level is >= 1 and <= 6 && trimmed.Length > level && trimmed[level] == ' ')
            {
                FlushParagraph();
                FlushList();
                blocks.Add(new MdHeading(level, ParseInline(trimmed[(level + 1)..].Trim())));
                continue;
            }

            if (ListItem(trimmed) is { } item)
            {
                FlushParagraph();
                if (list is not null && ordered != item.Ordered)
                {
                    FlushList();
                }

                ordered = item.Ordered;
                list ??= [];
                list.Add(ParseInline(item.Text));
                continue;
            }

            FlushList();
            paragraph.Add(trimmed);
        }

        FlushParagraph();
        FlushList();
        return blocks;
    }

    public static IReadOnlyList<MdInline> ParseInline(string text)
    {
        var result = new List<MdInline>();
        var plain = new System.Text.StringBuilder();

        void Add(MdInline inline)
        {
            if (plain.Length > 0)
            {
                result.Add(new MdInline(MdInlineKind.Text, plain.ToString()));
                plain.Clear();
            }

            result.Add(inline);
        }

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`' && Closing(text, "`", i + 1) is var codeEnd and > 0)
            {
                Add(new MdInline(MdInlineKind.Code, text[(i + 1)..codeEnd]));
                i = codeEnd + 1;
            }
            else if (text.AsSpan(i).StartsWith("**") && Closing(text, "**", i + 2) is var boldEnd and > 0)
            {
                Add(new MdInline(MdInlineKind.Bold, text[(i + 2)..boldEnd]));
                i = boldEnd + 2;
            }
            else if (text[i] is '*' or '_' && i + 1 < text.Length && text[i + 1] != ' '
                     && Closing(text, text[i].ToString(), i + 1) is var italicEnd and > 0)
            {
                Add(new MdInline(MdInlineKind.Italic, text[(i + 1)..italicEnd]));
                i = italicEnd + 1;
            }
            else if (text[i] == '[' && Link(text, i) is { } link)
            {
                Add(link.Inline);
                i = link.End;
            }
            else
            {
                plain.Append(text[i]);
                i++;
            }
        }

        if (plain.Length > 0)
        {
            result.Add(new MdInline(MdInlineKind.Text, plain.ToString()));
        }

        return result;
    }

    /// <summary>True for http, https and mailto URLs and for relative links; false for javascript:, data: and the rest.</summary>
    public static bool IsSafeHref(string href)
    {
        href = href.Trim();
        if (href.Length == 0 || href.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && href.Contains(':'))
        {
            return uri.Scheme is "http" or "https" or "mailto";
        }

        // Relative: anything without a scheme before the first path, query or fragment character.
        var colon = href.IndexOf(':');
        var boundary = href.IndexOfAny(['/', '?', '#']);
        return colon < 0 || (boundary >= 0 && boundary < colon);
    }

    private static (bool Ordered, string Text)? ListItem(string line)
    {
        if (line.Length > 2 && line[0] is '-' or '*' or '+' && line[1] == ' ')
        {
            return (false, line[2..].Trim());
        }

        var digits = line.TakeWhile(char.IsAsciiDigit).Count();
        if (digits > 0 && line.Length > digits + 2 && line[digits] is '.' or ')' && line[digits + 1] == ' ')
        {
            return (true, line[(digits + 2)..].Trim());
        }

        return null;
    }

    private static int Closing(string text, string marker, int from)
    {
        if (from >= text.Length)
        {
            return -1;
        }

        var end = text.IndexOf(marker, from, StringComparison.Ordinal);
        return end > from ? end : -1;
    }

    private static (MdInline Inline, int End)? Link(string text, int start)
    {
        var close = text.IndexOf("](", start + 1, StringComparison.Ordinal);
        if (close < 0)
        {
            return null;
        }

        var end = text.IndexOf(')', close + 2);
        if (end < 0)
        {
            return null;
        }

        var label = text[(start + 1)..close];
        var href = text[(close + 2)..end].Trim();
        return IsSafeHref(href)
            ? (new MdInline(MdInlineKind.Link, label, href), end + 1)
            : (new MdInline(MdInlineKind.Text, label), end + 1);
    }
}
