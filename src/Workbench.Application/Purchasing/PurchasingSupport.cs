using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>Accounts payable settings, bound from config section <c>Purchasing</c>.</summary>
public sealed class PurchasingOptions
{
    public const string Section = "Purchasing";

    /// <summary>The liability account subcontract retainage is credited to (created under 2000 when missing).</summary>
    public string RetainageAccountNumber { get; set; } = "2020";

    public string RetainageAccountName { get; set; } = "AP retainage";

    /// <summary>The cash account payments are credited to.</summary>
    public string CashAccountNumber { get; set; } = "1010";

    /// <summary>Our bank account as it appears on the positive-pay file.</summary>
    public string BankAccountNumber { get; set; } = "OPERATING";

    /// <summary>1099 reporting threshold (IRS: $600).</summary>
    public decimal Form1099Threshold { get; set; } = 600m;

    // ---- Procure-to-pay ----

    /// <summary>Requisitions totalling more than this also need Finance approval after the project managers.</summary>
    public decimal RequisitionFinanceThreshold { get; set; } = 10_000m;

    /// <summary>Three-way match: a billed unit price may differ from the PO price by up to this percent.</summary>
    public decimal MatchPriceTolerancePercent { get; set; } = 5m;
}

/// <summary>
/// Encrypts and decrypts full vendor TINs with ASP.NET Data Protection. Only the vendor service calls it; the plain TIN is
/// never logged, audited or exported.
/// </summary>
public sealed class TaxIdProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Workbench.Purchasing.VendorTaxId.v1");

    public string Protect(string digits) => _protector.Protect(digits);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);

    /// <summary>The nine digits of a TIN typed with or without dashes and spaces; <see cref="ArgumentException"/> otherwise.</summary>
    public static string Normalize(string? tin)
    {
        var digits = new string((tin ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (digits.Length != 9 || !digits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("A TIN has nine digits (for example 12-3456789 or 123-45-6789).");
        }

        if (digits.All(c => c == digits[0]))
        {
            throw new ArgumentException("That isn't a valid TIN.");
        }

        return digits;
    }

    public static string Format(string digits, string kind) =>
        kind == "SSN" ? $"{digits[..3]}-{digits[3..5]}-{digits[5..]}" : $"{digits[..2]}-{digits[2..]}";
}

internal static partial class PurchasingSupport
{
    public const string VendorEntity = nameof(Vendor);
    public const string SubcontractEntity = nameof(Subcontract);
    public const string BillEntity = nameof(ApBill);
    public const string RunEntity = nameof(ApPaymentRun);
    public const string PaymentEntity = nameof(ApPayment);
    public const string W9OwnerType = "VendorW9";
    public const string FinanceRoute = "Finance";

    /// <summary>Bills whose subcontract lines count as billed (submitted or later, not void or rejected).</summary>
    public static readonly ApBillStatus[] BilledStatuses = [ApBillStatus.PendingApproval, ApBillStatus.Approved, ApBillStatus.Posted];

    // ---- Enum mapping (contracts mirror the domain by name) ----

    public static VendorState ToContract(this VendorStatus s) => (VendorState)(int)s;
    public static VendorStatus ToDomain(this VendorState s) => (VendorStatus)(int)s;
    public static SubcontractState ToContract(this SubcontractStatus s) => (SubcontractState)(int)s;
    public static SubcontractPricing ToContract(this ClinPricing p) => (SubcontractPricing)(int)p;
    public static ClinPricing ToDomain(this SubcontractPricing p) => (ClinPricing)(int)p;
    public static BillState ToContract(this ApBillStatus s) => (BillState)(int)s;
    public static ApBillStatus ToDomain(this BillState s) => (ApBillStatus)(int)s;
    public static BillLineKind ToContract(this ApBillLineKind k) => (BillLineKind)(int)k;
    public static ApBillLineKind ToDomain(this BillLineKind k) => (ApBillLineKind)(int)k;
    public static BillLineState ToContract(this ApLineStatus s) => (BillLineState)(int)s;
    public static PaymentKind ToContract(this PaymentMethod m) => (PaymentKind)(int)m;
    public static PaymentMethod ToDomain(this PaymentKind m) => (PaymentMethod)(int)m;
    public static PaymentRunState ToContract(this PaymentRunStatus s) => (PaymentRunState)(int)s;
    public static PaymentState ToContract(this ApPaymentStatus s) => (PaymentState)(int)s;
    public static Contracts.Odcs.OdcCostType ToContract(this OdcType t) => (Contracts.Odcs.OdcCostType)(int)t;
    public static OdcType ToDomain(this Contracts.Odcs.OdcCostType t) => (OdcType)(int)t;
    public static Contracts.Odcs.OdcApprovalState ToContract(this OdcStatus s) => (Contracts.Odcs.OdcApprovalState)(int)s;

    // ---- Validation helpers ----

    public static string Required(string? value, string what, int max)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new ArgumentException($"{what} is required.");
        }

        return trimmed.Length > max ? throw new ArgumentException($"{what} can be at most {max} characters.") : trimmed;
    }

    public static string? Optional(string? value, string what, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > max ? throw new ArgumentException($"{what} can be at most {max} characters.") : trimmed;
    }

    public static decimal Money(decimal amount, string what, bool allowZero = false)
    {
        if (amount < 0 || (!allowZero && amount == 0))
        {
            throw new ArgumentException($"{what} must be greater than 0.");
        }

        return decimal.Round(amount, 2) != amount ? throw new ArgumentException($"{what} can have at most two decimal places.") : amount;
    }

    public static string Normalize(string? email) => (email ?? "").Trim().ToLowerInvariant();

    public static bool SameEmail(string? a, string? b) => Normalize(a) == Normalize(b) && Normalize(a).Length > 0;

    /// <summary>A vendor name reduced for duplicate checks: lower case, no punctuation, no corporate suffixes.</summary>
    public static string NameKey(string name)
    {
        var words = NonWord().Replace(name.ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !Suffixes.Contains(w));
        return string.Join(' ', words);
    }

    private static readonly HashSet<string> Suffixes = ["the", "inc", "incorporated", "llc", "corp", "corporation", "co", "company", "ltd", "limited", "lp", "llp", "pllc", "pc"];

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();

    public static void EnsureVendorCanTransact(Vendor vendor, string what)
    {
        if (vendor.Status == VendorStatus.OnHold)
        {
            throw new ArgumentException($"Vendor {vendor.Code} is on hold: {what} is blocked until the hold is released.");
        }

        if (vendor.Status == VendorStatus.Inactive)
        {
            throw new ArgumentException($"Vendor {vendor.Code} is inactive: reactivate it before {what}.");
        }
    }

    public static async Task<Vendor> FindVendorAsync(IWorkbenchDbContext db, Guid vendorId, CancellationToken ct, bool track = false)
    {
        var q = track ? db.Set<Vendor>() : db.Set<Vendor>().AsNoTracking();
        return await q.FirstOrDefaultAsync(v => v.Id == vendorId, ct) ?? throw WorkbenchNotFoundException.For("Vendor", vendorId);
    }

    /// <summary>The next "PREFIX-000001"-style number.</summary>
    public static string NextNumber(IEnumerable<string> existing, string prefix)
    {
        var max = existing.Select(n => n.StartsWith(prefix + "-", StringComparison.Ordinal) && int.TryParse(n[(prefix.Length + 1)..], out var i) ? i : 0)
            .DefaultIfEmpty(0).Max();
        return $"{prefix}-{max + 1:D6}";
    }

    // ---- Accounts ----

    /// <summary>The accounts-payable account (the GL's AccountsPayable posting rule, else 2010).</summary>
    public static async Task<Account> PayablesAccountAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var ruleAccount = await db.Set<PostingRule>().AsNoTracking()
            .Where(r => r.Kind == PostingRuleKind.AccountsPayable && r.Key == "").Select(r => (Guid?)r.AccountId).FirstOrDefaultAsync(ct);
        var account = ruleAccount is { } id
            ? await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.IsActive, ct)
            : await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == "2010" && a.IsActive, ct);
        return account ?? throw new ArgumentException("No active accounts payable account; map one under Accounting › Posting rules.");
    }

    public static async Task<Account> AccountByNumberAsync(IWorkbenchDbContext db, string number, string what, CancellationToken ct) =>
        await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == number && a.IsActive, ct)
            ?? throw new ArgumentException($"No active {what} account {number} in the chart of accounts.");

    /// <summary>
    /// The AP retainage liability account; added to the chart (under 2000 Liabilities when it exists) the first time it's
    /// needed, so charts installed before AP had it get it additively. The caller saves.
    /// </summary>
    public static async Task<Account> EnsureRetainageAccountAsync(IWorkbenchDbContext db, PurchasingOptions options, DateTime utcNow, CancellationToken ct)
    {
        var existing = await db.Set<Account>().FirstOrDefaultAsync(a => a.Number == options.RetainageAccountNumber, ct);
        if (existing is not null)
        {
            return existing.IsActive ? existing : throw new ArgumentException($"Retainage account {existing.Number} is inactive.");
        }

        var parent = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == "2000" && a.Type == AccountType.Liability, ct);
        var account = new Account
        {
            Number = options.RetainageAccountNumber,
            Name = options.RetainageAccountName,
            Type = AccountType.Liability,
            NormalBalance = NormalBalance.Credit,
            ParentId = parent?.Id,
            Description = "Subcontract retainage withheld from AP bills until release",
            CreatedAtUtc = utcNow,
        };
        db.Set<Account>().Add(account);
        return account;
    }

    // ---- CSV ----

    public static string Csv(IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendJoin(',', row.Select(Cell)).Append("\r\n");
        }

        return sb.ToString();
    }

    private static string Cell(object? value)
    {
        var text = value switch
        {
            null => "",
            decimal d => d.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        // Neutralize spreadsheet formulas (CSV injection) and quote when needed.
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0]) && value is string)
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }
}
