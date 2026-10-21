using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workbench.Contracts.Automations;
using Workbench.Domain.Automations;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Automations;

public static class AutomationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static AutomationDefinition Definition(string json) =>
        JsonSerializer.Deserialize<AutomationDefinition>(json, Options) ?? throw new InvalidOperationException("An automation version has no definition.");
}

/// <summary>
/// The run an automation's actions belong to, flowing with the async call: a change an action makes (setting a field)
/// queues the automations it triggers one level deeper, which is how chains are measured and loops stopped.
/// </summary>
public static class AutomationChain
{
    private static readonly AsyncLocal<Link?> Current = new();

    public sealed record Link(Guid RunId, int Depth);

    public static Link? Active => Current.Value;

    public static IDisposable Enter(Guid runId, int depth)
    {
        var previous = Current.Value;
        Current.Value = new Link(runId, depth);
        return new Restore(previous);
    }

    private sealed class Restore(Link? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>
/// Which enabled automations listen to record changes, per database (the context's options identify the tenant's
/// database), refreshed every <see cref="Ttl"/> and at once after an automation is saved in this process. The capture
/// hook asks it on every audited change, so a change nothing listens to costs a dictionary lookup.
/// </summary>
public sealed class AutomationIndex(TimeProvider clock, ILogger<AutomationIndex>? logger = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    public sealed record Item(Guid Id, AutomationTriggerKind Kind, string? EntityType, IReadOnlySet<string> Keys, bool HasBlock);

    public sealed record Snapshot(bool PausedAll, IReadOnlyList<Item> Items)
    {
        public static readonly Snapshot Empty = new(false, []);
    }

    private sealed record Entry(DateTime LoadedAtUtc, Snapshot Snapshot);

    private readonly ConditionalWeakTable<object, Entry> _entries = new();

    public Snapshot Get(IWorkbenchDbContext db)
    {
        var key = Key(db);
        var now = clock.GetUtcNow().UtcDateTime;
        if (_entries.TryGetValue(key, out var entry) && now - entry.LoadedAtUtc < Ttl)
        {
            return entry.Snapshot;
        }

        var snapshot = Load(db);
        _entries.AddOrUpdate(key, new Entry(now, snapshot));
        return snapshot;
    }

    public void Invalidate(IWorkbenchDbContext db) => _entries.Remove(Key(db));

    private Snapshot Load(IWorkbenchDbContext db)
    {
        try
        {
            var record = nameof(AutomationTriggerKind.RecordEvent);
            var field = nameof(AutomationTriggerKind.FieldChanged);
            var rows = db.Set<AutomationRecord>().AsNoTracking()
                .Where(a => a.IsEnabled && a.DeletedAtUtc == null && (a.TriggerKind == record || a.TriggerKind == field))
                .Select(a => new { a.Id, a.TriggerKind, a.EntityType, a.TriggerKeys, a.HasBlockingAction })
                .ToList();
            var paused = db.Set<AutomationSettings>().AsNoTracking().Select(s => s.PausedAll).FirstOrDefault();
            return new Snapshot(paused, rows.Select(r => new Item(r.Id, Enum.Parse<AutomationTriggerKind>(r.TriggerKind), r.EntityType,
                (r.TriggerKeys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase),
                r.HasBlockingAction)).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A database without the automation tables (before the baseline is regenerated) has no automations.
            logger?.LogWarning(ex, "Couldn't read the automation index; record automations are off until it can be read");
            return Snapshot.Empty;
        }
    }

    private static object Key(IWorkbenchDbContext db) =>
        db is DbContext context ? context.GetService<IDbContextOptions>() : db;
}

/// <summary>
/// The capture hook: <see cref="ProjectAccounting.AuditRecorder"/> calls it for every audit row, in the module's unit of work,
/// before the change is saved. For each enabled automation listening to that record type and event it either refuses the
/// change at once (an automation with a Block action whose conditions match throws <see cref="AutomationBlockedException"/>),
/// or queues a run (<see cref="AutomationQueueItem"/>) saved with the change, so automations run only for changes that were saved.
/// </summary>
public sealed class AutomationCapture(AutomationIndex index, IServiceProvider services, TimeProvider clock)
{
    public void OnAudit(IWorkbenchDbContext db, AuditEvent row)
    {
        var entity = AutomationEntities.ForAudit(row.EntityType);
        if (entity is null)
        {
            return;
        }

        var snapshot = index.Get(db);
        if (snapshot.PausedAll || snapshot.Items.Count == 0)
        {
            return;
        }

        var listeners = snapshot.Items.Where(i => i.EntityType == entity.Key).ToList();
        if (listeners.Count == 0)
        {
            return;
        }

        var change = RecordChange.FromAudit(row);
        var chain = AutomationChain.Active;
        foreach (var item in listeners)
        {
            var fires = item.Kind == AutomationTriggerKind.FieldChanged
                ? item.Keys.Any(change.Changed.Contains)
                : change.Events.Any(e => item.Keys.Contains(e.ToString()));
            if (!fires)
            {
                continue;
            }

            if (item.HasBlock)
            {
                // Validation runs now, against the record as it's about to be saved (the context tracks the change).
                // Blocking automations don't queue: a refused change has nothing to follow up.
                var guard = services.GetRequiredService<AutomationGuard>();
                Task.Run(() => guard.CheckAsync(db, item.Id, entity, row, change, CancellationToken.None)).GetAwaiter().GetResult();
                continue;
            }

            db.Set<AutomationQueueItem>().Add(new AutomationQueueItem
            {
                AutomationId = item.Id,
                TriggerKind = item.Kind.ToString(),
                TriggerDetail = item.Kind == AutomationTriggerKind.FieldChanged ? string.Join(",", item.Keys.Where(change.Changed.Contains)) : string.Join(",", change.Events),
                EntityType = entity.Key,
                RecordId = row.EntityId,
                Actor = row.Actor,
                ChangesJson = change.Json,
                Depth = chain is null ? 0 : chain.Depth + 1,
                ParentRunId = chain?.RunId,
                DedupKey = $"audit:{row.Id:N}:{item.Id:N}",
                CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            });
            services.GetService<AutomationSignal>()?.Pulse();
        }
    }
}

/// <summary>An audited change as automations see it: the record events it amounts to and the fields that changed.</summary>
public sealed record RecordChange(IReadOnlySet<RecordEventKind> Events, IReadOnlySet<string> Changed, string? Json, JsonElement? Before, JsonElement? After)
{
    public static RecordChange FromAudit(AuditEvent row) => From(row.Action, row.BeforeJson, row.AfterJson);

    public static RecordChange From(string action, string? beforeJson, string? afterJson)
    {
        var before = Parse(beforeJson);
        var after = Parse(afterJson);
        var events = new HashSet<RecordEventKind>();
        switch (action)
        {
            case "Created":
                events.Add(RecordEventKind.Created);
                break;
            case "Deleted":
                events.Add(RecordEventKind.Deleted);
                break;
            case "Submitted" or "Resubmitted":
                events.Add(RecordEventKind.Submitted);
                break;
            case "Approved":
                events.Add(RecordEventKind.Approved);
                break;
            case "Rejected" or "Returned":
                events.Add(RecordEventKind.Rejected);
                break;
        }

        if (action is not ("Created" or "Deleted"))
        {
            events.Add(RecordEventKind.Updated);
        }

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (before is { ValueKind: JsonValueKind.Object } b && after is { ValueKind: JsonValueKind.Object } a)
        {
            foreach (var property in a.EnumerateObject())
            {
                var old = b.TryGetProperty(property.Name, out var o) ? o.ToString() : null;
                if (old != property.Value.ToString())
                {
                    changed.Add(property.Name);
                }
            }
        }
        if (changed.Contains("status") || events.Overlaps([RecordEventKind.Submitted, RecordEventKind.Approved, RecordEventKind.Rejected]))
        {
            events.Add(RecordEventKind.StatusChanged);
        }

        var json = before is null && after is null ? null : AutomationJson.Serialize(new { before, after, action });
        return new RecordChange(events, changed, json, before, after);
    }

    /// <summary>Reads back the change a queue item carried.</summary>
    public static RecordChange? FromQueue(string? changesJson)
    {
        if (string.IsNullOrWhiteSpace(changesJson))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(changesJson);
        var root = doc.RootElement;
        string? Raw(string name) => root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Object ? v.GetRawText() : null;
        var action = root.TryGetProperty("action", out var act) ? act.GetString() ?? "Updated" : "Updated";
        return From(action, Raw("before"), Raw("after"));
    }

    public string? BeforeValue(string field) => Value(Before, field);

    public string? AfterValue(string field) => Value(After, field);

    private static string? Value(JsonElement? element, string field)
    {
        if (element is not { ValueKind: JsonValueKind.Object } e)
        {
            return null;
        }

        foreach (var property in e.EnumerateObject())
        {
            if (string.Equals(property.Name, field, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.ToString();
            }
        }

        return null;
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Wakes the automation background service soon after a change queued a run (it otherwise polls every few seconds).</summary>
public sealed class AutomationSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Pulse()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}
