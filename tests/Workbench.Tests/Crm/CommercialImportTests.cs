using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Application.Imports.Commercial;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Imports;
using Workbench.Domain.Identity;
using Workbench.Tests.Imports;
using D = Workbench.Domain.Crm;
using OrgRoles = Workbench.Contracts.Identity.OrgRoles;

namespace Workbench.Tests.Crm;

/// <summary>
/// The HubSpot, Salesforce and plain-CSV imports of companies, contacts and deals: auto-mapping of each source's headers, stage and
/// amount translation, duplicates left alone unless the user chooses to update, and what a bad row reports.
/// </summary>
public sealed class CommercialImportTests
{
    private const string Seller = "seller@example.com";

    private static async Task<ImportHost> HostAsync(bool businessDevelopment = true)
    {
        var host = await ImportHost.CreateAsync();
        await using (var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            await RoleStore.AddUserAsync(db, new AppUser { Email = Seller, DisplayName = "Jamie Ortiz" },
                businessDevelopment ? OrgRoles.Finance | OrgRoles.BusinessDevelopment : OrgRoles.Finance);
            await db.SaveChangesAsync();
        }

        host.User.Email = Seller;
        return host;
    }

    private static Task<ImportJobDetailDto> ImportAsync(ImportHost host, ImportSource source, ImportKind kind, string file, ImportOptionsDto? options = null) =>
        host.ImportAsync(source, kind, [Samples.File($"samples/import/{Folder(source)}/{file}")], options);

    private static string Folder(ImportSource source) => source switch { ImportSource.HubSpot => "hubspot", ImportSource.Salesforce => "salesforce", _ => "generic" };

    private static void Imported(ImportJobDetailDto job)
    {
        Assert.True(job.Job.Status == ImportJobStatus.Imported, $"{job.Job.Status} {job.Job.Message} {string.Join("; ", job.Errors.Select(e => $"{e.Row} {e.Field}: {e.Message}"))}");
        Assert.True(job.Job.Counts.Errors == 0, string.Join("; ", job.Errors.Select(e => $"{e.Row} {e.Field}: {e.Message}")));
    }

    [Fact]
    public async Task HubSpot_companies_contacts_and_deals_come_across_with_stages_and_amounts()
    {
        await using var host = await HostAsync();

        var companies = await ImportAsync(host, ImportSource.HubSpot, ImportKind.CrmAccounts, "companies.csv");
        var contacts = await ImportAsync(host, ImportSource.HubSpot, ImportKind.CrmContacts, "contacts.csv");
        var deals = await ImportAsync(host, ImportSource.HubSpot, ImportKind.Opportunities, "deals.csv");

        Imported(companies);
        Imported(contacts);
        Imported(deals);
        Assert.Equal((4, 4, 6), (companies.Job.Counts.Creates, contacts.Job.Counts.Creates, deals.Job.Counts.Creates));
        Assert.Equal("Name", companies.Mapping.Fields.Single(f => f.Value == "Company name").Key);
        Assert.Equal("Website", companies.Mapping.Fields.Single(f => f.Value == "Company domain name" || f.Value == "Website URL").Key);

        var accounts = host.Get<ICrmAccountService>();
        var all = await accounts.ListAccountsAsync();
        Assert.Equal(CrmAccountKind.Partner, all.Single(a => a.Name == "Copperline Software").Kind);
        Assert.Equal(CrmAccountKind.Other, all.Single(a => a.Name == "Brightwater Logistics").Kind);
        var nadia = (await accounts.ListContactsAsync()).Single(c => c.Email == "nadia.petrov@brightwater-logistics.example");
        Assert.Equal((ContactRole.Executive, "Brightwater Logistics"), (nadia.Role, nadia.AccountName));
        Assert.Equal(ContactRole.ContractingSpecialist, (await accounts.ListContactsAsync()).Single(c => c.LastName == "Webb").Role);

        var pipeline = await host.Get<IOpportunityService>().ListAsync();
        Assert.Equal(6, pipeline.Count);
        var warehouse = pipeline.Single(o => o.Name == "Brightwater warehouse analytics");
        Assert.Equal((OpportunityStage.Capture, 185_000m, OpportunitySource.HubSpot, OpportunityKind.New), (warehouse.Stage, warehouse.EstimatedValue, warehouse.Source, warehouse.Kind));
        Assert.Equal(all.Single(a => a.Name == "Brightwater Logistics").Id, warehouse.AgencyAccountId);
        Assert.Equal(new DateOnly(2026, 12, 18), warehouse.EstimatedAwardDate);
        Assert.Equal(OpportunityStage.Proposal, pipeline.Single(o => o.Name.StartsWith("Lumen Harbor")).Stage);
        var checkout = pipeline.Single(o => o.Name == "Tallgrass checkout replatform");
        Assert.Equal((OpportunityStage.Submitted, OpportunityKind.Recompete), (checkout.Stage, checkout.Kind));
        var won = pipeline.Single(o => o.Name == "Tallgrass loyalty app discovery");
        Assert.Equal((OpportunityStage.Won, 48_000m, new DateOnly(2026, 10, 15)), (won.Stage, won.AwardedValue, won.DecidedOn));
        Assert.Equal(OpportunityStage.Lost, pipeline.Single(o => o.Name == "Brightwater fleet dashboard").Stage);
        Assert.Equal(OpportunityStage.Identified, pipeline.Single(o => o.Name == "Copperline reseller program").Stage);
        Assert.All(pipeline, o => Assert.Null(o.BpWbsElementId)); // Imported stages are history: no gates, no charge codes.
    }

