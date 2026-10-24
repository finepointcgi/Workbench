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

    /// <summary>Several options; stored one per line.</summary>
    MultiSelect = 10,

    /// <summary>A link: a URL, or <c>[label](url)</c>.</summary>
    Hyperlink = 11,

    /// <summary>A symbol from the set in Options[0] (rag, flag, stars, priority); stored as its label.</summary>
    Symbol = 12,

    /// <summary>Percent complete, 0-100.</summary>
    Progress = 13,

    /// <summary>Read-only: prefix + sequence assigned when a row is added (Options: prefix, digits, first number; see <see cref="Column.NextNumber"/>).</summary>
    AutoNumber = 14,

    /// <summary>Read-only system columns, filled in from <see cref="Item.CreatedBy"/> and the other audit fields when a row is saved.</summary>
    CreatedBy = 15,

    CreatedDate = 16,

    ModifiedBy = 17,

    ModifiedDate = 18,
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

    /// <summary>A Dropdown column's status label colors: a palette key per option, in the order of <see cref="Options"/>.</summary>
    public List<string> OptionColors { get; set; } = [];

    /// <summary>An auto-number column's next number (assigned to the next row added; never reused).</summary>
    public int NextNumber { get; set; }
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

    /// <summary>When the row was added (stamped on save; older rows fall back to the time in their id).</summary>
    public DateTime? CreatedAtUtc { get; set; }

    /// <summary>Who added the row (an email), when known.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>When the row last changed (stamped on save).</summary>
    public DateTime? ModifiedAtUtc { get; set; }

    /// <summary>Who last changed the row (an email), when known.</summary>
    public string? ModifiedBy { get; set; }
}
