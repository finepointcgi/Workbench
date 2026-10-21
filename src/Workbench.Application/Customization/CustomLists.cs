using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Customization;
using Workbench.Domain.Customization;
using Workbench.Domain.ProjectAccounting;
using D = Workbench.Domain;

namespace Workbench.Application.Customization;

/// <summary>A built-in value list: its key, name, where Workbench uses it and today's values (the defaults until edited).</summary>
public sealed record SystemValueList(string Key, string Name, string Description, IReadOnlyList<string> UsedBy, IReadOnlyList<(string Code, string Label)> Items);

/// <summary>The value lists that used to be hard-coded dropdowns. Their codes are what records store, so they never change.</summary>
public static class ValueListCatalog
{
    public const string VendorSocioEconomic = "vendor-socioeconomic";
    public const string VendorBusinessSize = "vendor-business-size";
    public const string TaxExemptionReasons = "tax-exemption-reasons";

    public static IReadOnlyList<SystemValueList> All { get; } =
    [
        new(VendorSocioEconomic, "Vendor socio-economic categories", "Small business programs a vendor qualifies for (AP small business report).",
            ["Vendors", "AP small business report"],
            [("SDB", "SDB"), ("8(a)", "8(a)"), ("WOSB", "WOSB"), ("HUBZone", "HUBZone"), ("VOSB", "VOSB"), ("SDVOSB", "SDVOSB"), ("HBCU/MI", "HBCU/MI")]),
        new(VendorBusinessSize, "Vendor business sizes", "A vendor's size standard.", ["Vendors"],
            [("Small", "Small"), ("Large", "Large"), ("Other than small", "Other than small")]),
        new(TaxExemptionReasons, "Sales tax exemption reasons", "Why a customer's exemption certificate exempts its purchases.", ["Sales tax exemption certificates"],
            [("Resale", "Resale"), ("Government", "Government"), ("Nonprofit", "Nonprofit"), ("Manufacturing", "Manufacturing"), ("Other", "Other")]),
    ];

    public static SystemValueList? Find(string key) => All.FirstOrDefault(l => l.Key == key);
}

/// <summary>A built-in status (or other fixed value) set whose labels administrators may rename.</summary>
public sealed record StatusSetDefinition(string Key, string Name, string Description, Type Values, string? EntityType, bool AllowsSubStatuses, IReadOnlyDictionary<string, string>? Labels = null)
{
    public IReadOnlyList<string> ValueNames => Enum.GetNames(Values);

    public string DefaultLabel(string value) => Labels?.GetValueOrDefault(value) ?? Words(value);

    internal static string Words(string name) => BuiltIns.Words(name);
}

/// <summary>
/// The built-in status sets. Relabeling changes only what people see: the enum values, the state machines and every
/// rule keyed on them stay as they are. Sub-statuses are informational refinements recorded per record.
/// </summary>
public static class StatusSetCatalog
{
    public const string ProjectStatus = "project-status";
    public const string ContractStatus = "contract-status";
    public const string PurchaseOrderStatus = "purchase-order-status";
    public const string InvoiceStatus = "invoice-status";
    public const string ApBillStatus = "ap-bill-status";
    public const string TimesheetStatus = "timesheet-status";
    public const string ExpenseReportStatus = "expense-report-status";
    public const string OpportunityStage = "opportunity-stage";
    public const string OpportunitySource = "opportunity-source";
    public const string ExpenseCategory = "expense-category";

    public static IReadOnlyList<StatusSetDefinition> All { get; } =
    [
        new(ProjectStatus, "Project status", "Planning, active, on hold and closed projects.", typeof(D.ProjectAccounting.ProjectStatus), CustomizableEntityTypes.Project, true),
        new(ContractStatus, "Contract status", "Pending, active and closed contracts.", typeof(D.ProjectAccounting.ContractStatus), CustomizableEntityTypes.Contract, true),
        new(PurchaseOrderStatus, "Purchase order status", "From draft through issued, received and closed.", typeof(D.Purchasing.PurchaseOrderStatus), CustomizableEntityTypes.PurchaseOrder, true),
        new(InvoiceStatus, "Invoice status", "From draft through approved, sent and paid.", typeof(D.Billing.InvoiceStatus), CustomizableEntityTypes.Invoice, true),
        new(ApBillStatus, "AP bill status", "From draft through approval and posting.", typeof(D.Purchasing.ApBillStatus), CustomizableEntityTypes.ApBill, false),
        new(TimesheetStatus, "Timesheet status", "Draft, submitted, approved and rejected timesheets.", typeof(D.ProjectAccounting.TimesheetStatus), CustomizableEntityTypes.Timesheet, false),
        new(ExpenseReportStatus, "Expense report status", "Draft, submitted, approved and rejected expense reports.", typeof(OdcStatus), CustomizableEntityTypes.ExpenseReport, false),
        new(OpportunityStage, "Opportunity stage", "The pipeline stages a pursuit moves through.", typeof(D.Crm.OpportunityStage), CustomizableEntityTypes.Opportunity, true,
            new Dictionary<string, string> { ["NoBid"] = "No bid" }),
        new(OpportunitySource, "Opportunity source", "Where an opportunity came from (SAM.gov leads keep their link).", typeof(D.Crm.OpportunitySource), null, false,
            new Dictionary<string, string> { ["Manual"] = "Entered by hand", ["SamGov"] = "SAM.gov", ["CsvImport"] = "CSV import" }),
        new(ExpenseCategory, "Expense and ODC categories", "The categories of other direct costs and expense lines (travel keeps its per diem rules).", typeof(OdcType), null, false),
    ];

