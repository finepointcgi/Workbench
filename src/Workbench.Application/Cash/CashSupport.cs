using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Cash;
using Workbench.Domain.Accounting;
using Workbench.Domain.Cash;

namespace Workbench.Application.Cash;

/// <summary>Encrypts full bank account numbers with the host's Data Protection keys.</summary>
public sealed class BankAccountNumberProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Workbench.Cash.BankAccountNumber.v1");

    public string Protect(string digits) => _protector.Protect(digits);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}

internal static class CashSupport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string BankFeeAccountNumber = "6240";
    public const string InterestIncomeAccountNumber = "4910";

    public static string Mask(string last4) => $"••••{last4}";

    /// <summary>
    /// The digits of an account number typed with or without spaces and dashes: four (last four only) to seventeen.
    /// <see cref="ArgumentException"/> otherwise.
    /// </summary>
    public static string NormalizeAccountNumber(string number)
    {
        var digits = new string(number.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (digits.Length is < 4 or > 17 || !digits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("An account number has 4 to 17 digits (enter the full number, or just the last four).");
        }

        return digits;
    }

    /// <summary>A nine-digit ABA routing number with a valid checksum; null when empty.</summary>
    public static string? NormalizeRouting(string? routing)
    {
        if (string.IsNullOrWhiteSpace(routing))
        {
            return null;
        }

        var d = new string(routing.Where(char.IsAsciiDigit).ToArray());
        if (d.Length != 9 || routing.Trim().Any(c => !char.IsAsciiDigit(c) && c != '-' && c != ' '))
        {
            throw new ArgumentException("A routing number has nine digits.");
        }

        int N(int i) => d[i] - '0';
        var sum = 3 * (N(0) + N(3) + N(6)) + 7 * (N(1) + N(4) + N(7)) + (N(2) + N(5) + N(8));
        return sum % 10 == 0 ? d : throw new ArgumentException($"{d} isn't a valid ABA routing number (the check digit doesn't match).");
    }

    public static string? Serialize(CsvColumnMapping? mapping) => mapping is null ? null : JsonSerializer.Serialize(mapping, Json);

    public static CsvColumnMapping? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CsvColumnMapping>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<CashSettings> SettingsAsync(IWorkbenchDbContext db, CancellationToken ct) =>
        await db.Set<CashSettings>().AsNoTracking().FirstOrDefaultAsync(ct) ?? new CashSettings();

    /// <summary>
    /// The account with <paramref name="number"/>, created under the parent with <paramref name="parentNumber"/> when a
    /// chart installed before cash management doesn't have it yet (the default chart includes both booking accounts).
    /// </summary>
    public static async Task<Account?> EnsureAccountAsync(IWorkbenchDbContext db, string number, string name, AccountType type, string parentNumber,
        CostClassification costClass, DateTime now, CancellationToken ct)
    {
        var existing = await db.Set<Account>().FirstOrDefaultAsync(a => a.Number == number, ct);
        if (existing is not null)
        {
            return existing;
        }

        var parent = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == parentNumber, ct);
        if (parent is null)
        {
            return null;
        }

        var account = new Account
        {
            Number = number,
            Name = name,
            Type = type,
            NormalBalance = Account.DefaultNormalBalance(type),
            ParentId = parent.Id,
            CostClass = costClass,
            CreatedAtUtc = now,
        };
        db.Set<Account>().Add(account);
        return account;
    }

    public static StatementFileFormat ToContract(this StatementFormat v) => (StatementFileFormat)(int)v;
    public static StatementFormat ToDomain(this StatementFileFormat v) => (StatementFormat)(int)v;
    public static BankItemType ToContract(this BankTransactionType v) => (BankItemType)(int)v;
    public static MatchRule ToContract(this BankMatchRule v) => (MatchRule)(int)v;
    public static ReconciliationState ToContract(this BankReconciliationStatus v) => (ReconciliationState)(int)v;
    public static ForecastRepeat ToContract(this ForecastFrequency v) => (ForecastRepeat)(int)v;
    public static ForecastFrequency ToDomain(this ForecastRepeat v) => (ForecastFrequency)(int)v;

    public static StatementImportDto ToDto(this BankStatementImport i) => new(
        i.Id, i.BankAccountId, i.Format.ToContract(), i.FileName, i.PeriodStart, i.PeriodEnd, i.OpeningBalance, i.ClosingBalance,
        i.TransactionCount, i.DuplicateCount, i.ImportedBy, i.ImportedAtUtc);

    public static BankItemDto ToDto(this BankTransaction t, ISet<Guid> lockedMatches) => new(
        t.Id, t.Date, t.Amount, t.Type.ToContract(), t.Description, t.CheckNumber, t.BankReference, t.MatchId,
        t.MatchId is { } m && lockedMatches.Contains(m), t.JournalEntryId);
}
