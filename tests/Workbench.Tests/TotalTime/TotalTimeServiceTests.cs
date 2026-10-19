using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Time;
using Workbench.Application.TotalTime;
using Workbench.Contracts.Time;
using Workbench.Contracts.TotalTime;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Accounting;
using Workbench.Tests.Time;

namespace Workbench.Tests.TotalTime;

/// <summary>
/// Total time accounting through posting, revisions, the labor distribution and the method setting. Dana (exempt, 40-hour
/// week) costs $50 until 2026-10-19 and $60 from 2026-10-20; "today" is moved to Monday 2026-10-26 so a full week can be recorded.
/// </summary>
public sealed class TotalTimeServiceTests
{
    private static readonly DateOnly Monday = TimeTestHost.Monday;

    private static async Task<LaborPostingTests.Host> CreateAsync()
    {
        var host = await LaborPostingTests.CreateAsync();
        host.Time.Clock.UtcNow = new DateTime(2026, 10, 26, 15, 0, 0, DateTimeKind.Utc);
        return host;
    }

    private static TotalTimeService Service(LaborPostingTests.Host host) =>
        new(host.Time.Factory, new TimekeepingClock(host.Time.Clock, Microsoft.Extensions.Options.Options.Create(host.Time.Options)),
            new AuditRecorder(host.Time.User, host.Time.Clock), host.Time.Timesheets);

