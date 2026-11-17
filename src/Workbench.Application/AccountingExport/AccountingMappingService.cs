using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.AccountingExport;
using Workbench.Domain.Accounting;
using Workbench.Domain.AccountingExport;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.AccountingExport;

/// <summary>
/// The mapping screen's data: every Workbench account, customer, vendor, project (class), invoice line type (item) and the built-in
/// "no tax" code, with the name it has in the destination. Saved per target; a blank destination removes the mapping.
/// </summary>
public sealed class AccountingMappingService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : IAccountingMappingService
{
    public const string Entity = nameof(AccountingMapping);

    private sealed record Source(AccountingMapKind Kind, string Key, string Name, bool InUse);

    public async Task<AccountingMappingSetDto> GetAsync(AccountingTarget target, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await BuildAsync(db, target, cancellationToken);
    }

    public async Task<AccountingMappingSetDto> SaveAsync(AccountingTarget target, SaveAccountingMappingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var targetName = target.ToString();
        var sources = (await SourcesAsync(db, cancellationToken)).ToDictionary(s => (s.Kind, s.Key));
        var rows = await db.Set<AccountingMapping>().Where(m => m.Target == targetName).ToListAsync(cancellationToken);
        var existing = rows.ToDictionary(m => (m.MapKind, m.SourceKey));
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var e in request.Entries.GroupBy(e => (e.Kind, e.SourceKey)).Select(g => g.Last()))
        {
            if (!sources.TryGetValue((e.Kind, e.SourceKey), out var source))
            {
                throw new ArgumentException($"'{e.SourceKey}' isn't a {e.Kind.ToString().ToLowerInvariant()} in Workbench.");
            }

            var name = e.DestinationName?.Trim();
            var code = string.IsNullOrWhiteSpace(e.DestinationCode) ? null : e.DestinationCode.Trim();
            if (name is { Length: > 200 } || code is { Length: > 50 })
            {
                throw new ArgumentException($"The names for {source.Name} are too long (name 200, code 50 characters).");
            }

            existing.TryGetValue((e.Kind.ToString(), e.SourceKey), out var row);
            if (string.IsNullOrEmpty(name))
            {
                if (row is not null)
                {
                    db.Set<AccountingMapping>().Remove(row);
                    audit.Record(db, Entity, row.Id, "Deleted", before: new { targetName, e.Kind, e.SourceKey, row.DestinationName, row.DestinationCode });
                }

                continue;
            }

            if (row is null)
            {
                row = new AccountingMapping
                {
                    Target = targetName, MapKind = e.Kind.ToString(), SourceKey = e.SourceKey, SourceName = Clip(source.Name, 200),
                    DestinationName = name, DestinationCode = code, UpdatedBy = audit.Actor, UpdatedAtUtc = now,
                };
                db.Set<AccountingMapping>().Add(row);
                audit.Record(db, Entity, row.Id, "Created", after: new { targetName, e.Kind, e.SourceKey, name, code });
            }
            else if (row.DestinationName != name || row.DestinationCode != code)
            {
                audit.Record(db, Entity, row.Id, "Updated", before: new { row.DestinationName, row.DestinationCode }, after: new { name, code });
                row.DestinationName = name;
                row.DestinationCode = code;
                row.SourceName = Clip(source.Name, 200);
                row.UpdatedBy = audit.Actor;
                row.UpdatedAtUtc = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await BuildAsync(db, target, cancellationToken);
    }

    private static async Task<List<Source>> SourcesAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var list = new List<Source>();
        var usedAccounts = (await db.Set<JournalLine>().AsNoTracking().Select(l => l.AccountId).Distinct().ToListAsync(ct)).ToHashSet();
        foreach (var a in await db.Set<Account>().AsNoTracking().OrderBy(a => a.Number).ToListAsync(ct))
        {
            list.Add(new(AccountingMapKind.Account, a.Number, a.Name, usedAccounts.Contains(a.Id)));
        }

        var billed = (await db.Set<Invoice>().AsNoTracking().Select(i => i.CustomerId).Distinct().ToListAsync(ct)).ToHashSet();
        foreach (var c in await db.Set<Customer>().AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct))
        {
            list.Add(new(AccountingMapKind.Customer, c.Code, c.Name, billed.Contains(c.Id)));
        }

        var paid = (await db.Set<ApBill>().AsNoTracking().Select(b => b.VendorId).Distinct().ToListAsync(ct)).ToHashSet();
        foreach (var v in await db.Set<Vendor>().AsNoTracking().OrderBy(v => v.Name).ToListAsync(ct))
        {
            list.Add(new(AccountingMapKind.Vendor, v.Code, v.Name, paid.Contains(v.Id)));
        }

        var usedProjects = (await db.Set<JournalLine>().AsNoTracking().Where(l => l.ProjectId != null).Select(l => l.ProjectId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        foreach (var p in await db.Set<Project>().AsNoTracking().OrderBy(p => p.Code).ToListAsync(ct))
        {
            list.Add(new(AccountingMapKind.Class, p.Code, p.Name, usedProjects.Contains(p.Id)));
        }

        var usedKinds = (await db.Set<InvoiceLine>().AsNoTracking().Select(l => l.Kind).Distinct().ToListAsync(ct)).Select(k => k.ToString()).ToHashSet();
        foreach (var (key, name) in ExportDataLoader.InvoiceItemKeys)
        {
            list.Add(new(AccountingMapKind.Item, key, name, usedKinds.Contains(key)));
        }

        list.Add(new(AccountingMapKind.TaxCode, AccountingMapKeys.NoTax, "No tax (Workbench bills sales tax as its own line)", true));
        return list;
    }

    private static async Task<AccountingMappingSetDto> BuildAsync(IWorkbenchDbContext db, AccountingTarget target, CancellationToken ct)
    {
        var targetName = target.ToString();
        var saved = (await db.Set<AccountingMapping>().AsNoTracking().Where(m => m.Target == targetName).ToListAsync(ct)).ToDictionary(m => (m.MapKind, m.SourceKey));
        var rows = (await SourcesAsync(db, ct)).Select(s =>
        {
            saved.TryGetValue((s.Kind.ToString(), s.Key), out var m);
            return new AccountingMappingRowDto(s.Kind, s.Key, s.Name, m?.DestinationName, m?.DestinationCode, s.InUse, m is not null);
        }).ToList();
        var unmapped = rows.Where(r => !r.IsMapped).ToList();
        return new AccountingMappingSetDto(target, rows, unmapped.Count, unmapped.Count(r => r.InUse && MappingResolver.Blocks(r.Kind)));
    }

    private static string Clip(string text, int max) => text.Length > max ? text[..max] : text;
}
