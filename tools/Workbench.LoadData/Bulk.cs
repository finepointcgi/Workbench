using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Workbench.Infrastructure.Persistence;

namespace Workbench.LoadData;

/// <summary>
/// Buffers new entities and saves them in large batches, each on a fresh context with change detection off. Parents are
/// always added before their children, so a batch never references a row that a later batch inserts.
/// </summary>
internal sealed class Bulk(IDbContextFactory<WorkbenchDbContext> factory, int batchSize = 25_000)
{
    private readonly List<object> _pending = new(batchSize);
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly Stopwatch _saving = new();

    public long Total { get; private set; }

    public TimeSpan SaveTime => _saving.Elapsed;

    public IReadOnlyDictionary<string, long> Counts => _counts;

    public async Task AddAsync(object entity)
    {
        _pending.Add(entity);
        if (_pending.Count >= batchSize)
        {
            await FlushAsync();
        }
    }

    public async Task AddRangeAsync(IEnumerable<object> entities)
    {
        foreach (var entity in entities)
        {
            await AddAsync(entity);
        }
    }

    public async Task FlushAsync()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        _saving.Start();
        await using var db = await factory.CreateDbContextAsync();
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        foreach (var entity in _pending)
        {
            db.Add(entity);
            var name = entity.GetType().Name;
            _counts[name] = _counts.GetValueOrDefault(name) + 1;
        }

        await db.SaveChangesAsync();
        Total += _pending.Count;
        _pending.Clear();
        _saving.Stop();
    }
}
