using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Workbench.Contracts.Sheets;
using Workbench.Domain;

namespace Workbench.Application.Sheets;

/// <summary>
/// Stamps every sheet row as it is saved, whichever feature saves it (the grid, the board, forms, imports, the plan editor):
/// when it was added and last changed (<see cref="Item.CreatedAtUtc"/>, <see cref="Item.ModifiedAtUtc"/>), by whom when the
/// saving service said (<see cref="As"/>), the next number of each auto-number column for a new row, and the values of the
/// sheet's Created by / Created date / Modified by / Modified date columns. Those land in <see cref="Item.Values"/> like any
/// other cell, so filters, sorting, formulas, exports, automations and rules read them with no special case.
/// The database context calls <see cref="ApplyAsync"/> before every save.
/// </summary>
public static class SheetRowStamps
{
    private static readonly AsyncLocal<string?> Author = new();

    private static readonly ColumnType[] Filled =
        [ColumnType.AutoNumber, ColumnType.CreatedBy, ColumnType.CreatedDate, ColumnType.ModifiedBy, ColumnType.ModifiedDate];

    /// <summary>Records <paramref name="email"/> as the person making the following saves (in this async flow).</summary>
    public static IDisposable As(string? email)
    {
        var previous = Author.Value;
        Author.Value = string.IsNullOrWhiteSpace(email) || !email.Contains('@') ? previous : email.Trim().ToLowerInvariant();
        return new Restore(() => Author.Value = previous);
    }

    private static readonly AsyncLocal<bool> Quiet = new();

    /// <summary>
    /// Saves in this flow don't count as changing rows (a column added, removed or filled in on every row): new rows are
    /// still stamped, existing rows keep their Modified date and by.
    /// </summary>
    public static IDisposable Quietly()
    {
        var previous = Quiet.Value;
        Quiet.Value = true;
        return new Restore(() => Quiet.Value = previous);
    }

    /// <summary>The person the current flow saves as, if a service said.</summary>
    public static string? CurrentAuthor => Author.Value;

    public static async Task ApplyAsync(DbContext context, CancellationToken cancellationToken)
    {
        var rows = Changed(context);
        if (rows.Count > 0)
        {
            var sheetIds = rows.Select(r => r.Entity.SheetId).Distinct().ToList();
            Apply(context, rows, await Query(context, sheetIds).ToListAsync(cancellationToken));
        }
    }

    public static void Apply(DbContext context)
    {
        var rows = Changed(context);
        if (rows.Count > 0)
        {
            var sheetIds = rows.Select(r => r.Entity.SheetId).Distinct().ToList();
            Apply(context, rows, [.. Query(context, sheetIds)]);
        }
    }

    // Spelled out (not Filled.Contains) so every provider translates it with the column's string conversion.
    private static IQueryable<Column> Query(DbContext context, List<Guid> sheetIds) =>
        context.Set<Column>().Where(c => sheetIds.Contains(c.SheetId) && (c.Type == ColumnType.AutoNumber || c.Type == ColumnType.CreatedBy
            || c.Type == ColumnType.CreatedDate || c.Type == ColumnType.ModifiedBy || c.Type == ColumnType.ModifiedDate));

    /// <summary>When a row was added: its stamp, else the time in its (version 7) id, which every row gets when created.</summary>
    public static DateTime? CreatedAt(Item item) => item.CreatedAtUtc ?? TimeOfId(item.Id);

    /// <summary>The value of a system column for a row (null when not known).</summary>
    public static string? SystemValue(Item item, ColumnType type) => type switch
    {
        ColumnType.CreatedBy => item.CreatedBy,
        ColumnType.CreatedDate => CreatedAt(item) is { } created ? ColumnValues.Stamp(created) : null,
        ColumnType.ModifiedBy => item.ModifiedBy ?? item.CreatedBy,
        ColumnType.ModifiedDate => (item.ModifiedAtUtc ?? CreatedAt(item)) is { } modified ? ColumnValues.Stamp(modified) : null,
        _ => null,
    };

    /// <summary>Rows being added, and rows changed in more than their position among siblings.</summary>
    private static List<EntityEntry<Item>> Changed(DbContext context) =>
        context.ChangeTracker.Entries<Item>()
            .Where(e => e.State == EntityState.Added
                || (e.State == EntityState.Modified && !Quiet.Value && e.Properties.Any(p => p.IsModified && p.Metadata.Name != nameof(Item.Order))))
            .ToList();

    private static void Apply(DbContext context, List<EntityEntry<Item>> rows, List<Column> stored)
    {
        var now = DateTime.UtcNow;
        var author = Author.Value;
        // Columns added in this same save (a sheet copied with its rows) count too; deleted ones don't.
        var columns = stored.Where(c => context.Entry(c).State != EntityState.Deleted)
            .Concat(context.ChangeTracker.Entries<Column>().Where(e => e.State == EntityState.Added && Filled.Contains(e.Entity.Type)).Select(e => e.Entity))
            .DistinctBy(c => c.Id)
            .ToLookup(c => c.SheetId);

        foreach (var row in rows)
        {
            var item = row.Entity;
            if (row.State == EntityState.Added)
            {
                item.CreatedAtUtc ??= now;
                item.CreatedBy ??= author;
                item.ModifiedAtUtc ??= item.CreatedAtUtc;
                item.ModifiedBy ??= item.CreatedBy;
            }
            else
            {
                item.ModifiedAtUtc = now;
                if (!row.Property(i => i.ModifiedBy).IsModified && author is not null)
                {
                    item.ModifiedBy = author;
                }
            }

            if (!columns.Contains(item.SheetId))
            {
                continue;
            }

            var values = new Dictionary<string, string?>(item.Values);
            foreach (var column in columns[item.SheetId].OrderBy(c => c.Order))
            {
                var key = column.Id.ToString();
                if (column.Type == ColumnType.AutoNumber)
                {
                    if (row.State == EntityState.Added && string.IsNullOrEmpty(values.GetValueOrDefault(key)))
                    {
                        values[key] = Next(column);
                    }

                    continue;
                }

                if (SystemValue(item, column.Type) is { } value)
                {
                    values[key] = value;
                }
            }

            if (values.Count != item.Values.Count || values.Any(v => item.Values.GetValueOrDefault(v.Key) != v.Value))
            {
                item.Values = values;
            }
        }
    }

    /// <summary>The column's next number as text, moving the counter on (numbers are never reused).</summary>
    public static string Next(Column column)
    {
        var (_, _, start) = ColumnValues.AutoNumberSettings(column.Options);
        var number = Math.Max(column.NextNumber, start);
        column.NextNumber = number + 1;
        return ColumnValues.AutoNumber(column.Options, number);
    }

    /// <summary>The creation time held in a version 7 GUID (its first 48 bits are Unix milliseconds), else null.</summary>
    private static DateTime? TimeOfId(Guid id)
    {
        if (id.Version != 7)
        {
            return null;
        }

        var text = id.ToString("N");
        var ms = Convert.ToInt64(text[..12], 16);
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
