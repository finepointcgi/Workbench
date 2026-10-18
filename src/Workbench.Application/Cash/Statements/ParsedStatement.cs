using Workbench.Domain.Cash;

namespace Workbench.Application.Cash.Statements;

/// <summary>A statement line as parsed, before duplicate detection. <see cref="Amount"/> is signed (deposits positive).</summary>
public sealed record ParsedBankTransaction(
    DateOnly Date,
    decimal Amount,
    BankTransactionType Type,
    string Description,
    string? CheckNumber = null,
    string? Reference = null);

/// <summary>What a statement parser read: the lines and, when the file states them, the period, balances and account.</summary>
public sealed class ParsedStatement
{
    public List<ParsedBankTransaction> Transactions { get; } = [];
    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }

    /// <summary>The account number in the file (OFX ACCTID, BAI2 03 record), for checking it's the right account.</summary>
    public string? AccountNumber { get; set; }

    public List<string> Warnings { get; } = [];

    /// <summary>Fills in the period from the transaction dates when the file didn't state one.</summary>
    public ParsedStatement Complete()
    {
        if (Transactions.Count > 0)
        {
            PeriodStart ??= Transactions.Min(t => t.Date);
            PeriodEnd ??= Transactions.Max(t => t.Date);
        }

        return this;
    }

    /// <summary>
    /// The item type from a signed amount and the words in its description, for formats that don't say
    /// (CSV without a type column, OFX's generic DEBIT/CREDIT).
    /// </summary>
    public static BankTransactionType InferType(decimal amount, string? description, string? checkNumber)
    {
        if (!string.IsNullOrWhiteSpace(checkNumber) && amount < 0)
        {
            return BankTransactionType.Check;
        }

        var d = (description ?? "").ToUpperInvariant();
        if (d.Contains("INTEREST"))
        {
            return BankTransactionType.Interest;
        }

        if (d.Contains("FEE") || d.Contains("SERVICE CHARGE"))
        {
            return BankTransactionType.Fee;
        }

        if (d.Contains("WIRE"))
        {
            return BankTransactionType.Wire;
        }

        if (d.Contains("ACH") || d.Contains("EFT") || d.Contains("CCD") || d.Contains("PPD"))
        {
            return BankTransactionType.Ach;
        }

        if (d.Contains("TRANSFER") || d.Contains("XFER"))
        {
            return BankTransactionType.Transfer;
        }

        if (d.Contains("CHECK") || d.Contains("CHK"))
        {
            return amount < 0 ? BankTransactionType.Check : BankTransactionType.Deposit;
        }

        return amount >= 0 ? BankTransactionType.Deposit : BankTransactionType.Other;
    }
}
