using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.ProjectAccounting;
using Workbench.Domain;
using Workbench.Domain.Collaboration;
using Workbench.Domain.Imports;
using RecordAction = Workbench.Domain.Imports.ImportRecordAction;

namespace Workbench.Application.Imports;

/// <summary>
/// Undoes an import from its <see cref="ImportJobRecord"/>s, newest first: deletes what it created, restores what it
/// changed and re-inserts what it replaced. Refuses when any of those entities changed since the import, or when
/// something the import didn't create now references what it created (found through the EF model's foreign keys,
/// plus comments and attachments, which reference items without one).
/// </summary>
public sealed class ImportRollback(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, IServiceProvider services)
{
    private const int MaxBlockers = 50;

    public async Task<IReadOnlyList<string>> FindBlockersAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var context = EntitySnapshots.Context(db);
        var records = await db.Set<ImportJobRecord>().AsNoTracking().Where(r => r.ImportJobId == jobId).OrderByDescending(r => r.Sequence).ToListAsync(cancellationToken);
        var touched = records.Select(r => r.EntityId).ToHashSet();
        var createdIds = records.Where(r => r.Action == RecordAction.Created).Select(r => r.EntityId).ToHashSet();
        var otherJobs = await OtherJobLabelsAsync(db, jobId, cancellationToken);
        var blockers = new List<string>();
        var checkedState = new HashSet<Guid>();

        foreach (var record in records)
        {
            if (blockers.Count >= MaxBlockers)
            {
                break;
            }

            var entityType = EntitySnapshots.EntityType(context, record.EntityType);
            await using var read = await dbFactory.CreateAsync(cancellationToken);
            var current = await EntitySnapshots.CaptureAsync(read, entityType.ClrType, record.EntityId, cancellationToken);
            if (record.Action == RecordAction.Deleted)
            {
                if (current is not null && !createdIds.Contains(record.EntityId))
                {
                    blockers.Add($"{record.Label} exists again, so it can't be put back.");
                }

                continue;
            }

            // Several records can cover one entity; they all carry its final state, so check it once.
            if (!checkedState.Add(record.EntityId))
            {
                continue;
            }

            if (current is null)
            {
                if (record.Action == RecordAction.Updated)
                {
                    blockers.Add($"{record.Label} was deleted after the import.");
                }

                continue;
            }

            if (!EntitySnapshots.Same(current, record.AfterJson))
            {
                blockers.Add($"{record.Label} was changed after the import{otherJobs.GetValueOrDefault(record.EntityId)}.");
                continue;
            }

            if (record.Action == RecordAction.Created)
            {
                foreach (var (dependentType, dependentId) in await ReferencesAsync(context, entityType, record.EntityId, cancellationToken))
                {
                    if (!touched.Contains(dependentId))
                    {
                        blockers.Add($"{Describe(dependentType)} {dependentId.ToString()[..8]}{otherJobs.GetValueOrDefault(dependentId)} references {record.Label}.");
                    }
                }
            }
        }

        // Comments and attachments point at items without a foreign key.
        var items = records.Where(r => r.Action == RecordAction.Created && r.EntityType == typeof(Item).FullName).Select(r => r.EntityId).ToList();
        if (items.Count > 0)
        {
            var comments = await db.Set<Comment>().AsNoTracking().Where(c => items.Contains(c.ItemId)).Select(c => new { c.Id, c.AuthorEmail }).ToListAsync(cancellationToken);
            blockers.AddRange(comments.Where(c => !touched.Contains(c.Id)).Take(10).Select(c => $"A comment by {c.AuthorEmail} was added to an imported row."));
            var files = await db.Set<Attachment>().AsNoTracking().Where(a => items.Contains(a.ItemId)).Select(a => new { a.Id, a.FileName }).ToListAsync(cancellationToken);
            blockers.AddRange(files.Where(a => !touched.Contains(a.Id)).Take(10).Select(a => $"Attachment {a.FileName} was added to an imported row."));
        }

