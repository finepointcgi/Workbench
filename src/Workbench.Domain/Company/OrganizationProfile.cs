namespace Workbench.Domain.Company;

/// <summary>
/// The organization's company type and government-features switch, one row per tenant database. Missing means the
/// defaults: a government contractor with the federal pages showing.
/// </summary>
public sealed class OrganizationProfile : Entity
{
    /// <summary>Name of a <c>Contracts.Company.CompanyType</c>.</summary>
    public string CompanyType { get; set; } = "GovernmentContractor";

    /// <summary>Null follows the company type; true or false is an admin's explicit choice.</summary>
    public bool? ShowGovernmentFeatures { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }
}
