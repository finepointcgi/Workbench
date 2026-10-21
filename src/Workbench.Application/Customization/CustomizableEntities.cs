using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Domain;

namespace Workbench.Application.Customization;

/// <summary>A record's id and what people call it ("1001 · Program Alpha", "PO-000012").</summary>
public sealed record RecordTitle(Guid Id, string Title);

/// <summary>A built-in property the entity field catalog exposes (key = camelCase property name).</summary>
public sealed record BuiltInField(string Key, string Label, string FieldType, Type ClrType, IReadOnlyList<string>? Options, bool IsRequired, PropertyInfo Property);

/// <summary>
/// A record type that takes custom fields. Opt a new type in with one <c>services.AddCustomizableEntity(new CustomizableEntity&lt;T&gt; { ... })</c>
/// line in <see cref="CustomizationRegistration"/>: its key, labels, title, where its page is, who may read and change it,
/// and (optionally) when it counts as approved and which status set it uses.
/// </summary>
public abstract class CustomizableEntity
{
    /// <summary>Kebab-case key (see <c>CustomizableEntityTypes</c>).</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }
    public required string LabelPlural { get; init; }

    /// <summary>The <c>AuditEvent.EntityType</c> its own audit rows use, so custom field changes show in the same history.</summary>
    public abstract string AuditEntityType { get; }

    public abstract Type ClrType { get; }

    /// <summary>App-relative page of a record, for search hits and record links.</summary>
    public Func<Guid, string>? Link { get; init; }

    /// <summary>The status set (labels and sub-statuses) of its status field, if any.</summary>
    public string? StatusSet { get; init; }

    /// <summary>Organization roles that may read and change every record's values (lists, exports, search, filters).</summary>
    public required Func<OrgRoles, bool> ManageRoles { get; init; }

    /// <summary>
    /// Whether the current user may open this record, through the module's own permission-checked service (resolved from
    /// the caller's scope). Returning false or throwing not-found reads as "no such record".
    /// </summary>
    public required Func<IServiceProvider, Guid, CancellationToken, Task<bool>> CanReadAsync { get; init; }

    /// <summary>Built-in properties automations and forms may set through the entity field catalog (camelCase keys).</summary>
    public IReadOnlySet<string> EditableBuiltIns { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public abstract bool HasApproval { get; }

    public abstract Task<bool> ExistsAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct);

    public abstract Task<IReadOnlyDictionary<Guid, string>> TitlesAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct);

    public abstract Task<IReadOnlyList<RecordTitle>> AllAsync(IWorkbenchDbContext db, int limit, CancellationToken ct);

    /// <summary>True once the record is approved, posted, issued or closed: "read-only after approval" fields are locked.</summary>
    public abstract Task<bool> IsLockedAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct);

    /// <summary>The employee who owns the record (timesheet, expense report) or manages it (project), who may change its values too.</summary>
    public abstract Task<Guid?> OwnerEmployeeAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct);

    /// <summary>The record's current status value name, for sub-statuses.</summary>
    public abstract Task<string?> StatusAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct);

    public abstract IReadOnlyList<BuiltInField> BuiltInFields { get; }

    public abstract Task<object?> LoadAsync(IWorkbenchDbContext db, Guid id, bool tracking, CancellationToken ct);

    /// <summary>A read check through a contract service's get method (null = not found).</summary>
    public static Func<IServiceProvider, Guid, CancellationToken, Task<bool>> ReadVia<TService>(Func<TService, Guid, CancellationToken, Task<object?>> get)
        where TService : notnull =>
        async (sp, id, ct) =>
        {
            try
            {
                return await get(sp.GetRequiredService<TService>(), id, ct) is not null;
            }
            catch (WorkbenchNotFoundException)
            {
                return false;
            }
        };
}

