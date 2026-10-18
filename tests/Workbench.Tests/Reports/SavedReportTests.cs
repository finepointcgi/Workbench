using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Reports;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Reports;
using Workbench.Domain.Identity;
using Workbench.Domain.Reports;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Reports;

public sealed class SavedReportTests
{
    private static SaveReportRequest TimeReport(string name, ReportVisibility visibility = ReportVisibility.Private, params OrgRoles[] roles) =>
        new(name, null, visibility, roles, null, null,
            new ReportDefinition(ReportSourceKeys.TimeEntries, [new("employee"), new("hours"), new("labor_cost")], Range: ReportDateRange.All));

    private static SaveReportScheduleRequest Weekly(ReportDeliveryMode delivery = ReportDeliveryMode.Attachment, params string[] recipients) =>
        new(ReportFrequency.Weekly, DayOfWeek.Monday, null, 13, delivery, ReportFileFormat.Csv, recipients);

    [Fact]
    public async Task Private_and_shared_reports_and_who_may_change_them()
    {
        await using var kit = await ReportsKit.CreateAsync();
        var mine = await kit.As(Editor).Saved.CreateAsync(TimeReport("My hours"));
        Assert.True(mine.IsMine && mine.CanEdit);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.As(Owner).Saved.GetAsync(mine.Id));
        Assert.Empty(await kit.Saved.ListAsync());
        Assert.Single(await kit.As(Admin).Saved.ListAsync());

        var shared = await kit.As(Finance).Saved.CreateAsync(TimeReport("Team hours", ReportVisibility.Shared, OrgRoles.Employee));
        var seen = await kit.As(Owner).Saved.GetAsync(shared.Id);
        Assert.False(seen.CanEdit);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Saved.UpdateAsync(shared.Id, TimeReport("Mine now")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Saved.DeleteAsync(shared.Id));

