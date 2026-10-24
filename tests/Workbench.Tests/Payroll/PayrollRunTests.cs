using Microsoft.EntityFrameworkCore;
using Workbench.Application.Payroll;
using Workbench.Contracts;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Payroll;

public sealed class PayrollRunTests
{
    private static readonly DateOnly Week1 = PayrollKit.PeriodStart;
    private static readonly DateOnly Week2 = PayrollKit.PeriodStart.AddDays(7);

    [Fact]
    public async Task Preview_splits_approved_hours_and_flags_missing_and_unapproved_time()
    {
        await using var kit = await PayrollKit.CreateAsync();
        await kit.UpdateEmployeeAsync(kit.Fx.ViewerId, e => e.IsExempt = false);

        // viewer@ (non-exempt): 44 worked hours in the second week, so 4 h overtime; the first week is still submitted.
        await kit.AddApprovedHoursAsync(kit.Fx.ViewerId, kit.Fx.Wbs1, (Week2, 10), (Week2.AddDays(1), 10), (Week2.AddDays(3), 10), (Week2.AddDays(4), 10));
        await kit.AddApprovedHoursAsync(kit.Fx.ViewerId, kit.PtoWbs, (Week2.AddDays(5), 8));
        await kit.SetTimesheetStatusAsync(kit.Fx.ViewerId, Week1, TimesheetStatus.Submitted);
        await kit.AddApprovedHoursAsync(kit.Fx.AnalystId, kit.Fx.Wbs1, (Week1, 8));
        await kit.SetTimesheetStatusAsync(kit.Fx.AnalystId, Week1, TimesheetStatus.Approved);

        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        Assert.Equal(run.Id, (await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id)).Id);
        var preview = await kit.Runs.PreviewHoursAsync(run.Id);

        var viewer = preview.Employees.Single(e => e.EmployeeId == kit.Fx.ViewerId);
        Assert.Equal((40m, 4m, 8m), (viewer.RegularHours, viewer.OvertimeHours, viewer.LeaveHours));
        Assert.Equal("PTO", viewer.Leave.Single().EarningCode);
        Assert.Contains(viewer.Issues, i => i.Kind == HoursIssueKind.UnapprovedTimesheet);

        // analyst@ is exempt: no overtime, both weeks approved.
        var analyst = preview.Employees.Single(e => e.EmployeeId == kit.Fx.AnalystId);
        Assert.Equal((16m, 0m), (analyst.RegularHours, analyst.OvertimeHours));
        Assert.Empty(analyst.Issues);

