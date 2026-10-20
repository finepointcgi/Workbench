using System.Security.Cryptography;
using System.Text;
using Workbench.Application.Cash.Statements;

namespace Workbench.Application.Travel;

/// <summary>A card transaction as a feed read it. <see cref="Amount"/> is positive for a charge, negative for a credit.</summary>
public sealed record CardFeedTransaction(
    string CardLast4,
    DateOnly TransactionDate,
    DateOnly? PostedDate,
    decimal Amount,
    string Merchant,
    string? MerchantCategory = null,
    string? ExternalId = null);

public sealed record CardFeedResult(IReadOnlyList<CardFeedTransaction> Transactions, IReadOnlyList<string> Warnings);

/// <summary>
/// A source of card transactions. Today these read statement files the issuer exports (CSV, OFX); a live issuer
/// connection would implement the same contract. Register more with <c>services.AddSingleton&lt;ICardFeed, MyFeed&gt;()</c>;
/// the import uses the first one whose <see cref="CanRead"/> says yes. Feeds never call a bank.
/// </summary>
public interface ICardFeed
{
    /// <summary>"CSV", "OFX", ...: recorded on the import.</summary>
    string Format { get; }

    bool CanRead(string fileName, string content);

    /// <summary>Reads the file. Throws <see cref="ArgumentException"/> when it can't (the message is shown to Finance).</summary>
    CardFeedResult Read(string content);
}

/// <summary>
/// OFX/QFX credit card statements (CCSTMTRS). The card is the statement's account (its last four digits); OFX signs
/// charges negative, so they're flipped to positive. FITID is the bank's id for de-duplication.
/// </summary>
public sealed class OfxCardFeed : ICardFeed
{
    public string Format => "OFX";

    public bool CanRead(string fileName, string content) => OfxStatementParser.LooksLikeOfx(content);

    public CardFeedResult Read(string content)
    {
        var statement = OfxStatementParser.Parse(content);
        var last4 = CardFeeds.Last4(statement.AccountNumber);
        var warnings = new List<string>(statement.Warnings);
        if (last4.Length == 0)
        {
            warnings.Add("The statement doesn't say which card it's for (no account number); its charges are unmatched until assigned.");
        }

        var rows = statement.Transactions
            .Select(t => new CardFeedTransaction(last4, t.Date, t.Date, -t.Amount, CardFeeds.Clean(t.Description, 300) ?? "(no description)", null, t.Reference))
            .ToList();
        return new CardFeedResult(rows, warnings);
    }
}

/// <summary>
/// CSV card statements with a header row. Columns are found by name: date ("Transaction date", "Date"), posted date,
/// card ("Card", "Card number", "Last 4", "Account"), amount ("Amount", or "Debit"/"Charge" and "Credit"), merchant
/// ("Merchant", "Description", "Payee"), category ("Category", "MCC") and the bank's id ("Reference", "Transaction ID").
/// Positive amounts are charges.
/// </summary>
public sealed class CsvCardFeed : ICardFeed
{
    public string Format => "CSV";

    public bool CanRead(string fileName, string content) =>
        fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || (!OfxStatementParser.LooksLikeOfx(content) && content.Contains(','));

    public CardFeedResult Read(string content)
    {
        var rows = CsvStatementParser.ReadRows(content.TrimStart('﻿'), ',').Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
        if (rows.Count == 0)
        {
            throw new ArgumentException("The file is empty.");
        }

        var header = rows[0].Select(h => Normalize(h)).ToList();
        int Find(params string[] names) => names.Select(n => header.IndexOf(Normalize(n))).FirstOrDefault(i => i >= 0, -1);
        var date = Find("transaction date", "trans date", "date", "purchase date");
        var posted = Find("posted date", "post date", "posting date");
        var card = Find("card", "card number", "card no", "last 4", "last4", "card last 4", "account", "account number");
        var amount = Find("amount", "transaction amount", "billing amount");
        var debit = Find("debit", "charge", "charges");
        var credit = Find("credit", "credits", "payment");
        var merchant = Find("merchant", "merchant name", "description", "payee", "vendor");
        var category = Find("category", "merchant category", "mcc", "mcc description");
        var reference = Find("reference", "reference number", "transaction id", "id", "fitid");
        if (date < 0 || merchant < 0 || (amount < 0 && debit < 0))
        {
            throw new ArgumentException("A card statement needs date, merchant (or description) and amount (or debit) columns. "
                + $"Columns found: {string.Join(", ", rows[0].Select(h => h.Trim()))}.");
        }

        var warnings = new List<string>();
        var result = new List<CardFeedTransaction>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var line = i + 1;
            string? Cell(int index) => index >= 0 && index < row.Count && row[index].Trim().Length > 0 ? row[index].Trim() : null;
            var on = Cell(date) is { } d ? CsvStatementParser.ParseDate(d, null) : null;
            if (on is null)
            {
                warnings.Add($"Line {line}: \"{Cell(date)}\" isn't a date; the row was skipped.");
                continue;
            }

            decimal? value = amount >= 0 ? CsvStatementParser.ParseAmount(Cell(amount), line) : null;
            if (value is null && (debit >= 0 || credit >= 0))
            {
                var charge = CsvStatementParser.ParseAmount(Cell(debit), line) ?? 0;
                var refund = CsvStatementParser.ParseAmount(Cell(credit), line) ?? 0;
                value = Math.Abs(charge) - Math.Abs(refund);
            }

            if (value is null or 0)
            {
                warnings.Add($"Line {line}: no amount; the row was skipped.");
                continue;
            }

            result.Add(new CardFeedTransaction(
                CardFeeds.Last4(Cell(card)), on.Value, Cell(posted) is { } p ? CsvStatementParser.ParseDate(p, null) : null, decimal.Round(value.Value, 2),
                CardFeeds.Clean(Cell(merchant), 300) ?? "(no description)", CardFeeds.Clean(Cell(category), 100), CardFeeds.Clean(Cell(reference), 200)));
        }

        return new CardFeedResult(result, warnings);
    }

    private static string Normalize(string header) => new(header.Trim().ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
}

public static class CardFeeds
{
    /// <summary>The last four digits of a card or account number ("**** 1234", "4111111111111234"); empty when there are none.</summary>
    public static string Last4(string? number)
    {
        var digits = new string((number ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 4 ? digits[^4..] : digits;
    }

    public static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length > max ? t[..max] : t;
    }

    /// <summary>
    /// The key a charge is de-duplicated by within its program: the bank's id when the feed has one, otherwise a hash of
    /// card, date, amount and merchant plus how many identical rows came before it in the same file (so two identical
    /// coffees on one day both import, and importing the same file twice imports nothing).
    /// </summary>
    public static IReadOnlyList<string> DedupeKeys(IReadOnlyList<CardFeedTransaction> rows)
    {
        var seen = new Dictionary<string, int>();
        var keys = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.ExternalId))
            {
                keys.Add($"id:{row.CardLast4}:{row.ExternalId.Trim()}");
                continue;
            }

            var basis = $"{row.CardLast4}|{row.TransactionDate:yyyy-MM-dd}|{row.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}|{row.Merchant.Trim().ToUpperInvariant()}";
            var n = seen.GetValueOrDefault(basis);
            seen[basis] = n + 1;
            keys.Add("h:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{basis}|{n}")))[..40]);
        }

        return keys;
    }
}
