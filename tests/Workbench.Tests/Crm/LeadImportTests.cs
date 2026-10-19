using Workbench.Application.Crm;
using Workbench.Contracts;
using Workbench.Contracts.Crm;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Crm;

/// <summary>SAM.gov saved searches (the sample client), CSV import, de-duplication by solicitation number, and lead review.</summary>
public sealed class LeadImportTests
{
    private static SaveLeadSearchRequest ItServices(params SetAside[] setAsides) =>
        new("IT services", ["5415"], [], setAsides, null, 30);

    [Fact]
    public async Task The_sample_client_filters_like_the_api()
    {
        var sam = new FakeSamGovClient();
        var all = await sam.SearchAsync(new SamSearchQuery(CrmKit.Today.AddDays(-30), CrmKit.Today, [], [], [], null), CancellationToken.None);
        Assert.Equal(FakeSamGovClient.Samples.Count, all.Count);

        var it = await sam.SearchAsync(new SamSearchQuery(CrmKit.Today.AddDays(-30), CrmKit.Today, ["5415"], [SetAside.ServiceDisabledVeteranOwned], [], null), CancellationToken.None);
        Assert.All(it, n => Assert.StartsWith("5415", n.NaicsCode));
        Assert.All(it, n => Assert.Equal(SetAside.ServiceDisabledVeteranOwned, n.SetAside));
        Assert.Equal(2, it.Count);

        var army = await sam.SearchAsync(new SamSearchQuery(CrmKit.Today.AddDays(-7), CrmKit.Today, [], [], ["army"], "cloud"), CancellationToken.None);
        Assert.Equal("Enterprise Cloud Migration Support Services", Assert.Single(army).Title);
        Assert.All(all, n => Assert.True(n.ResponseDueOn > n.PostedOn));
    }

    [Theory]
    [InlineData("SBA", SetAside.SmallBusiness)]
    [InlineData("Total Small Business Set-Aside (FAR 19.5)", SetAside.SmallBusiness)]
    [InlineData("8AN", SetAside.EightA)]
    [InlineData("Service-Disabled Veteran-Owned Small Business (SDVOSB) Set-Aside (FAR 19.14)", SetAside.ServiceDisabledVeteranOwned)]
    [InlineData("", SetAside.None)]
    [InlineData("Something else", SetAside.Other)]
    public void Sam_set_aside_codes_and_descriptions_are_understood(string text, SetAside expected) =>
        Assert.Equal(expected, SamSetAsides.Parse(text));

    [Fact]
    public async Task A_saved_search_creates_new_leads_once_and_skips_solicitations_already_in_the_pipeline()
    {
        await using var kit = await CrmKit.CreateAsync();
        // Already pursuing the cloud migration (same solicitation, written differently).
        await kit.NewAsync("Cloud migration", solicitation: "w91quz 27 r 0012");
        var search = await kit.As(CrmKit.Bd).Leads.CreateSearchAsync(ItServices());

        var first = await kit.Leads.RunSearchAsync(search.Id);
        Assert.Equal(5, first.Found);
        Assert.Equal(4, first.Created);
        Assert.Equal(1, first.Duplicates);
        var leads = await kit.Leads.ListLeadsAsync();
        Assert.Equal(4, leads.Count);
        Assert.DoesNotContain(leads, l => l.SolicitationNumber == "W91QUZ-27-R-0012");
        Assert.All(leads, l => Assert.Equal("IT services", l.SavedSearchName));

        var again = await kit.Leads.RunSearchAsync(search.Id);
        Assert.Equal(0, again.Created);
        Assert.Equal(5, again.Duplicates);
        Assert.Equal(0, (await kit.Leads.ListSearchesAsync()).Single().LastRunNewLeads);

        // Dismissed leads stay known: they don't come back.
        await kit.Leads.DismissAsync(leads[0].Id);
        Assert.Equal(0, (await kit.Leads.RunSearchAsync(search.Id)).Created);
        Assert.Equal(3, (await kit.Leads.ListLeadsAsync()).Count);
    }

