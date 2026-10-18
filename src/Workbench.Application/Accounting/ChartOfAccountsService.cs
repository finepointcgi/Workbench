using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Domain.Accounting;

namespace Workbench.Application.Accounting;

public sealed class ChartOfAccountsService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : IChartOfAccountsService
{
    public async Task<IReadOnlyList<AccountDto>> ListAccountsAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var accounts = await db.Set<Account>().AsNoTracking().ToListAsync(cancellationToken);
        var used = (await db.Set<JournalLine>().AsNoTracking().Select(l => l.AccountId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var parents = accounts.Where(a => a.ParentId is not null).Select(a => a.ParentId!.Value).ToHashSet();
        return accounts.Where(a => includeInactive || a.IsActive)
            .OrderBy(a => a.Number, StringComparer.Ordinal)
            .Select(a => ToDto(a, parents.Contains(a.Id), used.Contains(a.Id)))
            .ToList();
    }

    public async Task<AccountDto> CreateAccountAsync(SaveAccountRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = new Account { Number = "", Name = "", CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
        await ApplyAsync(db, account, request, hasPostings: false, cancellationToken);
        db.Set<Account>().Add(account);
        audit.Record(db, nameof(Account), account.Id, "Created", after: Snapshot(account));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(account, false, false);
    }

    public async Task<AccountDto> UpdateAccountAsync(Guid accountId, SaveAccountRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = await FindAsync(db, accountId, cancellationToken);
        var hasPostings = await db.Set<JournalLine>().AnyAsync(l => l.AccountId == accountId, cancellationToken);
        var before = Snapshot(account);
        await ApplyAsync(db, account, request, hasPostings, cancellationToken);
        audit.Record(db, nameof(Account), account.Id, "Updated", before, Snapshot(account));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(account, await db.Set<Account>().AnyAsync(a => a.ParentId == accountId, cancellationToken), hasPostings);
    }

    public async Task<AccountDto> SetActiveAsync(Guid accountId, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = await FindAsync(db, accountId, cancellationToken);
        if (!isActive)
        {
            if (await db.Set<PostingRule>().AnyAsync(r => r.AccountId == accountId, cancellationToken))
            {
                throw new InvalidOperationException($"{account.Number} {account.Name} is used by a posting rule; map that rule to another account first.");
            }

            if (await db.Set<Account>().AnyAsync(a => a.ParentId == accountId && a.IsActive, cancellationToken))
            {
                throw new InvalidOperationException($"{account.Number} {account.Name} has active sub-accounts; deactivate them first.");
            }
        }
        else if (account.ParentId is { } parentId && !await db.Set<Account>().AnyAsync(a => a.Id == parentId && a.IsActive, cancellationToken))
        {
            throw new InvalidOperationException("Reactivate the parent account first.");
        }

        if (account.IsActive != isActive)
        {
            account.IsActive = isActive;
            audit.Record(db, nameof(Account), account.Id, isActive ? "Reactivated" : "Deactivated", new { IsActive = !isActive }, new { IsActive = isActive });
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToDto(account, await db.Set<Account>().AnyAsync(a => a.ParentId == accountId, cancellationToken),
            await db.Set<JournalLine>().AnyAsync(l => l.AccountId == accountId, cancellationToken));
    }

    public async Task<int> InstallDefaultChartAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<Account>().AnyAsync(cancellationToken))
        {
            return 0;
        }

        var (accounts, rules) = DefaultChartOfAccounts.Build(clock.GetUtcNow().UtcDateTime, audit.Actor);
        db.Set<Account>().AddRange(accounts);
        db.Set<PostingRule>().AddRange(rules);
        audit.Record(db, nameof(Account), Guid.Empty, "DefaultChartInstalled", after: new { Accounts = accounts.Count, Rules = rules.Count });
        await db.SaveChangesAsync(cancellationToken);
        return accounts.Count;
    }

    private static async Task ApplyAsync(IWorkbenchDbContext db, Account account, SaveAccountRequest request, bool hasPostings, CancellationToken ct)
    {
        var number = request.Number?.Trim() ?? "";
        var name = request.Name?.Trim() ?? "";
        if (number.Length is 0 or > 32)
        {
            throw new ArgumentException("An account number is required (up to 32 characters).");
        }

        if (name.Length is 0 or > 200)
        {
            throw new ArgumentException("An account name is required (up to 200 characters).");
        }

        if (await db.Set<Account>().AnyAsync(a => a.Number == number && a.Id != account.Id, ct))
        {
            throw new ArgumentException($"Account {number} already exists.");
        }

        var type = request.Type.ToDomain();
        if (hasPostings && type != account.Type)
        {
            throw new InvalidOperationException($"{account.Number} has postings, so its type can't change. Create a new account instead.");
        }

        if (request.ParentId is { } parentId)
        {
            var parent = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == parentId, ct)
                ?? throw new ArgumentException("The parent account doesn't exist.");
            if (parent.Type != type)
            {
                throw new ArgumentException($"A {type} account can't roll up into {parent.Number} ({parent.Type}).");
            }

            if (await db.Set<JournalLine>().AnyAsync(l => l.AccountId == parentId, ct))
            {
                throw new ArgumentException($"{parent.Number} has postings, so it can't become a summary account.");
            }

            // No cycles: walk up from the new parent.
            for (Guid? at = parentId; at is { } id; at = await db.Set<Account>().Where(a => a.Id == id).Select(a => a.ParentId).FirstOrDefaultAsync(ct))
            {
                if (id == account.Id)
                {
                    throw new ArgumentException("An account can't roll up into itself or one of its sub-accounts.");
                }
            }
        }

        account.Number = number;
        account.Name = name;
        account.Type = type;
        account.NormalBalance = request.NormalBalance?.ToDomain() ?? Account.DefaultNormalBalance(type);
        account.ParentId = request.ParentId;
        account.CostClass = type == AccountType.Expense ? request.CostClass.ToDomain() : CostClassification.None;
        account.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
    }

    private static async Task<Account> FindAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<Account>().FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Account", id);

    private static object Snapshot(Account a) => new { a.Number, a.Name, Type = a.Type.ToString(), a.ParentId, CostClass = a.CostClass.ToString(), a.IsActive };

    internal static AccountDto ToDto(Account a, bool hasChildren, bool hasPostings) => new(
        a.Id, a.Number, a.Name, a.Type.ToContract(), a.NormalBalance.ToContract(), a.IsActive, a.ParentId, a.CostClass.ToContract(), a.Description, hasChildren, hasPostings);
}
