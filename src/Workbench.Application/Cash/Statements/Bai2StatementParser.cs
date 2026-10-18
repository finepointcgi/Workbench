using System.Globalization;
using Workbench.Domain.Cash;

namespace Workbench.Application.Cash.Statements;

/// <summary>
/// Reads BAI2 (Cash Management Balance Reporting Specification, version 2) files: the group's as-of date, the account
/// identifier (03) with its summary amounts (010 opening ledger, 015 closing ledger) and the transaction detail (16)
/// records, with 88 continuations. Amounts carry two implied decimals; detail type codes 100–399 are credits and
/// 400–699 debits.
/// </summary>
public static class Bai2StatementParser
{
    public static bool LooksLikeBai2(string content) => content.TrimStart().StartsWith("01,", StringComparison.Ordinal);

    /// <summary>
    /// Parses the account whose number ends with <paramref name="accountLast4"/> (any account when the file has only one);
    /// several groups (days) for that account become one statement.
    /// </summary>
    public static ParsedStatement Parse(string content, string? accountLast4 = null)
    {
        if (!LooksLikeBai2(content))
        {
            throw new ArgumentException("This isn't a BAI2 file (it should start with an 01 file header record).");
        }

        var records = LogicalRecords(content);
        var accounts = records.Where(r => r[0] == "03").Select(r => Field(r, 1)).Distinct().ToList();
        string? wanted;
        if (accounts.Count <= 1)
        {
            wanted = accounts.FirstOrDefault();
        }
        else
        {
            wanted = accountLast4 is null ? null : accounts.FirstOrDefault(a => a.EndsWith(accountLast4, StringComparison.Ordinal));
            if (wanted is null)
            {
                throw new ArgumentException($"The BAI2 file has {accounts.Count} accounts ({string.Join(", ", accounts.Select(Mask))}) and none ends in {accountLast4}.");
            }
        }

        var statement = new ParsedStatement { AccountNumber = wanted };
        DateOnly? asOf = null;
        var inAccount = false;
        foreach (var r in records)
        {
            switch (r[0])
            {
                case "02":
                    asOf = ParseDate(Field(r, 4));
                    statement.PeriodStart = statement.PeriodStart is { } s && s < asOf ? s : asOf;
                    statement.PeriodEnd = statement.PeriodEnd is { } e && e > asOf ? e : asOf;
                    break;
                case "03":
                    inAccount = Field(r, 1) == wanted;
                    if (inAccount)
                    {
                        ReadSummaries(r, statement);
                    }

                    break;
                case "16" when inAccount:
                    statement.Transactions.Add(ReadDetail(r, asOf ?? throw new ArgumentException("A BAI2 16 record comes before any 02 group header.")));
                    break;
                case "49":
                    inAccount = false;
                    break;
            }
        }

        return statement.Complete();
    }

    /// <summary>Joins 88 continuation records onto the record they continue; drops each line's trailing "/".</summary>
    private static List<string[]> LogicalRecords(string content)
    {
        var lines = content.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var joined = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.EndsWith('/') ? raw[..^1] : raw;
            if (line.StartsWith("88,", StringComparison.Ordinal) && joined.Count > 0)
            {
                // A 16 record's continuation carries on its free text; elsewhere it carries on the fields.
                joined[^1] += (joined[^1].StartsWith("16,", StringComparison.Ordinal) ? " " : ",") + line[3..];
            }
            else
            {
                joined.Add(line);
            }
        }

        return joined.Select(l => l.Split(',')).ToList();
    }

    private static void ReadSummaries(string[] r, ParsedStatement statement)
    {
        // 03,account,currency,[type,amount,itemCount,fundsType(,funds fields)]...
        var i = 3;
        while (i < r.Length && r[i].Length > 0)
        {
            var type = r[i];
            var amount = ParseAmount(Field(r, i + 1));
            i += 3;
            i = SkipFunds(r, i);
            if (amount is null)
            {
                continue;
            }

            if (type == "010" && statement.OpeningBalance is null)
            {
                statement.OpeningBalance = amount;
            }
            else if (type == "015")
            {
                statement.ClosingBalance = amount;
            }
        }
    }

    private static ParsedBankTransaction ReadDetail(string[] r, DateOnly asOf)
    {
        // 16,type,amount,fundsType(,funds fields),bankReference,customerReference,text...
        var typeCode = Field(r, 1);
        var amount = ParseAmount(Field(r, 2)) ?? 0m;
        var i = SkipFunds(r, 3);
        var bankRef = Field(r, i);
        var customerRef = Field(r, i + 1);
        var text = i + 2 < r.Length ? string.Join(",", r[(i + 2)..]).Trim() : "";
        var code = int.TryParse(typeCode, NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : 0;
        var signed = code is >= 400 and < 700 ? -Math.Abs(amount) : Math.Abs(amount);
        var type = code switch
        {
            474 or 475 => BankTransactionType.Check,
            >= 140 and <= 169 or >= 450 and <= 469 => BankTransactionType.Ach,
            >= 191 and <= 199 or >= 491 and <= 499 => BankTransactionType.Wire,
            301 or 100 or 108 or 115 => BankTransactionType.Deposit,
            354 or 350 or 351 => BankTransactionType.Interest,
            661 or 698 or 699 or 690 => BankTransactionType.Fee,
            >= 201 and <= 209 or >= 501 and <= 509 => BankTransactionType.Transfer,
            _ => ParsedStatement.InferType(signed, text, null),
        };
        var check = type == BankTransactionType.Check ? CsvStatementParser.NormalizeCheck(customerRef.Length > 0 ? customerRef : bankRef) : null;
        var description = text.Length > 0 ? text : $"BAI {typeCode}";
        var reference = bankRef.Length > 0 ? bankRef : null;
        if (type != BankTransactionType.Check && customerRef.Length > 0)
        {
            description = $"{description} {customerRef}";
        }

        return new ParsedBankTransaction(asOf, signed, type, description, check, reference);
    }

    /// <summary>Skips the funds-type field at <paramref name="i"/> and the availability fields that follow it.</summary>
    private static int SkipFunds(string[] r, int i)
    {
        var fundsType = Field(r, i);
        i++;
        return fundsType.ToUpperInvariant() switch
        {
            "S" => i + 3,
            "V" => i + 2,
            "D" => i + 1 + 2 * (int.TryParse(Field(r, i), out var n) ? n : 0),
            _ => i,
        };
    }

    private static string Field(string[] r, int i) => i < r.Length ? r[i].Trim() : "";

    private static decimal? ParseAmount(string text) =>
        text.Length == 0 ? null
        : long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var cents) ? cents / 100m
        : throw new ArgumentException($"\"{text}\" isn't a BAI2 amount (whole cents, no decimal point).");

    private static DateOnly ParseDate(string text) =>
        DateOnly.TryParseExact(text, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new ArgumentException($"\"{text}\" isn't a BAI2 date (YYMMDD).");

    private static string Mask(string account) => account.Length <= 4 ? account : "…" + account[^4..];
}
