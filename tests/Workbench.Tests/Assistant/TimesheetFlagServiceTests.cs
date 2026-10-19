using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts;
using Workbench.Contracts.Assistant;
using Workbench.Domain.Assistant;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Assistant;

/// <summary>Flags on seeded data, with the real registrations: analyst@ reports to pm@; finance@ sees everyone.</summary>
public sealed class TimesheetFlagServiceTests
{
    private static readonly DateOnly Week = new(2026, 10, 19); // Monday

    /// <summary>Six usual weeks for analyst@ on 1001, then a week with Saturday hours, a holiday and a late, revised day.</summary>
    private static async Task<(AssistantKit Kit, Guid Odd, Guid Usual)> SeedAsync(bool enable = false)
    {
        var kit = await AssistantKit.CreateAsync();
        await using var db = await kit.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var category = await db.Set<LaborCategory>().FirstAsync();
        var now = DateTime.UtcNow;
        Timesheet Sheet(DateOnly start, TimesheetStatus status, int hoursPerDay, int extraSaturday = 0)
        {
            var sheet = new Timesheet { EmployeeId = kit.Data.AnalystId, PeriodStart = start, PeriodEnd = start.AddDays(6), Status = status, CreatedAtUtc = now, UpdatedAtUtc = now, SubmittedAtUtc = now };
            db.Set<Timesheet>().Add(sheet);
            for (var d = 0; d < 5; d++)
            {
                db.Set<TimeEntry>().Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = kit.Data.Wbs1, LaborCategoryId = category.Id, WorkDate = start.AddDays(d), Hours = hoursPerDay, EnteredAtUtc = now, UpdatedAtUtc = now });
                db.Set<TimeDayRecord>().Add(new TimeDayRecord { EmployeeId = kit.Data.AnalystId, TimesheetId = sheet.Id, WorkDate = start.AddDays(d), FirstEnteredAtUtc = start.AddDays(d).ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc) });
            }

            if (extraSaturday > 0)
            {
                db.Set<TimeEntry>().Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = kit.Data.Wbs1, LaborCategoryId = category.Id, WorkDate = start.AddDays(5), Hours = extraSaturday, EnteredAtUtc = now, UpdatedAtUtc = now });
            }

            return sheet;
        }

        Timesheet? usual = null;
        for (var w = 7; w >= 1; w--)
        {
            // The fixture already has an approved week for the analyst (Oct 12); one timesheet per period.
            if (Week.AddDays(-7 * w) != ProjectAccountingFixture.Day.AddDays(-2))
            {
                usual = Sheet(Week.AddDays(-7 * w), TimesheetStatus.Approved, 8);
            }
        }

        var odd = Sheet(Week, TimesheetStatus.Submitted, 8, extraSaturday: 5);
        odd.Revision = 1;
        odd.FirstSubmittedAtUtc = now;
        var lateDay = db.Set<TimeDayRecord>().Local.Single(d => d.TimesheetId == odd.Id && d.WorkDate == Week.AddDays(1));
        lateDay.FirstEnteredAtUtc = Week.AddDays(6).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
        db.Set<Holiday>().Add(new Holiday { Date = Week.AddDays(2), Name = "Company day" });
        db.Set<TimeEntryRevision>().Add(new TimeEntryRevision
        {
            TimesheetId = odd.Id, TimeEntryId = Guid.NewGuid(), WorkDate = Week, OldHours = 6, NewHours = 8, TimesheetRevision = 1,
            ChangedBy = Editor, ChangedAtUtc = now, Reason = "Forgot a meeting",
        });
        await db.SaveChangesAsync();
        if (enable)
        {
            await kit.EnableAsync();
        }

        return (kit, odd.Id, usual!.Id);
    }

    [Fact]
    public async Task The_supervisor_sees_the_flags_of_an_odd_timesheet()
    {
        var (kit, odd, usual) = await SeedAsync();
        await using var _ = kit;
        kit.As(Owner);

        var flags = await kit.Get<ITimesheetFlagService>().ListForTimesheetsAsync([odd, usual]);

        var oddFlags = Assert.Single(flags, f => f.TimesheetId == odd);
        var rules = oddFlags.Flags.Select(f => f.RuleKey).ToList();
        Assert.Contains(TimesheetFlagRules.Weekend, rules);
        Assert.Contains(TimesheetFlagRules.Holiday, rules);
        Assert.Contains(TimesheetFlagRules.LateEntry, rules);
        Assert.Contains(TimesheetFlagRules.Revisions, rules);
        Assert.Equal("Alex Rivera", oddFlags.EmployeeName);
        Assert.Equal($"time/history/{odd}", oddFlags.Link);
        Assert.Empty(Assert.Single(flags, f => f.TimesheetId == usual).Flags);
    }

    [Fact]
    public async Task Flags_are_only_for_reviewers_never_your_own_or_others()
    {
        var (kit, odd, _) = await SeedAsync();
        await using var _ = kit;

        kit.As(Editor); // the employee themself
        Assert.Empty(await kit.Get<ITimesheetFlagService>().ListForTimesheetsAsync([odd]));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Get<ITimesheetFlagService>().GetAsync(odd));

        kit.As(Outsider); // no reports
        Assert.Empty(await kit.Get<ITimesheetFlagService>().ReportAsync(new TimesheetFlagReportQuery(Week.AddDays(-60), Week.AddDays(7))));

        kit.As(Finance); // everyone's
        var report = await kit.Get<ITimesheetFlagService>().ReportAsync(new TimesheetFlagReportQuery(Week.AddDays(-60), Week.AddDays(7)));
        Assert.Equal(odd, report[0].TimesheetId); // most warnings first
        Assert.Contains(report, r => r.EmployeeName == "Olly Out"); // not anyone's report, but Finance sees everyone

        var weekendOnly = await kit.Get<ITimesheetFlagService>().ReportAsync(new TimesheetFlagReportQuery(Week.AddDays(-60), Week.AddDays(7), TimesheetFlagRules.Weekend));
        Assert.Equal(TimesheetFlagRules.Weekend, Assert.Single(Assert.Single(weekendOnly).Flags).RuleKey);
    }

    [Fact]
    public async Task Explanations_come_from_the_assistant_when_on_are_cached_and_audited()
    {
        var (kit, odd, _) = await SeedAsync(enable: true);
        await using var _ = kit;
        kit.As(Owner);
        var service = kit.Get<ITimesheetFlagService>();

        var first = await service.ExplainAsync(odd, TimesheetFlagRules.Weekend);
        Assert.True(first.FromAssistant);
        Assert.Contains("not a finding of wrongdoing", first.Text);
        var again = await service.ExplainAsync(odd, TimesheetFlagRules.Weekend);
        Assert.Equal(first.Text, again.Text);

        await using var db = await kit.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.Single(await db.Set<TimesheetFlagExplanation>().ToListAsync());
        var interaction = Assert.Single(await db.Set<AssistantInteraction>().Where(i => i.Kind == AssistantInteractionKind.FlagExplanation).ToListAsync());
        Assert.DoesNotContain("Alex", interaction.Prompt);
        var call = Assert.Single(await db.Set<AssistantToolCall>().Where(c => c.InteractionId == interaction.Id).ToListAsync());
        Assert.Contains($"Timesheet:{odd}", call.RecordsRead);
    }

    [Fact]
    public async Task With_the_assistant_off_the_explanation_is_the_rules_own_text()
    {
        var (kit, odd, _) = await SeedAsync();
        await using var _ = kit;
        kit.As(Owner);

        var explanation = await kit.Get<ITimesheetFlagService>().ExplainAsync(odd, TimesheetFlagRules.Holiday);

        Assert.False(explanation.FromAssistant);
        Assert.Contains("Company day", explanation.Text);
    }

    [Fact]
    public async Task Supervisors_see_who_has_not_submitted()
    {
        var (kit, _, _) = await SeedAsync();
        await using var _ = kit;
        kit.As(Owner);

        var rows = await kit.Get<ITimesheetSubmissionService>().GetStatusAsync(Week.AddDays(7));

        Assert.Equal(["Alex Rivera", "Val Chen"], rows.Select(r => r.EmployeeName).Order());
        Assert.All(rows, r => Assert.True(r.IsMissing));
        Assert.DoesNotContain(rows, r => r.EmployeeName == "Pat Morgan");

        var thisWeek = await kit.Get<ITimesheetSubmissionService>().GetStatusAsync(Week);
        Assert.False(thisWeek.Single(r => r.EmployeeName == "Alex Rivera").IsMissing);
    }
}
