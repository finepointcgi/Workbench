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

public readonly record struct Token(TokenKind Kind, string Text, int Position, double Number = 0);

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
                tokens.Add(new Token(TokenKind.Number, literal, start, double.Parse(literal, CultureInfo.InvariantCulture)));
                continue;
            }

            if (c == '"')
            {
                tokens.Add(new Token(TokenKind.String, ReadString(text, ref i), start));
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

                tokens.Add(new Token(TokenKind.Reference, name, start));
                i = close + 1;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Identifier, text[start..i], start));
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
                _ => throw new FormulaParseException($"Unexpected character '{c}' at position {start + 1}.", start),
            };
            tokens.Add(new Token(kind, text.Substring(start, length), start));
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
