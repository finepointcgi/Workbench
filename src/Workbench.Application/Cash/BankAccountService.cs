using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Cash;
using Workbench.Contracts.Security;
using Workbench.Domain.Accounting;
using Workbench.Domain.Cash;

namespace Workbench.Application.Cash;

public sealed class BankAccountService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    BankAccountNumberProtector protector,
    SecurityEventLog securityLog,
    TimeProvider clock) : IBankAccountService
{
    public async Task<IReadOnlyList<BankAccountDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var accounts = await db.Set<BankAccount>().AsNoTracking().Where(a => includeInactive || a.IsActive).OrderBy(a => a.Name).ToListAsync(cancellationToken);
        return await ToDtosAsync(db, accounts, cancellationToken);
    }

    public async Task<BankAccountDto> GetAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = await FindAsync(db, bankAccountId, cancellationToken);
        return (await ToDtosAsync(db, [account], cancellationToken))[0];
    }

    public async Task<BankAccountDto> CreateAsync(SaveBankAccountRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(request.AccountNumber))
        {
            throw new ArgumentException("Enter the account number (the full number, or just the last four digits).");
        }

        var account = new BankAccount
        {
            Name = "",
            BankName = "",
            AccountNumberLast4 = "",
            CreatedBy = audit.Actor,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        await ApplyAsync(db, account, request, cancellationToken);
        db.Set<BankAccount>().Add(account);
        audit.Record(db, nameof(BankAccount), account.Id, "Created", after: Snapshot(account));
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync(db, [account], cancellationToken))[0];
    }

    public async Task<BankAccountDto> UpdateAsync(Guid bankAccountId, SaveBankAccountRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = await db.Set<BankAccount>().FirstOrDefaultAsync(a => a.Id == bankAccountId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Bank account", bankAccountId);
        var before = Snapshot(account);
        var numberChanged = await ApplyAsync(db, account, request, cancellationToken);
        account.UpdatedBy = audit.Actor;
        account.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, nameof(BankAccount), account.Id, "Updated", before, Snapshot(account));
        await db.SaveChangesAsync(cancellationToken);
        if (numberChanged)
        {
            await securityLog.RecordAsync(CashSecurityEvents.AccountNumberChanged, $"bank account {account.Name}", SecurityOutcomes.Success,
                $"Now ending {account.AccountNumberLast4}", cancellationToken: cancellationToken);
        }

        return (await ToDtosAsync(db, [account], cancellationToken))[0];
    }

    public async Task<BankAccountNumberDto> RevealAccountNumberAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = await FindAsync(db, bankAccountId, cancellationToken);
        var target = $"bank account {account.Name}";
        if (account.ProtectedAccountNumber is null)
        {
            throw new InvalidOperationException($"Only the last four digits ({account.AccountNumberLast4}) of {account.Name} were entered; there is no full number to show.");
        }

        string number;
        try
        {
            number = protector.Unprotect(account.ProtectedAccountNumber);
        }
        catch (CryptographicException)
        {
            await securityLog.RecordAsync(CashSecurityEvents.AccountNumberRevealed, target, SecurityOutcomes.Failure, "The account number couldn't be decrypted (key unavailable)", cancellationToken: cancellationToken);
            throw new InvalidOperationException("The account number can't be decrypted with this server's keys. Re-enter it on the account.");
        }

        await securityLog.RecordAsync(CashSecurityEvents.AccountNumberRevealed, target, SecurityOutcomes.Success, $"Account ending {account.AccountNumberLast4}", cancellationToken: cancellationToken);
        return new BankAccountNumberDto(account.Id, number);
    }

    public async Task<IReadOnlyList<CashGlAccountDto>> ListCashGlAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => a.IsActive && a.Type == AccountType.Asset).ToListAsync(cancellationToken);
        var parents = accounts.Where(a => a.ParentId is not null).Select(a => a.ParentId!.Value).ToHashSet();

        // Detail asset accounts, cash ones first (10xx and anything named cash).
        return accounts.Where(a => !parents.Contains(a.Id))
            .OrderBy(a => a.Number.StartsWith("10", StringComparison.Ordinal) || a.Name.Contains("cash", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(a => a.Number)
            .Select(a => new CashGlAccountDto(a.Id, a.Number, a.Name)).ToList();
    }

    /// <summary>Applies a request; returns true when the account number changed.</summary>
    private async Task<bool> ApplyAsync(IWorkbenchDbContext db, BankAccount account, SaveBankAccountRequest request, CancellationToken ct)
    {
        var name = Require(request.Name, "a name", 200);
        var bank = Require(request.BankName, "the bank's name", 200);
        if (await db.Set<BankAccount>().AnyAsync(a => a.Id != account.Id && a.Name == name, ct))
        {
            throw new ArgumentException($"There's already a bank account named {name}.");
        }

        var gl = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.GlAccountId, ct)
            ?? throw new ArgumentException("Choose the GL cash account this bank account posts to.");
        if (gl.Type != AccountType.Asset || !gl.IsActive)
        {
            throw new ArgumentException($"{gl.Number} {gl.Name} isn't an active asset account; link a cash account such as 1010.");
        }

        if (await db.Set<Account>().AnyAsync(a => a.ParentId == gl.Id, ct))
        {
            throw new ArgumentException($"{gl.Number} {gl.Name} is a summary account; link one of its detail accounts.");
        }

        if (await db.Set<BankAccount>().AnyAsync(a => a.Id != account.Id && a.GlAccountId == gl.Id && a.IsActive, ct) && request.IsActive)
        {
            throw new ArgumentException($"Another active bank account already reconciles to {gl.Number}; give each bank account its own GL cash account.");
        }

        account.Name = name;
        account.BankName = bank;
        account.RoutingNumber = CashSupport.NormalizeRouting(request.RoutingNumber);
        account.GlAccountId = gl.Id;
        account.IsActive = request.IsActive;

        if (string.IsNullOrWhiteSpace(request.AccountNumber))
        {
            return false;
        }

        var digits = CashSupport.NormalizeAccountNumber(request.AccountNumber);
        var last4 = digits[^4..];
        var hadFull = account.ProtectedAccountNumber is not null;
        account.ProtectedAccountNumber = digits.Length > 4 ? protector.Protect(digits) : null;
        // Re-entering a full number always counts: the stored (encrypted) value is replaced.
        var changed = account.AccountNumberLast4 != last4 || hadFull || digits.Length > 4;
        account.AccountNumberLast4 = last4;
        return changed;
    }

    private static string Require(string? value, string what, int max)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            throw new ArgumentException($"Enter {what}.");
        }

        return v.Length <= max ? v : throw new ArgumentException($"{char.ToUpperInvariant(what[0])}{what[1..]} is too long (at most {max} characters).");
    }

    // The audit trail never holds the full number, only the last four.
    private static object Snapshot(BankAccount a) => new
    {
        a.Name,
        a.BankName,
        AccountNumber = CashSupport.Mask(a.AccountNumberLast4),
        FullNumberStored = a.ProtectedAccountNumber is not null,
        a.RoutingNumber,
        a.GlAccountId,
        a.Currency,
        a.IsActive,
    };

    private static async Task<BankAccount> FindAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<BankAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Bank account", id);

    internal static async Task<List<BankAccountDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyList<BankAccount> accounts, CancellationToken ct)
    {
        var ids = accounts.Select(a => a.Id).ToList();
        var glIds = accounts.Select(a => a.GlAccountId).Distinct().ToList();
        var gl = await db.Set<Account>().AsNoTracking().Where(a => glIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var balances = await CashLedger.BalancesAsync(db, glIds, null, ct);
        var imports = (await db.Set<BankStatementImport>().AsNoTracking().Where(i => ids.Contains(i.BankAccountId))
                .Select(i => new { i.BankAccountId, i.PeriodEnd, i.ClosingBalance, i.ImportedAtUtc }).ToListAsync(ct))
            .GroupBy(i => i.BankAccountId).ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.PeriodEnd).ThenByDescending(i => i.ImportedAtUtc).First());
        var reconciled = (await db.Set<BankReconciliation>().AsNoTracking()
                .Where(r => ids.Contains(r.BankAccountId) && r.Status == BankReconciliationStatus.Completed)
                .Select(r => new { r.BankAccountId, r.StatementEndDate }).ToListAsync(ct))
            .GroupBy(r => r.BankAccountId).ToDictionary(g => g.Key, g => g.Max(r => r.StatementEndDate));
        var unmatched = (await db.Set<BankTransaction>().AsNoTracking().Where(t => ids.Contains(t.BankAccountId) && t.MatchId == null)
                .Select(t => t.BankAccountId).ToListAsync(ct))
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());

        return accounts.Select(a =>
        {
            gl.TryGetValue(a.GlAccountId, out var account);
            imports.TryGetValue(a.Id, out var last);
            return new BankAccountDto(
                a.Id, a.Name, a.BankName, CashSupport.Mask(a.AccountNumberLast4), a.AccountNumberLast4, a.ProtectedAccountNumber is not null,
                a.RoutingNumber, a.GlAccountId, account?.Number ?? "?", account?.Name ?? "Unknown account", a.Currency, a.IsActive,
                balances.GetValueOrDefault(a.GlAccountId), last?.PeriodEnd, last?.ClosingBalance,
                reconciled.TryGetValue(a.Id, out var through) ? through : null, unmatched.GetValueOrDefault(a.Id),
                CashSupport.Deserialize(a.CsvMappingJson));
        }).ToList();
    }
}
