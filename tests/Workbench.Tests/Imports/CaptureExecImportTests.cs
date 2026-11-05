using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Imports;
using Workbench.Domain.Identity;
using D = Workbench.Domain.Crm;
using OrgRoles = Workbench.Contracts.Identity.OrgRoles;

namespace Workbench.Tests.Imports;

/// <summary>The Capture Exec sample exports: companies, contacts and opportunities, then a re-import and a rollback.</summary>
public sealed class CaptureExecImportTests
{
    private const string Capture = "capture@example.com";

    private static async Task<ImportHost> HostAsync(bool businessDevelopment = true)
    {
        var host = await ImportHost.CreateAsync();
        await using (var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            await RoleStore.AddUserAsync(db, new AppUser { Email = Capture, DisplayName = "Casey Capture" },
                businessDevelopment ? OrgRoles.Finance | OrgRoles.BusinessDevelopment : OrgRoles.Finance);
            await db.SaveChangesAsync();
        }

        host.User.Email = Capture;
        return host;
    }

    private static Task<ImportJobDetailDto> ImportAsync(ImportHost host, ImportKind kind, string file) =>
        host.ImportAsync(ImportSource.CaptureExec, kind, [Samples.File($"samples/import/captureexec/{file}")]);

    private static void Imported(ImportJobDetailDto job)
    {
        Assert.True(job.Job.Status == ImportJobStatus.Imported, $"{job.Job.Status} {job.Job.Message} {string.Join("; ", job.Errors.Select(e => $"{e.Row} {e.Field}: {e.Message}"))}");
        Assert.Equal(0, job.Job.Counts.Errors);
    }

    [Fact]
    public async Task Companies_contacts_and_opportunities_come_across_with_stages_and_outcomes()
    {
        await using var host = await HostAsync();

        var companies = await ImportAsync(host, ImportKind.CrmAccounts, "companies.csv");
        var contacts = await ImportAsync(host, ImportKind.CrmContacts, "contacts.csv");
        var opportunities = await ImportAsync(host, ImportKind.Opportunities, "opportunities.csv");

        Imported(companies);
        Imported(contacts);
        Imported(opportunities);
        Assert.Equal((7, 4, 4), (companies.Job.Counts.Creates, contacts.Job.Counts.Creates, opportunities.Job.Counts.Creates));

        var accounts = host.Get<ICrmAccountService>();
        var all = await accounts.ListAccountsAsync();
        var cisa = all.Single(a => a.Acronym == "CISA");
        Assert.Equal(CrmAccountKind.Agency, cisa.Kind);
        Assert.Equal("Department of Homeland Security", cisa.ParentName);
        Assert.Equal(CrmAccountKind.Office, all.Single(a => a.Acronym == "TAC").Kind);
        Assert.Equal(CrmAccountKind.Partner, all.Single(a => a.Name == "Apex Federal Solutions").Kind);
        Assert.Equal(ContactRole.ContractingOfficer, (await accounts.ListContactsAsync(cisa.Id)).Single(c => c.LastName == "Whitfield").Role);

        var pipeline = await host.Get<IOpportunityService>().ListAsync();
        var cdm = pipeline.Single(o => o.Name == "CISA CDM Dashboard Sustainment");
        Assert.Equal((OpportunityStage.Capture, 35, 80, 60, OpportunityKind.Recompete, OpportunitySource.CaptureExec), (cdm.Stage, cdm.Pwin, cdm.Pgo, cdm.PeriodOfPerformanceMonths!.Value, cdm.Kind, cdm.Source));
        Assert.Equal(cisa.Id, cdm.AgencyAccountId);
        Assert.Contains(cdm.Competitors, c => c.Name == "Ironclad Systems" && c.IsIncumbent);
        Assert.Null(cdm.BpWbsElementId); // Imported stages don't create charge codes.

        var cloud = pipeline.Single(o => o.Name == "VA Enterprise Cloud Migration Support");
        Assert.Equal((OpportunityStage.Proposal, 45, 90, SetAside.ServiceDisabledVeteranOwned), (cloud.Stage, cloud.Pwin, cloud.Pgo, cloud.SetAside));

        var lost = pipeline.Single(o => o.Name == "VA Help Desk Modernization");
        Assert.Equal((OpportunityStage.Lost, OutcomeReason.Price, "Ironclad Systems", new DateOnly(2026, 8, 14)), (lost.Stage, lost.OutcomeReason, lost.AwardedTo, lost.DecidedOn));
    }

