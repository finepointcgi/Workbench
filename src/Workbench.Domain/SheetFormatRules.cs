namespace Workbench.Domain;

/// <summary>
/// A conditional formatting rule of a sheet ("when Status is Stuck, color the row red"): the conditions and the format as
/// JSON (<c>SheetFormatRuleDefinition</c>). Rules apply in <see cref="Order"/>; an earlier rule wins when two set the same thing.
/// </summary>
public sealed class SheetFormatRule : Entity
{
    public Guid SheetId { get; set; }
    public int Order { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}
