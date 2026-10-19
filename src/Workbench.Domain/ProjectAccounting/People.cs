namespace Workbench.Domain.ProjectAccounting;

/// <summary>An organizational unit (division, department) used for reporting and approvals.</summary>
public sealed class OrgUnit : Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }
}

/// <summary>A person who records time. Linked to a sign-in account by <see cref="Email"/>.</summary>
public sealed class Employee : Entity, Companies.ICompanyOwned
{
    /// <summary>The employee's home company: labor cost is owed (accrued payroll) there, wherever the hours are charged.</summary>
    public Guid CompanyId { get; set; } = Companies.Company.DefaultId;

    public required string EmployeeNumber { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public Guid? OrgUnitId { get; set; }

    /// <summary>Approves this employee's timesheets and expenses.</summary>
    public Guid? SupervisorId { get; set; }

    /// <summary>Default labor category when an assignment doesn't specify one.</summary>
    public Guid? DefaultLaborCategoryId { get; set; }

    public DateOnly HireDate { get; set; }
    public DateOnly? TerminationDate { get; set; }
    public decimal StandardHoursPerWeek { get; set; } = 40m;
    public bool IsExempt { get; set; } = true;

    public bool IsActiveOn(DateOnly date) => date >= HireDate && (TerminationDate is null || date <= TerminationDate);
}

/// <summary>A labor category (e.g. "Senior Engineer II") used for budgets and contract bill rates.</summary>
public sealed class LaborCategory : Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>An employee's hourly cost, effective from a date until the next rate starts.</summary>
public sealed class EmployeeCostRate : Entity
{
    public Guid EmployeeId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public decimal HourlyCost { get; set; }
}