    [Fact]
    public async Task Importing_the_same_export_again_leaves_every_record_as_it_is_and_lists_the_duplicates()
    {
        await using var host = await HostAsync();
        await ImportAsync(host, ImportSource.HubSpot, ImportKind.CrmAccounts, "companies.csv");
        await ImportAsync(host, ImportSource.HubSpot, ImportKind.CrmContacts, "contacts.csv");
        await ImportAsync(host, ImportSource.HubSpot, ImportKind.Opportunities, "deals.csv");

        var companies = await ImportAsync(host, ImportSource.HubSpot, ImportKind.CrmAccounts, "companies.csv");
        var contacts = await ImportAsync(host, ImportSource.HubSpot, ImportKind.CrmContacts, "contacts.csv");
        var deals = await ImportAsync(host, ImportSource.HubSpot, ImportKind.Opportunities, "deals.csv");

        Assert.Equal((0, 0, 4), (companies.Job.Counts.Creates, companies.Job.Counts.Updates, companies.Job.Counts.Skips));
        Assert.Equal((0, 0, 4), (contacts.Job.Counts.Creates, contacts.Job.Counts.Updates, contacts.Job.Counts.Skips));
        Assert.Equal((0, 0, 6), (deals.Job.Counts.Creates, deals.Job.Counts.Updates, deals.Job.Counts.Skips));
        Assert.Contains(deals.Errors, e => e.IsWarning && e.Message.Contains("already in Workbench"));
        Assert.All(deals.Plan, p => Assert.StartsWith("Duplicate", p.Summary));
        Assert.Equal(4, (await host.Get<ICrmAccountService>().ListAccountsAsync()).Count);
    }

