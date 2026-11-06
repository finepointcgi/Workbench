using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Assistant;
using Workbench.Application.Crm;
using Workbench.Contracts;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Crm;
using Workbench.Domain.Assistant;
using static Workbench.Tests.Identity.SeededHost;
using D = Workbench.Domain.Crm;

namespace Workbench.Tests.Crm;

/// <summary>Lead fit: the rules, scoring through the service (assistant off and on), and who may score.</summary>
public sealed class LeadFitTests
{
    private const string Profile = "NAICS: 541511, 541512, 541519\nStatus: small business, SDVOSB\nWe build and run cloud migration and DevSecOps platforms and cybersecurity operations for the Army and DHS.";
    private static readonly DateOnly Today = CrmKit.Today;

    private static D.OpportunityLead Lead(string title, string? naics, D.SetAside setAside = D.SetAside.None, int? dueInDays = 30, string? description = null) =>
        new() { Title = title, NaicsCode = naics, SetAside = setAside, ResponseDueOn = dueInDays is { } d ? Today.AddDays(d) : null, Description = description };

    [Fact]
    public void Rules_score_a_close_match_high_and_a_stranger_low()
    {
        var good = LeadFitRules.Score(Profile, Lead("Enterprise Cloud Migration Support", "541512", D.SetAside.SmallBusiness, 45), Today)!;
        var poor = LeadFitRules.Score(Profile, Lead("Facilities Maintenance, Building 12", "561210", D.SetAside.HubZone, 30), Today)!;

        Assert.True(good.Score >= 70, good.Reason);
        Assert.True(poor.Score < 40, poor.Reason);
        Assert.Contains("NAICS 541512 is one of yours", good.Reason);
        Assert.Contains("HUBZone set-aside, which your profile doesn't claim", poor.Reason);
    }

    [Fact]
    public void Rules_give_credit_for_the_same_naics_group_and_penalize_a_passed_date()
    {
        var group = LeadFitRules.Score(Profile, Lead("Help desk", "541513", dueInDays: 30), Today)!;
        Assert.Contains("close to one of yours", group.Reason);

        var open = LeadFitRules.Score(Profile, Lead("Cloud migration", "541512", dueInDays: 30), Today)!;
        var passed = LeadFitRules.Score(Profile, Lead("Cloud migration", "541512", dueInDays: -2), Today)!;
        var soon = LeadFitRules.Score(Profile, Lead("Cloud migration", "541512", dueInDays: 5), Today)!;
        Assert.True(open.Score > soon.Score && soon.Score > passed.Score);
        Assert.Contains("response date has passed", passed.Reason);
    }

    [Fact]
    public void No_profile_means_no_score_and_the_score_stays_between_0_and_100()
    {
        Assert.Null(LeadFitRules.Score(null, Lead("x", "541512"), Today));
        Assert.Null(LeadFitRules.Score("  ", Lead("x", "541512"), Today));
        var max = LeadFitRules.Score(Profile, Lead("Cloud migration DevSecOps cybersecurity operations platforms", "541512", D.SetAside.SmallBusiness, 60, "Army cloud migration and DevSecOps platforms"), Today)!;
        Assert.InRange(max.Score, 0, 100);
    }

    [Fact]
    public async Task A_search_scores_new_leads_by_rules_when_the_assistant_is_off_and_the_list_ranks_them()
    {
        await using var kit = await CrmKit.CreateAsync();
        kit.As(CrmKit.Bd);
        await kit.Pipeline.SetCompanyProfileAsync(Profile);
        var search = await kit.Leads.CreateSearchAsync(new SaveLeadSearchRequest("All IT", [], [], [], null, 30));

        await kit.Leads.RunSearchAsync(search.Id);
        var leads = await kit.Leads.ListLeadsAsync();

        Assert.NotEmpty(leads);
        Assert.All(leads, l =>
        {
            Assert.NotNull(l.FitScore);
            Assert.False(string.IsNullOrWhiteSpace(l.FitReason));
            Assert.False(l.FitByAssistant);
        });
        var cloud = leads.Single(l => l.Title.StartsWith("Enterprise Cloud", StringComparison.Ordinal));
        var facilities = leads.Single(l => l.Title.StartsWith("Facilities", StringComparison.Ordinal));
        Assert.True(cloud.FitScore > facilities.FitScore);
    }

