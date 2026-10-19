using Workbench.Application;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Pricing;
using Workbench.Domain.Identity;
using Workbench.Tests.Identity;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Pricing;

/// <summary>The real registrations: pricing shows cost and indirect rates, so it's Finance, Admin and the Pricing role only.</summary>
public sealed class PricingPermissionTests
{
    private const string Pricer = "pricer@example.com";

    private static async Task<SeededHost> HostAsync()
    {
        var seeded = await SeededHost.CreateAsync();
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        db.Set<AppUser>().Add(new AppUser { Email = Pricer, DisplayName = "Riley", IsPricing = true });
        await db.SaveChangesAsync();
        return seeded;
    }

    [Theory]
    [InlineData(SeededHost.Viewer)]
    [InlineData(SeededHost.Owner)]
    [InlineData(SeededHost.Timekeeper)]
    public async Task Others_cannot_see_or_edit_prices(string email)
    {
        await using var seeded = await HostAsync();
        var pricing = seeded.As(email).Host.Get<IProposalPricingService>();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pricing.ListProposalsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pricing.CreateProposalAsync(new SaveProposalRequest("P-1", "Bid", PricingContractKind.FirmFixedPrice)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pricing.CalculateAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pricing.GetOptionsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pricing.ExportCostVolumeAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.Host.Get<IPricingOpportunityLookup>().SearchAsync(null));
    }

    [Theory]
    [InlineData(Pricer)]
    [InlineData(SeededHost.Finance)]
    [InlineData(SeededHost.Admin)]
    public async Task Pricing_finance_and_admins_price_proposals(string email)
    {
        await using var seeded = await HostAsync();
        var pricing = seeded.As(email).Host.Get<IProposalPricingService>();

        var proposal = await pricing.CreateProposalAsync(new SaveProposalRequest("P-1", "Bid", PricingContractKind.FirmFixedPrice, OpportunityId: Guid.NewGuid(),
            OpportunityName: "Recompete"));
        var result = await pricing.CalculateAsync(proposal.Models.Single().Id);

        Assert.Equal("Recompete", Assert.Single(await pricing.ListProposalsAsync()).OpportunityName);
        Assert.Equal(0m, result.Totals.Price);
        Assert.Empty(await seeded.Host.Get<IPricingOpportunityLookup>().SearchAsync("re"));
    }

    [Fact]
    public async Task Only_finance_and_admins_hand_off_a_won_price()
    {
        await using var seeded = await HostAsync();
        var pricing = seeded.As(Pricer).Host.Get<IProposalPricingService>();
        var proposal = await pricing.CreateProposalAsync(new SaveProposalRequest("P-1", "Bid", PricingContractKind.TimeAndMaterials));

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pricing.HandOffAsync(proposal.Models.Single().Id, new HandoffRequest(Guid.NewGuid(), Guid.NewGuid())));

        seeded.As(SeededHost.Finance);
        await Assert.ThrowsAsync<ArgumentException>(() => pricing.HandOffAsync(proposal.Models.Single().Id, new HandoffRequest(Guid.NewGuid(), Guid.NewGuid())));
    }

    [Fact]
    public void The_pricing_role_is_in_every_role_list()
    {
        Assert.Contains(OrgRoles.Pricing, OrgRoleChecks.All);
        Assert.True(OrgRoleChecks.Everything.HasFlag(OrgRoles.Pricing));
        Assert.True(OrgRoles.Pricing.CanPrice());
        Assert.True(OrgRoles.Finance.CanPrice());
        Assert.True(OrgRoles.Admin.CanPrice());
        Assert.False((OrgRoles.ProjectManager | OrgRoles.Timekeeper | OrgRoles.ResourceManager).CanPrice());
        Assert.Equal("Pricing", OrgRoleChecks.Label(OrgRoles.Pricing));
    }

    [Theory]
    [InlineData(OrgRoles.Pricing, true)]
    [InlineData(OrgRoles.Finance, true)]
    [InlineData(OrgRoles.Admin, true)]
    [InlineData(OrgRoles.ProjectManager, false)]
    [InlineData(OrgRoles.Employee, false)]
    public void The_menu_offers_pricing_to_those_who_can_price(OrgRoles roles, bool visible)
    {
        var access = new NavAccess(new CurrentUserInfo("someone@example.com", "Someone", roles, null));
        var entry = NavCatalog.Entries.Single(e => e.Href == "pricing");

        Assert.Equal(visible, entry.IsVisible(access));
    }
}
