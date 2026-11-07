using Workbench.Contracts.Crm;

namespace Workbench.Tests.Crm;

/// <summary>A partner or competitor typed by name links to the account of that name, so the account page and reports see it.</summary>
public sealed class TeamLinkTests
{
    private static SaveOpportunityRequest Request(CrmKit kit, IReadOnlyList<SaveTeammate>? partners, IReadOnlyList<SaveCompetitor>? rivals) =>
        new("Cloud migration", AgencyAccountId: kit.PeoId, Teammates: partners, Competitors: rivals);

    [Fact]
    public async Task A_typed_name_links_to_the_matching_account_ignoring_case_and_spaces()
    {
        await using var kit = await CrmKit.CreateAsync();
        var booz = await kit.Accounts.CreateAccountAsync(new SaveCrmAccountRequest("Booz Allen Hamilton", CrmAccountKind.Partner));
        var o = await kit.NewAsync();

        var saved = await kit.Opportunities.UpdateAsync(o.Id, Request(kit,
            [new SaveTeammate(null, "  booz allen hamilton ", TeamingRole.Subcontractor, 30m)],
            [new SaveCompetitor(null, "BOOZ ALLEN HAMILTON")]));

        Assert.Equal(booz.Id, saved.Teammates.Single().AccountId);
        Assert.Equal("Booz Allen Hamilton", saved.Teammates.Single().Name);
        Assert.Equal(booz.Id, saved.Competitors.Single().AccountId);
    }

    [Fact]
    public async Task A_name_with_no_matching_account_stays_as_typed()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await kit.NewAsync();

        var saved = await kit.Opportunities.UpdateAsync(o.Id, Request(kit, [new SaveTeammate(null, "Acme Small Co", TeamingRole.Subcontractor, 10m)], null));

        Assert.Null(saved.Teammates.Single().AccountId);
        Assert.Equal("Acme Small Co", saved.Teammates.Single().Name);
    }

    [Fact]
    public async Task A_name_that_matches_two_accounts_is_not_guessed()
    {
        await using var kit = await CrmKit.CreateAsync();
        await kit.Accounts.CreateAccountAsync(new SaveCrmAccountRequest("Twin Corp", CrmAccountKind.Partner));
        await kit.Accounts.CreateAccountAsync(new SaveCrmAccountRequest("Twin Corp", CrmAccountKind.Competitor));
        var o = await kit.NewAsync();

        var saved = await kit.Opportunities.UpdateAsync(o.Id, Request(kit, null, [new SaveCompetitor(null, "Twin Corp")]));

        Assert.Null(saved.Competitors.Single().AccountId);
    }
}