    [Fact]
    public async Task Importing_the_same_export_again_changes_nothing_and_an_edited_one_updates_in_place()
    {
        await using var host = await HostAsync();
        await ImportAsync(host, ImportKind.CrmAccounts, "companies.csv");
        await ImportAsync(host, ImportKind.Opportunities, "opportunities.csv");

        var again = await ImportAsync(host, ImportKind.Opportunities, "opportunities.csv");
        Imported(again);
        Assert.Equal((0, 0, 4), (again.Job.Counts.Creates, again.Job.Counts.Updates, again.Job.Counts.Skips));

        var csv = await File.ReadAllTextAsync(Samples.PathOf("samples/import/captureexec/opportunities.csv"));
        var edited = csv.Replace("CE-1042,VA Enterprise Cloud Migration Support,36C10B27Q0042,VA Technology Acquisition Center,541519,SDVOSB,T4NG2,FFP,New,22000000,Proposal",
            "CE-1042,VA Enterprise Cloud Migration Support,36C10B27Q0042,VA Technology Acquisition Center,541519,SDVOSB,T4NG2,FFP,New,24000000,Submitted");
        var update = await host.ImportAsync(ImportSource.CaptureExec, ImportKind.Opportunities, [Samples.Csv("opportunities.csv", edited)]);
        Imported(update);
        Assert.Equal(1, update.Job.Counts.Updates);
        var cloud = (await host.Get<IOpportunityService>().ListAsync()).Single(o => o.SolicitationNumber == "36C10B27Q0042");
        Assert.Equal((OpportunityStage.Submitted, 24_000_000m), (cloud.Stage, cloud.EstimatedValue));
        Assert.Contains(await host.Get<IOpportunityService>().GetHistoryAsync(cloud.Id), h => h.To == OpportunityStage.Submitted && h.Note == "Imported from Capture Exec");

        await host.Imports.RollBackAsync(update.Job.Id);
        cloud = await host.Get<IOpportunityService>().GetAsync(cloud.Id);
        Assert.Equal((OpportunityStage.Proposal, 22_000_000m), (cloud.Stage, cloud.EstimatedValue));
    }

    [Fact]
    public async Task Rolling_back_the_first_import_removes_the_opportunities()
    {
        await using var host = await HostAsync();
        await ImportAsync(host, ImportKind.CrmAccounts, "companies.csv");
        var job = await ImportAsync(host, ImportKind.Opportunities, "opportunities.csv");

        await host.Imports.RollBackAsync(job.Job.Id);

        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.False(await db.Set<D.Opportunity>().AnyAsync());
    }

    [Fact]
    public async Task Finance_alone_cannot_import_opportunities()
    {
        await using var host = await HostAsync(businessDevelopment: false);
        await ImportAsync(host, ImportKind.CrmAccounts, "companies.csv");

        var job = await host.PrepareAsync(ImportSource.CaptureExec, ImportKind.Opportunities, [Samples.File("samples/import/captureexec/opportunities.csv")]);

        Assert.Contains(job.Errors, e => e.Message.Contains("Only business development and administrators can import opportunities"));
    }

    [Fact]
    public async Task Unknown_stages_and_contacts_without_a_company_are_row_errors()
    {
        await using var host = await HostAsync();
        await ImportAsync(host, ImportKind.CrmAccounts, "companies.csv");

        var opportunities = await host.PrepareAsync(ImportSource.CaptureExec, ImportKind.Opportunities,
            [Samples.Csv("o.csv", "Opportunity Name,Status,PWin\nMystery,Pondering,140\n")]);
        var contacts = await host.PrepareAsync(ImportSource.CaptureExec, ImportKind.CrmContacts,
            [Samples.Csv("c.csv", "First Name,Last Name,Company\nAlex,Kim,Nowhere Inc\n")]);

        Assert.Contains(opportunities.Errors, e => e.Field == "Stage" && e.Message.Contains("Pondering"));
        Assert.Contains(opportunities.Errors, e => e.Field == "Pwin");
        Assert.Contains(contacts.Errors, e => e.Field == "Company" && e.Message.Contains("Nowhere Inc"));
    }
}