        return blockers.Distinct().Take(MaxBlockers).ToList();
    }

    /// <summary>Reverts every record. Call <see cref="FindBlockersAsync"/> first.</summary>
    public async Task RollBackAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var context = EntitySnapshots.Context(db);
        var records = await db.Set<ImportJobRecord>().AsNoTracking().Where(r => r.ImportJobId == jobId).OrderByDescending(r => r.Sequence).ToListAsync(cancellationToken);
        var reason = $"Rolled back import job {jobId.ToString()[..8]}";
        var blobs = new List<string>();
        var inMemory = context.Database.ProviderName?.Contains("InMemory", StringComparison.Ordinal) == true;
        await using var transaction = inMemory ? null : await context.Database.BeginTransactionAsync(cancellationToken);

        foreach (var record in records)
        {
            var entityType = EntitySnapshots.EntityType(context, record.EntityType);
            var entity = await context.FindAsync(entityType.ClrType, [record.EntityId], cancellationToken);
            var name = entityType.ClrType.Name;
            switch (record.Action)
            {
                case RecordAction.Created when entity is not null:
                    var before = EntitySnapshots.Serialize(context, entity);
                    if (entity is Attachment attachment)
                    {
                        blobs.Add(attachment.StorageKey);
                    }

                    context.Remove(entity);
                    if (entity is not ImportKeyMap)
                    {
                        audit.Record(db, name, record.EntityId, "RolledBack", before: before, reason: reason);
                    }

                    break;
                case RecordAction.Updated when entity is not null && record.BeforeJson is not null:
                    var changed = EntitySnapshots.Serialize(context, entity);
                    EntitySnapshots.Apply(context, entity, record.BeforeJson);
                    audit.Record(db, name, record.EntityId, "RolledBack", before: changed, after: record.BeforeJson, reason: reason);
                    break;
                case RecordAction.Deleted when entity is null && record.BeforeJson is not null:
                    context.Add(EntitySnapshots.Materialize(entityType, record.EntityId, record.BeforeJson));
                    audit.Record(db, name, record.EntityId, "RolledBack", after: record.BeforeJson, reason: reason);
                    break;
            }

            // One save per record keeps deletes in reverse creation order (children before parents).
            await db.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        if (blobs.Count > 0 && services.GetService<IBlobStore>() is { } store)
        {
            foreach (var key in blobs)
            {
                try
                {
                    await store.DeleteAsync(key, cancellationToken);
                }
                catch (IOException)
                {
                    // The row is gone; an orphaned blob is harmless.
                }
            }
        }
    }

    /// <summary>Rows of other entity types (or the same, for parent links) whose foreign key points at the entity.</summary>
    private static async Task<List<(IEntityType Type, Guid Id)>> ReferencesAsync(DbContext context, IEntityType principal, Guid id, CancellationToken cancellationToken)
    {
        var found = new List<(IEntityType, Guid)>();
        foreach (var fk in principal.GetReferencingForeignKeys())
        {
            if (fk.Properties.Count != 1 || fk.DeclaringEntityType.FindPrimaryKey() is not { Properties.Count: 1 } key || key.Properties[0].ClrType != typeof(Guid))
            {
                continue;
            }

            var method = typeof(ImportRollback).GetMethod(nameof(DependentIdsAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(fk.DeclaringEntityType.ClrType);
            var ids = await (Task<List<Guid>>)method.Invoke(null, [context, fk.Properties[0], key.Properties[0].Name, id, cancellationToken])!;
            found.AddRange(ids.Select(d => (fk.DeclaringEntityType, d)));
        }

        return found;
    }

    private static Task<List<Guid>> DependentIdsAsync<T>(DbContext context, IProperty foreignKey, string keyName, Guid id, CancellationToken cancellationToken)
        where T : class
    {
        var e = Expression.Parameter(typeof(T), "e");
        var property = Expression.Call(typeof(EF), nameof(EF.Property), [foreignKey.ClrType], e, Expression.Constant(foreignKey.Name));
        var equals = Expression.Equal(property, Expression.Constant(id, foreignKey.ClrType));
        var where = Expression.Lambda<Func<T, bool>>(equals, e);
        var select = Expression.Lambda<Func<T, Guid>>(Expression.Call(typeof(EF), nameof(EF.Property), [typeof(Guid)], e, Expression.Constant(keyName)), e);
        return context.Set<T>().AsNoTracking().Where(where).Select(select).Take(20).ToListAsync(cancellationToken);
    }

    /// <summary>For each entity a later (still imported) job touched: " (by import job abcd1234)".</summary>
    private static async Task<Dictionary<Guid, string>> OtherJobLabelsAsync(IWorkbenchDbContext db, Guid jobId, CancellationToken cancellationToken)
    {
        var active = await db.Set<ImportJob>().AsNoTracking()
            .Where(j => j.Id != jobId && (j.Status == ImportJobStatus.Imported || j.Status == ImportJobStatus.Failed))
            .Select(j => j.Id).ToListAsync(cancellationToken);
        var rows = await db.Set<ImportJobRecord>().AsNoTracking().Where(r => active.Contains(r.ImportJobId))
            .Select(r => new { r.EntityId, r.ImportJobId }).ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.EntityId).ToDictionary(g => g.Key, g => $" (by import job {g.First().ImportJobId.ToString()[..8]}; roll that back first)");
    }

    /// <summary>"ChargeAssignment" → "Charge assignment".</summary>
    private static string Describe(IEntityType type) =>
        System.Text.RegularExpressions.Regex.Replace(type.ClrType.Name, "(?<=[a-z])([A-Z])", m => " " + m.Value.ToLowerInvariant());
}
