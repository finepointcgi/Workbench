namespace Workbench.Domain.Sheets;

/// <summary>A sheet saved as a template for its workspace ("Save as template"). Built-in templates live in code.</summary>
public sealed class SheetTemplate : Entity
{
    public Guid WorkspaceId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public SheetTemplateContent Content { get; set; } = new();
}

/// <summary>A template's columns and rows. Keys tie rows, lookups and parents together; ids are made fresh on use.</summary>
public sealed class SheetTemplateContent
{
    public bool SchedulingEnabled { get; set; }

    public List<TemplateColumn> Columns { get; set; } = [];

    public List<TemplateRow> Rows { get; set; } = [];

    public List<TemplateReference> References { get; set; } = [];
}

/// <summary>
/// A column. For a Lookup, Options[0] is the key of the template's link column; a LinkedRow's Options[0] stays the
/// linked sheet's id.
/// </summary>
public sealed class TemplateColumn
{
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    public ColumnType Type { get; set; }

    public List<string> Options { get; set; } = [];

    public string? Formula { get; set; }

    public bool IsRestricted { get; set; }
}

/// <summary>A row. Dates are days from the template's first date, so a new sheet's plan starts today.</summary>
public sealed class TemplateRow
{
    public string Key { get; set; } = "";

    public string? ParentKey { get; set; }

    public string Title { get; set; } = "";

    public int? StartDay { get; set; }

    public int? FinishDay { get; set; }

    public bool IsMilestone { get; set; }

    /// <summary>Values by column key.</summary>
    public Dictionary<string, string?> Values { get; set; } = [];
}

public sealed class TemplateReference
{
    public string Name { get; set; } = "";

    public Guid SourceSheetId { get; set; }

    public List<string> Fields { get; set; } = [];
}
