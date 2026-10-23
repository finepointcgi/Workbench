using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Formulas;
using Workbench.Application.Identity;
using Workbench.Domain;
using Workbench.Domain.Formulas;
using Workbench.Domain.Sheets;

namespace Workbench.Application.Sheets;

/// <summary>
/// Works out a sheet's computed cells: lookup columns (from rows linked in another sheet), cross-sheet references and
/// formulas. Nothing is stored; every read recalculates, so lookups and references are always live.
/// <para>
/// Other sheets are read as the current user: a sheet they can't see contributes nothing (lookups are blank, links are
/// dropped from what they get back, references are #NO ACCESS), and its restricted columns count as hidden unless they may
/// see them. Without an <see cref="AccessControl"/> (raw services in tests) every sheet is visible, as for the system user.
/// </para>
/// </summary>
public sealed class SheetCalculator(
    IWorkbenchDbContextFactory dbFactory, TimeProvider clock, AccessControl? access = null, RestrictedColumnAccess? restricted = null)
{
    /// <summary>How deep lookups and references may chain through other sheets before giving up.</summary>
    private const int MaxDepth = 4;

    public static readonly string[] BuiltInFields = ["title", "start", "finish"];

    /// <summary>True when the current user can see <paramref name="sheetId"/> (always, without access control).</summary>
    public async Task<bool> CanSeeSheetAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
        access is null || await access.GetSheetRoleAsync(sheetId, cancellationToken) is not null;

    /// <summary>
    /// The sheet's items with computed values merged into <see cref="Item.Values"/>: lookups and formulas added, links the
    /// user can't follow removed. The caller has already decided the user may read this sheet; its own restricted
    /// columns are left for the permission decorator to strip. The items are copies; nothing is tracked.
    /// </summary>
    public async Task<IReadOnlyList<Item>> ComputeAsync(
        IWorkbenchDbContext db, Guid sheetId, IReadOnlyList<Column> columns, IReadOnlyList<Item> items, CancellationToken cancellationToken)
    {
        var session = new Session(this, db, cancellationToken);
        return (await session.ComputeAsync(sheetId, columns, items, depth: 0)).Items;
    }

    /// <summary>
    /// Another sheet as the current user sees it, fully computed, or null when they can't see it. Hidden columns are left
    /// out of <see cref="ComputedSheet.Columns"/> and their values removed.
    /// </summary>
    public async Task<ComputedSheet?> ReadVisibleAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken)
    {
        var session = new Session(this, db, cancellationToken);
        var result = await session.GetAsync(sheetId, depth: 0);
        return result.Status == ReadStatus.Ok ? result.Sheet : null;
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    /// <summary>Columns of <paramref name="sheetId"/> hidden from the current user (none without access control).</summary>
    private async Task<IReadOnlySet<Guid>> HiddenAsync(Guid sheetId, CancellationToken cancellationToken) =>
        restricted is null || access is null ? new HashSet<Guid>() : await restricted.HiddenColumnsAsync(sheetId, cancellationToken);

    private enum ReadStatus
    {
        Ok,
        NoAccess,
        Missing,
        Circular,
    }

    private readonly record struct ReadResult(ReadStatus Status, ComputedSheet? Sheet);

    /// <summary>One calculation: caches each sheet it reads and notices sheets that read each other.</summary>
    private sealed class Session(SheetCalculator owner, IWorkbenchDbContext db, CancellationToken cancellationToken)
    {
        private readonly Dictionary<Guid, ReadResult> _cache = [];
        private readonly HashSet<Guid> _inProgress = [];

        public async Task<ReadResult> GetAsync(Guid sheetId, int depth)
        {
            if (_cache.TryGetValue(sheetId, out var cached))
            {
                return cached;
            }

            if (_inProgress.Contains(sheetId) || depth > MaxDepth)
            {
                return new ReadResult(ReadStatus.Circular, null);
            }

            if (!await owner.CanSeeSheetAsync(sheetId, cancellationToken))
            {
                return _cache[sheetId] = new ReadResult(ReadStatus.NoAccess, null);
            }

            var sheet = await db.Sheets.AsNoTracking().Where(s => s.Id == sheetId).Select(s => new { s.Id, s.Name }).FirstOrDefaultAsync(cancellationToken);
            if (sheet is null)
            {
                return _cache[sheetId] = new ReadResult(ReadStatus.Missing, null);
            }

            var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).OrderBy(c => c.Order).ToListAsync(cancellationToken);
            var items = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
            var hidden = await owner.HiddenAsync(sheetId, cancellationToken);

            var computed = await ComputeAsync(sheetId, columns, items, depth);
            var visibleColumns = columns.Where(c => !hidden.Contains(c.Id)).ToList();
            foreach (var item in computed.Items.Where(_ => hidden.Count > 0))
            {
                item.Values = item.Values.Where(v => !(Guid.TryParse(v.Key, out var id) && hidden.Contains(id))).ToDictionary(v => v.Key, v => v.Value);
            }

            var result = new ComputedSheet(sheet.Id, sheet.Name, visibleColumns, computed.Items, computed.ValueTypes, hidden);
            return _cache[sheetId] = new ReadResult(ReadStatus.Ok, result);
        }

        public async Task<(IReadOnlyList<Item> Items, IReadOnlyDictionary<Guid, ColumnType> ValueTypes)> ComputeAsync(
            Guid sheetId, IReadOnlyList<Column> columns, IReadOnlyList<Item> source, int depth)
        {
            // Copies, so callers' entities (and EF's) are never changed.
            var items = source.Select(Copy).ToList();
            var valueTypes = new Dictionary<Guid, ColumnType>();
            _inProgress.Add(sheetId);
            try
            {
                await ApplyLinksAsync(sheetId, columns, items, valueTypes, depth);
                var references = await ReferencesAsync(sheetId, columns, depth);
                var computed = FormulaCalculator.Calculate(columns, items, owner.Today(), new FormulaInputs(references, valueTypes));
                foreach (var item in items)
                {
                    if (computed.TryGetValue(item.Id, out var row))
                    {
                        foreach (var (columnId, value) in row)
                        {
                            item.Values[columnId.ToString()] = value;
                        }
                    }
                }
            }
            finally
            {
                _inProgress.Remove(sheetId);
            }

            return (items, valueTypes);
        }

        /// <summary>Keeps only links the user can follow, then fills lookup columns from the linked rows.</summary>
        private async Task ApplyLinksAsync(Guid sheetId, IReadOnlyList<Column> columns, List<Item> items, Dictionary<Guid, ColumnType> valueTypes, int depth)
        {
            var links = columns.Where(c => c.Type == ColumnType.LinkedRow).ToList();
            var lookups = columns.Where(c => c.Type == ColumnType.Lookup).ToList();
            foreach (var item in items)
            {
                foreach (var lookup in lookups)
                {
                    item.Values.Remove(lookup.Id.ToString()); // computed, never stored
                }
            }

            if (links.Count == 0)
            {
                return;
            }

            var targets = new Dictionary<Guid, ComputedSheet?>();
            foreach (var link in links)
            {
                var targetId = SheetLinks.TargetOf(link);
                ComputedSheet? target = null;
                if (targetId == sheetId)
                {
                    // Linking rows of the same sheet: look up stored values (formulas of this sheet aren't worked out yet).
                    target = new ComputedSheet(sheetId, "", columns, items, new Dictionary<Guid, ColumnType>(), new HashSet<Guid>());
                }
                else if (targetId is { } id)
                {
                    var read = await GetAsync(id, depth + 1);
                    target = read.Status == ReadStatus.Ok ? read.Sheet : null;
                }

                targets[link.Id] = target;
                var key = link.Id.ToString();
                foreach (var item in items)
                {
                    if (!item.Values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    var kept = target is null ? [] : SheetLinks.Parse(raw).Where(target.Contains).ToList();
                    if (kept.Count == 0)
                    {
                        item.Values.Remove(key);
                    }
                    else
                    {
                        item.Values[key] = SheetLinks.Format(kept);
                    }
                }
            }

            foreach (var lookup in lookups)
            {
                if (SheetLinks.LookupOf(lookup) is not { } spec
                    || links.FirstOrDefault(l => l.Id == spec.LinkColumnId) is not { } link
                    || targets[link.Id] is not { } target
                    || !target.HasField(spec.Field))
                {
                    continue;
                }

                valueTypes[lookup.Id] = target.TypeOf(spec.Field);
                var key = lookup.Id.ToString();
                foreach (var item in items)
                {
                    var values = SheetLinks.Parse(item.Values.GetValueOrDefault(link.Id.ToString()))
                        .Select(id => target.ValueOf(id, spec.Field))
                        .Where(v => !string.IsNullOrEmpty(v))
                        .ToList();
                    if (values.Count > 0)
                    {
                        item.Values[key] = string.Join(", ", values);
                    }
                }
            }
        }

        /// <summary>The values of the cross-sheet references this sheet's formulas use, by name.</summary>
        private async Task<IReadOnlyDictionary<string, FormulaValue>?> ReferencesAsync(Guid sheetId, IReadOnlyList<Column> columns, int depth)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns.Where(c => !string.IsNullOrWhiteSpace(c.Formula) && c.Formula.Contains('{')))
            {
                try
                {
                    names.UnionWith(Formula.Parse(column.Formula!).SheetReferences);
                }
                catch (FormulaParseException)
                {
                    // Shows #NAME? anyway.
                }
            }

            if (names.Count == 0)
            {
                return null;
            }

            var references = await db.Set<SheetReference>().AsNoTracking().Where(r => r.SheetId == sheetId).ToListAsync(cancellationToken);
            var values = new Dictionary<string, FormulaValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in references.Where(r => names.Contains(r.Name)))
            {
                values[reference.Name.Trim()] = await ResolveAsync(reference, depth);
            }

            return values;
        }

        private async Task<FormulaValue> ResolveAsync(SheetReference reference, int depth)
        {
            var read = await GetAsync(reference.SourceSheetId, depth + 1);
            switch (read.Status)
            {
                case ReadStatus.NoAccess:
                    return FormulaValue.FromError(FormulaErrors.NoAccess);
                case ReadStatus.Missing:
                    return FormulaValue.FromError(FormulaErrors.Ref);
                case ReadStatus.Circular:
                    return FormulaValue.FromError(FormulaErrors.Circular);
            }

            var source = read.Sheet!;
            if (reference.Fields.Count == 0)
            {
                return FormulaValue.FromError(FormulaErrors.Ref);
            }

            foreach (var field in reference.Fields)
            {
                if (source.IsHidden(field))
                {
                    return FormulaValue.FromError(FormulaErrors.NoAccess);
                }

                if (!source.HasField(field))
                {
                    return FormulaValue.FromError(FormulaErrors.Ref);
                }
            }

            var cells = new List<FormulaValue>();
            foreach (var item in source.Outline())
            {
                cells.AddRange(reference.Fields.Select(f => source.TypedValueOf(item, f)));
            }

            return reference.Fields.Count == 1 ? FormulaValue.FromList(cells) : FormulaValue.FromTable(cells, reference.Fields.Count);
        }

        private static Item Copy(Item i) => new()
        {
            Id = i.Id,
            SheetId = i.SheetId,
            ParentId = i.ParentId,
            Order = i.Order,
            Title = i.Title,
            Start = i.Start,
            Finish = i.Finish,
            CostpointWbsId = i.CostpointWbsId,
            WbsCodeIsAuto = i.WbsCodeIsAuto,
            Values = new Dictionary<string, string?>(i.Values),
            IsMilestone = i.IsMilestone,
        };
    }
}