    public static StatusSetDefinition? Find(string key) => All.FirstOrDefault(s => s.Key == key);

    public static StatusSetDefinition Get(string key) => Find(key) ?? throw new ArgumentException($"There is no status set '{key}'.");
}

public sealed class CustomListService(IWorkbenchDbContextFactory dbFactory, CustomizableEntityRegistry registry, AuditRecorder audit, TimeProvider clock) : ICustomListService
{
    private const string AuditList = "ValueList";
    private const string AuditStatus = "StatusLabel";

    public async Task<IReadOnlyList<ValueListDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var stored = await db.Set<ValueList>().AsNoTracking().Include(l => l.Items).ToListAsync(cancellationToken);
        var usage = await UsageAsync(db, cancellationToken);
        var result = ValueListCatalog.All.Select(s => ToDto(s, stored.FirstOrDefault(l => l.Key == s.Key), usage)).ToList();
        result.AddRange(stored.Where(l => ValueListCatalog.Find(l.Key) is null).Select(l => ToDto(null, l, usage)));
        return result.OrderBy(l => !l.IsSystem).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<ValueListDto> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await GetAsync(db, key, cancellationToken) ?? throw new WorkbenchNotFoundException($"There is no value list '{key}'.");
    }

    internal async Task<ValueListDto?> GetAsync(IWorkbenchDbContext db, string key, CancellationToken ct)
    {
        var stored = await db.Set<ValueList>().AsNoTracking().Include(l => l.Items).FirstOrDefaultAsync(l => l.Key == key, ct);
        var system = ValueListCatalog.Find(key);
        return stored is null && system is null ? null : ToDto(system, stored, await UsageAsync(db, ct));
    }

    public async Task<ValueListDto> SaveAsync(SaveValueListRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = (request.Key ?? "").Trim().ToLowerInvariant();
        if (key.Length is 0 or > 64 || !key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            throw new ArgumentException("A list key is 1–64 lower-case letters, digits and hyphens (for example program-offices).");
        }

        var name = Required(request.Name, "The list needs a name.", 100);
        var items = (request.Items ?? []).Select(i => (Code: Required(i.Code, "Every item needs a code.", 100), Label: string.IsNullOrWhiteSpace(i.Label) ? i.Code.Trim() : Clip(i.Label.Trim(), 200), i.IsActive)).ToList();
        if (items.Count == 0)
        {
            throw new ArgumentException("A list needs at least one item.");
        }

        if (items.GroupBy(i => i.Code, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } dup)
        {
            throw new ArgumentException($"The code '{dup.Key}' is in the list twice.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var list = await db.Set<ValueList>().Include(l => l.Items).FirstOrDefaultAsync(l => l.Key == key, cancellationToken);
        var system = ValueListCatalog.Find(key);
        var before = list is null ? system?.Items.Select(i => i.Code).ToList() : list.Items.Select(i => i.Code).ToList();
        var removed = (before ?? []).Where(c => !items.Any(i => string.Equals(i.Code, c, StringComparison.Ordinal))).ToList();
        foreach (var code in removed)
        {
            if (await IsCodeUsedAsync(db, key, code, cancellationToken))
            {
                throw new InvalidOperationException($"'{code}' is stored on records, so it can't be removed. Turn it off instead.");
            }
        }

        if (list is null)
        {
            list = new ValueList { Key = key, Name = name, IsSystem = system is not null };
            db.Add(list);
        }

        list.Name = name;
        list.Description = string.IsNullOrWhiteSpace(request.Description) ? system?.Description : Clip(request.Description.Trim(), 500);
        list.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        list.UpdatedBy = audit.Actor;
        foreach (var gone in list.Items.Where(i => !items.Any(n => n.Code == i.Code)).ToList())
        {
            list.Items.Remove(gone);
            db.Remove(gone);
        }

        for (var i = 0; i < items.Count; i++)
        {
            var (code, label, active) = items[i];
            var item = list.Items.FirstOrDefault(x => x.Code == code);
            if (item is null)
            {
                item = new ValueListItem { ValueListId = list.Id, Code = code, Label = label };
                list.Items.Add(item);
                db.Add(item);
            }

            (item.Label, item.Order, item.IsActive) = (label, i, active);
        }

        audit.Record(db, AuditList, list.Id, "Saved", before is null ? null : new { items = before }, new { key, name, items = items.Select(i => $"{i.Code}={i.Label}{(i.IsActive ? "" : " (off)")}") });
        await db.SaveChangesAsync(cancellationToken);
        return (await GetAsync(db, key, cancellationToken))!;
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var list = await db.Set<ValueList>().Include(l => l.Items).FirstOrDefaultAsync(l => l.Key == key, cancellationToken);
        if (list is null)
        {
            if (ValueListCatalog.Find(key) is not null)
            {
                return; // Already the defaults.
            }

            throw new WorkbenchNotFoundException($"There is no value list '{key}'.");
        }

        if (ValueListCatalog.Find(key) is null && await db.Set<CustomFieldDefinition>().AnyAsync(f => f.ValueListKey == key, cancellationToken))
        {
            throw new InvalidOperationException("Custom fields use this list. Point them at another list first.");
        }

        audit.Record(db, AuditList, list.Id, ValueListCatalog.Find(key) is null ? "Deleted" : "Reset to defaults", new { key, items = list.Items.Select(i => i.Code) });
        db.RemoveRange(list.Items);
        db.Remove(list);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StatusSetDto>> ListStatusSetsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var labels = await db.Set<StatusLabel>().AsNoTracking().ToListAsync(cancellationToken);
        var subs = await db.Set<SubStatus>().AsNoTracking().ToListAsync(cancellationToken);
        return StatusSetCatalog.All.Select(s => ToDto(s, labels, subs)).ToList();
    }

    public async Task<StatusSetDto> SaveStatusSetAsync(string setKey, SaveStatusSetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var set = StatusSetCatalog.Get(setKey);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var labels = await db.Set<StatusLabel>().Where(l => l.SetKey == set.Key).ToListAsync(cancellationToken);
        var subs = await db.Set<SubStatus>().Where(s => s.SetKey == set.Key).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var changes = new List<string>();
        foreach (var v in request.Values ?? [])
        {
            var value = set.ValueNames.FirstOrDefault(n => string.Equals(n, v.Value, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"{set.Name} has no value '{v.Value}'.");
            var label = string.IsNullOrWhiteSpace(v.Label) ? null : Clip(v.Label.Trim(), 100);
            var row = labels.FirstOrDefault(l => l.Value == value);
            if (label is null || label == set.DefaultLabel(value))
            {
                if (row is not null)
                {
                    db.Remove(row);
                    changes.Add($"{value}: default");
                }
            }
            else if (row is null)
            {
                db.Add(new StatusLabel { SetKey = set.Key, Value = value, Label = label, UpdatedAtUtc = now, UpdatedBy = audit.Actor });
                changes.Add($"{value}: {label}");
            }
            else if (row.Label != label)
            {
                (row.Label, row.UpdatedAtUtc, row.UpdatedBy) = (label, now, audit.Actor);
                changes.Add($"{value}: {label}");
            }

            if (v.SubStatuses is null)
            {
                continue;
            }

            if (!set.AllowsSubStatuses && v.SubStatuses.Count > 0)
            {
                throw new ArgumentException($"{set.Name} doesn't take sub-statuses.");
            }

            var wanted = v.SubStatuses.Select(s => (Code: Slug(Required(s.Code, "Every sub-status needs a code.", 64)), Label: Required(string.IsNullOrWhiteSpace(s.Label) ? s.Code : s.Label, "", 100), s.IsActive)).ToList();
            var mine = subs.Where(s => s.ParentValue == value).ToList();
            foreach (var gone in mine.Where(s => !wanted.Any(w => w.Code == s.Code)))
            {
                if (await db.Set<RecordSubStatus>().AnyAsync(r => r.SetKey == set.Key && r.Code == gone.Code, cancellationToken))
                {
                    throw new InvalidOperationException($"Records use the sub-status '{gone.Label}', so it can't be removed. Turn it off instead.");
                }

                db.Remove(gone);
                changes.Add($"{value} › {gone.Code}: removed");
            }

            for (var i = 0; i < wanted.Count; i++)
            {
                var (code, label2, active) = wanted[i];
                if (subs.FirstOrDefault(s => s.Code == code && s.ParentValue != value) is not null)
                {
                    throw new ArgumentException($"The sub-status code '{code}' is already used under another status.");
                }

                var row2 = mine.FirstOrDefault(s => s.Code == code);
                if (row2 is null)
                {
                    row2 = new SubStatus { SetKey = set.Key, ParentValue = value, Code = code, Label = label2 };
                    db.Add(row2);
                    subs.Add(row2);
                    changes.Add($"{value} › {code}: {label2}");
                }

                (row2.Label, row2.Order, row2.IsActive) = (label2, i, active);
            }
        }

        if (changes.Count > 0)
        {
            audit.Record(db, AuditStatus, Guid.Empty, $"Saved {set.Key}", after: new { set = set.Key, changes });
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(set, await db.Set<StatusLabel>().AsNoTracking().ToListAsync(cancellationToken), await db.Set<SubStatus>().AsNoTracking().ToListAsync(cancellationToken));
    }

    public async Task<RecordSubStatusDto> GetSubStatusAsync(string setKey, Guid recordId, CancellationToken cancellationToken = default)
    {
        var set = StatusSetCatalog.Get(setKey);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await CurrentAsync(db, set, recordId, cancellationToken);
    }

    public async Task<RecordSubStatusDto> SetSubStatusAsync(string setKey, Guid recordId, string? code, CancellationToken cancellationToken = default)
    {
        var set = StatusSetCatalog.Get(setKey);
        if (!set.AllowsSubStatuses || set.EntityType is null)
        {
            throw new ArgumentException($"{set.Name} doesn't take sub-statuses.");
        }

        var entity = registry.Get(set.EntityType);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var status = await entity.StatusAsync(db, recordId, cancellationToken) ?? throw WorkbenchNotFoundException.For(entity.Label, recordId);
        var row = await db.Set<RecordSubStatus>().FirstOrDefaultAsync(r => r.SetKey == set.Key && r.RecordId == recordId, cancellationToken);
        if (string.IsNullOrWhiteSpace(code))
        {
            if (row is not null)
            {
                db.Remove(row);
                audit.Record(db, entity.AuditEntityType, recordId, "Sub-status cleared", new { subStatus = row.Code });
            }
        }
        else
        {
            var sub = await db.Set<SubStatus>().AsNoTracking().FirstOrDefaultAsync(s => s.SetKey == set.Key && s.Code == code.Trim(), cancellationToken)
                ?? throw new ArgumentException($"There is no sub-status '{code}'.");
            if (sub.ParentValue != status)
            {
                throw new ArgumentException($"'{sub.Label}' refines {set.DefaultLabel(sub.ParentValue)}, but the {entity.Label.ToLowerInvariant()} is {set.DefaultLabel(status)}.");
            }

            if (!sub.IsActive && row?.Code != sub.Code)
            {
                throw new ArgumentException($"'{sub.Label}' is turned off.");
            }

            var before = row?.Code;
            if (row is null)
            {
                row = new RecordSubStatus { SetKey = set.Key, RecordId = recordId, ParentValue = status, Code = sub.Code };
                db.Add(row);
            }

            (row.ParentValue, row.Code, row.UpdatedAtUtc, row.UpdatedBy) = (status, sub.Code, clock.GetUtcNow().UtcDateTime, audit.Actor);
            audit.Record(db, entity.AuditEntityType, recordId, "Sub-status set", before is null ? null : new { subStatus = before }, new { subStatus = sub.Code, label = sub.Label });
        }

        await db.SaveChangesAsync(cancellationToken);
        return await CurrentAsync(db, set, recordId, cancellationToken);
    }

    private async Task<RecordSubStatusDto> CurrentAsync(IWorkbenchDbContext db, StatusSetDefinition set, Guid recordId, CancellationToken ct)
    {
        var status = set.EntityType is null ? null : await registry.Get(set.EntityType).StatusAsync(db, recordId, ct);
        var row = await db.Set<RecordSubStatus>().AsNoTracking().FirstOrDefaultAsync(r => r.SetKey == set.Key && r.RecordId == recordId, ct);
        if (row is null || row.ParentValue != status)
        {
            // The record moved on to another status: its old sub-status no longer applies.
            return new(set.Key, recordId, status, null, null);
        }

        var label = await db.Set<SubStatus>().AsNoTracking().Where(s => s.SetKey == set.Key && s.Code == row.Code).Select(s => s.Label).FirstOrDefaultAsync(ct);
        return new(set.Key, recordId, status, row.Code, label ?? row.Code);
    }

    private static StatusSetDto ToDto(StatusSetDefinition set, IReadOnlyList<StatusLabel> labels, IReadOnlyList<SubStatus> subs) =>
        new(set.Key, set.Name, set.Description, set.AllowsSubStatuses, set.EntityType,
            set.ValueNames.Select(v => new StatusValueDto(v, set.DefaultLabel(v),
                labels.FirstOrDefault(l => l.SetKey == set.Key && l.Value == v)?.Label ?? set.DefaultLabel(v),
                subs.Where(s => s.SetKey == set.Key && s.ParentValue == v).OrderBy(s => s.Order).Select(s => new ValueListItemDto(s.Code, s.Label, s.Order, s.IsActive)).ToList())).ToList());

    private static ValueListDto ToDto(SystemValueList? system, ValueList? stored, IReadOnlyDictionary<string, List<string>> usage)
    {
        var key = stored?.Key ?? system!.Key;
        var items = stored is not null
            ? stored.Items.OrderBy(i => i.Order).Select(i => new ValueListItemDto(i.Code, i.Label, i.Order, i.IsActive)).ToList()
            : system!.Items.Select((i, n) => new ValueListItemDto(i.Code, i.Label, n, true)).ToList();
        var usedBy = (system?.UsedBy ?? []).Concat(usage.GetValueOrDefault(key) ?? []).Distinct().ToList();
        return new(key, stored?.Name ?? system!.Name, stored?.Description ?? system?.Description, system is not null, stored is not null, items, usedBy);
    }

    private async Task<IReadOnlyDictionary<string, List<string>>> UsageAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var fields = await db.Set<CustomFieldDefinition>().AsNoTracking().Where(f => f.ValueListKey != null).Select(f => new { f.ValueListKey, f.EntityType, f.Label }).ToListAsync(ct);
        return fields.GroupBy(f => f.ValueListKey!).ToDictionary(g => g.Key, g => g.Select(f => $"{registry.Find(f.EntityType)?.Label ?? f.EntityType} › {f.Label}").ToList());
    }

    private static async Task<bool> IsCodeUsedAsync(IWorkbenchDbContext db, string listKey, string code, CancellationToken ct)
    {
        var fieldIds = await db.Set<CustomFieldDefinition>().Where(f => f.ValueListKey == listKey).Select(f => f.Id).ToListAsync(ct);
        if (fieldIds.Count > 0)
        {
            var quoted = System.Text.Json.JsonSerializer.Serialize(code);
            if (await db.Set<CustomFieldValue>().AnyAsync(v => fieldIds.Contains(v.FieldId) && (v.TextValue == code || v.TextValue!.Contains(quoted)), ct))
            {
                return true;
            }
        }

        return listKey switch
        {
            ValueListCatalog.VendorBusinessSize => await db.Set<D.Purchasing.Vendor>().AnyAsync(v => v.BusinessSize == code, ct),
            ValueListCatalog.VendorSocioEconomic => (await db.Set<D.Purchasing.Vendor>().Where(v => v.SocioEconomicCategories != null).Select(v => v.SocioEconomicCategories!).ToListAsync(ct))
                .Any(s => s.Split(',', StringSplitOptions.TrimEntries).Contains(code, StringComparer.OrdinalIgnoreCase)),
            ValueListCatalog.TaxExemptionReasons => await db.Set<D.SalesTax.TaxExemptionCertificate>().AnyAsync(c => c.Reason == code, ct),
            _ => false,
        };
    }

    private static string Required(string? value, string message, int max) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(message) : Clip(value.Trim(), max);

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    internal static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        var slug = string.Join('_', new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? throw new ArgumentException("Use letters or digits.") : slug.Length > 64 ? slug[..64] : slug;
    }
}

/// <summary>Reads a value list's items inside a caller's unit of work (built-in lists answer with their defaults until edited).</summary>
public static class ValueLists
{
    public static async Task<IReadOnlyList<ValueListItemDto>> ItemsAsync(IWorkbenchDbContext db, string key, CancellationToken ct)
    {
        var stored = await db.Set<ValueList>().AsNoTracking().Include(l => l.Items).FirstOrDefaultAsync(l => l.Key == key, ct);
        return stored is not null
            ? stored.Items.OrderBy(i => i.Order).Select(i => new ValueListItemDto(i.Code, i.Label, i.Order, i.IsActive)).ToList()
            : ValueListCatalog.Find(key)?.Items.Select((i, n) => new ValueListItemDto(i.Code, i.Label, n, true)).ToList() ?? [];
    }
}
