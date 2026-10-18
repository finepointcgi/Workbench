using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Imports;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Collaboration;
using Workbench.Domain.Imports;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Scheduling;
using ImportKind = Workbench.Domain.Imports.ImportKind;
using ImportSource = Workbench.Domain.Imports.ImportSource;

namespace Workbench.Application.Imports.Smartsheet;

/// <summary>
/// Smartsheet sheets (from the API or an .xlsx export) → Workbench sheets: columns with types, rows with hierarchy and
/// values, start/finish, predecessors as Gantt dependencies and, from the API, optionally comments and attachments.
/// Sheets match on name (or the Smartsheet sheet id), rows on the Smartsheet row id, so importing again updates.
/// A WBS code column links rows to a project's WBS elements and makes the sheet that project's plan.
/// </summary>
public abstract class SheetImporter : IImporter
{
    public const string RowScope = "smartsheet.row";
    public const string SheetScope = "smartsheet.sheet";
    public const string CommentScope = "smartsheet.comment";
    public const string AttachmentScope = "smartsheet.attachment";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public abstract ImportSource Source { get; }
    public ImportKind Kind => ImportKind.Sheets;
    public abstract string Title { get; }
    public abstract string Description { get; }
    public abstract string? SampleFile { get; }

    public IReadOnlyList<ImportField> Fields { get; } =
    [
        new("Title", "Task name", true, "The primary column: each row's title.", "Task Name", "Task", "Name", "Primary", "Title", "Activity"),
        new("Start", "Start", false, "Start date (the Gantt start column).", "Start Date", "Start", "Begin", "Planned Start"),
        new("Finish", "Finish", false, "Finish date (the Gantt end column).", "End Date", "Finish", "Finish Date", "End", "Due Date", "Due", "Planned Finish"),
        new("Duration", "Duration", false, "Used for the finish date when a row has none (working days).", "Duration", "Dur"),
        new("Predecessors", "Predecessors", false, "Smartsheet predecessors (FS/SS/FF/SF with lag) become Gantt dependencies.", "Predecessors", "Predecessor", "Depends On"),
        new("WbsCode", "WBS code", false, "Links rows to the chosen project's WBS elements by code.", "WBS", "WBS Code", "WBS ID", "WBS Element", "Charge Code", "Costpoint WBS", "PROJ_ID", "Project ID"),
        new("RowId", "Row ID", false, ".xlsx only: a column with the Smartsheet row id, for matching on re-import. Without it rows match on their title path.", "Row ID", "Smartsheet Row ID", "RowId"),
    ];

    /// <summary>The job's sheets in the common shape.</summary>
    protected abstract IReadOnlyList<SourceSheet> Load(IReadOnlyList<ImportJobFile> files);