    [Fact]
    public async Task Accepting_a_lead_makes_an_identified_opportunity_matched_to_the_agency_account()
    {
        await using var kit = await CrmKit.CreateAsync();
        var search = await kit.As(CrmKit.Bd).Leads.CreateSearchAsync(new SaveLeadSearchRequest("Army", [], ["army"], [], null, 30));
        await kit.Leads.RunSearchAsync(search.Id);
        var lead = (await kit.Leads.ListLeadsAsync()).Single(l => l.SolicitationNumber == "W91QUZ-27-R-0012");

        var opportunity = await kit.Leads.AcceptAsync(lead.Id, new AcceptLeadRequest());
        Assert.Equal(OpportunityStage.Identified, opportunity.Stage);
        Assert.Equal(OpportunitySource.SamGov, opportunity.Source);
        Assert.Equal("W91QUZ-27-R-0012", opportunity.SolicitationNumber);
        Assert.Equal(kit.PeoId, opportunity.AgencyAccountId);
        Assert.Equal(48_000_000m, opportunity.EstimatedValue);
        Assert.Equal(SetAside.SmallBusiness, opportunity.SetAside);
        Assert.Equal(lead.ResponseDueOn, opportunity.ProposalDueOn);
        Assert.Equal(CrmKit.Bd, opportunity.CaptureManagerEmail);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Leads.AcceptAsync(lead.Id, new AcceptLeadRequest()));
        Assert.Equal(LeadStatus.Accepted, (await kit.Leads.ListLeadsAsync(null)).Single(l => l.Id == lead.Id).Status);
        Assert.Equal(0, (await kit.Leads.RunSearchAsync(search.Id)).Created);
    }

    [Fact]
    public async Task Csv_import_reads_the_columns_dedupes_and_reports_bad_rows()
    {
        await using var kit = await CrmKit.CreateAsync();
        await kit.NewAsync("Already pursuing", solicitation: "ABC-1");
        const string csv = """
            Title,Solicitation Number,Notice Id,Agency,NAICS,Set-Aside,Posted Date,Response Date,Estimated Value
            "Data platform, phase 2",DEF-2,n-2,NIH,541511,8(a),2026-10-01,2026-11-15,"$1,250,000"
            Duplicate of the pipeline,ABC 1,n-1,Army,541512,SBA,2026-10-02,,
            Same solicitation twice,DEF-2,n-3,NIH,541511,,,,
            ,GHI-3,n-4,VA,,,,,
            Bad value,JKL-4,n-5,VA,541511,,,,lots
            Helpdesk,MNO-5,n-6,VA,541513,SDVOSBC,10/15/2026,11/30/2026,
            """;

        var result = await kit.As(CrmKit.Bd).Leads.ImportCsvAsync(csv);
        Assert.Equal(6, result.Found);
        Assert.Equal(2, result.Created);
        Assert.Equal(2, result.Duplicates);
        Assert.Equal(2, result.Errors.Count);

        var leads = await kit.Leads.ListLeadsAsync();
        var data = leads.Single(l => l.SolicitationNumber == "DEF-2");
        Assert.Equal("Data platform, phase 2", data.Title);
        Assert.Equal(SetAside.EightA, data.SetAside);
        Assert.Equal(1_250_000m, data.EstimatedValue);
        Assert.Equal(new DateOnly(2026, 11, 15), data.ResponseDueOn);
        Assert.Equal(OpportunitySource.CsvImport, data.Source);
        Assert.Equal(new DateOnly(2026, 11, 30), leads.Single(l => l.SolicitationNumber == "MNO-5").ResponseDueOn);

        Assert.Equal(0, (await kit.Leads.ImportCsvAsync(csv)).Created);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Leads.ImportCsvAsync("Agency,NAICS\nVA,541511"));
    }

    [Fact]
    public async Task Only_business_development_runs_imports_and_reviews_leads()
    {
        await using var kit = await CrmKit.CreateAsync();
        var search = await kit.As(CrmKit.Bd).Leads.CreateSearchAsync(ItServices());
        await kit.Leads.RunSearchAsync(search.Id);

        kit.As(Finance);
        Assert.NotEmpty(await kit.Leads.ListLeadsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Leads.RunSearchAsync(search.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Leads.ImportCsvAsync("Title\nX"));
        var leadId = (await kit.Leads.ListLeadsAsync())[0].Id;
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Leads.AcceptAsync(leadId, new AcceptLeadRequest()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Viewer).Leads.ListSearchesAsync());
    }

    [Fact]
    public async Task The_real_client_reads_sam_gov_json()
    {
        var handler = new StubHandler("""
            {"totalRecords":1,"opportunitiesData":[{"noticeId":"abc123","title":"Cloud support","solicitationNumber":"W91-1",
             "fullParentPathName":"DEPT OF DEFENSE.DEPT OF THE ARMY.PEO EIS","postedDate":"2026-10-15","type":"Solicitation",
             "typeOfSetAside":"SBA","responseDeadLine":"2026-11-20T17:00:00-05:00","naicsCode":"541512","uiLink":"https://sam.gov/opp/abc123/view",
             "placeOfPerformance":{"city":{"name":"Fort Belvoir"},"state":{"code":"VA"}},"award":{"amount":"1500000"}}]}
            """);
        var client = new SamGovOpportunitiesClient(new HttpClient(handler), "key-123", "https://example.test/search");

        var notice = Assert.Single(await client.SearchAsync(new SamSearchQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 21), ["541512"], [SetAside.SmallBusiness], ["army"], "cloud"),
            CancellationToken.None));

        Assert.Equal("DEPT OF DEFENSE", notice.Agency);
        Assert.Equal("DEPT OF THE ARMY / PEO EIS", notice.Office);
        Assert.Equal(SetAside.SmallBusiness, notice.SetAside);
        Assert.Equal(new DateOnly(2026, 11, 20), notice.ResponseDueOn);
        Assert.Equal("Fort Belvoir, VA", notice.PlaceOfPerformance);
        Assert.Equal(1_500_000m, notice.EstimatedValue);
        var url = handler.Requests.Single();
        Assert.Contains("api_key=key-123", url);
        Assert.Contains("postedFrom=10/01/2026", url);
        Assert.Contains("ncode=541512", url);
        Assert.Contains("typeOfSetAside=SBA", url);
        Assert.Contains("title=cloud", url);

        var failing = new SamGovOpportunitiesClient(new HttpClient(new StubHandler("{}", System.Net.HttpStatusCode.Forbidden)), "bad");
        await Assert.ThrowsAsync<ArgumentException>(() => failing.SearchAsync(new SamSearchQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 21), [], [], [], null), CancellationToken.None));
    }

    private sealed class StubHandler(string json, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