    [Fact]
    public async Task A_changed_export_overwrites_nothing_until_update_is_chosen_and_then_only_the_filled_cells()
    {
        await using var host = await HostAsync();
        await ImportAsync(host, ImportSource.HubSpot, ImportKind.CrmAccounts, "companies.csv");
        await ImportAsync(host, ImportSource.HubSpot, ImportKind.Opportunities, "deals.csv");
        var csv = await File.ReadAllTextAsync(Samples.PathOf("samples/import/hubspot/deals.csv"));
        var edited = Samples.Csv("deals.csv", csv.Replace("185000,Presentation scheduled", "205000,Decision maker bought-in"));

        var careful = await host.ImportAsync(ImportSource.HubSpot, ImportKind.Opportunities, [edited]);
        var warehouse = (await host.Get<IOpportunityService>().ListAsync()).Single(o => o.Name == "Brightwater warehouse analytics");
        Assert.Equal((0, 0), (careful.Job.Counts.Creates, careful.Job.Counts.Updates));
        Assert.Equal((OpportunityStage.Capture, 185_000m), (warehouse.Stage, warehouse.EstimatedValue));

        var update = await host.ImportAsync(ImportSource.HubSpot, ImportKind.Opportunities, [edited], new ImportOptionsDto(UpdateExisting: true));
        Imported(update);
        Assert.Equal((1, 5), (update.Job.Counts.Updates, update.Job.Counts.Skips)); // The other five are unchanged.
        warehouse = (await host.Get<IOpportunityService>().ListAsync()).Single(o => o.Name == "Brightwater warehouse analytics");
        Assert.Equal((OpportunityStage.Proposal, 205_000m), (warehouse.Stage, warehouse.EstimatedValue));

        await host.Imports.RollBackAsync(update.Job.Id);
        warehouse = (await host.Get<IOpportunityService>().ListAsync()).Single(o => o.Name == "Brightwater warehouse analytics");
        Assert.Equal((OpportunityStage.Capture, 185_000m), (warehouse.Stage, warehouse.EstimatedValue));
    }

    [Fact]
    public async Task Salesforce_headers_and_stages_map_and_the_domain_matches_however_the_website_is_written()
    {
        await using var host = await HostAsync();
        var accounts = host.Get<ICrmAccountService>();
        await accounts.CreateAccountAsync(new SaveCrmAccountRequest("Redwood Mutual Insurance", CrmAccountKind.Other, Website: "https://redwoodmutual.example/"));

        var companies = await ImportAsync(host, ImportSource.Salesforce, ImportKind.CrmAccounts, "accounts.csv");
        var contacts = await ImportAsync(host, ImportSource.Salesforce, ImportKind.CrmContacts, "contacts.csv");
        var opportunities = await ImportAsync(host, ImportSource.Salesforce, ImportKind.Opportunities, "opportunities.csv");

        Imported(companies);
        Imported(contacts);
        Imported(opportunities);
        Assert.Equal((3, 1), (companies.Job.Counts.Creates, companies.Job.Counts.Skips)); // www.redwoodmutual.example is the website already on Redwood Mutual Insurance.
        Assert.Contains(companies.Plan, p => p.Action == ImportRowAction.Skip && p.Summary.Contains("website domain"));
        Assert.Equal(CrmAccountKind.Partner, (await accounts.ListAccountsAsync()).Single(a => a.Name == "Stonebridge Partners").Kind);
        Assert.Equal("Company", contacts.Mapping.Fields.Single(f => f.Value == "Account.Name").Key);
        Assert.Equal(ContactRole.Executive, (await accounts.ListContactsAsync()).Single(c => c.LastName == "Raman").Role);

        var deals = await host.Get<IOpportunityService>().ListAsync();
        Assert.Equal(6, deals.Count);
        Assert.All(deals, d => Assert.Equal(OpportunitySource.Salesforce, d.Source));
        Dictionary<string, OpportunityStage> stages = deals.ToDictionary(d => d.Name, d => d.Stage);
        Assert.Equal(OpportunityStage.Submitted, stages["Redwood claims workflow modernization"]); // Negotiation/Review
        Assert.Equal(OpportunityStage.Capture, stages["Granite Peak meter data platform"]); // Value Proposition
        Assert.Equal(OpportunityStage.Identified, stages["Harborview demand forecasting pilot"]); // Prospecting
        Assert.Equal(OpportunityStage.Won, stages["Redwood fraud analytics"]);
        Assert.Equal(OpportunityStage.Lost, stages["Granite Peak outage reporting"]);
        Assert.Equal(OpportunityStage.Proposal, stages["Harborview supplier portal"]); // Proposal/Price Quote
        var renewal = deals.Single(d => d.Name == "Redwood claims workflow modernization");
        Assert.Equal((OpportunityKind.Recompete, 640_000m, 90, "Jamie Ortiz is not used"), (renewal.Kind, renewal.EstimatedValue, renewal.Pwin, "Jamie Ortiz is not used"));
        Assert.Contains("Next step: Legal review of MSA", renewal.Description);
        Assert.Equal(accounts.ListAccountsAsync().Result.Single(a => a.Name == "Redwood Mutual Insurance").Id, renewal.AgencyAccountId);
    }