    public Task<ImportPreview> PreviewAsync(ImportJob job, IReadOnlyList<ImportJobFile> files, CancellationToken cancellationToken)
    {
        var sheets = Load(files);
        var first = sheets.FirstOrDefault();
        var headers = sheets.SelectMany(s => s.Columns.Select(c => c.Title)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sample = first is null ? [] : first.Rows.Take(5)
            .Select(r => (IReadOnlyList<string>)first.Columns.Select(c => r.Cells.TryGetValue(c.Title, out var cell) ? cell.Display ?? cell.Value ?? "" : "").ToList())
            .ToList();
        return Task.FromResult(new ImportPreview(headers, sample, SuggestMapping(sheets)));
    }

    public ImportMappingDto SuggestMapping(IReadOnlyList<SourceSheet> sheets)
    {
        var columns = sheets.SelectMany(s => s.Columns).GroupBy(c => c.Title, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var titles = columns.Select(c => c.Title).ToList();
        var fields = FieldMatcher.AutoMap(Fields, titles);
        string? Tagged(params string[] tags) => columns.FirstOrDefault(c => c.Tags.Any(tags.Contains))?.Title;
        fields["Title"] = columns.FirstOrDefault(c => c.Primary)?.Title ?? fields["Title"] ?? titles.FirstOrDefault();
        fields["Start"] = Tagged("GANTT_START_DATE") ?? fields["Start"];
        fields["Finish"] = Tagged("GANTT_END_DATE") ?? fields["Finish"];
        fields["Duration"] = Tagged("GANTT_DURATION") ?? columns.FirstOrDefault(c => c.Type == "DURATION")?.Title ?? fields["Duration"];
        fields["Predecessors"] = Tagged("GANTT_PREDECESSOR") ?? columns.FirstOrDefault(c => c.Type == "PREDECESSOR")?.Title ?? fields["Predecessors"];
        if (sheets.Any(s => s.FromApi))
        {
            fields["RowId"] = null;
        }

        var used = fields.Values.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = columns.Where(c => !used.Contains(c.Title))
            .Select(c =>
            {
                var values = sheets.SelectMany(s => s.Rows).Select(r => r.Cells.TryGetValue(c.Title, out var cell) ? cell : null).OfType<SourceCell>().ToList();
                var kind = KindFor(c, values);
                var options = kind == ColumnKind.Dropdown
                    ? c.Options.Concat(values.Select(v => v.Value).OfType<string>()).Distinct(StringComparer.Ordinal).ToList()
                    : [];
                return new ImportColumnDto(c.Title, c.Title, kind, c.SystemType is null && !SmartsheetSources.SystemColumnTypes.Contains(c.Type), options, c.Type);
            })
            .ToList();
        return new ImportMappingDto(fields, list);
    }

    /// <summary>Smartsheet column type → Workbench column type.</summary>
    public static ColumnKind KindFor(SourceColumn column, IReadOnlyList<SourceCell> values) => column.Type switch
    {
        "DATE" or "DATETIME" or "ABSTRACT_DATETIME" => ColumnKind.Date,
        "CHECKBOX" => ColumnKind.Checkbox,
        "PICKLIST" => ColumnKind.Dropdown,
        "CONTACT_LIST" => ColumnKind.Person,
        "TEXT_NUMBER" when values.Count > 0 && values.All(v => v.Value is { } x && decimal.TryParse(x, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) =>
            values.Any(v => v.Display?.Contains('$') == true) ? ColumnKind.Currency : ColumnKind.Number,
        _ => ColumnKind.Text,
    };

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken)
    {
        var sheets = Load(context.Files);
        var fields = context.Mapping.Fields;
        if (fields.GetValueOrDefault("Title") is null)
        {
            context.Run.Error("", 0, "Title", "Map a column to Task name.");
            return;
        }

        context.Run.TotalRows = sheets.Sum(s => s.Rows.Count);
        foreach (var sheet in sheets)
        {
            await new SheetRun(this, context, sheet, cancellationToken).RunAsync();
        }
    }

    /// <summary>One sheet's import.</summary>
    private sealed class SheetRun(SheetImporter importer, ImportContext context, SourceSheet source, CancellationToken cancellationToken)
    {
        private readonly ImportRun _run = context.Run;
        private readonly ISheetService _sheets = context.Services.GetRequiredService<ISheetService>();
        private readonly IColumnService _columns = context.Services.GetRequiredService<IColumnService>();
        private readonly IItemService _items = context.Services.GetRequiredService<IItemService>();
        private readonly IDependencyService _dependencies = context.Services.GetRequiredService<IDependencyService>();
        private readonly IWorkbenchDbContextFactory _dbFactory = context.Services.GetRequiredService<IWorkbenchDbContextFactory>();
        private readonly Dictionary<string, Guid> _itemForRow = [];
        private readonly Dictionary<int, string> _rowForNumber = [];
        private string File => source.FromApi ? source.Name : source.Id;

        private string? Field(string name) => context.Mapping.Fields.GetValueOrDefault(name);

        public async Task RunAsync()
        {
            var (sheetId, existingColumns, existingItems) = await ResolveSheetAsync();
            if (sheetId is not { } id)
            {
                return;
            }

            var columnIds = await ColumnsAsync(id, existingColumns);
            var linked = await RowsAsync(id, columnIds, existingItems);
            await DependenciesAsync(id);
            await LinkWbsAsync(id, linked);
            if (source.FromApi && _run.Applying && context.ApiToken is { } token)
            {
                if (context.Options.IncludeComments)
                {
                    await CommentsAsync(id, token);
                }

                if (context.Options.IncludeAttachments)
                {
                    await AttachmentsAsync(id, token);
                }
            }
            else if (source.FromApi && !_run.Applying && (context.Options.IncludeComments || context.Options.IncludeAttachments))
            {
                _run.Warning(File, 0, null, "Comments and attachments are fetched from Smartsheet during the import.");
            }
        }

        private async Task<(Guid?, IReadOnlyList<ColumnDto>, IReadOnlyList<ItemDto>)> ResolveSheetAsync()
        {
            Guid? existing = null;
            if (source.FromApi)
            {
                existing = await FindKeyAsync(SheetScope, source.Id);
            }

            if (existing is { } mapped && await _sheets.GetAsync(mapped, cancellationToken) is null)
            {
                existing = null;
            }

            existing ??= (await _sheets.ListAsync(cancellationToken)).FirstOrDefault(s => string.Equals(s.Name, source.Name, StringComparison.OrdinalIgnoreCase))?.Id;
            if (existing is { } found)
            {
                var detail = await _sheets.GetAsync(found, cancellationToken);
                _run.Skip(File, 0, "Sheet", source.Name, "Exists: rows are matched and updated");
                return (found, detail?.Columns ?? [], await _items.ListAsync(found, cancellationToken));
            }

            var scheduling = source.Scheduling || (Field("Start") is not null && Field("Finish") is not null);
            var id = await _run.CreateAsync(File, 0, typeof(Sheet), "Sheet", source.Name, $"{source.Rows.Count} rows{(scheduling ? ", scheduling on" : "")}", async () =>
            {
                var created = await _sheets.CreateAsync(new CreateSheetRequest(source.Name,
                    source.FromApi ? $"Imported from Smartsheet (sheet {source.Id})." : $"Imported from the Smartsheet export {source.Id}.", scheduling), cancellationToken);
                return created.Id;
            }, cancellationToken);
            if (id is { } newId && source.FromApi)
            {
                await AddKeyAsync(SheetScope, source.Id, newId, $"Sheet {source.Name}");
            }

            return (id, [], []);
        }

        private async Task<Dictionary<string, (Guid Id, ColumnKind Kind, HashSet<string> Options)>> ColumnsAsync(Guid sheetId, IReadOnlyList<ColumnDto> existing)
        {
            var ids = new Dictionary<string, (Guid, ColumnKind, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in context.Mapping.Columns.Where(c => c.Include && source.Columns.Any(s => string.Equals(s.Title, c.Source, StringComparison.OrdinalIgnoreCase))))
            {
                var name = string.IsNullOrWhiteSpace(column.Name) ? column.Source : column.Name.Trim();
                var options = column.Type == ColumnKind.Dropdown
                    ? column.Options.Concat(source.Rows.Select(r => r.Cells.TryGetValue(column.Source, out var c) ? c.Value : null).OfType<string>())
                        .Select(o => o.Trim()).Where(o => o.Length > 0).Distinct(StringComparer.Ordinal).ToList()
                    : [];
                var match = existing.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    if ((int)match.Type != (int)column.Type)
                    {
                        _run.Warning(File, 0, column.Source, $"Column {name} already exists as {match.Type}; values are imported into it as they are.");
                    }

                    var missing = options.Except(match.Options, StringComparer.Ordinal).ToList();
                    if (missing.Count > 0)
                    {
                        await _run.UpdateAsync(File, 0, typeof(Column), match.Id, "Column", name, $"Adds options {string.Join(", ", missing)}",
                            () => _columns.UpdateAsync(sheetId, match.Id, new UpdateColumnRequest(match.Name, [.. match.Options, .. missing], match.Formula, match.Order), cancellationToken),
                            cancellationToken);
                    }

                    ids[column.Source] = (match.Id, match.Type, [.. match.Options, .. missing]);
                    continue;
                }

                var id = await _run.CreateAsync(File, 0, typeof(Column), "Column", name, $"{column.Type}{(options.Count > 0 ? $" ({string.Join(", ", options.Take(8))})" : "")}",
                    async () => (await _columns.CreateAsync(sheetId, new CreateColumnRequest(name, column.Type, options.Count > 0 ? options : null), cancellationToken)).Id,
                    cancellationToken);
                if (id is { } created)
                {
                    ids[column.Source] = (created, column.Type, [.. options]);
                }
            }

            return ids;
        }

        private async Task<List<(Guid ItemId, string Code, SourceRow Row)>> RowsAsync(Guid sheetId,
            Dictionary<string, (Guid Id, ColumnKind Kind, HashSet<string> Options)> columns, IReadOnlyList<ItemDto> existingItems)
        {
            var byId = existingItems.ToDictionary(i => i.Id);
            var keys = await KeysAsync(RowScope);
            var titles = new Dictionary<string, string>();
            var links = new List<(Guid, string, SourceRow)>();
            foreach (var row in source.Rows.OrderBy(r => r.Number))
            {
                _rowForNumber[row.Number] = row.Id;
                var title = Text(row, Field("Title"))?.Trim();
                var parentItem = row.ParentId is { } p && _itemForRow.TryGetValue(p, out var pid) ? pid : (Guid?)null;
                titles[row.Id] = (row.ParentId is { } pr && titles.TryGetValue(pr, out var parentPath) ? parentPath + " > " : "") + (title ?? "");
                if (string.IsNullOrEmpty(title))
                {
                    if (row.Cells.Count == 0)
                    {
                        continue;
                    }

                    _run.Warning(File, row.Number, "Title", "Row has no task name; imported as \"(untitled)\".");
                    title = "(untitled)";
                }

                var start = Date(row, Field("Start"));
                var finish = Date(row, Field("Finish"));
                if (finish is null && start is { } s && Field("Duration") is { } durationColumn && row.Cells.TryGetValue(durationColumn, out var durationCell)
                    && (durationCell.DurationDays ?? (durationCell.Value is { } dv ? SmartsheetSources.ParseDuration(dv) : null)) is { } days && days > 0)
                {
                    finish = AddWorkingDays(s, (int)Math.Ceiling(days) - 1);
                }

                if (start is { } a && finish is { } b && b < a)
                {
                    _run.Warning(File, row.Number, "Finish", $"Finish {b:yyyy-MM-dd} is before start {a:yyyy-MM-dd}; the finish is set to the start.");
                    finish = start;
                }

                var values = new Dictionary<string, string?>();
                foreach (var (sourceTitle, column) in columns)
                {
                    if (row.Cells.TryGetValue(sourceTitle, out var cell) && Value(cell, column.Kind) is { } value)
                    {
                        values[column.Id.ToString()] = value;
                    }
                }

                var code = Text(row, Field("WbsCode"))?.Trim();
                var key = RowKey(row, titles[row.Id]);
                var label = $"{row.Number} {Shorten(title)}";
                if (keys.TryGetValue(key, out var itemId) && byId.TryGetValue(itemId, out var item))
                {
                    _itemForRow[row.Id] = item.Id;
                    var merged = new Dictionary<string, string?>(item.Values);
                    foreach (var (k, v) in values)
                    {
                        merged[k] = v;
                    }

                    var newCode = code ?? item.CostpointWbsId;
                    var changed = new List<string>();
                    if (item.Title != title) changed.Add("title");
                    if (item.Start != start) changed.Add("start");
                    if (item.Finish != finish) changed.Add("finish");
                    if (item.ParentId != parentItem) changed.Add("parent");
                    if (newCode != item.CostpointWbsId) changed.Add("WBS");
                    if (values.Any(kv => item.Values.GetValueOrDefault(kv.Key) != kv.Value)) changed.Add("values");
                    if (code is not null)
                    {
                        links.Add((item.Id, code, row));
                    }

                    if (changed.Count == 0)
                    {
                        _run.Skip(File, row.Number, "Row", label, "Unchanged");
                        continue;
                    }

                    await _run.UpdateAsync(File, row.Number, typeof(Item), item.Id, "Row", label, $"Changes {string.Join(", ", changed)}", async () =>
                    {
                        if (item.ParentId != parentItem)
                        {
                            await _items.MoveAsync(sheetId, item.Id, new MoveItemRequest(parentItem, int.MaxValue), cancellationToken);
                        }

                        await _items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest(title, start, finish, newCode, merged), cancellationToken);
                    }, cancellationToken);
                    continue;
                }

                var summary = string.Join(" · ", new[] { start is null ? null : $"{start:yyyy-MM-dd}", finish is null ? null : $"→ {finish:yyyy-MM-dd}", code is null ? null : $"WBS {code}",
                    parentItem is null ? null : "child row" }.OfType<string>());
                var created = await _run.CreateAsync(File, row.Number, typeof(Item), "Row", label, summary.Length == 0 ? title : summary, async () =>
                {
                    var dto = await _items.CreateAsync(sheetId, new CreateItemRequest(title, parentItem, null, start, finish, values), cancellationToken);
                    if (code is not null)
                    {
                        await _items.UpdateAsync(sheetId, dto.Id, new UpdateItemRequest(dto.Title, dto.Start, dto.Finish, code, dto.Values), cancellationToken);
                    }

                    return dto.Id;
                }, cancellationToken);
                if (created is { } newId)
                {
                    _itemForRow[row.Id] = newId;
                    await AddKeyAsync(RowScope, key, newId, $"Row key {Shorten(title)}");
                    if (code is not null)
                    {
                        links.Add((newId, code, row));
                    }
                }
            }

            return links;
        }

        private async Task DependenciesAsync(Guid sheetId)
        {
            if (Field("Predecessors") is not { } column)
            {
                return;
            }

            IReadOnlyList<DependencyDto> existing = _run.CreatedIds.Contains(sheetId) ? [] : await _dependencies.ListAsync(sheetId, cancellationToken);
            var pairs = existing.Select(d => (d.PredecessorId, d.SuccessorId)).ToHashSet();
            foreach (var row in source.Rows.OrderBy(r => r.Number))
            {
                if (!row.Cells.TryGetValue(column, out var cell) || !_itemForRow.TryGetValue(row.Id, out var successor))
                {
                    continue;
                }

                var predecessors = cell.Predecessors ?? (cell.Value is { } text ? SmartsheetSources.ParsePredecessors(text) : []);
                foreach (var p in predecessors)
                {
                    var predecessorRow = p.RowId ?? (p.RowNumber is { } n && _rowForNumber.TryGetValue(n, out var r) ? r : null);
                    if (predecessorRow is null || !_itemForRow.TryGetValue(predecessorRow, out var predecessor))
                    {
                        _run.Warning(File, row.Number, "Predecessors", $"Predecessor {p.RowNumber?.ToString(CultureInfo.InvariantCulture) ?? p.RowId} isn't an imported row; skipped.");
                        continue;
                    }

                    var type = p.Type.ToUpperInvariant() switch
                    {
                        "SS" => DependencyKind.StartToStart,
                        "FF" => DependencyKind.FinishToFinish,
                        "SF" => DependencyKind.StartToFinish,
                        _ => DependencyKind.FinishToStart,
                    };
                    var number = source.Rows.FirstOrDefault(x => x.Id == predecessorRow)?.Number;
                    var key = $"{number}{p.Type.ToUpperInvariant()}{(p.LagDays == 0 ? "" : $" {p.LagDays:+0;-0}d")} → {row.Number}";
                    if (!pairs.Add((predecessor, successor)))
                    {
                        _run.Skip(File, row.Number, "Dependency", key, "Exists");
                        continue;
                    }

                    await _run.CreateAsync(File, row.Number, typeof(Dependency), "Dependency", key, type.ToString(),
                        async () => (await _dependencies.AddAsync(sheetId, new CreateDependencyRequest(predecessor, successor, type, p.LagDays), cancellationToken)).Id,
                        cancellationToken);
                }
            }
        }

        /// <summary>Links rows to the chosen project's WBS elements (WbsElement.ItemId) and makes the sheet the project's plan.</summary>
        private async Task LinkWbsAsync(Guid sheetId, List<(Guid ItemId, string Code, SourceRow Row)> rows)
        {
            if (context.Options.ProjectId is not { } projectId)
            {
                if (rows.Count > 0)
                {
                    _run.Warning(File, 0, "WbsCode", "Choose a project to link the WBS codes to; they're kept on the rows only.");
                }

                return;
            }

            var projects = context.Services.GetRequiredService<IProjectService>();
            var project = await projects.GetProjectAsync(projectId, cancellationToken);
            if (project is null)
            {
                _run.Error(File, 0, null, "The chosen project no longer exists.");
                return;
            }

            var wbs = (await projects.ListWbsAsync(projectId, cancellationToken)).ToDictionary(w => w.Code, StringComparer.OrdinalIgnoreCase);
            var links = new List<(WbsElementDto Element, Guid ItemId, SourceRow Row)>();
            foreach (var (itemId, code, row) in rows)
            {
                if (!wbs.TryGetValue(code, out var element))
                {
                    _run.Warning(File, row.Number, "WbsCode", $"WBS {code} isn't in project {project.Code}; the row keeps the code but isn't linked.");
                    continue;
                }

                if (element.ItemId == itemId)
                {
                    _run.Skip(File, row.Number, "WBS link", code, "Already linked");
                    continue;
                }

                if (links.Any(l => l.Element.Id == element.Id))
                {
                    _run.Warning(File, row.Number, "WbsCode", $"WBS {code} is on more than one row; only the first is linked.");
                    continue;
                }

                links.Add((element, itemId, row));
            }

            var linkSheet = context.Options.LinkPlanSheet && project.SheetId != sheetId;
            if (linkSheet && project.SheetId is not null)
            {
                _run.Warning(File, 0, null, $"Project {project.Code} already had a plan sheet; this sheet replaces it as the plan (the old sheet is kept).");
            }

            if (!_run.Applying)
            {
                foreach (var (element, _, row) in links)
                {
                    _run.PlanUpdate(File, row.Number, "WBS link", element.Code, $"{element.Name} ↔ row {row.Number}");
                }

                if (linkSheet)
                {
                    _run.PlanUpdate(File, 0, "Plan sheet", project.Code, $"{source.Name} becomes {project.Code}'s plan sheet");
                }

                return;
            }

            if (links.Count == 0 && !linkSheet)
            {
                return;
            }

            var tracked = links.Select(l => (typeof(WbsElement), l.Element.Id, $"WBS element {l.Element.Code}")).ToList();
            if (linkSheet)
            {
                tracked.Add((typeof(Project), projectId, $"Project {project.Code}"));
            }

            var audit = context.Services.GetRequiredService<AuditRecorder>();
            await _run.TrackAsync(tracked, async () =>
            {
                await using var db = await _dbFactory.CreateAsync(cancellationToken);
                if (linkSheet)
                {
                    var entity = await db.Set<Project>().FirstAsync(p => p.Id == projectId, cancellationToken);
                    var before = new { entity.SheetId };
                    entity.SheetId = sheetId;
                    audit.Record(db, nameof(Project), entity.Id, "PlanSheetLinked", before, new { entity.SheetId }, $"Smartsheet import job {context.Job.Id.ToString()[..8]}");
                }

                var ids = links.Select(l => l.Element.Id).ToList();
                foreach (var element in await db.Set<WbsElement>().Where(w => ids.Contains(w.Id)).ToListAsync(cancellationToken))
                {
                    var before = new { element.ItemId };
                    element.ItemId = links.First(l => l.Element.Id == element.Id).ItemId;
                    audit.Record(db, nameof(WbsElement), element.Id, "LinkedToPlan", before, new { element.ItemId }, $"Smartsheet import job {context.Job.Id.ToString()[..8]}");
                }

                await db.SaveChangesAsync(cancellationToken);
                return true;
            }, cancellationToken);

            foreach (var (element, _, row) in links)
            {
                _run.PlanUpdate(File, row.Number, "WBS link", element.Code, $"{element.Name} ↔ row {row.Number}");
            }

            if (linkSheet)
            {
                _run.PlanUpdate(File, 0, "Plan sheet", project.Code, $"{source.Name} is now {project.Code}'s plan sheet");
            }
        }

        private async Task CommentsAsync(Guid sheetId, string token)
        {
            var client = context.Services.GetRequiredService<ISmartsheetClient>();
            var comments = context.Services.GetRequiredService<ICommentService>();
            var done = await KeysAsync(CommentScope);
            foreach (var discussion in await client.ListDiscussionsAsync(token, source.Id, cancellationToken))
            {
                var rowId = discussion.ParentId.ToString(CultureInfo.InvariantCulture);
                if (!string.Equals(discussion.ParentType, "ROW", StringComparison.OrdinalIgnoreCase) || !_itemForRow.TryGetValue(rowId, out var itemId))
                {
                    continue;
                }

                var number = source.Rows.FirstOrDefault(r => r.Id == rowId)?.Number ?? 0;
                foreach (var comment in (discussion.Comments ?? []).OrderBy(c => c.CreatedAt))
                {
                    var key = comment.Id.ToString(CultureInfo.InvariantCulture);
                    if (done.ContainsKey(key))
                    {
                        _run.Skip(File, number, "Comment", key, "Already imported");
                        continue;
                    }

                    // Say who wrote it originally; names only, so the import doesn't @mention anyone.
                    var body = $"{comment.Text.Trim()}\n\n— {comment.CreatedBy?.Name ?? "Smartsheet user"}, {comment.CreatedAt:yyyy-MM-dd} (from Smartsheet)";
                    var id = await _run.CreateAsync(File, number, typeof(Comment), "Comment", key, Shorten(comment.Text),
                        async () => (await comments.CreateAsync(sheetId, itemId, new CreateCommentRequest(body.Replace("@", "@​")), cancellationToken)).Id, cancellationToken);
                    if (id is { } created)
                    {
                        await AddKeyAsync(CommentScope, key, created, $"Comment key {key}");
                    }
                }
            }
        }

        private async Task AttachmentsAsync(Guid sheetId, string token)
        {
            var client = context.Services.GetRequiredService<ISmartsheetClient>();
            var attachments = context.Services.GetRequiredService<IAttachmentService>();
            var done = await KeysAsync(AttachmentScope);
            foreach (var attachment in await client.ListAttachmentsAsync(token, source.Id, cancellationToken))
            {
                var rowId = attachment.ParentId.ToString(CultureInfo.InvariantCulture);
                var number = source.Rows.FirstOrDefault(r => r.Id == rowId)?.Number ?? 0;
                if (!string.Equals(attachment.ParentType, "ROW", StringComparison.OrdinalIgnoreCase) || !_itemForRow.TryGetValue(rowId, out var itemId))
                {
                    continue;
                }

                if (!string.Equals(attachment.AttachmentType, "FILE", StringComparison.OrdinalIgnoreCase))
                {
                    _run.Warning(File, number, null, $"Attachment {attachment.Name} is a link ({attachment.AttachmentType}), not a file; skipped.");
                    continue;
                }

                var key = attachment.Id.ToString(CultureInfo.InvariantCulture);
                if (done.ContainsKey(key))
                {
                    _run.Skip(File, number, "Attachment", attachment.Name, "Already imported");
                    continue;
                }

                var id = await _run.CreateAsync(File, number, typeof(Attachment), "Attachment", attachment.Name, attachment.MimeType ?? "file", async () =>
                {
                    var bytes = await client.DownloadAttachmentAsync(token, source.Id, key, cancellationToken);
                    using var stream = new MemoryStream(bytes);
                    return (await attachments.UploadAsync(sheetId, itemId, attachment.Name, stream, cancellationToken)).Id;
                }, cancellationToken);
                if (id is { } created)
                {
                    await AddKeyAsync(AttachmentScope, key, created, $"Attachment key {attachment.Name}");
                }
            }
        }

        private string RowKey(SourceRow row, string titlePath) =>
            source.FromApi ? row.Id
            : Text(row, Field("RowId")) is { } rowId ? rowId
            : $"xlsx:{source.Name}:{titlePath}";

        private async Task<Dictionary<string, Guid>> KeysAsync(string scope)
        {
            await using var db = await _dbFactory.CreateAsync(cancellationToken);
            var rows = await db.Set<ImportKeyMap>().AsNoTracking().Where(k => k.Scope == scope).ToListAsync(cancellationToken);
            return rows.GroupBy(k => k.ExternalId).ToDictionary(g => g.Key, g => g.Last().EntityId);
        }

        private async Task<Guid?> FindKeyAsync(string scope, string externalId)
        {
            await using var db = await _dbFactory.CreateAsync(cancellationToken);
            return await db.Set<ImportKeyMap>().AsNoTracking().Where(k => k.Scope == scope && k.ExternalId == externalId)
                .Select(k => (Guid?)k.EntityId).FirstOrDefaultAsync(cancellationToken);
        }

        private async Task AddKeyAsync(string scope, string externalId, Guid entityId, string label)
        {
            if (!_run.Applying)
            {
                return;
            }

            var key = new ImportKeyMap { Scope = scope, ExternalId = externalId.Length > 128 ? externalId[..128] : externalId, EntityId = entityId, ImportJobId = context.Job.Id };
            await using (var db = await _dbFactory.CreateAsync(cancellationToken))
            {
                db.Set<ImportKeyMap>().Add(key);
                await db.SaveChangesAsync(cancellationToken);
            }

            await _run.RecordCreatedAsync(typeof(ImportKeyMap), key.Id, label, cancellationToken);
        }

        private static string? Text(SourceRow row, string? column) =>
            column is not null && row.Cells.TryGetValue(column, out var cell) ? cell.Display ?? cell.Value : null;

        private static DateOnly? Date(SourceRow row, string? column) =>
            column is not null && row.Cells.TryGetValue(column, out var cell) && (cell.Value ?? cell.Display) is { } text ? ParseDate(text) : null;

        private static DateOnly? ParseDate(string text) =>
            text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : MappedRow.ParseDate(text);

        /// <summary>A cell as a Workbench invariant value for the column type, or null when there's nothing usable.</summary>
        private static string? Value(SourceCell cell, ColumnKind kind)
        {
            var raw = cell.Value ?? cell.Display;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            switch (kind)
            {
                case ColumnKind.Date:
                    return ParseDate(raw)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case ColumnKind.Number:
                case ColumnKind.Currency:
                    return decimal.TryParse(raw.Replace("$", "").Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
                        ? n.ToString(CultureInfo.InvariantCulture) : null;
                case ColumnKind.Checkbox:
                    return MappedRow.ParseBool(raw) is { } b ? (b ? "true" : "false") : null;
                case ColumnKind.Person:
                    return (cell.Email ?? raw).Trim().ToLowerInvariant();
                case ColumnKind.Text:
                    return (cell.Display ?? raw).Trim();
                default:
                    return raw.Trim();
            }
        }

        private static DateOnly AddWorkingDays(DateOnly start, int days)
        {
            var date = start;
            while (days > 0)
            {
                date = date.AddDays(1);
                if (date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                {
                    days--;
                }
            }

            return date;
        }

        private static string Shorten(string text) => text.Length > 60 ? text[..57] + "..." : text;
    }
}

/// <summary>Sheets fetched from the Smartsheet REST API (stored in the job as JSON, one file per sheet).</summary>
public sealed class SmartsheetApiSheetImporter : SheetImporter
{
    public override ImportSource Source => ImportSource.SmartsheetApi;
    public override string Title => "Smartsheet sheets (API)";
    public override string Description => "Connect with a Smartsheet API token, pick sheets, and import their columns, rows, hierarchy, dates and predecessors; optionally comments and attachments.";
    public override string? SampleFile => null;

    protected override IReadOnlyList<SourceSheet> Load(IReadOnlyList<ImportJobFile> files) =>
        files.Select(f => SmartsheetSources.FromApi(JsonSerializer.Deserialize<SmartsheetSheet>(f.Content, Json)
            ?? throw new ArgumentException($"{f.FileName} isn't a Smartsheet sheet."))).ToList();
}

/// <summary>A Smartsheet sheet exported as .xlsx (File → Export → Export to Microsoft Excel).</summary>
public sealed class SmartsheetExcelImporter : SheetImporter
{
    public override ImportSource Source => ImportSource.SmartsheetExcel;
    public override string Title => "Smartsheet .xlsx export";
    public override string Description => "A sheet exported to Excel: the first row is the headers, row hierarchy comes from the outline (grouping) level or indentation.";
    public override string? SampleFile => "samples/import/smartsheet/project-plan.xlsx";

    protected override IReadOnlyList<SourceSheet> Load(IReadOnlyList<ImportJobFile> files) =>
        files.Select(f => SmartsheetSources.FromExcel(TabularFile.Read(f.FileName, f.Content))).ToList();
}
