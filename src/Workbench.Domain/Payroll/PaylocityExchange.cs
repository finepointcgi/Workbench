namespace Workbench.Domain.Payroll;

// The file-based Paylocity exchange: what was exported (history with checksums), how Workbench org units map to Paylocity
// cost centers, and which employee fields Workbench owns when the roster is imported from Paylocity.

/// <summary>
/// One hours export of a pay period, kept as evidence of exactly what was handed to the payroll provider. Append-only: a
/// deliberate re-export of the same period is a new row with <see cref="IsReExport"/> and the reason in <see cref="Note"/>.
/// </summary>
public sealed class PayrollExportRecord : Entity
{
    public Guid RunId { get; set; }
    public Guid PayGroupId { get; set; }
    public Guid PayPeriodId { get; set; }

    /// <summary>1 for the first export of the run, then 2, 3 ...</summary>
    public int Sequence { get; set; }

    /// <summary><c>file</c> (downloaded) or <c>api</c> (sent through the provider's API).</summary>
    public string Method { get; set; } = "file";

    public required string FileName { get; set; }

    /// <summary>Lower-case hex SHA-256 of <see cref="Content"/>: compare it with the file that was uploaded.</summary>
    public required string Sha256 { get; set; }

    public required string Content { get; set; }
    public int LineCount { get; set; }
    public int EmployeeCount { get; set; }
    public decimal TotalHours { get; set; }

    /// <summary>Employees flagged (unapproved time, missing ids ...) when it was exported with the issues acknowledged.</summary>
    public int FlaggedCount { get; set; }

    public bool IsReExport { get; set; }
    public string? Note { get; set; }
    public required string ExportedBy { get; set; }
    public DateTime ExportedAtUtc { get; set; }
}

/// <summary>An org unit's Paylocity cost center code. Without a row the export sends the org unit's own code.</summary>
public sealed class PaylocityCostCenterMapping : Entity
{
    public Guid OrgUnitId { get; set; }
    public required string Code { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Roster import settings (one row): the employee fields Workbench owns. An update from a Paylocity roster never changes
/// an owned field of an existing employee (it is still filled in for a new one).
/// </summary>
public sealed class PaylocityRosterSetting : Entity
{
    /// <summary>Field keys separated by <c>;</c> (see <c>PaylocityRosterFields</c>).</summary>
    public string OwnedFields { get; set; } = "";

    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}