    [Fact]
    public async Task A_plain_csv_maps_by_common_names_and_reads_last_comma_first_names()
    {
        await using var host = await HostAsync();

        var companies = await ImportAsync(host, ImportSource.GenericCsv, ImportKind.CrmAccounts, "companies.csv");
        var contacts = await ImportAsync(host, ImportSource.GenericCsv, ImportKind.CrmContacts, "contacts.csv");
        var deals = await ImportAsync(host, ImportSource.GenericCsv, ImportKind.Opportunities, "deals.csv");

        Imported(companies);
        Imported(contacts);
        Imported(deals);
        Assert.Contains((await host.Get<ICrmAccountService>().ListContactsAsync()), c => c is { FirstName: "Ife", LastName: "Okoro" });
        var pipeline = await host.Get<IOpportunityService>().ListAsync();
        Assert.Equal((OpportunityStage.Proposal, 70, 72_000m, OpportunitySource.CsvImport),
            (pipeline.Single(o => o.Name.StartsWith("Orchard")).Stage, pipeline.Single(o => o.Name.StartsWith("Orchard")).Pwin, pipeline.Single(o => o.Name.StartsWith("Orchard")).EstimatedValue,
                pipeline.Single(o => o.Name.StartsWith("Orchard")).Source));
        Assert.Equal(OpportunityStage.Won, pipeline.Single(o => o.Name == "Vantage Freight rate-quote tool").Stage);
        // No ID column: the deal matches again on its name and company.
        var again = await ImportAsync(host, ImportSource.GenericCsv, ImportKind.Opportunities, "deals.csv");
        Assert.Equal((0, 4), (again.Job.Counts.Creates, again.Job.Counts.Skips));
    }

    [Fact]
    public async Task A_custom_stage_is_placed_by_its_probability_or_reported_when_there_is_none()
    {
        await using var host = await HostAsync();
        await ImportAsync(host, ImportSource.GenericCsv, ImportKind.CrmAccounts, "companies.csv");

        var placed = await host.PrepareAsync(ImportSource.GenericCsv, ImportKind.Opportunities,
            [Samples.Csv("d.csv", "Deal,Company,Stage,Probability\nSecurity review pending,Orchard Lane Studios,Legal hold,45%\n")]);
        var stuck = await host.PrepareAsync(ImportSource.GenericCsv, ImportKind.Opportunities,
            [Samples.Csv("d.csv", "Deal,Company,Stage\nSecurity review pending,Orchard Lane Studios,Legal hold\n")]);

        Assert.Equal(0, placed.Job.Counts.Errors);
        Assert.Contains(placed.Errors, e => e.IsWarning && e.Field == "Stage" && e.Message.Contains("Capture"));
        Assert.Contains(stuck.Errors, e => !e.IsWarning && e.Field == "Stage" && e.Message.Contains("Legal hold"));
    }