/// <summary>A sheet with every computed cell filled in, as one user sees it.</summary>
public sealed class ComputedSheet
{
    private readonly Dictionary<Guid, Item> _byId;
    private readonly IReadOnlyDictionary<Guid, ColumnType> _valueTypes;
    private readonly IReadOnlySet<Guid> _hidden;

    public ComputedSheet(Guid id, string name, IReadOnlyList<Column> columns, IReadOnlyList<Item> items,
        IReadOnlyDictionary<Guid, ColumnType> valueTypes, IReadOnlySet<Guid> hidden)
    {
        Id = id;
        Name = name;
        Columns = columns;
        Items = items;
        _valueTypes = valueTypes;
        _hidden = hidden;
        _byId = items.ToDictionary(i => i.Id);
    }

    public Guid Id { get; }

    public string Name { get; }

    /// <summary>The columns the user may see, in order.</summary>
    public IReadOnlyList<Column> Columns { get; }

    public IReadOnlyList<Item> Items { get; }

    public bool Contains(Guid itemId) => _byId.ContainsKey(itemId);

    public Item? Find(Guid itemId) => _byId.GetValueOrDefault(itemId);

    public bool IsHidden(string field) => Guid.TryParse(field, out var id) && _hidden.Contains(id);

    public bool HasField(string field) =>
        SheetCalculator.BuiltInFields.Contains(field, StringComparer.OrdinalIgnoreCase)
        || (Guid.TryParse(field, out var id) && Columns.Any(c => c.Id == id));

