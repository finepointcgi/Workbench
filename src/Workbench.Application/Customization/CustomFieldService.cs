using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Reports;
using Workbench.Contracts;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Reports;
using Workbench.Domain.Customization;
using Workbench.Domain.Identity;
using CustomFieldType = Workbench.Contracts.Customization.CustomFieldType;
using DomainType = Workbench.Domain.Customization.CustomFieldType;

namespace Workbench.Application.Customization;

/// <summary>
/// The seam for sensitive custom fields: may the current user see (and change) values guarded by a field's permission key?
/// The default is Finance and administrators; the custom roles permission catalog replaces it with a field-level check
/// (register another implementation with <c>Replace</c>).
/// </summary>
public interface ICustomFieldAccessPolicy
{
    Task<bool> CanSeeSensitiveAsync(string entityType, string permissionKey, CancellationToken cancellationToken = default);
}

public sealed class FinanceAdminCustomFieldAccessPolicy(AccessControl access) : ICustomFieldAccessPolicy
{
    public async Task<bool> CanSeeSensitiveAsync(string entityType, string permissionKey, CancellationToken cancellationToken = default) =>
        access.IsAuthenticated && (await access.GetOrgRolesAsync(cancellationToken)).IsFinance();
}

/// <summary>
/// Custom field definitions and values. Values live in one shared table (<see cref="CustomFieldValue"/>): a new entity type
/// needs no schema change, typed columns filter and sort the same on SQLite and PostgreSQL (no provider-specific JSON
/// queries), retired fields keep their rows, and one index serves search. Who may read or change a record is checked by
/// <c>PermissionCheckingCustomFieldService</c>; sensitive values are masked here, through <see cref="ICustomFieldAccessPolicy"/>,
/// so every reader (pages, API, CSV, report writer, assistant) gets the same masking.
/// </summary>
public sealed class CustomFieldService(
    IWorkbenchDbContextFactory dbFactory, CustomizableEntityRegistry registry, CustomListService lists, ICustomFieldAccessPolicy policy,
    AuditRecorder audit, TimeProvider clock) : ICustomFieldService
{
    public const string AuditDefinition = "CustomField";
    public const string AuditValuesChanged = "Custom fields changed";
    private const int ExportLimit = 50_000;

    public Task<IReadOnlyList<CustomizableEntityDto>> ListEntityTypesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CustomizableEntityDto>>(registry.All.Select(e => new CustomizableEntityDto(e.Key, e.Label, e.LabelPlural, e.HasApproval, e.StatusSet)).ToList());

    public async Task<IReadOnlyList<CustomFieldDefinitionDto>> ListDefinitionsAsync(string entityType, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        registry.Get(entityType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DefinitionsAsync(db, entityType, includeInactive, cancellationToken);
    }

    public async Task<CustomFieldDefinitionDto> SaveDefinitionAsync(Guid? id, SaveCustomFieldRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entity = registry.Get(request.EntityType);
        var label = Clean(request.Label, 100) ?? throw new ArgumentException("The field needs a label.");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        CustomFieldDefinition field;
        object? before = null;
        var now = clock.GetUtcNow().UtcDateTime;
        if (id is { } existingId)
        {
            field = await db.Set<CustomFieldDefinition>().FirstOrDefaultAsync(f => f.Id == existingId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Custom field", existingId);
            if (field.EntityType != entity.Key)
            {
                throw new ArgumentException("A field can't move to another record type.");
            }

            before = Snapshot(field);
            var hasValues = await db.Set<CustomFieldValue>().AnyAsync(v => v.FieldId == field.Id, cancellationToken);
            if (hasValues && field.FieldType != CustomFieldCodec.ToDomain(request.FieldType))
            {
                throw new InvalidOperationException($"{field.Label} already has values, so its type can't change. Retire it and add a new field instead.");
            }

            if (!string.IsNullOrWhiteSpace(request.Key) && !string.Equals(CustomListService.Slug(request.Key), field.Key, StringComparison.Ordinal))
            {
                throw new ArgumentException("A field's key never changes (reports, the API and automations use it).");
            }
        }
        else
        {
            var key = CustomListService.Slug(string.IsNullOrWhiteSpace(request.Key) ? label : request.Key);
            if (entity.BuiltInFields.Any(b => string.Equals(b.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                key = $"{key}_custom";
            }

            if (await db.Set<CustomFieldDefinition>().AnyAsync(f => f.EntityType == entity.Key && f.Key == key, cancellationToken))
            {
                throw new ArgumentException($"{entity.LabelPlural} already have a field with the key '{key}'.");
            }

            var order = await db.Set<CustomFieldDefinition>().Where(f => f.EntityType == entity.Key).MaxAsync(f => (int?)f.Order, cancellationToken) ?? -1;
            field = new CustomFieldDefinition { EntityType = entity.Key, Key = key, Label = label, Order = order + 1, CreatedAtUtc = now };
            db.Add(field);
        }

        field.Label = label;
        field.HelpText = Clean(request.HelpText, 500);
        field.FieldType = CustomFieldCodec.ToDomain(request.FieldType);
        field.IsRequired = request.IsRequired;
        field.ReadOnlyAfterApproval = request.ReadOnlyAfterApproval;
        field.IsSensitive = request.IsSensitive;
        field.IsActive = request.IsActive;
        field.ValueListKey = field.FieldType is DomainType.Picklist or DomainType.MultiPicklist ? Clean(request.ValueListKey, 64) : null;
        field.LinkEntityType = field.FieldType == DomainType.RecordLink ? Clean(request.LinkEntityType, 64) : null;
        if (field.FieldType is DomainType.Picklist or DomainType.MultiPicklist)
        {
            if (field.ValueListKey is null || await lists.GetAsync(db, field.ValueListKey, cancellationToken) is null)
            {
                throw new ArgumentException("Choose the value list the picklist offers.");
            }
        }

        if (field.FieldType == DomainType.RecordLink && registry.Find(field.LinkEntityType) is null)
        {
            throw new ArgumentException("Choose the kind of record the field links to.");
        }

        field.DefaultValue = null;
        if (!string.IsNullOrWhiteSpace(request.DefaultValue))
        {
            var options = await OptionsAsync(db, field, cancellationToken);
            var parsed = CustomFieldCodec.Parse(field, request.DefaultValue, options);
            await CheckReferencesAsync(db, field, parsed, cancellationToken);
            field.DefaultValue = CustomFieldCodec.ToText(field.FieldType, parsed);
        }

        field.UpdatedAtUtc = now;
        field.UpdatedBy = audit.Actor;
        audit.Record(db, AuditDefinition, field.Id, before is null ? "Created" : "Changed", before, Snapshot(field));
        await db.SaveChangesAsync(cancellationToken);
        return (await DefinitionsAsync(db, entity.Key, true, cancellationToken)).First(f => f.Id == field.Id);
    }

    public async Task<CustomFieldDefinitionDto> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var field = await db.Set<CustomFieldDefinition>().FirstOrDefaultAsync(f => f.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Custom field", id);
        if (field.IsActive != isActive)
        {
            field.IsActive = isActive;
            field.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            field.UpdatedBy = audit.Actor;
            audit.Record(db, AuditDefinition, field.Id, isActive ? "Restored" : "Retired", after: new { field.EntityType, field.Key, field.Label });
            await db.SaveChangesAsync(cancellationToken);
        }

        return (await DefinitionsAsync(db, field.EntityType, true, cancellationToken)).First(f => f.Id == id);
    }

    public async Task<IReadOnlyList<CustomFieldDefinitionDto>> ReorderAsync(string entityType, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        var entity = registry.Get(entityType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var fields = await db.Set<CustomFieldDefinition>().Where(f => f.EntityType == entity.Key).ToListAsync(cancellationToken);
        var ordered = (orderedIds ?? []).Distinct().Select(id => fields.FirstOrDefault(f => f.Id == id) ?? throw new ArgumentException("A field in the new order isn't one of this record type's fields.")).ToList();
        ordered.AddRange(fields.Except(ordered).OrderBy(f => f.Order));
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Order = i;
        }

        audit.Record(db, AuditDefinition, Guid.Empty, "Reordered", after: new { entityType = entity.Key, order = ordered.Select(f => f.Key) });
        await db.SaveChangesAsync(cancellationToken);
        return await DefinitionsAsync(db, entity.Key, true, cancellationToken);
    }

    public async Task<RecordCustomFieldsDto> GetValuesAsync(string entityType, Guid? recordId, CancellationToken cancellationToken = default)
    {
        var entity = registry.Get(entityType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await BuildAsync(db, entity, recordId, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ValidateAsync(string entityType, Guid? recordId, SaveCustomFieldValuesRequest request, CancellationToken cancellationToken = default)
    {
        var entity = registry.Get(entityType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await PlanAsync(db, entity, recordId, request, cancellationToken)).Errors;
    }

    public async Task<RecordCustomFieldsDto> SaveValuesAsync(string entityType, Guid recordId, SaveCustomFieldValuesRequest request, CancellationToken cancellationToken = default)
    {
        var entity = registry.Get(entityType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var plan = await PlanAsync(db, entity, recordId, request, cancellationToken);
        if (plan.Errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", plan.Errors));
        }

        if (plan.Changes.Count > 0)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var change in plan.Changes)
            {
                var row = plan.Rows.FirstOrDefault(r => r.FieldId == change.Field.Id);
                if (change.After is null)
                {
                    if (row is not null)
                    {
                        db.Remove(row);
                    }

                    continue;
                }

                if (row is null)
                {
                    row = new CustomFieldValue { FieldId = change.Field.Id, EntityType = entity.Key, RecordId = recordId };
                    db.Add(row);
                }

                (row.TextValue, row.NumberValue, row.DateValue, row.BoolValue) = (change.After.Text, change.After.Number, change.After.Date, change.After.Bool);
                row.SearchText = SearchText(change.Display);
                row.UpdatedAtUtc = now;
                row.UpdatedBy = audit.Actor;
            }

            // Sensitive values never go into audit rows; the change itself is recorded.
            string? AuditText(Change c, StoredValue? v) => c.Field.IsSensitive ? (v is null ? null : "(sensitive value)") : CustomFieldCodec.ToText(c.Field.FieldType, v);
            audit.Record(db, entity.AuditEntityType, recordId, AuditValuesChanged,
                plan.Changes.ToDictionary(c => c.Field.Key, c => AuditText(c, c.Before)),
                plan.Changes.ToDictionary(c => c.Field.Key, c => AuditText(c, c.After)));
            await db.SaveChangesAsync(cancellationToken);
        }

        return await BuildAsync(db, entity, recordId, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> FilterAsync(string entityType, IReadOnlyList<CustomFieldFilter> filters, CancellationToken cancellationToken = default)
    {
        var entity = registry.Get(entityType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var defs = await db.Set<CustomFieldDefinition>().AsNoTracking().Where(f => f.EntityType == entity.Key).ToListAsync(cancellationToken);
        HashSet<Guid>? result = null;
        HashSet<Guid>? all = null;
        async Task<HashSet<Guid>> AllAsync() => all ??= (await entity.AllAsync(db, ExportLimit, cancellationToken)).Select(r => r.Id).ToHashSet();
        foreach (var filter in filters ?? [])
        {
            var field = defs.FirstOrDefault(f => f.Key == filter.Key) ?? throw new ArgumentException($"{entity.LabelPlural} have no custom field '{filter.Key}'.");
            if (await IsMaskedAsync(entity, field, cancellationToken))
            {
                throw new WorkbenchForbiddenException($"You can't filter on {field.Label}.");
            }

            var rows = db.Set<CustomFieldValue>().AsNoTracking().Where(v => v.FieldId == field.Id);
            HashSet<Guid> matched;
            if (filter.Operator is CustomFieldOperator.IsBlank or CustomFieldOperator.IsNotBlank)
            {
                var withValue = (await rows.Select(v => v.RecordId).ToListAsync(cancellationToken)).ToHashSet();
                matched = filter.Operator == CustomFieldOperator.IsNotBlank ? withValue : (await AllAsync()).Except(withValue).ToHashSet();
            }
            else
            {
                matched = (await MatchAsync(db, field, rows, filter, cancellationToken)).ToHashSet();
                if (filter.Operator == CustomFieldOperator.NotEquals)
                {
                    matched = (await AllAsync()).Except(matched).ToHashSet();
                }
            }

            result = result is null ? matched : result.Intersect(matched).ToHashSet();
        }

        return (result ?? await AllAsync()).ToList();
    }

    public async Task<IReadOnlyList<CustomFieldSearchHitDto>> SearchAsync(string text, string? entityType = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        var needle = SearchText(text);
        if (needle.Length < 2)
        {
            return [];
        }

        if (entityType is not null)
        {
            registry.Get(entityType);
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var defs = await db.Set<CustomFieldDefinition>().AsNoTracking().Where(f => f.IsActive && (entityType == null || f.EntityType == entityType)).ToListAsync(cancellationToken);
        var visible = new List<CustomFieldDefinition>();
        foreach (var f in defs.Where(f => registry.Find(f.EntityType) is not null))
        {
            if (!await IsMaskedAsync(registry.Get(f.EntityType), f, cancellationToken))
            {
                visible.Add(f);
            }
        }

        var ids = visible.Select(f => f.Id).ToList();
        var rows = await db.Set<CustomFieldValue>().AsNoTracking().Where(v => ids.Contains(v.FieldId) && v.SearchText.Contains(needle))
            .OrderBy(v => v.EntityType).ThenBy(v => v.RecordId).Take(Math.Clamp(limit, 1, 500)).ToListAsync(cancellationToken);
        var hits = new List<CustomFieldSearchHitDto>();
        foreach (var group in rows.GroupBy(r => r.EntityType))
        {
            var entity = registry.Get(group.Key);
            var titles = await entity.TitlesAsync(db, group.Select(r => r.RecordId).ToList(), cancellationToken);
            foreach (var row in group.Where(r => titles.ContainsKey(r.RecordId)))
            {
                var field = visible.First(f => f.Id == row.FieldId);
                var display = await DisplayAsync(db, field, CustomFieldCodec.Of(row), await OptionsAsync(db, field, cancellationToken), cancellationToken);
                hits.Add(new(entity.Key, entity.Label, row.RecordId, titles[row.RecordId], entity.Link?.Invoke(row.RecordId), field.Label, display ?? ""));
            }
        }

        return hits;
    }

    public async Task<CustomFieldTableDto> GetTableAsync(string entityType, IReadOnlyList<Guid>? recordIds = null, CancellationToken cancellationToken = default)
    {
        var entity = registry.Get(entityType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await TableAsync(db, entity, recordIds, cancellationToken);
    }

    public async Task<CustomFieldExportDto> ExportCsvAsync(string entityType, CancellationToken cancellationToken = default)
    {
        var entity = registry.Get(entityType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var records = await entity.AllAsync(db, ExportLimit, cancellationToken);
        var table = await TableAsync(db, entity, records.Select(r => r.Id).ToList(), cancellationToken, display: false);
        var columns = new List<ReportColumnDto> { new("id", "Id", ReportColumnType.Text), new("record", entity.Label, ReportColumnType.Text) };
        columns.AddRange(table.Fields.Select(f => new ReportColumnDto(f.Key, f.Label, ReportColumn(f.FieldType))));
        var rows = records.Select(r => new ReportRowDto(ReportRowKind.Detail, 0,
            new List<string?> { r.Id.ToString(), r.Title }.Concat(table.Fields.Select(f => table.Values.GetValueOrDefault(r.Id)?.GetValueOrDefault(f.Key))).ToList())).ToList();
        var result = new ReportResultDto($"{entity.LabelPlural} custom fields", null, columns, rows, [], clock.GetUtcNow().UtcDateTime);
        return new($"{entity.Key}-custom-fields-{clock.GetUtcNow().UtcDateTime:yyyyMMdd}.csv", ReportFiles.CsvContentType, ReportFiles.Csv(result));
    }

    public async Task CopyValuesAsync(string entityType, Guid fromRecordId, Guid toRecordId, CancellationToken cancellationToken = default)
    {
        var entity = registry.Get(entityType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await entity.ExistsAsync(db, toRecordId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For(entity.Label, toRecordId);
        }

        var active = await db.Set<CustomFieldDefinition>().AsNoTracking().Where(f => f.EntityType == entity.Key && f.IsActive).Select(f => f.Id).ToListAsync(cancellationToken);
        var source = await db.Set<CustomFieldValue>().AsNoTracking().Where(v => v.RecordId == fromRecordId && active.Contains(v.FieldId)).ToListAsync(cancellationToken);
        var target = await db.Set<CustomFieldValue>().Where(v => v.RecordId == toRecordId).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var s in source)
        {
            var row = target.FirstOrDefault(t => t.FieldId == s.FieldId);
            if (row is null)
            {
                row = new CustomFieldValue { FieldId = s.FieldId, EntityType = entity.Key, RecordId = toRecordId };
                db.Add(row);
            }

            (row.TextValue, row.NumberValue, row.DateValue, row.BoolValue, row.SearchText, row.UpdatedAtUtc, row.UpdatedBy) =
                (s.TextValue, s.NumberValue, s.DateValue, s.BoolValue, s.SearchText, now, audit.Actor);
        }

        if (source.Count > 0)
        {
            audit.Record(db, entity.AuditEntityType, toRecordId, "Custom fields copied", after: new { from = fromRecordId, fields = source.Count });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<CustomFieldOptionDto>> LookupRecordsAsync(string entityType, string? text, int limit = 20, CancellationToken cancellationToken = default)
    {
        var entity = registry.Get(entityType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var needle = text?.Trim() ?? "";
        return (await entity.AllAsync(db, 5000, cancellationToken))
            .Where(r => needle.Length == 0 || r.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 200))
            .Select(r => new CustomFieldOptionDto(r.Id.ToString(), r.Title)).ToList();
    }

    /// <summary>Invariant values (sensitive ones masked) of these records, in the caller's unit of work: the report writer's columns.</summary>
    internal Task<CustomFieldTableDto> RawTableAsync(IWorkbenchDbContext db, string entityType, IReadOnlyList<Guid> recordIds, CancellationToken ct) =>
        TableAsync(db, registry.Get(entityType), recordIds, ct, display: false);

    // ---- Internals ----

    private sealed record Change(CustomFieldDefinition Field, StoredValue? Before, StoredValue? After, string? Display);

    private sealed record Plan(List<string> Errors, List<Change> Changes, List<CustomFieldValue> Rows);

    private async Task<Plan> PlanAsync(IWorkbenchDbContext db, CustomizableEntity entity, Guid? recordId, SaveCustomFieldValuesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        var changes = new List<Change>();
        if (recordId is { } existing && !await entity.ExistsAsync(db, existing, ct))
        {
            throw WorkbenchNotFoundException.For(entity.Label, existing);
        }

        var defs = await db.Set<CustomFieldDefinition>().AsNoTracking().Where(f => f.EntityType == entity.Key).OrderBy(f => f.Order).ToListAsync(ct);
        var rows = recordId is { } rid ? await db.Set<CustomFieldValue>().Where(v => v.RecordId == rid && v.EntityType == entity.Key).ToListAsync(ct) : [];
        var locked = recordId is { } lid && await entity.IsLockedAsync(db, lid, ct);
        var values = request.Values ?? new Dictionary<string, string?>();
        foreach (var key in values.Keys.Where(k => !defs.Any(f => f.Key == k)))
        {
            errors.Add($"{entity.LabelPlural} have no custom field '{key}'.");
        }

        foreach (var field in defs)
        {
            var row = rows.FirstOrDefault(r => r.FieldId == field.Id);
            var before = row is null ? null : CustomFieldCodec.Of(row);
            var supplied = values.TryGetValue(field.Key, out var input);
            if (!field.IsActive)
            {
                if (supplied)
                {
                    errors.Add($"{field.Label} is retired and can't be changed.");
                }

                continue;
            }

            if (await IsMaskedAsync(entity, field, ct))
            {
                if (supplied)
                {
                    throw new WorkbenchForbiddenException($"You can't change {field.Label}.");
                }

                continue; // Required checks don't apply to values the user can't see.
            }

            StoredValue? after = before;
            if (supplied)
            {
                try
                {
                    var options = await OptionsAsync(db, field, ct);
                    after = CustomFieldCodec.Parse(field, input, options, before?.Text);
                    await CheckReferencesAsync(db, field, after, ct);
                }
                catch (ArgumentException ex)
                {
                    errors.Add(ex.Message);
                    continue;
                }
            }
            else if (recordId is null && field.DefaultValue is not null)
            {
                after = CustomFieldCodec.Parse(field, field.DefaultValue, await OptionsAsync(db, field, ct), field.DefaultValue);
            }

            var changed = !CustomFieldCodec.Same(before, after);
            if (changed && locked && field.ReadOnlyAfterApproval)
            {
                errors.Add($"{field.Label} can't change once the {entity.Label.ToLowerInvariant()} is approved.");
                continue;
            }

            if (field.IsRequired && after is null && !(locked && field.ReadOnlyAfterApproval))
            {
                errors.Add($"{field.Label} is required.");
                continue;
            }

            if (changed)
            {
                changes.Add(new(field, before, after, await DisplayAsync(db, field, after, await OptionsAsync(db, field, ct), ct)));
            }
        }

        return new(errors, changes, rows);
    }

    private async Task<RecordCustomFieldsDto> BuildAsync(IWorkbenchDbContext db, CustomizableEntity entity, Guid? recordId, CancellationToken ct)
    {
        if (recordId is { } id && !await entity.ExistsAsync(db, id, ct))
        {
            throw WorkbenchNotFoundException.For(entity.Label, id);
        }

        var defs = await db.Set<CustomFieldDefinition>().AsNoTracking().Where(f => f.EntityType == entity.Key && f.IsActive).OrderBy(f => f.Order).ToListAsync(ct);
        var rows = recordId is { } rid ? await db.Set<CustomFieldValue>().AsNoTracking().Where(v => v.RecordId == rid && v.EntityType == entity.Key).ToListAsync(ct) : [];
        var locked = recordId is { } lid && await entity.IsLockedAsync(db, lid, ct);
        var fields = new List<CustomFieldValueDto>();
        foreach (var f in defs)
        {
            var options = await OptionsAsync(db, f, ct);
            var masked = await IsMaskedAsync(entity, f, ct);
            var row = rows.FirstOrDefault(r => r.FieldId == f.Id);
            var stored = row is not null ? CustomFieldCodec.Of(row)
                : recordId is null && f.DefaultValue is not null ? SafeParse(f, f.DefaultValue, options) : null;
            var value = masked ? null : CustomFieldCodec.ToText(f.FieldType, stored);
            var display = masked ? (stored is null ? null : CustomFieldValues.Masked) : await DisplayAsync(db, f, stored, options, ct);
            fields.Add(new(f.Id, f.Key, f.Label, f.HelpText, CustomFieldCodec.ToContract(f.FieldType), f.IsRequired, f.IsActive, masked,
                masked || (locked && f.ReadOnlyAfterApproval), value, display,
                options?.Select(o => new CustomFieldOptionDto(o.Code, o.Label)).ToList(), f.LinkEntityType));
        }

        return new(entity.Key, recordId, locked, fields);
    }

    private async Task<CustomFieldTableDto> TableAsync(IWorkbenchDbContext db, CustomizableEntity entity, IReadOnlyList<Guid>? recordIds, CancellationToken ct, bool display = true)
    {
        var defs = await DefinitionsAsync(db, entity.Key, false, ct);
        var defRows = await db.Set<CustomFieldDefinition>().AsNoTracking().Where(f => f.EntityType == entity.Key && f.IsActive).ToListAsync(ct);
        var query = db.Set<CustomFieldValue>().AsNoTracking().Where(v => v.EntityType == entity.Key);
        var rows = new List<CustomFieldValue>();
        if (recordIds is null)
        {
            rows = await query.Take(ExportLimit * Math.Max(1, defs.Count)).ToListAsync(ct);
        }
        else
        {
            foreach (var chunk in recordIds.Distinct().Chunk(500))
            {
                rows.AddRange(await query.Where(v => chunk.Contains(v.RecordId)).ToListAsync(ct));
            }
        }

        var values = new Dictionary<Guid, IReadOnlyDictionary<string, string?>>();
        foreach (var f in defRows)
        {
            var masked = await IsMaskedAsync(entity, f, ct);
            var options = await OptionsAsync(db, f, ct);
            foreach (var row in rows.Where(r => r.FieldId == f.Id))
            {
                var stored = CustomFieldCodec.Of(row);
                var text = masked ? CustomFieldValues.Masked
                    : display ? await DisplayAsync(db, f, stored, options, ct)
                    : CustomFieldCodec.ToText(f.FieldType, stored);
                if (!values.TryGetValue(row.RecordId, out var map))
                {
                    values[row.RecordId] = map = new Dictionary<string, string?>(StringComparer.Ordinal);
                }

                ((Dictionary<string, string?>)map)[f.Key] = text;
            }
        }

        return new(defs, values);
    }

    private async Task<IReadOnlyList<Guid>> MatchAsync(IWorkbenchDbContext db, CustomFieldDefinition field, IQueryable<CustomFieldValue> rows, CustomFieldFilter filter, CancellationToken ct)
    {
        var raw = filter.Value?.Trim() ?? "";
        if (filter.Operator == CustomFieldOperator.Contains)
        {
            var needle = SearchText(raw);
            return await rows.Where(v => v.SearchText.Contains(needle)).Select(v => v.RecordId).ToListAsync(ct);
        }

        if (field.FieldType == DomainType.MultiPicklist && filter.Operator is CustomFieldOperator.Equals or CustomFieldOperator.NotEquals)
        {
            var code = CustomFieldCodec.Parse(field, raw, await OptionsAsync(db, field, ct), raw)?.Text is { } json ? CustomFieldCodec.Codes(json)[0] : raw;
            var quoted = System.Text.Json.JsonSerializer.Serialize(code);
            return await rows.Where(v => v.TextValue!.Contains(quoted)).Select(v => v.RecordId).ToListAsync(ct);
        }

        var parsed = CustomFieldCodec.Parse(field, raw, await OptionsAsync(db, field, ct), raw) ?? throw new ArgumentException($"{field.Label}: give a value to compare with.");
        return (field.FieldType, filter.Operator) switch
        {
            // Compared in memory: SQLite keeps decimals as text, so a database comparison would be alphabetical ("3" >= "10").
            (DomainType.Number or DomainType.Currency, _) => (await rows.Select(v => new { v.RecordId, v.NumberValue }).ToListAsync(ct))
                .Where(v => filter.Operator switch
                {
                    CustomFieldOperator.AtLeast => v.NumberValue >= parsed.Number,
                    CustomFieldOperator.AtMost => v.NumberValue <= parsed.Number,
                    _ => v.NumberValue == parsed.Number,
                })
                .Select(v => v.RecordId).ToList(),
            (DomainType.Date, CustomFieldOperator.AtLeast) => await rows.Where(v => v.DateValue >= parsed.Date).Select(v => v.RecordId).ToListAsync(ct),
            (DomainType.Date, CustomFieldOperator.AtMost) => await rows.Where(v => v.DateValue <= parsed.Date).Select(v => v.RecordId).ToListAsync(ct),
            (DomainType.Date, _) => await rows.Where(v => v.DateValue == parsed.Date).Select(v => v.RecordId).ToListAsync(ct),
            (DomainType.YesNo, _) => await rows.Where(v => v.BoolValue == parsed.Bool).Select(v => v.RecordId).ToListAsync(ct),
            (_, CustomFieldOperator.AtLeast or CustomFieldOperator.AtMost) => throw new ArgumentException($"{field.Label}: at least / at most need a number or date field."),
            (DomainType.Text or DomainType.LongText or DomainType.Url, _) => await rows.Where(v => v.SearchText == SearchText(parsed.Text)).Select(v => v.RecordId).ToListAsync(ct),
            _ => await rows.Where(v => v.TextValue == parsed.Text).Select(v => v.RecordId).ToListAsync(ct),
        };
    }

    private async Task<bool> IsMaskedAsync(CustomizableEntity entity, CustomFieldDefinition field, CancellationToken ct) =>
        field.IsSensitive && !await policy.CanSeeSensitiveAsync(entity.Key, CustomFieldValues.PermissionKey(entity.Key, field.Key), ct);

    private async Task<IReadOnlyList<ValueListItemDto>?> OptionsAsync(IWorkbenchDbContext db, CustomFieldDefinition field, CancellationToken ct) =>
        field.FieldType is DomainType.Picklist or DomainType.MultiPicklist && field.ValueListKey is { } key
            ? (await lists.GetAsync(db, key, ct))?.Items ?? []
            : null;

    private async Task CheckReferencesAsync(IWorkbenchDbContext db, CustomFieldDefinition field, StoredValue? value, CancellationToken ct)
    {
        if (value?.Text is not { } text)
        {
            return;
        }

        if (field.FieldType == DomainType.Person && !await db.Set<AppUser>().AnyAsync(u => u.Email == text && u.IsActive, ct))
        {
            throw new ArgumentException($"{field.Label}: {text} isn't an active Workbench user.");
        }

        if (field.FieldType == DomainType.RecordLink && (registry.Find(field.LinkEntityType) is not { } target || !await target.ExistsAsync(db, Guid.Parse(text), ct)))
        {
            throw new ArgumentException($"{field.Label}: that record doesn't exist.");
        }
    }

    private async Task<string?> DisplayAsync(IWorkbenchDbContext db, CustomFieldDefinition field, StoredValue? value, IReadOnlyList<ValueListItemDto>? options, CancellationToken ct)
    {
        if (value?.Text is { } text && field.FieldType == DomainType.Person)
        {
            return await db.Set<AppUser>().AsNoTracking().Where(u => u.Email == text).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? text;
        }

        if (value?.Text is { } id && field.FieldType == DomainType.RecordLink && registry.Find(field.LinkEntityType) is { } target && Guid.TryParse(id, out var gid))
        {
            return (await target.TitlesAsync(db, [gid], ct)).GetValueOrDefault(gid) ?? id;
        }

        return CustomFieldCodec.Display(field.FieldType, value, options);
    }

    private async Task<IReadOnlyList<CustomFieldDefinitionDto>> DefinitionsAsync(IWorkbenchDbContext db, string entityType, bool includeInactive, CancellationToken ct)
    {
        var defs = await db.Set<CustomFieldDefinition>().AsNoTracking().Where(f => f.EntityType == entityType && (includeInactive || f.IsActive)).OrderBy(f => f.Order).ToListAsync(ct);
        var ids = defs.Select(f => f.Id).ToList();
        var counts = await db.Set<CustomFieldValue>().AsNoTracking().Where(v => ids.Contains(v.FieldId)).GroupBy(v => v.FieldId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return defs.Select(f => new CustomFieldDefinitionDto(f.Id, f.EntityType, f.Key, f.Label, f.HelpText, CustomFieldCodec.ToContract(f.FieldType), f.IsRequired, f.DefaultValue,
            f.ReadOnlyAfterApproval, f.IsActive, f.IsSensitive, f.Order, f.ValueListKey, f.LinkEntityType,
            f.IsSensitive ? CustomFieldValues.PermissionKey(f.EntityType, f.Key) : null, counts.FirstOrDefault(c => c.Key == f.Id)?.Count ?? 0)).ToList();
    }

    private static StoredValue? SafeParse(CustomFieldDefinition f, string text, IReadOnlyList<ValueListItemDto>? options)
    {
        try
        {
            return CustomFieldCodec.Parse(f, text, options, text);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static object Snapshot(CustomFieldDefinition f) =>
        new { f.EntityType, f.Key, f.Label, type = f.FieldType.ToString(), f.IsRequired, f.DefaultValue, f.ReadOnlyAfterApproval, f.IsActive, f.IsSensitive, f.ValueListKey, f.LinkEntityType };

    internal static string SearchText(string? display)
    {
        var text = (display ?? "").Trim().ToLowerInvariant();
        return text.Length <= 400 ? text : text[..400];
    }

    private static string? Clean(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var t && t.Length > max ? t[..max] : value.Trim();

    internal static ReportColumnType ReportColumn(CustomFieldType type) => type switch
    {
        CustomFieldType.Number => ReportColumnType.Number,
        CustomFieldType.Currency => ReportColumnType.Money,
        CustomFieldType.Date => ReportColumnType.Date,
        CustomFieldType.YesNo => ReportColumnType.Boolean,
        _ => ReportColumnType.Text,
    };

    internal static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
