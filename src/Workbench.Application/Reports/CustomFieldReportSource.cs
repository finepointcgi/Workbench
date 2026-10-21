using Workbench.Application.Customization;
using Workbench.Contracts.Customization;

namespace Workbench.Application.Reports;

/// <summary>
/// A report-writer source with the active custom fields of its rows' record type appended as <c>cf.&lt;key&gt;</c> fields.
/// Values come through <see cref="CustomFieldService"/>, so sensitive ones are masked for people without their permission.
/// </summary>
public sealed class CustomFieldReportSource(ReportSource inner, IReadOnlyList<CustomFieldDefinitionDto> definitions, CustomFieldService customFields) : ReportSource
{
    public override string Key => inner.Key;
    public override string Name => inner.Name;
    public override string Description => inner.Description;
    public override string? DateField => inner.DateField;
    public override string? CustomFieldEntity => inner.CustomFieldEntity;

    public override IReadOnlyList<SourceField> Fields { get; } =
        inner.Fields.Concat(definitions.Select(d => new SourceField(CustomFieldValues.CatalogPrefix + d.Key, d.Label, CustomFieldService.ReportColumn(d.FieldType),
            Description: d.HelpText ?? "Custom field"))).ToList();

    public override string? Unavailable(ReportAccessContext access) => inner.Unavailable(access);

    public override async Task<SourceRows> LoadAsync(IWorkbenchDbContext db, ReportAccessContext access, DateOnly? from, DateOnly? to, DateOnly today, CancellationToken ct)
    {
        var rows = await inner.LoadAsync(db, access, from, to, today, ct);
        var ids = (rows.RecordIds ?? []).OfType<Guid>().Distinct().ToList();
        var table = await customFields.RawTableAsync(db, inner.CustomFieldEntity!, ids, ct);
        var widened = rows.Rows.Select((row, i) =>
        {
            var values = rows.RecordIds?[i] is { } id ? table.Values.GetValueOrDefault(id) : null;
            return row.Concat(definitions.Select(d => (object?)Typed(d.FieldType, values?.GetValueOrDefault(d.Key)))).ToArray();
        }).ToList();
        return rows with { Rows = widened };
    }

    /// <summary>The source with its custom fields, or the source itself when it has none.</summary>
    public static async Task<ReportSource> WithCustomFieldsAsync(ReportSource source, CustomFieldService? customFields, CancellationToken ct)
    {
        if (source.CustomFieldEntity is not { } entity || customFields is null)
        {
            return source;
        }

        var defs = await customFields.ListDefinitionsAsync(entity, false, ct);
        return defs.Count == 0 ? source : new CustomFieldReportSource(source, defs, customFields);
    }

    // The engine formats values by column type: give it numbers, dates and flags rather than their text.
    private static object? Typed(CustomFieldType type, string? text) => text is null || text == CustomFieldValues.Masked ? text : type switch
    {
        CustomFieldType.Number or CustomFieldType.Currency => decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
        CustomFieldType.Date => DateOnly.ParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        CustomFieldType.YesNo => text == "true",
        _ => text,
    };
}