    [Fact]
    public async Task A_deal_for_an_unknown_company_keeps_the_name_and_a_contact_for_one_is_an_error()
    {
        await using var host = await HostAsync();

        var deal = await host.ImportAsync(ImportSource.GenericCsv, ImportKind.Opportunities, [Samples.Csv("d.csv", "Deal,Company,Value\nBig one,Nowhere Inc,\"$1,250,000\"\n")]);
        var contact = await host.PrepareAsync(ImportSource.GenericCsv, ImportKind.CrmContacts, [Samples.Csv("c.csv", "First Name,Last Name,Company\nAlex,Kim,Nowhere Inc\n")]);

        Imported(deal);
        Assert.Contains(deal.Errors, e => e.IsWarning && e.Field == "Company");
        var created = (await host.Get<IOpportunityService>().ListAsync()).Single();
        Assert.Equal(("Nowhere Inc", 1_250_000m, null), (created.AgencyName, created.EstimatedValue, created.AgencyAccountId));
        Assert.Contains(contact.Errors, e => e.Field == "Company" && e.Message.Contains("Nowhere Inc"));
    }

    [Fact]
    public async Task Finance_alone_cannot_import_deals_and_duplicates_in_the_file_are_errors()
    {
        await using var finance = await HostAsync(businessDevelopment: false);
        var refused = await finance.PrepareAsync(ImportSource.HubSpot, ImportKind.Opportunities, [Samples.File("samples/import/hubspot/deals.csv")]);
        Assert.Contains(refused.Errors, e => e.Message.Contains("Only business development and administrators can import deals"));

        await using var host = await HostAsync();
        var twice = await host.PrepareAsync(ImportSource.GenericCsv, ImportKind.CrmAccounts,
            [Samples.Csv("c.csv", "Company,Website\nAcme Co,acme.example\nAcme Corporation,https://www.acme.example/home\nOther,other.example\n")]);
        Assert.Contains(twice.Errors, e => !e.IsWarning && e.Row == 3 && e.Message.Contains("more than once"));
        Assert.Equal(2, twice.Job.Counts.Creates);
    }

    [Theory]
    [InlineData("Appointment scheduled", OpportunityStage.Identified)]
    [InlineData("Qualified to buy", OpportunityStage.Qualified)]
    [InlineData("Presentation scheduled", OpportunityStage.Capture)]
    [InlineData("Decision maker bought-in", OpportunityStage.Proposal)]
    [InlineData("Contract sent", OpportunityStage.Submitted)]
    [InlineData("Closed won", OpportunityStage.Won)]
    [InlineData("Closed lost", OpportunityStage.Lost)]
    [InlineData("Prospecting", OpportunityStage.Identified)]
    [InlineData("Needs Analysis", OpportunityStage.Qualified)]
    [InlineData("Id. Decision Makers", OpportunityStage.Capture)]
    [InlineData("Perception Analysis", OpportunityStage.Capture)]
    [InlineData("Proposal/Price Quote", OpportunityStage.Proposal)]
    [InlineData("Negotiation/Review", OpportunityStage.Submitted)]
    public void Default_stages_of_both_systems_translate_to_pipeline_stages(string stage, OpportunityStage expected) =>
        Assert.Equal(expected, CommercialStages.Parse(stage));

    [Theory]
    [InlineData("https://www.Example.com/about?x=1", "example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("HTTP://shop.example.co.uk/", "shop.example.co.uk")]
    [InlineData("not a site", null)]
    [InlineData("", null)]
    public void Websites_reduce_to_a_domain(string website, string? expected) => Assert.Equal(expected, CommercialImporter.Domain(website));

    [Fact]
    public async Task Each_source_offers_the_three_kinds_and_its_sample_files_exist()
    {
        await using var host = await HostAsync();
        var kinds = await host.Imports.ListKindsAsync();

        foreach (var source in new[] { ImportSource.HubSpot, ImportSource.Salesforce, ImportSource.GenericCsv })
        {
            var mine = kinds.Where(k => k.Source == source).ToList();
            Assert.Equal([ImportKind.CrmAccounts, ImportKind.CrmContacts, ImportKind.Opportunities], mine.Select(k => k.Kind).Order());
            Assert.All(mine, k => Assert.True(File.Exists(Samples.PathOf(k.SampleFile!)), k.SampleFile));
        }
    }
}
