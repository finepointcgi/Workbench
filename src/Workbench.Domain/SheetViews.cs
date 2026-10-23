namespace Workbench.Domain;

/// <summary>
/// A saved view of a sheet's grid: sort, filters and hidden columns (<c>SheetViewDefinition</c> as JSON). Personal views
/// are their owner's; shared views are everyone's who can open the sheet. At most one shared view per sheet is the sheet's
/// default (<see cref="IsSheetDefault"/>).
/// </summary>
public sealed class SheetView : Entity
{
    public Guid SheetId { get; set; }
    public required string Name { get; set; }
    public required string OwnerEmail { get; set; }
    public bool IsShared { get; set; }
    public bool IsSheetDefault { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A person's own default view of a sheet, over the sheet's default.</summary>
public sealed class SheetViewPreference : Entity
{
    public required string UserEmail { get; set; }
    public Guid SheetId { get; set; }
    public Guid ViewId { get; set; }
}
