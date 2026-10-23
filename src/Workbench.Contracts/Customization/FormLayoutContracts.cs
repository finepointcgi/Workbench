namespace Workbench.Contracts.Customization;

// Form layouts: the order of fields and sections on a record page or edit dialog, the section headings, which optional
// fields are hidden and which are required. A layout is set for everyone, a role, a company or a role in a company; the
// most specific one that exists applies, and with none the page looks exactly as it was built (the built-in layout).
// Layouts never relax a rule: a field the domain needs can't be hidden or made optional, and the server checks fields a
// layout makes required when the record is saved.

/// <summary>
/// Entity type keys shared by layouts, list views and the field catalog: the custom-field keys
/// (<see cref="CustomizableEntityTypes"/>), plus sales orders, which have a list but no custom fields.
/// </summary>
public static class CustomizableEntities
{
    public const string Project = CustomizableEntityTypes.Project;
    public const string Contract = CustomizableEntityTypes.Contract;
    public const string Employee = CustomizableEntityTypes.Employee;
    public const string Vendor = CustomizableEntityTypes.Vendor;
    public const string Customer = CustomizableEntityTypes.Customer;
    public const string PurchaseOrder = CustomizableEntityTypes.PurchaseOrder;
    public const string ApBill = CustomizableEntityTypes.ApBill;
    public const string Invoice = CustomizableEntityTypes.Invoice;
    public const string ExpenseReport = CustomizableEntityTypes.ExpenseReport;
    public const string Part = CustomizableEntityTypes.Part;
    public const string Opportunity = CustomizableEntityTypes.Opportunity;
    public const string Timesheet = CustomizableEntityTypes.Timesheet;
    public const string SalesOrder = "sales-order";
}

/// <summary>
/// Field types as forms and lists show them (Text, LongText, Number, Money, Date, Bool, Select, MultiSelect, Person). The
/// field catalog reports custom fields by their <see cref="CustomFieldType"/>; <see cref="Of"/> maps those to the form types.
/// </summary>
public static class FormFieldTypes
{
    public static string Of(string catalogType) => catalogType switch
    {
        nameof(CustomFieldType.Currency) => "Money",
        nameof(CustomFieldType.YesNo) => "Bool",
        nameof(CustomFieldType.Picklist) => "Select",
        nameof(CustomFieldType.MultiPicklist) => "MultiSelect",
        nameof(CustomFieldType.Url) or nameof(CustomFieldType.RecordLink) => "Text",
        _ => catalogType,
    };
}

/// <summary>One field's place in a section. <see cref="Required"/> adds a requirement; it never removes one.</summary>
public sealed record FormFieldPlacement(string Key, bool Hidden = false, bool Required = false);

/// <summary>A section of a form. <see cref="Title"/> empty means no heading (as on single-section forms).</summary>
public sealed record FormSectionLayout(string Key, string Title, IReadOnlyList<FormFieldPlacement> Fields);

/// <summary>
/// A saved layout, or the built-in one (<see cref="Id"/> null). <see cref="CompanyId"/> and <see cref="RoleKey"/> null mean
/// "every company" and "everyone".
/// </summary>
public sealed record FormLayoutDto(
    Guid? Id,
    string EntityType,
    Guid? CompanyId,
    string? RoleKey,
    IReadOnlyList<FormSectionLayout> Sections,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

/// <summary>A form the layout editor can change, and where it appears.</summary>
public sealed record FormEntityTypeDto(string EntityType, string Name, string ShownOn);

/// <param name="DomainRequired">The record can't be saved without it: never hidden, always required.</param>
/// <param name="Required">Domain-required, or made required by the layout.</param>
public sealed record EffectiveFormField(
    string Key,
    string Label,
    string FieldType,
    bool IsCustom,
    bool DomainRequired,
    bool Required,
    IReadOnlyList<string>? Options,
    IReadOnlyList<string>? OptionLabels = null)
{
    /// <summary>The label to show for a stored option code (the code itself when there's no label).</summary>
    public string LabelOf(string option)
    {
        var index = Options is null ? -1 : Options.ToList().IndexOf(option);
        return index >= 0 && OptionLabels is { } labels && index < labels.Count ? labels[index] : option;
    }
}

public sealed record EffectiveFormSection(string Key, string Title, IReadOnlyList<EffectiveFormField> Fields);

/// <summary>
/// The layout a form renders with. <see cref="Sections"/> hold only the fields to show, in order. <see cref="HiddenKeys"/>
/// lists fields hidden by the layout or by field permissions: pages keep (and save back) their values unchanged.
/// </summary>
/// <param name="LayoutId">The saved layout that applied, or null for the built-in layout.</param>
/// <param name="Source">Which layout applied, for people ("Built-in", "Finance", "Finance in FED").</param>
public sealed record EffectiveFormLayout(
    string EntityType,
    Guid? LayoutId,
    string Source,
    IReadOnlyList<EffectiveFormSection> Sections,
    IReadOnlyList<string> HiddenKeys)
{
    public bool IsBuiltIn => LayoutId is null;
}

/// <summary>Creates or replaces the layout for one entity type, company and role.</summary>
public sealed record SaveFormLayoutRequest(string EntityType, Guid? CompanyId, string? RoleKey, IReadOnlyList<FormSectionLayout> Sections);

/// <summary>What a person with <paramref name="RoleKey"/> working in <paramref name="CompanyId"/> would see, optionally with an unsaved draft.</summary>
public sealed record PreviewFormLayoutRequest(string EntityType, Guid? CompanyId, string RoleKey, SaveFormLayoutRequest? Draft = null);

public interface IFormLayoutService
{
    Task<IReadOnlyList<FormEntityTypeDto>> ListEntityTypesAsync(CancellationToken ct = default);

    /// <summary>Every field of the entity type the editor can place (built-in and custom).</summary>
    Task<IReadOnlyList<EntityFieldDto>> ListFieldsAsync(string entityType, CancellationToken ct = default);

    /// <summary>The page as built: the fallback when no layout applies.</summary>
    Task<FormLayoutDto> GetBuiltInAsync(string entityType, CancellationToken ct = default);

    /// <summary>Saved layouts for the entity type (managers of layouts only).</summary>
    Task<IReadOnlyList<FormLayoutDto>> ListAsync(string entityType, CancellationToken ct = default);

    /// <summary>The layout for the signed-in person, in the company they work in.</summary>
    Task<EffectiveFormLayout> GetEffectiveAsync(string entityType, CancellationToken ct = default);

    /// <summary>The layout a role would get (managers of layouts only).</summary>
    Task<EffectiveFormLayout> PreviewAsync(PreviewFormLayoutRequest request, CancellationToken ct = default);

    /// <summary>Validates and saves the layout (managers of layouts only).</summary>
    Task<FormLayoutDto> SaveAsync(SaveFormLayoutRequest request, CancellationToken ct = default);

    /// <summary>Deletes a saved layout, so the next one in the fallback applies (managers of layouts only).</summary>
    Task DeleteAsync(Guid layoutId, CancellationToken ct = default);
}