public sealed class CustomizableEntity<T> : CustomizableEntity
    where T : Entity
{
    private IReadOnlyList<BuiltInField>? _builtIns;

    /// <summary>A translatable projection, for example <c>p =&gt; new RecordTitle(p.Id, p.Code + " · " + p.Name)</c>.</summary>
    public required Expression<Func<T, RecordTitle>> Title { get; init; }

    public Func<T, bool>? IsLocked { get; init; }
    public Func<T, Guid?>? OwnerEmployee { get; init; }
    public Func<T, string?>? Status { get; init; }

    public override string AuditEntityType => typeof(T).Name;
    public override Type ClrType => typeof(T);
    public override bool HasApproval => IsLocked is not null;

    public override Task<bool> ExistsAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) => db.Set<T>().AnyAsync(e => e.Id == id, ct);

    public override async Task<IReadOnlyDictionary<Guid, string>> TitlesAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var list = ids.Distinct().ToList();
        var rows = new List<RecordTitle>();
        foreach (var chunk in list.Chunk(500))
        {
            rows.AddRange(await db.Set<T>().AsNoTracking().Where(e => chunk.Contains(e.Id)).Select(Title).ToListAsync(ct));
        }

        return rows.ToDictionary(r => r.Id, r => r.Title);
    }

    public override async Task<IReadOnlyList<RecordTitle>> AllAsync(IWorkbenchDbContext db, int limit, CancellationToken ct) =>
        (await db.Set<T>().AsNoTracking().Select(Title).Take(limit).ToListAsync(ct)).OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();

    public override async Task<bool> IsLockedAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        IsLocked is not null && await db.Set<T>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) is { } e && IsLocked(e);

    public override async Task<Guid?> OwnerEmployeeAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        OwnerEmployee is not null && await db.Set<T>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) is { } e ? OwnerEmployee(e) : null;

    public override async Task<string?> StatusAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        Status is not null && await db.Set<T>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) is { } e ? Status(e) : null;

    public override async Task<object?> LoadAsync(IWorkbenchDbContext db, Guid id, bool tracking, CancellationToken ct) =>
        await (tracking ? db.Set<T>() : db.Set<T>().AsNoTracking()).FirstOrDefaultAsync(e => e.Id == id, ct);

    public override IReadOnlyList<BuiltInField> BuiltInFields => _builtIns ??= BuiltIns.For(typeof(T));
}

/// <summary>The registered customizable entity types (a singleton: definitions only, no tenant data).</summary>
public sealed class CustomizableEntityRegistry(IEnumerable<CustomizableEntity> entities)
{
    private readonly Dictionary<string, CustomizableEntity> _byKey = entities.GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

    public IReadOnlyList<CustomizableEntity> All => _byKey.Values.OrderBy(e => e.LabelPlural, StringComparer.Ordinal).ToList();

    public IReadOnlyList<string> Keys => All.Select(e => e.Key).ToList();

    public CustomizableEntity? Find(string? key) => key is not null && _byKey.TryGetValue(key, out var e) ? e : null;

    public CustomizableEntity Get(string? entityType) =>
        Find(entityType) ?? throw new ArgumentException($"'{entityType}' doesn't take custom fields. Use one of: {string.Join(", ", _byKey.Keys.Order(StringComparer.Ordinal))}.");
}

/// <summary>Built-in fields read by reflection: public scalar properties (strings, numbers, dates, flags, enums, ids).</summary>
internal static class BuiltIns
{
    public static IReadOnlyList<BuiltInField> For(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != nameof(Entity.Id) && Kind(p.PropertyType) is not null)
            .Select(p =>
            {
                var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                var required = p.GetCustomAttributes().Any(a => a.GetType().Name == "RequiredMemberAttribute") || (t.IsValueType && Nullable.GetUnderlyingType(p.PropertyType) is null && t != typeof(bool));
                return new BuiltInField(CamelCase(p.Name), Words(p.Name), Kind(p.PropertyType)!, t, t.IsEnum ? Enum.GetNames(t) : null, required, p);
            })
            .ToList();

    private static string? Kind(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(string) ? "Text"
            : t == typeof(decimal) || t == typeof(int) || t == typeof(long) || t == typeof(double) ? "Number"
            : t == typeof(DateOnly) || t == typeof(DateTime) ? "Date"
            : t == typeof(bool) ? "YesNo"
            : t.IsEnum ? "Picklist"
            : t == typeof(Guid) ? "RecordLink"
            : null;
    }

    public static string CamelCase(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    public static string Words(string name)
    {
        var trimmed = name.EndsWith("Utc", StringComparison.Ordinal) ? name[..^3] : name;
        trimmed = trimmed.EndsWith("Id", StringComparison.Ordinal) && trimmed.Length > 2 ? trimmed[..^2] : trimmed;
        var chars = new List<char>();
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (i > 0 && char.IsUpper(trimmed[i]) && !char.IsUpper(trimmed[i - 1]))
            {
                chars.Add(' ');
            }

            chars.Add(i == 0 ? trimmed[i] : char.ToLowerInvariant(trimmed[i]));
        }

        return new string([.. chars]);
    }
}

/// <summary>Typed add/remove on the context interface (it exposes only sets).</summary>
internal static class CustomizationDb
{
    public static void Add<T>(this IWorkbenchDbContext db, T entity)
        where T : class => db.Set<T>().Add(entity);

    public static void Remove<T>(this IWorkbenchDbContext db, T entity)
        where T : class => db.Set<T>().Remove(entity);

    public static void RemoveRange<T>(this IWorkbenchDbContext db, IEnumerable<T> entities)
        where T : class => db.Set<T>().RemoveRange(entities);
}
