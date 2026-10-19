namespace Workbench.Domain.Tasks;

/// <summary>
/// A person assigned to a sheet item (a task): the item's dedicated Assignees field, separate from Person columns. The
/// item's Finish is its due date. Removed with the item.
/// </summary>
public sealed class ItemAssignee : Entity
{
    public Guid SheetId { get; set; }

    public Guid ItemId { get; set; }

    /// <summary>Lower-case: a user in the directory.</summary>
    public required string Email { get; set; }

    /// <summary>Who assigned them (an email, or the automation actor).</summary>
    public required string AssignedBy { get; set; }

    public DateTime AssignedAtUtc { get; set; }
}