    [Fact]
    public async Task Without_a_profile_nothing_is_scored()
    {
        await using var kit = await CrmKit.CreateAsync();
        kit.As(CrmKit.Bd);
        var search = await kit.Leads.CreateSearchAsync(new SaveLeadSearchRequest("All IT", [], [], [], null, 30));
        await kit.Leads.RunSearchAsync(search.Id);

        Assert.Equal(0, (await kit.Leads.ScoreLeadsAsync()).Scored);
        Assert.All(await kit.Leads.ListLeadsAsync(), l => Assert.Null(l.FitScore));
    }

    [Fact]
    public async Task Scoring_leads_already_scored_needs_rescoreAll_and_follows_a_changed_profile()
    {
        await using var kit = await CrmKit.CreateAsync();
        kit.As(CrmKit.Bd);
        await kit.Pipeline.SetCompanyProfileAsync(Profile);
        var search = await kit.Leads.CreateSearchAsync(new SaveLeadSearchRequest("All IT", [], [], [], null, 30));
        await kit.Leads.RunSearchAsync(search.Id);
        var before = (await kit.Leads.ListLeadsAsync()).ToDictionary(l => l.Id, l => l.FitScore);

        Assert.Equal(0, (await kit.Leads.ScoreLeadsAsync()).Scored);

        await kit.Pipeline.SetCompanyProfileAsync("NAICS: 561210\nStatus: HUBZone\nFacilities maintenance and janitorial services for GSA buildings.");
        var result = await kit.Leads.ScoreLeadsAsync(rescoreAll: true);

        Assert.Equal(before.Count, result.Scored);
        var after = await kit.Leads.ListLeadsAsync();
        Assert.True(after.Single(l => l.Title.StartsWith("Facilities", StringComparison.Ordinal)).FitScore > before.Single(b => after.Any(a => a.Id == b.Key && a.Title.StartsWith("Facilities", StringComparison.Ordinal))).Value);
    }

    [Fact]
    public async Task The_assistant_refines_scores_when_it_is_on_and_the_interaction_is_audited()
    {
        await using var kit = await CrmKit.CreateAsync(s => s.Configure<AssistantOptions>(o => o.EnableFakeProvider = true));
        kit.As(Admin);
        await kit.Get<IAssistantAdminService>().SaveSettingsAsync(new SaveAssistantSettingsRequest(true, AssistantOptions.FakeConnection, null, AcceptNotice: true));
        kit.As(CrmKit.Bd);
        await kit.Pipeline.SetCompanyProfileAsync(Profile);
        var search = await kit.Leads.CreateSearchAsync(new SaveLeadSearchRequest("All IT", [], [], [], null, 30));

        await kit.Leads.RunSearchAsync(search.Id);

        var leads = await kit.Leads.ListLeadsAsync();
        Assert.All(leads, l =>
        {
            Assert.NotNull(l.FitScore);
            Assert.True(l.FitByAssistant);
        });
        await using var db = await kit.Factory.CreateAsync();
        var interaction = await db.Set<AssistantInteraction>().AsNoTracking().SingleAsync(i => i.Kind == AssistantInteractionKind.LeadFit);
        Assert.Equal(AssistantOutcome.Answered, interaction.Outcome);
    }

    [Fact]
    public void The_assistants_answer_is_validated_before_it_is_used()
    {
        var known = Guid.NewGuid();
        var invented = Guid.NewGuid();
        var json = $$"""{"fits":[{"id":"{{known}}","score":140,"reason":"  Strong NAICS match.  "},{"id":"{{invented}}","score":50,"reason":"made up"},{"id":"{{known}}","score":"high","reason":"bad"}]}""";

        var fits = LeadFitScorer.TryParse(json, new HashSet<Guid> { known })!;

        var fit = Assert.Single(fits);
        Assert.Equal(known, fit.Key);
        Assert.Equal(100, fit.Value.Score);
        Assert.Equal("Strong NAICS match.", fit.Value.Reason);
        Assert.Null(LeadFitScorer.TryParse("not json", new HashSet<Guid> { known }));
        Assert.Null(LeadFitScorer.TryParse("""{"fits":[]}""", new HashSet<Guid> { known }));
    }

    [Fact]
    public async Task Only_business_development_scores_leads_and_sets_the_profile()
    {
        await using var kit = await CrmKit.CreateAsync();
        kit.As(Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Leads.ScoreLeadsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Pipeline.SetCompanyProfileAsync("x"));
        kit.As(CrmKit.Bd);
        Assert.Equal("x", (await kit.Pipeline.SetCompanyProfileAsync(" x ")).CompanyProfile);
        Assert.Null((await kit.Pipeline.SetCompanyProfileAsync("  ")).CompanyProfile);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Pipeline.SetCompanyProfileAsync(new string('x', 8_001)));
    }
}
