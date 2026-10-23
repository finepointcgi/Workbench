namespace Workbench.Domain.Sheets;

/// <summary>A report combining rows from several sheets. Run live, as the person viewing it.</summary>
public sealed class SheetReport : Entity
{
    public required string Name { get; set; }

    /// <summary>Lower-case email of the person who made it; only they (or an administrator) change it.</summary>
    public required string OwnerEmail { get; set; }

    /// <summary>Shared with this workspace's members, or private when null.</summary>
    public Guid? WorkspaceId { get; set; }

    public List<Guid> SheetIds { get; set; } = [];

    /// <summary>Column names, matched by name in each sheet.</summary>
    public List<string> Columns { get; set; } = [];

    public List<SheetReportFilter> Filters { get; set; } = [];

    /// <summary>True: a row must match every filter; false: any.</summary>
    public bool MatchAll { get; set; } = true;

    public string? SortBy { get; set; }

    public bool SortDescending { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class SheetReportFilter
{
    public string Column { get; set; } = "";

    /// <summary>A <c>SheetReportOperator</c> name.</summary>
    public string Operator { get; set; } = "Is";

    public string? Value { get; set; }
}