    private static async Task SetPolicyAsync(LaborPostingTests.Host host, DateOnly from, LaborCostMethod method)
    {
        await using var db = await host.Time.Factory.CreateAsync();
        db.Set<LaborCostPolicy>().Add(new LaborCostPolicy { EffectiveFrom = from, Method = method, Reason = "Test", CreatedBy = "admin@example.com", CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    /// <summary>Dana records <paramref name="hoursPerDay"/> on the direct charge, Monday to Friday of the given week, and Pat approves it.</summary>
    private static async Task<Guid> ApprovedWeekAsync(LaborPostingTests.Host host, DateOnly monday, decimal hoursPerDay = 10m, decimal overheadPerDay = 0m)
    {
        host.Time.SignInAs(host.Time.Dana);
        var sheet = await host.Time.Timesheets.GetOrCreateMyTimesheetAsync(monday);
        var entries = Enumerable.Range(0, 5).Select(d => new TimeEntryInput(host.Time.Direct.Id, monday.AddDays(d), hoursPerDay)).ToList();
        if (overheadPerDay > 0)
        {
            entries.AddRange(Enumerable.Range(0, 5).Select(d => new TimeEntryInput(host.Time.Overhead.Id, monday.AddDays(d), overheadPerDay)));
        }

        await host.Time.Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, new SaveTimeEntriesRequest(entries));
        await host.Time.Timesheets.SubmitAsync(sheet.Timesheet.Id, new SubmitTimesheetRequest(true));
        host.Time.SignInAs(host.Time.Pat);
        await host.Approvals.ApproveAsync(sheet.Timesheet.Id);
        return sheet.Timesheet.Id;
    }

    [Fact]
    public async Task Posting_uses_the_effective_rate_and_the_labor_distribution_agrees_with_the_ledger()
    {
        var host = await CreateAsync();
        await SetPolicyAsync(host, Monday, LaborCostMethod.TotalTimeEffectiveRate);

        await ApprovedWeekAsync(host, Monday, hoursPerDay: 8m, overheadPerDay: 2m); // 40 direct + 10 indirect = 50 hours.

        // (8 × 50 + 32 × 60) direct + (2 × 50 + 8 × 60) overhead = 2,900, spread over 50 hours: × 40/50.
        Assert.Equal(1856m, await host.Gl.BalanceAsync("5010"));   // 2,320 × 40/50 of it is direct
        Assert.Equal(-2320m, await host.Gl.BalanceAsync("2100"));  // accrued payroll: the salary, not 2,900

        var csv = await host.Time.Reports.ExportLaborDistributionCsvAsync(Monday, Monday.AddDays(6));
        var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')).ToList();
        Assert.Equal(2320m, rows.Sum(r => decimal.Parse(r[8], CultureInfo.InvariantCulture)));
        Assert.Equal(10m, rows.Sum(r => decimal.Parse(r[9], CultureInfo.InvariantCulture)));
        Assert.Equal(1856m, rows.Where(r => r[4] == "1001.01").Sum(r => decimal.Parse(r[8], CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task A_revised_timesheet_is_recomputed_and_both_costs_are_in_the_audit_trail()
    {
        var host = await CreateAsync();
        await SetPolicyAsync(host, Monday, LaborCostMethod.TotalTimeEffectiveRate);
        var id = await ApprovedWeekAsync(host, Monday); // 50 hours
        Assert.Equal(2320m, await host.Gl.BalanceAsync("5010")); // (10 × 50 + 40 × 60) × 40/50

        host.Time.SignInAs(host.Time.Dana);
        await host.Timesheets.ReopenAsync(id, "Friday was recorded twice");
        await host.Time.Timesheets.SaveEntriesAsync(id, new SaveTimeEntriesRequest(
            Enumerable.Range(0, 5).Select(d => new TimeEntryInput(host.Time.Direct.Id, Monday.AddDays(d), d == 4 ? 0m : 10m)).ToList(), Reason: "Duplicate Friday"));
        await host.Time.Timesheets.SubmitAsync(id, new SubmitTimesheetRequest(true));
        host.Time.SignInAs(host.Time.Pat);
        await host.Approvals.ApproveAsync(id);

        Assert.Equal(2300m, await host.Gl.BalanceAsync("5010")); // 40 hours now: the salary at the hourly rates, 10 × 50 + 30 × 60
        Assert.Equal(1, (await host.Gl.EntriesAsync(JournalSource.Timesheet)).Single(e => e.Status == JournalStatus.Posted).SourceRevision);

        await using var db = await host.Time.Factory.CreateAsync();
        var trail = (await db.Set<AuditEvent>().Where(a => a.EntityType == LaborCostAudit.EntityType && a.EntityId == id).ToListAsync())
            .OrderBy(a => a.OccurredAtUtc).ThenBy(a => a.Id).ToList();
        Assert.Equal(["Computed", "Recomputed"], trail.Select(a => a.Action));
        static decimal Field(string? json, string name) => System.Text.Json.JsonDocument.Parse(json!).RootElement.GetProperty(name).GetDecimal();
        Assert.Equal(2320m, Field(trail[1].BeforeJson, "laborCost"));
        Assert.Equal(10m, Field(trail[1].BeforeJson, "uncompensatedHours"));
        Assert.Equal(2300m, Field(trail[1].AfterJson, "laborCost"));
        Assert.Equal(0m, Field(trail[1].AfterJson, "uncompensatedHours"));
        Assert.Equal(1m, Field(trail[1].AfterJson, "revision"));
        Assert.Contains("revision 1", trail[1].Reason);
    }

    [Fact]
    public async Task The_method_switches_at_a_period_boundary_and_earlier_periods_keep_theirs()
    {
        var host = await CreateAsync();
        host.Time.Clock.UtcNow = new DateTime(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc);
        var next = Monday.AddDays(7);
        await SetPolicyAsync(host, next, LaborCostMethod.Hybrid);
        var first = await ApprovedWeekAsync(host, Monday);
        var second = await ApprovedWeekAsync(host, next);

        await using var db = await host.Time.Factory.CreateAsync();
        var book = await LaborCostBook.LoadAsync(db, [first, second], CancellationToken.None);

        var before = book.Sheets[first].Cost;
        Assert.Equal(LaborCostMethod.Standard, before.Method);
        Assert.Equal(2300m, before.LaborCost);          // 10 × 50 + 30 × 60; Friday's 10 hours uncompensated at no cost
        Assert.Equal(10m, before.UncompensatedHours);
        Assert.False(before.EffectiveRateApplied);

        var after = book.Sheets[second].Cost;
        Assert.Equal(LaborCostMethod.Hybrid, after.Method);
        Assert.Equal(2400m, after.LaborCost);           // 40 × $60 spread over 50 hours
        Assert.Equal(48m, after.EffectiveRate);
        Assert.True(after.EffectiveRateApplied);
        Assert.Equal(2300m + 2400m, await host.Gl.BalanceAsync("5010"));
    }

    [Fact]
    public async Task A_new_method_starts_at_the_next_period_boundary_or_later()
    {
        var host = await CreateAsync(); // Today: Monday 2026-10-26, so the next period starts 11-02.
        var service = Service(host);

        var settings = await service.GetSettingsAsync();
        Assert.Equal(LaborCostMethodKind.Standard, settings.Current);
        Assert.Equal(new DateOnly(2026, 11, 2), settings.EarliestChange);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetMethodAsync(new(LaborCostMethodKind.TotalTimeEffectiveRate, new DateOnly(2026, 10, 28), "Mid-period")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetMethodAsync(new(LaborCostMethodKind.TotalTimeEffectiveRate, new DateOnly(2026, 11, 4), " ")));

        settings = await service.SetMethodAsync(new(LaborCostMethodKind.TotalTimeEffectiveRate, new DateOnly(2026, 11, 4), "Adopting TTA"));
        var policy = Assert.Single(settings.History);
        Assert.Equal(new DateOnly(2026, 11, 2), policy.EffectiveFrom); // Snapped to the start of its period.
        Assert.Equal(LaborCostMethodKind.Standard, settings.Current);  // Not yet: this period keeps Standard.

        await using var db = await host.Time.Factory.CreateAsync();
        Assert.Equal(LaborCostMethod.Standard, await LaborCostPolicies.MethodOnAsync(db, Monday.AddDays(7), CancellationToken.None));
        Assert.Equal(LaborCostMethod.TotalTimeEffectiveRate, await LaborCostPolicies.MethodOnAsync(db, new DateOnly(2026, 11, 2), CancellationToken.None));
        Assert.Single(await db.Set<AuditEvent>().Where(a => a.EntityType == nameof(LaborCostPolicy)).ToListAsync());
    }

    [Fact]
    public async Task Non_exempt_overtime_is_costed_as_before()
    {
        var host = await CreateAsync();
        await SetPolicyAsync(host, Monday, LaborCostMethod.TotalTimeEffectiveRate);
        await using (var db = await host.Time.Factory.CreateAsync())
        {
            var dana = await db.Set<Employee>().SingleAsync(e => e.Id == host.Time.Dana.Id);
            dana.IsExempt = false;
            await db.SaveChangesAsync();
        }

        var id = await ApprovedWeekAsync(host, Monday);

        Assert.Equal(2900m, await host.Gl.BalanceAsync("5010")); // every hour: 10 × 50 + 40 × 60
        var summary = await Service(host).GetTimesheetSummaryAsync(id);
        Assert.False(summary.IsExempt);
        Assert.Equal(0m, summary.UncompensatedHours);
    }

    [Fact]
    public async Task Timesheet_summary_and_reports_show_uncompensated_hours()
    {
        var host = await CreateAsync();
        await SetPolicyAsync(host, Monday, LaborCostMethod.Hybrid);
        var id = await ApprovedWeekAsync(host, Monday, hoursPerDay: 8m, overheadPerDay: 2m);

        host.Time.SignInAs(host.Time.Dana);
        var summary = await Service(host).GetTimesheetSummaryAsync(id);
        Assert.True(summary.IsExempt);
        Assert.Equal((40m, 50m, 10m), (summary.StandardHours, summary.WorkedHours, summary.UncompensatedHours));
        Assert.Equal(LaborCostMethodKind.Hybrid, summary.Method);
        Assert.Contains("uncompensated", summary.PolicyNote);

        host.Time.SignInAs(host.Time.Pat);
        var report = await Service(host).GetReportAsync(Monday, Monday.AddDays(6));
        var rate = Assert.Single(report.EffectiveRates);
        Assert.Equal((50m, 10m, 2320m, true), (rate.WorkedHours, rate.UncompensatedHours, rate.LaborCost, rate.EffectiveRateApplied));
        Assert.Equal(46.4m, rate.EffectiveRate); // 2,320 ÷ 50

        Assert.Equal(10m, report.Uncompensated.Sum(u => u.UncompensatedHours));
        Assert.Equal(8m, report.Uncompensated.Single(u => u.ProjectCode == "1001").UncompensatedHours);  // 40 of 50 hours
        Assert.Equal(2m, report.Uncompensated.Single(u => u.ProjectCode == "IND").UncompensatedHours);

        var direct = report.ProjectEffect.Single(p => p.ProjectCode == "1001");
        Assert.Equal(1840m, direct.StandardCost);   // Standard: Monday to Thursday paid (8 × 50 + 24 × 60), Friday uncompensated
        Assert.Equal(1856m, direct.TotalTimeCost);
        Assert.Equal(1856m, direct.AppliedCost);
        Assert.Equal(report.ProjectEffect.Sum(p => p.AppliedCost), report.ProjectEffect.Sum(p => p.TotalTimeCost));
    }
}
