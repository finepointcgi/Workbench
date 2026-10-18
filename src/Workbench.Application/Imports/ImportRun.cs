using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Imports;
using Workbench.Domain.Imports;
using RecordAction = Workbench.Domain.Imports.ImportRecordAction;

namespace Workbench.Application.Imports;

public enum ImportMode
{
    /// <summary>Check rows against the mapping and existing data; change nothing.</summary>
    Validate,

    /// <summary>Work out the creates, updates and skips; change nothing.</summary>
    DryRun,

    /// <summary>Do it, through the services, recording every touched entity for rollback.</summary>
    Apply,
}

/// <summary>
/// One pass of an importer over its rows. Importers describe each row's outcome through <see cref="CreateAsync"/>,
/// <see cref="UpdateAsync"/> and <see cref="Skip"/>; in validate and dry-run modes nothing runs and creates get a
/// placeholder id (so later rows can refer to earlier ones), in apply mode the action runs and what it touched is recorded.
/// </summary>
public sealed class ImportRun(ImportMode mode, Guid jobId, IWorkbenchDbContextFactory dbFactory)
{
    private const int MaxListed = 5000;
    private readonly HashSet<(string File, int Row)> _errorRows = [];
    private int _sequence;

    public ImportMode Mode => mode;
    public bool Applying => mode == ImportMode.Apply;
    public Guid JobId => jobId;
    public List<ImportErrorDto> Errors { get; } = [];
    public List<ImportPlanRowDto> Plan { get; } = [];
    public int TotalRows { get; set; }
    public int Creates { get; private set; }
    public int Updates { get; private set; }
    public int Skips { get; private set; }
    public int ErrorRows => _errorRows.Count;

    /// <summary>Ids of entities created in this run (placeholders when not applying).</summary>
    public HashSet<Guid> CreatedIds { get; } = [];

    public void Error(string file, int row, string? field, string message)
    {
        _errorRows.Add((file, row));
        if (Errors.Count < MaxListed)
        {
            Errors.Add(new ImportErrorDto(file, row, field, message));
        }
    }

    public void Warning(string file, int row, string? field, string message)
    {
        if (Errors.Count < MaxListed)
        {
            Errors.Add(new ImportErrorDto(file, row, field, message, IsWarning: true));
        }
    }

    public bool HasError(string file, int row) => _errorRows.Contains((file, row));

    public void Skip(string file, int row, string entityType, string key, string reason)
    {
        Skips++;
        AddPlan(new ImportPlanRowDto(file, row, ImportRowAction.Skip, entityType, key, reason));
    }

    /// <summary>Counts and lists an update that the caller performs (and tracks) itself.</summary>
    public void PlanUpdate(string file, int row, string entityType, string key, string summary)
    {
        Updates++;
        AddPlan(new ImportPlanRowDto(file, row, ImportRowAction.Update, entityType, key, summary));
    }

    /// <summary>
    /// Creates an entity of <paramref name="type"/>: returns its id, a placeholder when not applying, or null when the
    /// service refused it (reported as a row error).
    /// </summary>
    public async Task<Guid?> CreateAsync(string file, int row, Type type, string entityType, string key, string summary,
        Func<Task<Guid>> create, CancellationToken cancellationToken)
    {
        if (!Applying)
        {
            Creates++;
            AddPlan(new ImportPlanRowDto(file, row, ImportRowAction.Create, entityType, key, summary));
            var placeholder = Guid.CreateVersion7();
            CreatedIds.Add(placeholder);
            return placeholder;
        }

        Guid id;
        try
        {
            id = await create();
        }
        catch (Exception ex) when (IsRowError(ex))
        {
            Error(file, row, null, ex.Message);
            return null;
        }

        Creates++;
        CreatedIds.Add(id);
        AddPlan(new ImportPlanRowDto(file, row, ImportRowAction.Create, entityType, key, summary));
        await RecordAsync(type, id, RecordAction.Created, $"{entityType} {key}", before: null, cancellationToken);
        return id;
    }

