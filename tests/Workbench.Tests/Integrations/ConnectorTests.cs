using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Integrations;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Accounting;
using Workbench.Domain.Companies;
using Workbench.Domain.Integrations;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Tests.Integrations;

public sealed class ConnectorTests
{
    private const string TeamsUrl = "https://acme.webhook.office.com/webhookb2/secret-path-123";

    [Fact]
    public async Task The_four_prebuilt_connectors_are_offered()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var kinds = await host.Get<IConnectorService>().ListKindsAsync();
        Assert.Equal(["teams", "slack", "gl-export", "report-export"], kinds.Select(k => k.Kind));
        Assert.Equal(ConnectorMode.Events, kinds.Single(k => k.Kind == "teams").Mode);
        Assert.Equal(ConnectorMode.Schedule, kinds.Single(k => k.Kind == "gl-export").Mode);
    }

    [Fact]
    public async Task Teams_posts_subscribed_events_as_cards_and_keeps_the_webhook_url_secret()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var connectors = host.Get<IConnectorService>();
        var teams = await connectors.CreateAsync(new SaveConnectorRequest("teams", "Finance channel", null,
            new Dictionary<string, string?> { ["webhookUrl"] = TeamsUrl }, ["project.created"]));
        Assert.Equal(["webhookUrl"], teams.SecretsSet);
        Assert.DoesNotContain(TeamsUrl, JsonSerializer.Serialize(teams));

        await host.Get<IProjectService>().CreateProjectAsync(new SaveProjectRequest("5005", "Hangar", null, null, ProjectState.Active, null, null));
        await host.Get<IntegrationDispatcher>().RunOnceAsync(CancellationToken.None);

        var sent = Assert.Single(host.Transport.Sent);
        Assert.Equal(new Uri(TeamsUrl), sent.Url);
        using (var card = JsonDocument.Parse(sent.Body))
        {
            var content = card.RootElement.GetProperty("attachments")[0].GetProperty("content");
            Assert.Equal("AdaptiveCard", content.GetProperty("type").GetString());
            Assert.Equal("Project created: 5005 Hangar", content.GetProperty("body")[0].GetProperty("text").GetString());
        }

        // The run log and the delivery log show the host, never the secret path.
        var run = Assert.Single(await connectors.ListRunsAsync(teams.Id));
        Assert.Equal(ConnectorRunState.Succeeded, run.Status);
        Assert.Equal("project.created", run.EventType);
        var delivery = Assert.Single(await host.Get<IWebhookService>().ListDeliveriesAsync(new DeliveryQuery(ConnectorId: teams.Id)));
        var detail = await host.Get<IWebhookService>().GetDeliveryAsync(delivery.Id);
        Assert.DoesNotContain("secret-path-123", Assert.Single(detail.Attempts).RequestUrl);

        // Saving without the secret keeps it; the stored value is encrypted.
        await connectors.UpdateAsync(teams.Id, new SaveConnectorRequest("teams", "Finance channel", null, null, ["project.created", "period.closed"]));
        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var row = await db.Set<IntegrationConnector>().AsNoTracking().SingleAsync();
        Assert.DoesNotContain("secret-path", row.SecretsProtected);
        Assert.Equal(["webhookUrl"], (await connectors.GetAsync(teams.Id)).SecretsSet);
    }

    [Fact]
    public async Task Slack_test_messages_and_failures_are_logged_and_retried()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var connectors = host.Get<IConnectorService>();
        var slack = await connectors.CreateAsync(new SaveConnectorRequest("slack", "Ops", null,
            new Dictionary<string, string?> { ["webhookUrl"] = "https://hooks.slack.com/services/T0/B0/xyz" }, ["project.created"]));

        var test = await connectors.RunNowAsync(slack.Id);
        Assert.Equal(ConnectorRunState.Succeeded, test.Status);
        using (var body = JsonDocument.Parse(host.Transport.Sent.Single().Body))
        {
            Assert.Equal("Workbench test message", body.RootElement.GetProperty("text").GetString());
        }

        host.Transport.Default = FakeTransport.Status(503);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await host.Get<IProjectService>().CreateProjectAsync(new SaveProjectRequest("6006", "Retry me", null, null, ProjectState.Active, null, null));
        await host.Get<IntegrationDispatcher>().RunOnceAsync(CancellationToken.None);
        var failed = (await connectors.ListRunsAsync(slack.Id)).First();
        Assert.Equal(ConnectorRunState.Failed, failed.Status);
        Assert.Contains("Will retry", failed.Message);

        host.Transport.Default = FakeTransport.Status(200);
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await host.Get<IntegrationDispatcher>().RunOnceAsync(CancellationToken.None);
        Assert.Equal(ConnectorRunState.Succeeded, (await connectors.ListRunsAsync(slack.Id)).First().Status);
    }

    [Fact]
    public async Task The_gl_export_writes_a_companys_posted_journal_as_iif_and_uploads_it_over_sftp()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var company = await SeedJournalAsync(host);
        var connectors = host.Get<IConnectorService>();
        var gl = await connectors.CreateAsync(new SaveConnectorRequest("gl-export", "Sub books",
            new Dictionary<string, string>
            {
                ["company"] = company.ToString(), ["format"] = "iif", ["range"] = "previous-month", ["accountMap"] = "1010=Checking",
                ["destination"] = "sftp", ["sftpHost"] = "sftp.example.com", ["sftpUser"] = "wb", ["sftpHostKey"] = "SHA256:abcdEFGH",
            },
            new Dictionary<string, string?> { ["sftpPassword"] = "pw" }, Schedule: ConnectorScheduleKind.Monthly, RunAtHourUtc: 5, RunOnDay: 2));
        Assert.Equal(new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc), gl.NextRunAtUtc);

        var run = await connectors.RunNowAsync(gl.Id);
        Assert.Equal(ConnectorRunState.Succeeded, run.Status);
        Assert.Equal(1, run.ItemCount);
        Assert.Equal("gl-MAIN-20260901-20260930.iif", run.OutputFileName);

        var upload = Assert.Single(host.Sftp.Uploads);
        Assert.Equal("sftp.example.com", upload.Target.Host);
        Assert.Equal("abcdEFGH", upload.Target.HostKeySha256);
        var iif = Encoding.UTF8.GetString(upload.Content);
        Assert.StartsWith("!TRNS\tTRNSID\tTRNSTYPE\tDATE\tACCNT", iif);
        Assert.Contains("TRNS\t\tGENERAL JOURNAL\t09/15/2026\tChecking\t\t250.00\tJE-1\tRent", iif);
        Assert.Contains("SPL\t\tGENERAL JOURNAL\t09/15/2026\t6100 Rent\t\t-250.00\tJE-1\tRent", iif);
        Assert.DoesNotContain("JE-2", iif); // a draft
        Assert.DoesNotContain("JE-3", iif); // another company

        var file = await connectors.GetRunFileAsync(run.Id);
        Assert.Equal(upload.Content, file.Content);
    }

    [Fact]
    public async Task Scheduled_connectors_run_when_due_and_back_off_after_a_failure()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var company = await SeedJournalAsync(host);
        var connectors = host.Get<IConnectorService>();
        var gl = await connectors.CreateAsync(new SaveConnectorRequest("gl-export", "Nightly CSV",
            new Dictionary<string, string> { ["company"] = company.ToString(), ["format"] = "csv", ["range"] = "previous-day" },
            Schedule: ConnectorScheduleKind.Daily, RunAtHourUtc: 13));
        Assert.Equal(new DateTime(2026, 10, 19, 13, 0, 0, DateTimeKind.Utc), gl.NextRunAtUtc);

        var dispatcher = host.Get<IntegrationDispatcher>();
        Assert.Equal(0, (await dispatcher.RunOnceAsync(CancellationToken.None)).ConnectorRuns);
        host.Clock.Advance(TimeSpan.FromHours(1.5));
        Assert.Equal(1, (await dispatcher.RunOnceAsync(CancellationToken.None)).ConnectorRuns);
        var run = Assert.Single(await connectors.ListRunsAsync(gl.Id));
        Assert.Equal(ConnectorRunTrigger.Schedule, run.Trigger);
        Assert.StartsWith("Date,Entry,Source,Account number", Encoding.UTF8.GetString((await connectors.GetRunFileAsync(run.Id)).Content));
        Assert.Equal(new DateTime(2026, 10, 20, 13, 0, 0, DateTimeKind.Utc), (await connectors.GetAsync(gl.Id)).NextRunAtUtc);

        // A failure (the company was removed from the settings' reach) retries well before tomorrow's slot.
        await using (var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var row = await db.Set<IntegrationConnector>().SingleAsync();
            row.SettingsJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["company"] = Guid.NewGuid().ToString(), ["format"] = "csv" });
            row.NextRunAtUtc = host.Clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
        }

        await dispatcher.RunOnceAsync(CancellationToken.None);
        var after = await connectors.GetAsync(gl.Id);
        Assert.StartsWith("Failed", after.LastStatus);
        Assert.True(after.NextRunAtUtc < host.Clock.GetUtcNow().UtcDateTime.AddMinutes(5));
    }

    [Fact]
    public void Iif_and_csv_cells_are_safe()
    {
        var line = new Application.Integrations.Connectors.GlExportConnector.ExportLine(new DateOnly(2026, 1, 2), "JE-9", "Desc\twith tab", "Manual", "=HYPERLINK(\"x\")", "1",
            10m, 0m, "line\nbreak", null);
        Assert.DoesNotContain("\twith", Application.Integrations.Connectors.GlExportConnector.Iif([line]).Split("\r\n")[3].Replace("\t\t", "\t"));
        var csv = Application.Integrations.Connectors.GlExportConnector.Csv([line]);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);
    }

    private static async Task<Guid> SeedJournalAsync(IntegrationHost host)
    {
        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var main = await db.Set<Company>().FirstOrDefaultAsync(c => c.Id == Company.DefaultId);
        if (main is null)
        {
            main = new Company { Id = Company.DefaultId, Code = "MAIN", LegalName = "Main Co" };
            db.Set<Company>().Add(main);
        }

        var other = new Company { Code = "SUB", LegalName = "Sub Co" };
        db.Set<Company>().Add(other);
        var year = new FiscalYear { Year = 2026, Start = new DateOnly(2025, 10, 1), End = new DateOnly(2026, 9, 30) };
        var sep = new FiscalPeriod { FiscalYearId = year.Id, Name = "2026-09", Number = 12, Start = new DateOnly(2026, 9, 1), End = new DateOnly(2026, 9, 30) };
        var oct = new FiscalPeriod { FiscalYearId = year.Id, Name = "2026-10", Number = 1, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2026, 10, 31) };
        db.Set<FiscalYear>().Add(year);
        db.Set<FiscalPeriod>().AddRange(sep, oct);
        var cash = new Account { Number = "1010", Name = "Cash", Type = AccountType.Asset };
        var rent = new Account { Number = "6100", Name = "Rent", Type = AccountType.Expense };
        db.Set<Account>().AddRange(cash, rent);

        JournalEntry Entry(string number, int seq, Guid company, DateOnly date, Guid period, JournalStatus status)
        {
            var e = new JournalEntry { Number = number, Sequence = seq, CompanyId = company, Date = date, FiscalPeriodId = period, Status = status, Description = "Rent",
                CreatedBy = "x@example.com", Source = JournalSource.Manual, SourceId = Guid.NewGuid() };
            e.Lines.Add(new JournalLine { JournalEntryId = e.Id, CompanyId = company, LineNumber = 1, AccountId = cash.Id, Debit = 250m, Memo = "Rent" });
            e.Lines.Add(new JournalLine { JournalEntryId = e.Id, CompanyId = company, LineNumber = 2, AccountId = rent.Id, Credit = 250m, Memo = "Rent" });
            return e;
        }

        db.Set<JournalEntry>().AddRange(
            Entry("JE-1", 1, main.Id, new DateOnly(2026, 9, 15), sep.Id, JournalStatus.Posted),
            Entry("JE-2", 2, main.Id, new DateOnly(2026, 9, 16), sep.Id, JournalStatus.Draft),
            Entry("JE-3", 3, other.Id, new DateOnly(2026, 9, 17), sep.Id, JournalStatus.Posted),
            Entry("JE-4", 4, main.Id, new DateOnly(2026, 10, 18), oct.Id, JournalStatus.Posted));
        await db.SaveChangesAsync();
        return main.Id;
    }
}
