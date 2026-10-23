namespace Workbench.Domain;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
}

public sealed class Workspace : Entity
{
    public required string Name { get; set; }
    public List<Sheet> Sheets { get; } = [];
}

/// <summary>A grid of items. A project is a sheet with scheduling turned on.</summary>
public sealed class Sheet : Entity
{
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool SchedulingEnabled { get; set; }

    /// <summary>Costpoint project this sheet is linked to, if any.</summary>
    public string? CostpointProjectId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<Column> Columns { get; } = [];
    public List<Item> Items { get; } = [];
}

public enum ColumnType
{
    Text,
    Number,
    Currency,
    Date,
    Person,
    Dropdown,
    Checkbox,
    CostpointLookup,

    /// <summary>Links to rows of another sheet (Options[0] is that sheet's id); the value is the linked item ids, comma-separated.</summary>
    LinkedRow = 8,

    /// <summary>Shows a field of the rows linked in a LinkedRow column (Options: the link column's id, then the field key); computed on read.</summary>
    Lookup = 9,
}

public sealed class Column : Entity
{
    public Guid SheetId { get; set; }
    public required string Name { get; set; }
    public ColumnType Type { get; set; }
    public int Order { get; set; }
    public List<string> Options { get; set; } = [];
    public string? Formula { get; set; }

    /// <summary>
    /// Only Finance, administrators and the workspace's owners see this column and its values (and any formula column
    /// reading it); everyone else's reads leave it out.
    /// </summary>
    public bool IsRestricted { get; set; }
}

/// <summary>A row in a sheet, which is also a task on boards and the Gantt.</summary>
public sealed class Item : Entity
{
    public Guid SheetId { get; set; }
    public Guid? ParentId { get; set; }
    public int Order { get; set; }
    public required string Title { get; set; }
    public DateOnly? Start { get; set; }
    public DateOnly? Finish { get; set; }

    /// <summary>Costpoint WBS element / charge code this item rolls up to.</summary>
    public string? CostpointWbsId { get; set; }

    /// <summary>
    /// True when Workbench numbered <see cref="CostpointWbsId"/> from the outline (on a project's plan), so it follows the
    /// task when it's indented, outdented or moved. A code someone typed is false and kept as typed.
    /// </summary>
    public bool WbsCodeIsAuto { get; set; }

    /// <summary>Custom cell values keyed by column id, stored as JSON.</summary>
    public Dictionary<string, string?> Values { get; set; } = [];

    /// <summary>A milestone: a point in time (a deliverable, a review, a decision) with no duration, so Start = Finish.</summary>
    public bool IsMilestone { get; set; }
}
