using System.Globalization;
using System.Text;

namespace Workbench.Domain.Formulas;

public enum TokenKind
{
    Number,
    String,

    /// <summary>A bracketed column reference such as <c>[Planned hours]</c>; the text is the name without brackets.</summary>
    Reference,
    Identifier,

    /// <summary>A cross-sheet reference such as <c>{Open risks}</c>; the text is the name without braces.</summary>
    SheetReference,

    /// <summary><c>@cell</c>: the value being tested inside a criterion (COUNTIFS, SUMIFS, COLLECT, ...).</summary>
    CurrentCell,
    Colon,
    Plus,
    Minus,
    Star,
    Slash,
    Caret,
    Ampersand,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    LeftParen,
    RightParen,
    Comma,
    End,
}

/// <summary>A token. <see cref="Length"/> is how many characters of the formula it covers (for references, the brackets too).</summary>
public readonly record struct Token(TokenKind Kind, string Text, int Position, double Number = 0, int Length = 0);

/// <summary>Thrown when formula text can't be tokenized or parsed. <see cref="Position"/> is a zero-based character index.</summary>
public sealed class FormulaParseException(string message, int position) : Exception(message)
{
    public int Position { get; } = position;
}

public static class FormulaTokenizer
{
    public static IReadOnlyList<Token> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }

                if (i < text.Length && text[i] == '.')
                {
                    i++;
                    while (i < text.Length && char.IsAsciiDigit(text[i]))
                    {
                        i++;
                    }
                }

                var literal = text[start..i];
                tokens.Add(new Token(TokenKind.Number, literal, start, double.Parse(literal, CultureInfo.InvariantCulture), i - start));
                continue;
            }

            if (c == '"')
            {
                var literal = ReadString(text, ref i);
                tokens.Add(new Token(TokenKind.String, literal, start, Length: i - start));
                continue;
            }

            if (c == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close < 0)
                {
                    throw new FormulaParseException($"Missing ']' to close the column reference at position {start + 1}.", start);
                }

                var name = text[(i + 1)..close].Trim();
                if (name.Length == 0)
                {
                    throw new FormulaParseException($"Empty column reference '[]' at position {start + 1}. Put a column name inside the brackets.", start);
                }

                tokens.Add(new Token(TokenKind.Reference, name, start, Length: close + 1 - start));
                i = close + 1;

                // Smartsheet's [Column]@row means this row's value, which is what [Column] already is.
                if (string.Compare(text, i, "@row", 0, 4, StringComparison.OrdinalIgnoreCase) == 0
                    && (i + 4 >= text.Length || !char.IsLetterOrDigit(text[i + 4])))
                {
                    i += 4;
                }

                continue;
            }

            if (c == '{')
            {
                var close = text.IndexOf('}', i + 1);
                if (close < 0)
                {
                    throw new FormulaParseException($"Missing '}}' to close the sheet reference at position {start + 1}.", start);
                }

                var name = text[(i + 1)..close].Trim();
                if (name.Length == 0)
                {
                    throw new FormulaParseException($"Empty sheet reference '{{}}' at position {start + 1}. Put the name of a cross-sheet reference inside the braces.", start);
                }

                tokens.Add(new Token(TokenKind.SheetReference, name, start, Length: close + 1 - start));
                i = close + 1;
                continue;
            }

            if (c == '@' && string.Compare(text, i + 1, "cell", 0, 4, StringComparison.OrdinalIgnoreCase) == 0
                && (i + 5 >= text.Length || !char.IsLetterOrDigit(text[i + 5])))
            {
                tokens.Add(new Token(TokenKind.CurrentCell, "@cell", start, Length: 5));
                i += 5;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Identifier, text[start..i], start, Length: i - start));
                continue;
            }

            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            (TokenKind kind, int length) = c switch
            {
                '+' => (TokenKind.Plus, 1),
                '-' => (TokenKind.Minus, 1),
                '*' => (TokenKind.Star, 1),
                '/' => (TokenKind.Slash, 1),
                '^' => (TokenKind.Caret, 1),
                '&' => (TokenKind.Ampersand, 1),
                '=' => (TokenKind.Equal, 1),
                '<' when next == '>' => (TokenKind.NotEqual, 2),
                '<' when next == '=' => (TokenKind.LessOrEqual, 2),
                '<' => (TokenKind.Less, 1),
                '>' when next == '=' => (TokenKind.GreaterOrEqual, 2),
                '>' => (TokenKind.Greater, 1),
                '(' => (TokenKind.LeftParen, 1),
                ')' => (TokenKind.RightParen, 1),
                ',' => (TokenKind.Comma, 1),
                ':' => (TokenKind.Colon, 1),
                _ => throw new FormulaParseException($"Unexpected character '{c}' at position {start + 1}.", start),
            };
            tokens.Add(new Token(kind, text.Substring(start, length), start, Length: length));
            i += length;
        }

        tokens.Add(new Token(TokenKind.End, "", text.Length));
        return tokens;
    }

    /// <summary>Reads a double-quoted string; a doubled quote (<c>""</c>) inside it is a literal quote.</summary>
    private static string ReadString(string text, ref int i)
    {
        var start = i;
        var builder = new StringBuilder();
        i++;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    builder.Append('"');
                    i += 2;
                    continue;
                }

                i++;
                return builder.ToString();
            }

            builder.Append(text[i++]);
        }

        throw new FormulaParseException($"Missing closing quote for the text that starts at position {start + 1}.", start);
    }
}