        var financeOnly = await kit.As(Finance).Saved.CreateAsync(TimeReport("Finance hours", ReportVisibility.Shared, OrgRoles.Finance));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.As(Owner).Saved.GetAsync(financeOnly.Id));

        // Running a shared report applies the reader's own access.
        var asFinance = await kit.As(Finance).Saved.RunAsync(shared.Id);
        Assert.Equal(3, asFinance.Details().Count());
        Assert.Equal("Team hours", asFinance.Title);
        var asAnalyst = await kit.As(Editor).Saved.RunAsync(shared.Id);
        Assert.Single(asAnalyst.Details());
        Assert.DoesNotContain(asAnalyst.Columns, c => c.Key == "labor_cost");

        var updated = await kit.As(Finance).Saved.UpdateAsync(shared.Id, TimeReport("Team hours v2", ReportVisibility.Private));
        Assert.Equal(ReportVisibility.Private, updated.Visibility);
        await kit.Saved.DeleteAsync(shared.Id);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Saved.GetAsync(shared.Id));
    }

    [Fact]
    public async Task Saved_standard_reports_keep_their_parameters_and_validation_rejects_bad_input()
    {
        await using var kit = await ReportsKit.CreateAsync();
        kit.As(Finance);
        var saved = await kit.Saved.CreateAsync(new SaveReportRequest("Labor Oct 14", null, ReportVisibility.Private, null, StandardReportKeys.LaborDistribution, ReportsKit.Day, null));
        Assert.Equal(ReportsKit.Day.From, saved.Parameters!.From);
        var result = await kit.Saved.RunAsync(saved.Id);
        Assert.Equal(14m, result.Total().Num(result, "hours"));
        var csv = await kit.Saved.ExportAsync(saved.Id, ReportFileFormat.Csv);
        Assert.Contains("Alex Rivera", System.Text.Encoding.UTF8.GetString(csv.Content));

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Saved.CreateAsync(TimeReport("")));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Saved.CreateAsync(new SaveReportRequest("x", null, ReportVisibility.Private, null, "nope", null, null)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Saved.CreateAsync(TimeReport("x", ReportVisibility.Shared)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Saved.CreateAsync(TimeReport("x") with { Definition = new ReportDefinition(ReportSourceKeys.TimeEntries, [new("drop table")]) }));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Saved.AddScheduleAsync(saved.Id, Weekly() with { HourUtc = 24 }));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Saved.AddScheduleAsync(saved.Id, Weekly(ReportDeliveryMode.Link, "stranger@evil.example")));
    }

    [Fact]
    public void Next_run_is_the_first_matching_hour_after_now()
    {
        var now = new DateTime(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc); // Tuesday
        Assert.Equal(new DateTime(2026, 10, 21, 13, 0, 0), ReportScheduleMath.Next(new ReportSchedule { OwnerEmail = "a", Format = "Xlsx", Frequency = ReportScheduleFrequency.Daily, HourUtc = 13 }, now));
        Assert.Equal(new DateTime(2026, 10, 20, 16, 0, 0), ReportScheduleMath.Next(new ReportSchedule { OwnerEmail = "a", Format = "Xlsx", Frequency = ReportScheduleFrequency.Daily, HourUtc = 16 }, now));
        Assert.Equal(new DateTime(2026, 10, 26, 13, 0, 0), ReportScheduleMath.Next(new ReportSchedule { OwnerEmail = "a", Format = "Xlsx", Frequency = ReportScheduleFrequency.Weekly, DayOfWeek = DayOfWeek.Monday, HourUtc = 13 }, now));
        Assert.Equal(new DateTime(2026, 11, 1, 6, 0, 0), ReportScheduleMath.Next(new ReportSchedule { OwnerEmail = "a", Format = "Xlsx", Frequency = ReportScheduleFrequency.Monthly, DayOfMonth = 1, HourUtc = 6 }, now));
    }

    [Fact]
    public async Task Scheduled_reports_run_as_their_owner_and_never_repeat()
    {
        await using var kit = await ReportsKit.CreateAsync();
        // The analyst shares a time report with everyone and schedules it; pm@ gets a link.
        var report = await kit.As(Editor).Saved.CreateAsync(TimeReport("My hours", ReportVisibility.Shared, OrgRoles.Employee));
        var schedule = await kit.Saved.AddScheduleAsync(report.Id, Weekly(ReportDeliveryMode.Attachment, Owner));
        Assert.Equal(new DateTime(2026, 10, 26, 13, 0, 0), schedule.NextRunUtc);
        Assert.Equal([Owner], schedule.AdditionalRecipients);
        await MakeDueAsync(kit, schedule.Id);

        // Whoever is "signed in" for the job (here Finance) doesn't matter: the run is the analyst's.
        kit.As(Finance);
        var runner = kit.Get<ReportScheduleRunner>();
        Assert.Equal(1, await runner.RunDueAsync(CancellationToken.None));
        var mail = kit.Email.Sent.Single(m => m.To == Editor);
        var file = Assert.Single(mail.Attachments!);
        var csv = System.Text.Encoding.UTF8.GetString(file.Content);
        Assert.Contains("Alex Rivera", csv);
        Assert.DoesNotContain("Val Chen", csv);
        Assert.DoesNotContain("Labor cost", csv);
        var link = kit.Email.Sent.Single(m => m.To == Owner);
        Assert.Null(link.Attachments);
        Assert.Contains($"reports/saved/{report.Id}", link.TextBody);

        // Already advanced: running again sends nothing.
        Assert.Equal(0, await runner.RunDueAsync(CancellationToken.None));
        Assert.Equal(2, kit.Email.Sent.Count);
        var after = (await kit.As(Editor).Saved.GetAsync(report.Id)).Schedules.Single();
        Assert.Equal(new DateTime(2026, 10, 26, 13, 0, 0), after.NextRunUtc);
        Assert.StartsWith("Sent to analyst@example.com", after.LastStatus);
        Assert.Equal(ReportsKit.Now.UtcDateTime, after.LastRunUtc);
    }

    [Fact]
    public async Task Schedules_of_deactivated_owners_stop_and_send_now_is_owner_only()
    {
        await using var kit = await ReportsKit.CreateAsync();
        var report = await kit.As(Viewer).Saved.CreateAsync(TimeReport("Val's hours", ReportVisibility.Shared, OrgRoles.Employee));
        var schedule = await kit.Saved.AddScheduleAsync(report.Id, Weekly());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Saved.SendNowAsync(schedule.Id));

        var sent = await kit.As(Viewer).Saved.SendNowAsync(schedule.Id);
        Assert.StartsWith("Sent to viewer@example.com", sent.LastStatus);
        Assert.Contains("Val Chen", System.Text.Encoding.UTF8.GetString(kit.Email.Sent.Single().Attachments![0].Content));
        Assert.Equal(new DateTime(2026, 10, 26, 13, 0, 0), sent.NextRunUtc);

        await using (var db = await kit.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            (await db.Set<AppUser>().SingleAsync(u => u.Email == Viewer)).IsActive = false;
            await db.SaveChangesAsync();
        }

        await MakeDueAsync(kit, schedule.Id);
        Assert.Equal(0, await kit.Get<ReportScheduleRunner>().RunDueAsync(CancellationToken.None));
        Assert.Single(kit.Email.Sent);
        await using (var db = await kit.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var row = await db.Set<ReportSchedule>().SingleAsync(s => s.Id == schedule.Id);
            Assert.False(row.Enabled);
            Assert.Contains("deactivated", row.LastStatus);
        }
    }

    [Fact]
    public async Task A_scheduled_report_the_owner_can_no_longer_run_fails_without_data()
    {
        await using var kit = await ReportsKit.CreateAsync();
        var report = await kit.As(Owner).Saved.CreateAsync(new SaveReportRequest("1001 status", null, ReportVisibility.Private, null,
            StandardReportKeys.ProjectStatus, ReportsKit.Day with { ProjectId = kit.Data.Project1 }, null));
        var schedule = await kit.Saved.AddScheduleAsync(report.Id, Weekly());
        await using (var db = await kit.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            // pm@ stops managing 1001.
            (await db.Set<Workbench.Domain.ProjectAccounting.Project>().SingleAsync(p => p.Id == kit.Data.Project1)).ProjectManagerId = null;
            await db.SaveChangesAsync();
        }

        await MakeDueAsync(kit, schedule.Id);
        Assert.Equal(1, await kit.Get<ReportScheduleRunner>().RunDueAsync(CancellationToken.None));
        var mail = kit.Email.Sent.Single();
        Assert.Null(mail.Attachments);
        Assert.StartsWith("Scheduled report failed", mail.Subject);
        Assert.StartsWith("Failed:", (await kit.Saved.GetAsync(report.Id)).Schedules.Single().LastStatus);
    }

    private static async Task MakeDueAsync(ReportsKit kit, Guid scheduleId)
    {
        await using var db = await kit.Get<IWorkbenchDbContextFactory>().CreateAsync();
        (await db.Set<ReportSchedule>().SingleAsync(s => s.Id == scheduleId)).NextRunUtc = ReportsKit.Now.UtcDateTime.AddMinutes(-5);
        await db.SaveChangesAsync();
    }
}
