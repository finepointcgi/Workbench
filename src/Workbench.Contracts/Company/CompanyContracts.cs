namespace Workbench.Contracts.Company;

/// <summary>
/// What kind of company this organization is. It decides the words the product uses (see <see cref="CompanyTerms"/>)
/// and whether federal-only pages show by default. Government contractor is the default and changes nothing.
/// </summary>
public enum CompanyType
{
    GovernmentContractor = 0,
    ProfessionalServices = 1,
    EngineeringConstruction = 2,
}

/// <param name="Type">The company type.</param>
/// <param name="ShowGovernmentFeatures">Whether government contracting pages and fields (DCAA, CPSR, CAS, CMMC, SAM.gov leads, WAWF, incurred cost) show.</param>
/// <param name="GovernmentFeaturesOverridden">True when an admin set the switch; false when it follows the company type.</param>
/// <param name="IsSet">False until an admin has chosen a type (the setup checklist asks).</param>
/// <param name="CanEdit">Only Admins change it.</param>
public sealed record CompanyProfileDto(CompanyType Type, bool ShowGovernmentFeatures, bool GovernmentFeaturesOverridden, bool IsSet, bool CanEdit);

/// <param name="ShowGovernmentFeatures">Null makes the switch follow the company type.</param>
public sealed record UpdateCompanyProfileRequest(CompanyType Type, bool? ShowGovernmentFeatures);

/// <summary>The company type and the government-features switch. Readable by everyone signed in (menus and wording need it); changed by Admins.</summary>
public interface ICompanyProfileService
{
    Task<CompanyProfileDto> GetAsync(CancellationToken cancellationToken = default);

    Task<CompanyProfileDto> UpdateAsync(UpdateCompanyProfileRequest request, CancellationToken cancellationToken = default);
}
