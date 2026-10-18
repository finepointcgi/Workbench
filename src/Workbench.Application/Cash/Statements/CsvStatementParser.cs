using System.Globalization;
using System.Text;
using Workbench.Contracts.Cash;
using Workbench.Domain.Cash;

namespace Workbench.Application.Cash.Statements;

/// <summary>
/// Reads a bank's CSV export through a <see cref="CsvColumnMapping"/>: quoted fields, any one-character delimiter,
/// one signed amount column or separate debit and credit columns, and amounts like <c>(1,234.50)</c> or <c>$-12.00</c>.
/// </summary>
public static class CsvStatementParser
{
    private static readonly string[] CommonDateFormats =
        ["yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy", "MM/dd/yy", "M/d/yy", "yyyyMMdd", "dd-MMM-yyyy", "d-MMM-yyyy", "yyyy/MM/dd"];

    public static ParsedStatement Parse(string content, CsvColumnMapping mapping)
    {
        Validate(mapping);
        var delimiter = mapping.Delimiter is "\\t" or "tab" ? '\t' : mapping.Delimiter[0];
        var rows = ReadRows(content, delimiter).Where(r => r.Any(f => !string.IsNullOrWhiteSpace(f))).ToList();
        var statement = new ParsedStatement();
        if (rows.Count == 0)
        {
            return statement;
        }

        var header = mapping.HasHeader ? rows[0] : null;
        int? Col(string? spec, string label) => spec is null or "" ? null : Resolve(spec, header, label);
        var date = Col(mapping.Date, "date")!.Value;
        var description = Col(mapping.Description, "description")!.Value;
        var amount = Col(mapping.Amount, "amount");
        var debit = Col(mapping.Debit, "debit");
        var credit = Col(mapping.Credit, "credit");
        var check = Col(mapping.CheckNumber, "check number");
        var reference = Col(mapping.Reference, "reference");
        var type = Col(mapping.Type, "type");

        var first = mapping.HasHeader ? 1 : 0;
        for (var i = first; i < rows.Count; i++)
        {
            var row = rows[i];
            var line = i + 1;
            string? Field(int? index) => index is { } x && x < row.Count && !string.IsNullOrWhiteSpace(row[x]) ? row[x].Trim() : null;

            var dateText = Field(date) ?? throw new ArgumentException($"Line {line}: the date is empty.");
            var parsedDate = ParseDate(dateText, mapping.DateFormat)
                ?? throw new ArgumentException($"Line {line}: \"{dateText}\" isn't a date{(string.IsNullOrWhiteSpace(mapping.DateFormat) ? "" : $" in the format {mapping.DateFormat}")}.");

            decimal value;
            if (amount is not null)
            {
                value = ParseAmount(Field(amount), line) ?? throw new ArgumentException($"Line {line}: the amount is empty.");
            }
            else
            {
                var d = ParseAmount(Field(debit), line) ?? 0m;
                var c = ParseAmount(Field(credit), line) ?? 0m;
                value = Math.Abs(c) - Math.Abs(d);
            }

            if (mapping.InvertSign)
            {
                value = -value;
            }

            if (value == 0)
            {
                statement.Warnings.Add($"Line {line}: zero amount skipped.");
                continue;
            }

            var text = Field(description) ?? "";
            var checkNumber = NormalizeCheck(Field(check));
            var kind = TypeFrom(Field(type)) ?? ParsedStatement.InferType(value, text, checkNumber);
            statement.Transactions.Add(new ParsedBankTransaction(parsedDate, decimal.Round(value, 2), kind, text, checkNumber, Field(reference)));
        }

        return statement.Complete();
    }

    private static void Validate(CsvColumnMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.Date) || string.IsNullOrWhiteSpace(mapping.Description))
        {
            throw new ArgumentException("The CSV mapping needs a date column and a description column.");
        }

        if (string.IsNullOrWhiteSpace(mapping.Amount) && string.IsNullOrWhiteSpace(mapping.Debit) && string.IsNullOrWhiteSpace(mapping.Credit))
        {
            throw new ArgumentException("The CSV mapping needs an amount column, or debit and credit columns.");
        }

        if (string.IsNullOrEmpty(mapping.Delimiter))
        {
            throw new ArgumentException("Choose a delimiter (a comma, semicolon or tab).");
        }
    }

    private static int Resolve(string spec, List<string>? header, string label)
    {
        spec = spec.Trim();
        if (int.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1)
        {
            return number - 1;
        }

        if (header is null)
        {
            throw new ArgumentException($"The {label} column \"{spec}\" is a name, but the file has no header row; use a column number.");
        }

        var index = header.FindIndex(h => string.Equals(h.Trim(), spec, StringComparison.OrdinalIgnoreCase));
        return index >= 0
            ? index
            : throw new ArgumentException($"The file has no \"{spec}\" column for the {label}. Columns: {string.Join(", ", header.Select(h => h.Trim()))}.");
    }

    public static DateOnly? ParseDate(string text, string? format)
    {
        text = text.Trim();
        if (!string.IsNullOrWhiteSpace(format))
        {
            return DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact) ? exact : null;
        }

        if (DateOnly.TryParseExact(text, CommonDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }

        // A timestamp column ("2026-10-05 00:00:00").
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? DateOnly.FromDateTime(dt) : null;
    }

    public static decimal? ParseAmount(string? text, int line)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim().Replace("$", "").Replace(",", "").Replace(" ", "");
        var negative = false;
        if (t.StartsWith('(') && t.EndsWith(')'))
        {
            negative = true;
            t = t[1..^1];
        }

        if (t.EndsWith("CR", StringComparison.OrdinalIgnoreCase))
        {
            t = t[..^2];
        }
        else if (t.EndsWith("DR", StringComparison.OrdinalIgnoreCase))
        {
            negative = !negative;
            t = t[..^2];
        }

        if (!decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"Line {line}: \"{text}\" isn't an amount.");
        }

        return negative ? -value : value;
    }

    private static BankTransactionType? TypeFrom(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var t = text.Trim().ToUpperInvariant();
        return t switch
        {
            "CHECK" or "CHK" or "CHEQUE" => BankTransactionType.Check,
            "DEPOSIT" or "DEP" or "CREDIT" => BankTransactionType.Deposit,
            "ACH" or "ACH DEBIT" or "ACH CREDIT" or "EFT" => BankTransactionType.Ach,
            "WIRE" or "WIRE IN" or "WIRE OUT" => BankTransactionType.Wire,
            "FEE" or "SERVICE CHARGE" or "SRVCHG" => BankTransactionType.Fee,
            "INTEREST" or "INT" => BankTransactionType.Interest,
            "TRANSFER" or "XFER" => BankTransactionType.Transfer,
            _ => null,
        };
    }

    /// <summary>Check numbers without leading zeros, so "0010001" on the statement matches check 10001.</summary>
    public static string? NormalizeCheck(string? check)
    {
        if (string.IsNullOrWhiteSpace(check))
        {
            return null;
        }

        var t = check.Trim();
        var trimmed = t.TrimStart('0');
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Splits CSV text into rows of fields (RFC 4180 quoting; quoted fields may span lines).</summary>
    public static IEnumerable<List<string>> ReadRows(string content, char delimiter)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                yield return row;
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