        // pm@ has no timesheets at all; outsider@ isn't in the pay group.
        Assert.Contains(preview.Employees.Single(e => e.EmployeeId == kit.Fx.PmId).Issues, i => i.Kind == HoursIssueKind.MissingTimesheet);
        Assert.DoesNotContain(preview.Employees, e => e.EmployeeId == kit.Fx.OutsiderId);
        Assert.Equal(2, preview.FlaggedCount);
    }

    [Fact]
    public async Task Hours_wont_export_with_flagged_employees_until_acknowledged_and_only_approved_hours_go_out()
    {
        await using var kit = await PayrollKit.CreateAsync();
        await kit.AddApprovedHoursAsync(kit.Fx.ViewerId, kit.Fx.Wbs1, (Week1, 6));
        await kit.SetTimesheetStatusAsync(kit.Fx.ViewerId, Week1, TimesheetStatus.Submitted);
        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest()));
        Assert.Contains("acknowledge", refused.Message);

        var file = await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(AcknowledgeIssues: true));
        Assert.Equal("paylocity-hours-bw-20261018.csv", file.FileName);
        var rows = file.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1..];
        Assert.Contains("W1234,2002,Alex Rivera,10/05/2026,10/18/2026,10/23/2026,E,REG,8.00,,,", rows);

        // viewer@'s submitted 6 h aren't exported; only the approved 4 h of week 2 are. Salaried pm@'s regular hours never are.
        Assert.Contains(rows, r => r.StartsWith("W1234,3003,") && r.Contains(",REG,4.00,"));
        Assert.DoesNotContain(rows, r => r.Contains(",1001,"));
        Assert.Equal(PayrollRunState.Exported, (await kit.Runs.GetAsync(run.Id)).Status);
    }

    [Fact]
    public async Task Register_import_maps_employees_checks_net_pay_and_is_idempotent()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Runs.ImportRegisterAsync(run.Id, new ImportRegisterRequest("r.csv", PayrollKit.Register)));
        await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true));

        var bad = PayrollKit.Register.Replace("320.00", "330.00");
        Assert.Contains("net pay", (await Assert.ThrowsAsync<ArgumentException>(() => kit.Runs.ImportRegisterAsync(run.Id, new ImportRegisterRequest("r.csv", bad)))).Message);

        var withStranger = PayrollKit.Register + "\nW1234,9999,Somebody Else,10/23/2026,100,0,7.65,0,0,100";
        var first = await kit.Runs.ImportRegisterAsync(run.Id, new ImportRegisterRequest("register.csv", withStranger));
        Assert.False(first.AlreadyImported);
        Assert.Equal(3, first.Lines.Count);
        Assert.Equal(kit.Fx.AnalystId, first.Lines.Single(l => l.ProviderEmployeeId == "2002").EmployeeId);
        Assert.Null(first.Lines.Single(l => l.ProviderEmployeeId == "9999").EmployeeId);
        Assert.Contains(first.Warnings, w => w.Contains("9999"));

        var again = await kit.Runs.ImportRegisterAsync(run.Id, new ImportRegisterRequest("register.csv", withStranger));
        Assert.True(again.AlreadyImported);
        Assert.Equal(first.RegisterId, again.RegisterId);
        Assert.Equal(PayrollRunState.RegisterImported, (await kit.Runs.GetAsync(run.Id)).Status);
    }

    [Fact]
    public async Task Reconciliation_explains_gross_against_labor_at_pay_rates()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var run = await ImportedRunAsync(kit);

        var recon = await kit.Runs.GetReconciliationAsync(run.Id);
        var analyst = recon.Rows.Single(r => r.EmployeeId == kit.Fx.AnalystId);
        Assert.Equal((8m, 50m, 400m, 400m, 0m), (analyst.WorkedHours, analyst.PayRate!.Value, analyst.ExpectedGross, analyst.RegisterGross, analyst.Unexplained));

        // The GL accrued 8 h at the 87.50 cost rate: gross is 300 under it.
        Assert.Equal((700m, -300m), (analyst.AccruedLaborCost, analyst.LaborVariance));

        // pm@ is salaried with no hours: the whole period salary is salaried variance.
        var pm = recon.Rows.Single(r => r.EmployeeId == kit.Fx.PmId);
        Assert.Equal((4_000m, 4_000m, 0m), (pm.SalariedVariance, pm.ExpectedGross, pm.Unexplained));
        Assert.Equal(4_400m, recon.Totals.RegisterGross);
        Assert.Equal((336.60m, 450m, 3_370m), (recon.EmployerTaxes, recon.EmployerBenefits, recon.NetPay));
    }

    [Fact]
    public async Task Posting_writes_a_balanced_payroll_journal_once_and_void_reverses_it()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var run = await ImportedRunAsync(kit);

        var posted = await kit.Runs.PostAsync(run.Id);
        Assert.Equal(PayrollRunState.Posted, posted.Status);
        Assert.NotNull(posted.JournalNumber);
        Assert.Equal(posted.JournalEntryId, (await kit.Runs.PostAsync(run.Id)).JournalEntryId);

        var entries = await kit.PayrollEntriesAsync();
        var entry = Assert.Single(entries);
        Assert.Equal((JournalSource.Payroll, run.RegisterId ?? (await kit.Runs.GetAsync(run.Id)).RegisterId), (entry.Source, entry.SourceId));
        Assert.Equal(new DateOnly(2026, 10, 23), entry.Date);
        Assert.Equal(entry.TotalDebit, entry.TotalCredit);

        var numbers = await kit.AccountNumbersAsync();
        decimal Net(string account) => entry.Lines.Where(l => numbers[l.AccountId] == account).Sum(l => l.Debit - l.Credit);
        Assert.Equal(700m, Net("2100"));     // accrued labor cleared (analyst 8 h × 87.50; pm@ has no accrual)
        Assert.Equal(3_700m, Net("6050"));   // gross 4,400 − 700
        Assert.Equal(336.60m, Net("6020"));
        Assert.Equal(450m, Net("6030"));
        Assert.Equal(-3_370m, Net("2120"));
        Assert.Equal(-(860m + 336.60m), Net("2130"));
        Assert.Equal(-(170m + 450m), Net("2140"));

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Runs.VoidAsync(run.Id, new VoidPayrollRequest(" ")));
        var voided = await kit.Runs.VoidAsync(run.Id, new VoidPayrollRequest("Wrong register"));
        Assert.Equal(PayrollRunState.Voided, voided.Status);
        entries = await kit.PayrollEntriesAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal(JournalStatus.Reversed, entries[0].Status);
        Assert.All(entries.SelectMany(e => e.Lines).GroupBy(l => l.AccountId), g => Assert.Equal(0m, g.Sum(l => l.Debit - l.Credit)));

        // A new run for the same period starts over.
        var next = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        Assert.NotEqual(run.Id, next.Id);
        Assert.Equal(PayrollRunState.Draft, next.Status);
    }

    [Theory]
    [InlineData(Owner)]
    [InlineData(Editor)]
    [InlineData(Viewer)]
    [InlineData(Timekeeper)]
    public async Task Payroll_is_finance_and_admin_only(string email)
    {
        await using var kit = await PayrollKit.CreateAsync();
        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        kit.As(email);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Groups.ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Groups.GeneratePeriodsAsync(kit.Group.Id, 2027));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Profiles.ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Profiles.RevealAsync(kit.Fx.PmId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Profiles.SaveAsync(kit.Fx.PmId, new SavePayProfileRequest(null, null, PayTypeKind.Hourly, HourlyRate: 1)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Providers.ListProvidersAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Runs.PreviewHoursAsync(run.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Runs.PostAsync(run.Id));
    }

    [Fact]
    public async Task Pay_rates_are_masked_until_revealed_and_reveals_are_security_events()
    {
        await using var kit = await PayrollKit.CreateAsync();
        kit.As(Admin);

        var listed = (await kit.Profiles.ListAsync()).Single(p => p.EmployeeId == kit.Fx.PmId);
        Assert.True(listed.HasRate);
        Assert.Null(listed.AnnualSalary);
        Assert.Null((await kit.Profiles.GetAsync(kit.Fx.AnalystId)).HourlyRate);

        var revealed = await kit.Profiles.RevealAsync(kit.Fx.PmId);
        Assert.Equal(104_000m, revealed.AnnualSalary);

        // Saving without a rate keeps the one on file; the response is masked again.
        var saved = await kit.Profiles.SaveAsync(kit.Fx.PmId, new SavePayProfileRequest(kit.Group.Id, "1001-A", PayTypeKind.Salaried));
        Assert.Null(saved.AnnualSalary);
        Assert.Equal(104_000m, (await kit.Profiles.RevealAsync(kit.Fx.PmId)).AnnualSalary);
        await kit.Profiles.SaveAsync(kit.Fx.PmId, new SavePayProfileRequest(kit.Group.Id, "1001", PayTypeKind.Salaried, AnnualSalary: 110_000m));

        await using var db = await kit.Factory.CreateAsync();
        var events = await db.Set<SecurityEvent>().AsNoTracking().Select(e => e.EventType).ToListAsync();
        Assert.Equal(2, events.Count(e => e == PayrollSecurityEvents.PayRateRevealed));
        Assert.Contains(PayrollSecurityEvents.PayRateChanged, events);

        // Audit rows record that a rate changed, never the rate.
        var audit = await db.Set<AuditEvent>().AsNoTracking().Where(a => a.EntityType == PayrollProfileService.AuditEntityType).ToListAsync();
        Assert.NotEmpty(audit);
        Assert.DoesNotContain(audit, a => (a.AfterJson + a.BeforeJson).Contains("104000") || (a.AfterJson + a.BeforeJson).Contains("110000"));
        Assert.Contains(audit, a => a.Reason == "Pay rate changed");
    }

    [Fact]
    public async Task Providers_list_paylocity_first_and_the_connectable_adapters()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var providers = await kit.Providers.ListProvidersAsync();
        Assert.Equal("paylocity", providers[0].Key);
        Assert.All(providers, p => Assert.True(p.IsSupported));
        Assert.Equal(["adp", "gusto", "paychex"], providers.Where(p => p.HasConnectScreen).Select(p => p.Key).Order());
        Assert.All(providers.Where(p => p.HasConnectScreen), p => Assert.False(p.ApiConfigured));
        Assert.All(providers.Where(p => p.HasConnectScreen), p => Assert.Equal("Not connected", p.Status));
        var adp = await kit.Groups.CreateAsync(new SavePayGroupRequest("ADP", "ADP", PayFrequencyKind.Weekly, "adp", "1", PayrollKit.PeriodStart));
        Assert.Equal("adp", adp.ProviderKey);

        var settings = await kit.Providers.SaveSettingsAsync("paylocity", new SavePayrollProviderSettingsRequest([new("REG", "R1"), new("OT", "O1"), new("LV.PTO", "VAC")], false));
        Assert.Equal("VAC", settings.EarningCodes.Single(m => m.Category == "LV.PTO").EarningCode);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Providers.SaveSettingsAsync("paylocity", new SavePayrollProviderSettingsRequest([new("REG", "R1")], false)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Providers.SyncEmployeesAsync(kit.Group.Id, dryRun: false));
        var dry = await kit.Providers.SyncEmployeesAsync(kit.Group.Id, dryRun: true);
        Assert.All(dry.Items, i => Assert.Equal(EmployeeSyncAction.Update, i.Action));
    }

    /// <summary>A run with analyst@'s 8 approved hours exported and <see cref="PayrollKit.Register"/> imported.</summary>
    private static async Task<PayrollRunDto> ImportedRunAsync(PayrollKit kit)
    {
        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true));
        await kit.Runs.ImportRegisterAsync(run.Id, new ImportRegisterRequest("register.csv", PayrollKit.Register));
        return await kit.Runs.GetAsync(run.Id);
    }
}