    /// <summary>The field's name: Title, Start, Finish or the column's name.</summary>
    public string? NameOf(string field) =>
        SheetCalculator.BuiltInFields.FirstOrDefault(b => string.Equals(b, field, StringComparison.OrdinalIgnoreCase)) is { } builtIn
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(builtIn)
            : Guid.TryParse(field, out var id) ? Columns.FirstOrDefault(c => c.Id == id)?.Name : null;

    public ColumnType TypeOf(string field) => field.ToLowerInvariant() switch
    {
        "title" => ColumnType.Text,
        "start" or "finish" => ColumnType.Date,
        _ => Guid.TryParse(field, out var id) && Columns.FirstOrDefault(c => c.Id == id) is { } column
            ? _valueTypes.TryGetValue(column.Id, out var type) ? type : column.Type
            : ColumnType.Text,
    };

    /// <summary>The stored or computed cell string for an item and field, or null.</summary>
    public string? ValueOf(Guid itemId, string field) => Find(itemId) is { } item ? ValueOf(item, field) : null;

    public string? ValueOf(Item item, string field) => field.ToLowerInvariant() switch
    {
        "title" => item.Title,
        "start" => item.Start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "finish" => item.Finish?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => Guid.TryParse(field, out var id) && !_hidden.Contains(id) ? item.Values.GetValueOrDefault(id.ToString()) : null,
    };