    /// <summary>Updates an existing entity; snapshots it before and after. Returns false when the service refused it.</summary>
    public async Task<bool> UpdateAsync(string file, int row, Type type, Guid id, string entityType, string key, string summary,
        Func<Task> update, CancellationToken cancellationToken)
    {
        if (!Applying)
        {
            Updates++;
            AddPlan(new ImportPlanRowDto(file, row, ImportRowAction.Update, entityType, key, summary));
            return true;
        }

        var ok = await TrackAsync([(type, id, $"{entityType} {key}")], async () =>
        {
            try
            {
                await update();
                return true;
            }
            catch (Exception ex) when (IsRowError(ex))
            {
                Error(file, row, null, ex.Message);
                return false;
            }
        }, cancellationToken);
        if (ok)
        {
            Updates++;
            AddPlan(new ImportPlanRowDto(file, row, ImportRowAction.Update, entityType, key, summary));
        }

        return ok;
    }

    /// <summary>
    /// Runs <paramref name="action"/> and records each listed (existing) entity as updated, with its state before and
    /// after. Use it for side effects such as a funding modification changing a CLIN's and contract's totals.
    /// </summary>
    public async Task<T> TrackAsync<T>(IReadOnlyList<(Type Type, Guid Id, string Label)> entities, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var before = new List<string?>();
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            foreach (var (type, id, _) in entities)
            {
                before.Add(CreatedIds.Contains(id) ? null : await EntitySnapshots.CaptureAsync(db, type, id, cancellationToken));
            }
        }

        var result = await action();
        for (var i = 0; i < entities.Count; i++)
        {
            // Entities this run created are rolled back by deleting them, so there's nothing to restore.
            if (before[i] is { } snapshot)
            {
                await RecordAsync(entities[i].Type, entities[i].Id, RecordAction.Updated, entities[i].Label, snapshot, cancellationToken);
            }
        }

        return result;
    }

    /// <summary>Records entities created as a side effect of a service call (funding actions of a modification...).</summary>
    public async Task RecordCreatedAsync(Type type, Guid id, string label, CancellationToken cancellationToken)
    {
        if (Applying && CreatedIds.Add(id))
        {
            await RecordAsync(type, id, RecordAction.Created, label, null, cancellationToken);
        }
    }

    /// <summary>Records an entity a service call removed, with its last state, so rollback can put it back.</summary>
    public async Task RecordDeletedAsync(Type type, Guid id, string label, string snapshot, CancellationToken cancellationToken)
    {
        if (Applying)
        {
            await RecordAsync(type, id, RecordAction.Deleted, label, snapshot, cancellationToken);
        }
    }

    /// <summary>Retakes the after-snapshot of every record, so rollback compares against the state the whole job left.</summary>
    public async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        if (!Applying)
        {
            return;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var records = await db.Set<ImportJobRecord>().Where(r => r.ImportJobId == jobId && r.Action != RecordAction.Deleted).ToListAsync(cancellationToken);
        var context = EntitySnapshots.Context(db);
        foreach (var record in records)
        {
            var type = EntitySnapshots.EntityType(context, record.EntityType).ClrType;
            await using var read = await dbFactory.CreateAsync(cancellationToken);
            record.AfterJson = await EntitySnapshots.CaptureAsync(read, type, record.EntityId, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordAsync(Type type, Guid id, RecordAction action, string label, string? before, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var after = action == RecordAction.Deleted ? null : await EntitySnapshots.CaptureAsync(db, type, id, cancellationToken);
        db.Set<ImportJobRecord>().Add(new ImportJobRecord
        {
            ImportJobId = jobId,
            Sequence = Interlocked.Increment(ref _sequence) + await LastSequenceAsync(db, cancellationToken),
            EntityType = type.FullName!,
            EntityId = id,
            Action = action,
            Label = label.Length > 300 ? label[..300] : label,
            BeforeJson = before,
            AfterJson = after,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private int? _baseSequence;

    private async Task<int> LastSequenceAsync(IWorkbenchDbContext db, CancellationToken cancellationToken) =>
        _baseSequence ??= await db.Set<ImportJobRecord>().Where(r => r.ImportJobId == jobId).Select(r => (int?)r.Sequence).MaxAsync(cancellationToken) ?? 0;

    private void AddPlan(ImportPlanRowDto row)
    {
        if (Plan.Count < MaxListed)
        {
            Plan.Add(row);
        }
    }

    public static bool IsRowError(Exception ex) => ex is ArgumentException or WorkbenchNotFoundException or FormatException;
}
