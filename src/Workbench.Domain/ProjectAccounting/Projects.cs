namespace Workbench.Domain.ProjectAccounting;

public enum ProjectStatus
{
    Planning,
    Active,
    OnHold,
    Closed,
}

/// <summary>
/// A project under a contract (or an indirect/overhead project with no contract).
/// Its plan lives in <see cref="SheetId"/>; WBS elements map to that sheet's items.
/// </summary>
public sealed class Project : Entity, Companies.ICompanyOwned
{
    /// <summary>The legal company whose ledger the project's cost posts to (the contract's company when it has one).</summary>
    public Guid CompanyId { get; set; } = Companies.Company.DefaultId;

    public required string Code { get; set; }
    public required string Name { get; set; }
    public Guid? ContractId { get; set; }
    public Guid? ProjectManagerId { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;
    public DateOnly? Start { get; set; }
    public DateOnly? Finish { get; set; }

    /// <summary>The plan sheet (Gantt / grid) for this project.</summary>
    public Guid? SheetId { get; set; }
}

public enum ChargeKind
{
    /// <summary>Direct work on a contract.</summary>
    Direct,

    /// <summary>Indirect work: overhead, G&amp;A, B&amp;P, IR&amp;D.</summary>
    Indirect,

    /// <summary>Paid leave and holidays.</summary>
    Leave,
}

/// <summary>
/// A WBS element. Leaf elements that are open and chargeable are the charge codes people
/// record time and ODCs against. <see cref="ItemId"/> ties it to the matching task in the plan sheet.
/// </summary>
public sealed class WbsElement : Entity
{
    public Guid ProjectId { get; set; }
    public Guid? ParentId { get; set; }

    /// <summary>Full code, e.g. <c>1001.01.02</c>. Unique across the organization.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }
    public ChargeKind Kind { get; set; } = ChargeKind.Direct;
    public bool IsChargeable { get; set; } = true;
    public bool IsOpen { get; set; } = true;

    /// <summary>When true, only employees with a <see cref="ChargeAssignment"/> may charge it.</summary>
    public bool RequiresAssignment { get; set; } = true;

    public Guid? ItemId { get; set; }
    public int Order { get; set; }

    /// <summary>The CLIN this element's cost is billed and funded against (direct elements of contract projects).</summary>
    public Guid? ClinId { get; set; }
}

/// <summary>Allows an employee to charge a WBS element, optionally for a date range and labor category.</summary>
public sealed class ChargeAssignment : Entity
{
    public Guid WbsElementId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid? LaborCategoryId { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}
