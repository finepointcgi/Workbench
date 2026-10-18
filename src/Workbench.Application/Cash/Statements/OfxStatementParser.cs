using System.Globalization;
using System.Text.RegularExpressions;
using Workbench.Domain.Cash;

namespace Workbench.Application.Cash.Statements;

/// <summary>
/// Reads OFX bank statements, both OFX 1.x (SGML, as in Quicken's .qfx, where leaf elements have no closing tag) and
/// OFX 2.x (XML): the account (BANKACCTFROM/ACCTID), the period (DTSTART/DTEND), the ledger balance and each STMTTRN.
/// </summary>
public static partial class OfxStatementParser
{
    public static bool LooksLikeOfx(string content) =>
        content.Contains("<OFX>", StringComparison.OrdinalIgnoreCase) || content.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase);

    public static ParsedStatement Parse(string content)
    {
        if (!LooksLikeOfx(content))
        {
            throw new ArgumentException("This isn't an OFX or QFX file (no <OFX> element).");
        }

        var start = content.IndexOf("<OFX>", StringComparison.OrdinalIgnoreCase);
        var body = start >= 0 ? content[start..] : content;
        var statement = new ParsedStatement();
        string? openTransaction = null;
        Dictionary<string, string>? txn = null;
        var path = new Stack<string>();

        foreach (Match m in Tag().Matches(body))
        {
            var closing = m.Groups[1].Value == "/";
            var name = m.Groups[2].Value.ToUpperInvariant();
            var value = m.Groups[3].Value.Trim();
            if (closing)
            {
                if (name == "STMTTRN" && txn is not null)
                {
                    statement.Transactions.Add(ToTransaction(txn));
                    txn = null;
                    openTransaction = null;
                }

                // Pop back to the matching aggregate (SGML leaves have no closing tags, so they were never pushed).
                // An XML leaf's closing tag (</TRNAMT>) closes nothing that was pushed.
                if (path.Contains(name))
                {
                    while (path.Pop() != name)
                    {
                    }
                }

                continue;
            }

            if (value.Length == 0)
            {
                // An aggregate opens.
                path.Push(name);
                if (name == "STMTTRN")
                {
                    txn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    openTransaction = name;
                }

                continue;
            }

            var decoded = System.Net.WebUtility.HtmlDecode(value);
            if (openTransaction is not null && txn is not null)
            {
                txn[name] = decoded;
                continue;
            }

            var parent = path.Count > 0 ? path.Peek() : "";
            switch (name)
            {
                case "ACCTID" when parent is "BANKACCTFROM" or "CCACCTFROM":
                    statement.AccountNumber = decoded;
                    break;
                case "DTSTART" when parent == "BANKTRANLIST":
                    statement.PeriodStart = ParseDate(decoded);
                    break;
                case "DTEND" when parent == "BANKTRANLIST":
                    statement.PeriodEnd = ParseDate(decoded);
                    break;
                case "BALAMT" when parent == "LEDGERBAL":
                    statement.ClosingBalance = ParseAmount(decoded);
                    break;
            }
        }

        if (txn is not null)
        {
            statement.Transactions.Add(ToTransaction(txn));
        }

        // OFX DTEND is often the instant after the last day (midnight); keep it as the last posted day when it's later.
        if (statement.PeriodEnd is { } end && statement.Transactions.Count > 0 && end > statement.Transactions.Max(t => t.Date).AddDays(1))
        {
            statement.Warnings.Add($"The statement period ends {end:yyyy-MM-dd}, after its last transaction.");
        }

        return statement.Complete();
    }

    private static ParsedBankTransaction ToTransaction(Dictionary<string, string> t)
    {
        var date = t.TryGetValue("DTPOSTED", out var posted) ? ParseDate(posted) : throw new ArgumentException("An OFX transaction has no DTPOSTED.");
        var amount = t.TryGetValue("TRNAMT", out var a) ? ParseAmount(a) : throw new ArgumentException("An OFX transaction has no TRNAMT.");
        var name = t.GetValueOrDefault("NAME");
        var memo = t.GetValueOrDefault("MEMO");
        var description = string.Join(" ", new[] { name, memo }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        var check = CsvStatementParser.NormalizeCheck(t.GetValueOrDefault("CHECKNUM"));
        var reference = t.GetValueOrDefault("FITID") ?? t.GetValueOrDefault("REFNUM");
        var type = (t.GetValueOrDefault("TRNTYPE") ?? "").ToUpperInvariant() switch
        {
            "CHECK" => BankTransactionType.Check,
            "DEP" => BankTransactionType.Deposit,
            "INT" or "DIV" => BankTransactionType.Interest,
            "FEE" or "SRVCHG" => BankTransactionType.Fee,
            "XFER" => BankTransactionType.Transfer,
            "DIRECTDEP" or "DIRECTDEBIT" or "PAYMENT" => BankTransactionType.Ach,
            _ => ParsedStatement.InferType(amount, description, check),
        };
        return new ParsedBankTransaction(date, amount, type, description, check, reference);
    }

    /// <summary>OFX dates: <c>YYYYMMDD[HHMMSS[.XXX]][[+|-]offset:TZ]</c>; only the date part matters.</summary>
    public static DateOnly ParseDate(string text)
    {
        var digits = text.Trim();
        if (digits.Length < 8 || !DateOnly.TryParseExact(digits[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            throw new ArgumentException($"\"{text}\" isn't an OFX date.");
        }

        return d;
    }

    private static decimal ParseAmount(string text) =>
        decimal.TryParse(text.Trim().Replace(",", "."), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)
            ? decimal.Round(v, 2)
            : throw new ArgumentException($"\"{text}\" isn't an OFX amount.");

    [GeneratedRegex(@"<(/?)([A-Za-z0-9.]+)>([^<]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();
}
