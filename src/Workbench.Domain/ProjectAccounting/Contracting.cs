namespace Workbench.Domain.ProjectAccounting;

public sealed class Customer : Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }

    /// <summary>For example DoD, Civilian, Commercial, Prime contractor.</summary>
    public string? Category { get; set; }
}

public enum ContractType
{
    FirmFixedPrice,
    TimeAndMaterials,
    LaborHour,
    CostPlusFixedFee,
    CostPlusAwardFee,
    CostPlusIncentiveFee,
}

public enum ContractStatus
{
    Pending,
    Active,
    Closed,
}

public sealed class Contract : Entity, Companies.ICompanyOwned
{
    /// <summary>The legal company that holds the contract; its projects and invoices belong to the same company.</summary>
    public Guid CompanyId { get; set; } = Companies.Company.DefaultId;

    public Guid CustomerId { get; set; }
    public required string ContractNumber { get; set; }
    public required string Name { get; set; }
    public ContractType Type { get; set; }
    public ContractStatus Status { get; set; } = ContractStatus.Active;
    public bool IsPrime { get; set; } = true;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    /// <summary>Total contract value / ceiling.</summary>
    public decimal CeilingValue { get; set; }

    /// <summary>Amount funded (obligated) to date; spending past this is at risk.</summary>
    public decimal FundedValue { get; set; }
}

/// <summary>Bill rate for a labor category on a contract, effective from a date.</summary>
public sealed class ContractLaborRate : Entity
{
    public Guid ContractId { get; set; }
    public Guid LaborCategoryId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public decimal HourlyBillRate { get; set; }
}
