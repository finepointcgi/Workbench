using Workbench.Application.OrgProfile;
using Workbench.Contracts;
using Workbench.Contracts.Company;
using Workbench.Tests.Time;

namespace Workbench.Tests.CompanyTypes;

/// <summary>The company type is stored per organization: government contractor until an admin chooses, and only an admin chooses.</summary>
public sealed class CompanyProfileServiceTests
{
    private static async Task<(TimeTestHost Host, CompanyProfileService Service)> NewAsync()
    {
        var host = await TimeTestHost.CreateAsync();
        var recorder = new Workbench.Application.ProjectAccounting.AuditRecorder(host.User, host.Clock);
        var service = new CompanyProfileService(host.Factory, new Workbench.Application.Identity.AccessControl(host.Factory, host.User), recorder, host.Clock);
        return (host, service);
    }

    [Fact]
    public async Task A_new_organization_is_a_government_contractor_with_the_federal_pages_showing()
    {
        var (_, service) = await NewAsync();

        var profile = await service.GetAsync();

        Assert.Equal(CompanyType.GovernmentContractor, profile.Type);
        Assert.True(profile.ShowGovernmentFeatures);
        Assert.False(profile.IsSet);
        Assert.Equal("contract", (await service.GetTermsAsync()).Get("contract"));
    }

    [Fact]
    public async Task Choosing_a_commercial_type_hides_government_features_unless_the_admin_turns_them_on()
    {
        var (_, service) = await NewAsync();

        var prof = await service.UpdateAsync(new UpdateCompanyProfileRequest(CompanyType.ProfessionalServices, null));
        Assert.True(prof.IsSet);
        Assert.False(prof.ShowGovernmentFeatures);
        Assert.False(prof.GovernmentFeaturesOverridden);
        Assert.Equal("agreement", (await service.GetTermsAsync()).Get("contract"));

        var on = await service.UpdateAsync(new UpdateCompanyProfileRequest(CompanyType.ProfessionalServices, true));
        Assert.True(on.ShowGovernmentFeatures);
        Assert.True(on.GovernmentFeaturesOverridden);

        var back = await service.UpdateAsync(new UpdateCompanyProfileRequest(CompanyType.GovernmentContractor, null));
        Assert.True(back.ShowGovernmentFeatures);
        Assert.Equal("contract", (await service.GetTermsAsync()).Get("contract"));
    }

    [Fact]
    public async Task Only_an_admin_changes_it_but_everyone_can_read_it()
    {
        var (host, service) = await NewAsync();
        await service.UpdateAsync(new UpdateCompanyProfileRequest(CompanyType.EngineeringConstruction, null));
        host.User.Email = "someone@example.com";

        var profile = await service.GetAsync();

        Assert.Equal(CompanyType.EngineeringConstruction, profile.Type);
        Assert.False(profile.CanEdit);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.UpdateAsync(new UpdateCompanyProfileRequest(CompanyType.ProfessionalServices, null)));
    }

    [Fact]
    public async Task Server_code_gets_the_terms_without_a_permission_check()
    {
        var (host, service) = await NewAsync();
        await service.UpdateAsync(new UpdateCompanyProfileRequest(CompanyType.ProfessionalServices, null));
        host.User.Email = "someone@example.com";

        var terms = await CompanyProfileService.LoadTermsAsync(host.Factory);

        Assert.Equal("Agreement C-1 is missing an authorized budget", terms.ReplaceTerms("Contract C-1 is missing a funded value"));
    }

    [Fact]
    public async Task An_undefined_type_is_refused()
    {
        var (_, service) = await NewAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(new UpdateCompanyProfileRequest((CompanyType)42, null)));
    }
}
