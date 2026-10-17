namespace Workbench.Domain.Collaboration;

/// <summary>One entry in a sheet's activity log. Kept after the item it describes is deleted.</summary>
public sealed class ActivityEvent : Entity
{
    public Guid SheetId { get; set; }
    public Guid? ItemId { get; set; }
    public required string Actor { get; set; }

    /// <summary>One of the <c>ActivityKinds</c> constants in the contracts, e.g. <c>item.updated</c>.</summary>
    public required string Kind { get; set; }

    public required string Summary { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}