    /// <summary>A cell as a formula value: typed by its column, errors kept as errors.</summary>
    public FormulaValue TypedValueOf(Item item, string field)
    {
        var raw = ValueOf(item, field);
        return FormulaErrors.IsError(raw) ? FormulaValue.FromError(raw!) : FormulaValue.FromCell(raw, TypeOf(field));
    }

    /// <summary>Items in outline order: each before its children, siblings by order.</summary>
    public IReadOnlyList<Item> Outline()
    {
        var children = Items.Where(i => i.ParentId is { } p && _byId.ContainsKey(p)).ToLookup(i => i.ParentId!.Value);
        var list = new List<Item>(Items.Count);
        var visited = new HashSet<Guid>();
        foreach (var root in Items.Where(i => i.ParentId is not { } p || !_byId.ContainsKey(p)).OrderBy(i => i.Order).Concat(Items))
        {
            Walk(root);
        }

        return list;

        void Walk(Item item)
        {
            if (!visited.Add(item.Id))
            {
                return;
            }

            list.Add(item);
            foreach (var child in children[item.Id].OrderBy(c => c.Order))
            {
                Walk(child);
            }
        }
    }
}

/// <summary>How link and lookup columns keep their settings in <see cref="Column.Options"/>, and link cell values.</summary>
public static class SheetLinks
{
    /// <summary>The sheet a LinkedRow column links to (Options[0]).</summary>
    public static Guid? TargetOf(Column column) =>
        column.Type == ColumnType.LinkedRow && column.Options.Count > 0 && Guid.TryParse(column.Options[0], out var id) ? id : null;

    /// <summary>A Lookup column's link column and field (Options[0], Options[1]).</summary>
    public static (Guid LinkColumnId, string Field)? LookupOf(Column column) =>
        column.Type == ColumnType.Lookup && column.Options.Count > 1 && Guid.TryParse(column.Options[0], out var link)
            ? (link, column.Options[1].Trim().ToLowerInvariant())
            : null;

    /// <summary>The item ids in a link cell (comma-separated); anything that isn't an id is ignored.</summary>
    public static IReadOnlyList<Guid> Parse(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => Guid.TryParse(p, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

    public static string Format(IEnumerable<Guid> ids) => string.Join(",", ids);
}
