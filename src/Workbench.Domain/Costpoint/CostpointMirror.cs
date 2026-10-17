namespace Workbench.Domain.Costpoint;

// Read-only mirrors of Costpoint data. Costpoint is the system of record: only the sync writes
// these tables, and nothing here is ever written back to Costpoint.

public sealed class CpProject : Entity
{
    public required string ProjectId { get; set; }
    public required string Name { get; set; }
    public string? OrganizationId { get; set; }
    public DateOnly? Start { get; set; }
    public DateOnly? End { get; set; }
    public bool Active { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

public sealed class CpWbsElement : Entity
{
    public required string ProjectId { get; set; }
    public required string WbsId { get; set; }
    public string? ParentWbsId { get; set; }
    public required string Name { get; set; }
    public int Level { get; set; }
    public bool ChargeAllowed { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

public sealed class CpEmployee : Entity
{
    public required string EmployeeId { get; set; }
    public required string Name { get; set; }
    public string? Email { get; set; }
    public string? OrganizationId { get; set; }
    public string? LaborCategoryId { get; set; }
    public bool Active { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

public sealed class CpLaborCategory : Entity
{
    public required string LaborCategoryId { get; set; }
    public required string Name { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

/// <summary>Budget for a project or WBS element, either for one fiscal period or for the whole life.</summary>
public sealed class CpBudget : Entity
{
    /// <summary><see cref="FiscalPeriod"/> value for a total (not per-period) budget.</summary>
    public const string TotalPeriod = "TOTAL";

    public required string ProjectId { get; set; }

    /// <summary>WBS element, or empty for a project-level budget.</summary>
    public string WbsId { get; set; } = "";

    /// <summary><c>yyyy-MM</c>, or <see cref="TotalPeriod"/>.</summary>
    public required string FiscalPeriod { get; set; }

    public decimal Hours { get; set; }
    public decimal Amount { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

/// <summary>Labor actuals for a WBS element in one fiscal period, summed across employees.</summary>
public sealed class CpActual : Entity
{
    public required string ProjectId { get; set; }
    public required string WbsId { get; set; }

    /// <summary><c>yyyy-MM</c>.</summary>
    public required string FiscalPeriod { get; set; }

    public decimal Hours { get; set; }
    public decimal Cost { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

public enum CpSyncStatus
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>One pull from Costpoint into the mirror tables.</summary>
public sealed class CpSyncRun : Entity
{
    public required string Source { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public CpSyncStatus Status { get; set; }
    public int Projects { get; set; }
    public int WbsElements { get; set; }
    public int Employees { get; set; }
    public int LaborCategories { get; set; }
    public int Budgets { get; set; }
    public int Actuals { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Deleted { get; set; }

    /// <summary>Rows the source skipped as invalid (for example bad CSV lines).</summary>
    public int Skipped { get; set; }

    public string? Error { get; set; }
}
