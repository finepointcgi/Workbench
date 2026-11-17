using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Payroll;
using Workbench.Contracts;
using Workbench.Contracts.Payroll;
using Workbench.Contracts.Time;
using Workbench.Domain.Hr;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Hr;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Payroll;

/// <summary>The file-based Paylocity exchange: golden files, export history and guard, mapping, roster import, permissions.</summary>
public sealed class PaylocityExchangeTests
{
    private static readonly DateOnly Week1 = new(2026, 10, 5);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Payroll", "Fixtures", "paylocity", name));

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // ---- Golden files ----

    [Fact]
    public void Hours_export_matches_the_golden_file()
    {
        var csv = PaylocityFormats.WriteHours(new PayrollHoursExport("W1234", "BW", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 23),
        [
            new("3003", "Val Chen", "HOL", 8m, null),
            new("2002", "Alex Rivera", "REG", 77.5m, "ENG-WEST"),
            new("2002", "Alex Rivera", "OT", 2.5m, "ENG-WEST"),
            new("1001", "Morgan, Pat", "PTO", 8m, "ENG"),
            new("1001", "Morgan, Pat", "REG", 0m, "ENG"),
        ]));
        Assert.Equal(Fixture("hours-golden.csv").ReplaceLineEndings("\r\n"), csv); // git may store the fixture with LF
    }

    [Fact]
    public void Roster_sample_parses_and_ignores_pay_and_identity_columns()
    {
        var rows = PaylocityRosterParser.Parse(Fixture("roster-sample.csv"));
        Assert.Equal(4, rows.Count);
        var sam = rows.Single(r => r.PaylocityId == "4004");
        Assert.Equal(("Sam Nguyen", "sam.nguyen@example.com", false, EmploymentTypeKind.PartTime), (sam.Name, sam.Email, sam.IsExempt, sam.EmploymentType));
        Assert.Equal(new DateOnly(2026, 11, 2), sam.HireDate);
        Assert.Equal(new DateOnly(2026, 9, 30), rows.Single(r => r.PaylocityId == "5005").TerminationDate);
        Assert.Equal("pat.morgan@example.com", rows[0].Email);
    }

    [Fact]
    public void Roster_accepts_last_comma_first_and_reports_missing_columns()
    {
        var rows = PaylocityRosterParser.Parse("Emp Id,Employee Name,Email,Hire Date\r\n77,\"Doe, Jane\",JANE@example.com,2026-01-05\r\n");
        Assert.Equal(("Jane Doe", "jane@example.com", new DateOnly(2026, 1, 5)), (rows[0].Name, rows[0].Email, rows[0].HireDate));
        Assert.Contains("Employee Id", Assert.Throws<ArgumentException>(() => PaylocityRosterParser.Parse("Name\r\nA B\r\n")).Message);
        Assert.Contains("isn't a date", PaylocityRosterParser.Parse("Employee Id,Name,Hire Date\r\n1,A B,notadate\r\n")[0].Errors.Single());
    }

    // ---- Export history and the double-export guard ----

    private static async Task<(PayrollKit Kit, PayrollRunDto Run)> ExportedAsync()
    {
        var kit = await PayrollKit.CreateAsync();
        await kit.AddApprovedHoursAsync(kit.Fx.AnalystId, kit.Fx.Wbs1, (Week1, 8));
        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        return (kit, run);
    }

    [Fact]
    public async Task An_export_is_kept_with_its_checksum_and_a_second_one_needs_a_note()
    {
        var (kit, run) = await ExportedAsync();
        await using var _ = kit;
        var file = await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true));

        var history = await kit.Get<IPaylocityService>().ListExportsAsync();
        var first = Assert.Single(history);
        Assert.Equal((1, "file", false, null), (first.Sequence, first.Method, first.IsReExport, first.Note));
        Assert.Equal(Sha(file.Content), first.Sha256);
        Assert.Equal(file.FileName, first.FileName);
        Assert.True(first.LineCount > 0 && first.TotalHours >= 8m);

        // The same period again is refused, and a blank or whitespace note doesn't count.
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true)));
        Assert.Contains("already exported", refused.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true, "   ")));
        Assert.Single(await kit.Get<IPaylocityService>().ListExportsAsync());

        await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true, "Paylocity rejected the first file"));
        var all = await kit.Get<IPaylocityService>().ListExportsAsync(kit.Group.Id, kit.Period.Id);
        Assert.Equal([2, 1], all.Select(h => h.Sequence));
        Assert.Equal((true, "Paylocity rejected the first file"), (all[0].IsReExport, all[0].Note));
    }

    [Fact]
    public async Task A_voided_run_does_not_reopen_the_period_without_a_note()
    {
        var (kit, run) = await ExportedAsync();
        await using var _ = kit;
        await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true));
        await kit.Runs.VoidAsync(run.Id, new VoidPayrollRequest("wrong period"));

        var again = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        Assert.NotEqual(run.Id, again.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Runs.ExportHoursAsync(again.Id, new ExportHoursRequest(true)));
        await kit.Runs.ExportHoursAsync(again.Id, new ExportHoursRequest(true, "Old run was voided"));
        Assert.Equal(2, (await kit.Get<IPaylocityService>().ListExportsAsync()).Count);
    }

    [Fact]
    public async Task A_refused_export_leaves_no_history_and_the_stored_file_is_downloadable()
    {
        var (kit, run) = await ExportedAsync();
        await using var _ = kit;
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest())); // flagged employees
        Assert.Empty(await kit.Get<IPaylocityService>().ListExportsAsync());

        var file = await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true));
        var id = (await kit.Get<IPaylocityService>().ListExportsAsync()).Single().Id;
        var stored = await kit.Get<IPaylocityService>().GetExportFileAsync(id);
        Assert.Equal((file.Content, Sha(file.Content)), (stored.Content, stored.Sha256));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Get<IPaylocityService>().GetExportFileAsync(Guid.NewGuid()));

        await using (var db = await kit.Factory.CreateAsync())
        {
            var row = await db.Set<Domain.Payroll.PayrollExportRecord>().SingleAsync();
            row.Content += "tampered";
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Get<IPaylocityService>().GetExportFileAsync(id));
    }

    // ---- Mapping ----

    [Fact]
    public async Task Cost_centers_map_org_units_and_the_export_uses_them()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var paylocity = kit.Get<IPaylocityService>();
        Guid unit;
        await using (var db = await kit.Factory.CreateAsync())
        {
            var org = new OrgUnit { Code = "ENG", Name = "Engineering" };
            db.Set<OrgUnit>().Add(org);
            unit = org.Id;
            (await db.Set<Employee>().FirstAsync(e => e.Id == kit.Fx.AnalystId)).OrgUnitId = org.Id;
            await db.SaveChangesAsync();
        }

        await kit.AddApprovedHoursAsync(kit.Fx.AnalystId, kit.Fx.Wbs1, (Week1, 8));
        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        var before = (await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true))).Content;
        Assert.Matches(",2002,Alex Rivera,10/05/2026,10/18/2026,10/23/2026,E,REG,\\d+\\.\\d\\d,,,ENG\r\n", before);

        var mapping = await paylocity.SaveCostCentersAsync([new SaveCostCenterMapping(unit, " 0100 ")]);
        Assert.Equal(("0100", "0100"), (mapping.CostCenters.Single().MappedCode, mapping.CostCenters.Single().SentAs));
        var after = (await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(true, "mapping changed"))).Content;
        Assert.Matches(",E,REG,\\d+\\.\\d\\d,,,0100\r\n", after);

        mapping = await paylocity.SaveCostCentersAsync([new SaveCostCenterMapping(unit, "")]);
        Assert.Equal(("ENG", null), (mapping.CostCenters.Single().SentAs, mapping.CostCenters.Single().MappedCode));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => paylocity.SaveCostCentersAsync([new SaveCostCenterMapping(Guid.NewGuid(), "X")]));
    }

    [Fact]
    public async Task Mapping_flags_missing_and_duplicate_paylocity_ids()
    {
        await using var kit = await PayrollKit.CreateAsync();
        var mapping = await kit.Get<IPaylocityService>().GetMappingAsync();
        Assert.Equal("W1234", Assert.Single(mapping.Companies).CompanyCode);
        Assert.Equal(3, mapping.Employees.Count);
        Assert.All(mapping.Employees, e => Assert.Equal("Ready", e.Status));
        Assert.Empty(mapping.Problems);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Profiles.SaveAsync(kit.Fx.ViewerId, new SavePayProfileRequest(kit.Group.Id, "2002", PayTypeKind.Hourly)));
        await using (var db = await kit.Factory.CreateAsync()) // the profile service refuses duplicates; a bad edit elsewhere could still leave one
        {
            (await db.Set<Domain.Payroll.EmployeePayProfile>().SingleAsync(p => p.EmployeeId == kit.Fx.ViewerId)).ProviderEmployeeId = "2002";
            (await db.Set<Domain.Payroll.EmployeePayProfile>().SingleAsync(p => p.EmployeeId == kit.Fx.PmId)).ProviderEmployeeId = null;
            await db.SaveChangesAsync();
        }

        mapping = await kit.Get<IPaylocityService>().GetMappingAsync();
        Assert.Equal(["DuplicateId", "DuplicateId", "NoId"], mapping.Employees.Select(e => e.Status).Order(StringComparer.Ordinal));
        Assert.Contains(mapping.Problems, p => p.Contains("no Paylocity id"));
        Assert.Contains(mapping.Problems, p => p.Contains("Duplicate Paylocity id(s): 2002"));
    }

    // ---- Roster import ----

    private static async Task<HrKit> RosterKitAsync()
    {
        var kit = await HrKit.CreateAsync();
        await using var db = await kit.Factory.CreateAsync();
        db.Set<OrgUnit>().Add(new OrgUnit { Code = "ENG", Name = "Engineering" });
        await db.SaveChangesAsync();
        kit.As(HrKit.Hr);
        return kit;
    }

    private static PaylocityRosterRequest Req(string csv, bool dryRun = true, Guid? group = null, bool terms = false, bool rehires = false) =>
        new("roster.csv", csv, dryRun, group, terms, rehires);

    [Fact]
    public async Task A_dry_run_shows_the_changes_and_writes_nothing()
    {
        await using var kit = await RosterKitAsync();
        var paylocity = kit.Get<IPaylocityService>();
        var result = await paylocity.ImportRosterAsync(Req(Fixture("roster-sample.csv"), group: kit.Payroll.Group.Id));

        Assert.True(result.DryRun);
        Assert.Equal(Sha(Fixture("roster-sample.csv")), result.Sha256);
        Assert.Equal((1, 2, 0, 1), (result.Count(RosterAction.Create), result.Count(RosterAction.Update), result.Count(RosterAction.Error), result.Count(RosterAction.Skip)));
        var pm = result.Rows.Single(r => r.PaylocityId == "1001");
        Assert.Equal(("Paylocity id", kit.Payroll.Fx.PmId), (pm.MatchedBy, pm.EmployeeId) is ("Paylocity id", var id) ? ("Paylocity id", id) : ("", Guid.Empty));
        Assert.Contains(pm.Changes, c => c.Field == "Email" && c.To == "pat.morgan@example.com" && c.Applied);
        Assert.Contains("Already terminated", result.Rows.Single(r => r.PaylocityId == "5005").Notices.Single());
        Assert.Equal(["Val Chen"], result.MissingFromFile.Select(m => m.Name)); // viewer@ has 3003 and isn't in the file

        await using var db = await kit.Factory.CreateAsync();
        Assert.Equal(4, await db.Set<Employee>().CountAsync());
        Assert.Equal("pm@example.com", (await db.Set<Employee>().FirstAsync(e => e.Id == kit.Payroll.Fx.PmId)).Email);
    }

    [Fact]
    public async Task Applying_creates_and_updates_and_a_second_run_changes_nothing()
    {
        await using var kit = await RosterKitAsync();
        var paylocity = kit.Get<IPaylocityService>();
        var result = await paylocity.ImportRosterAsync(Req(Fixture("roster-sample.csv"), dryRun: false, group: kit.Payroll.Group.Id));
        Assert.Equal((1, 2), (result.Count(RosterAction.Create), result.Count(RosterAction.Update)));

        await using (var db = await kit.Factory.CreateAsync())
        {
            var sam = await db.Set<Employee>().SingleAsync(e => e.EmployeeNumber == "4004");
            Assert.Equal(("sam.nguyen@example.com", false, new DateOnly(2026, 11, 2)), (sam.Email, sam.IsExempt, sam.HireDate));
            Assert.Equal("ENG", (await db.Set<OrgUnit>().SingleAsync(o => o.Id == sam.OrgUnitId)).Code);
            Assert.Equal("4004", (await db.Set<Domain.Payroll.EmployeePayProfile>().SingleAsync(p => p.EmployeeId == sam.Id)).ProviderEmployeeId);
            var hr = await db.Set<EmployeeHrProfile>().SingleAsync(p => p.EmployeeId == sam.Id);
            Assert.Equal(("Analyst", Domain.Payroll.EmploymentType.PartTime), (hr.JobTitle, hr.EmploymentType));
            Assert.False(await db.Set<Employee>().AnyAsync(e => e.EmployeeNumber == "5005")); // already terminated: not created
            Assert.Equal("pat.morgan@example.com", (await db.Set<Employee>().SingleAsync(e => e.Id == kit.Payroll.Fx.PmId)).Email);
            Assert.Contains(await db.Set<Domain.ProjectAccounting.AuditEvent>().ToListAsync(), a => a.EntityType == "Employee" && a.Action == "Updated from Paylocity roster");
            // No pay rate was read or written, and the SSN column left no trace.
            Assert.DoesNotContain(await db.Set<Domain.ProjectAccounting.AuditEvent>().ToListAsync(), a => (a.AfterJson ?? "").Contains("000-00-0000"));
            Assert.Equal(104_000m, (await db.Set<Domain.Payroll.EmployeePayProfile>().SingleAsync(p => p.EmployeeId == kit.Payroll.Fx.PmId)).AnnualSalary);
        }

        var again = await paylocity.ImportRosterAsync(Req(Fixture("roster-sample.csv"), dryRun: false, group: kit.Payroll.Group.Id));
        Assert.Equal((0, 0, 3), (again.Count(RosterAction.Create), again.Count(RosterAction.Update), again.Count(RosterAction.NoChange)));
    }

    [Fact]
    public async Task Workbench_owned_fields_are_never_overwritten_on_an_existing_employee()
    {
        await using var kit = await RosterKitAsync();
        var paylocity = kit.Get<IPaylocityService>();
        await paylocity.SaveRosterSettingsAsync(new SavePaylocityRosterSettingsRequest([PaylocityRosterFields.Email, PaylocityRosterFields.Exempt, PaylocityRosterFields.Email]));
        Assert.Equal([PaylocityRosterFields.Email, PaylocityRosterFields.Exempt], (await paylocity.GetRosterSettingsAsync()).OwnedFields);
        await Assert.ThrowsAsync<ArgumentException>(() => paylocity.SaveRosterSettingsAsync(new SavePaylocityRosterSettingsRequest(["salary"])));

        var csv = "Employee Id,Last Name,First Name,Work Email,FLSA Status,Job Title\r\n2002,Rivera,Alex,new@example.com,Non-Exempt,Lead\r\n";
        var result = await paylocity.ImportRosterAsync(Req(csv, dryRun: false));
        var row = Assert.Single(result.Rows);
        Assert.All(row.Changes.Where(c => c.Field is "Email" or "Exempt (FLSA)"), c => Assert.False(c.Applied));
        Assert.Contains(row.Changes, c => c.Field == "Job title" && c.Applied);

        await using var db = await kit.Factory.CreateAsync();
        var analyst = await db.Set<Employee>().SingleAsync(e => e.Id == kit.Payroll.Fx.AnalystId);
        Assert.Equal(("analyst@example.com", true), (analyst.Email, analyst.IsExempt));
        Assert.Equal("Lead", (await db.Set<EmployeeHrProfile>().SingleAsync(p => p.EmployeeId == analyst.Id)).JobTitle);
    }

    [Fact]
    public async Task Rows_that_could_be_two_people_are_errors_never_guesses()
    {
        await using var kit = await RosterKitAsync();
        var paylocity = kit.Get<IPaylocityService>();
        var csv = string.Join("\r\n",
            "Employee Id,Last Name,First Name,Work Email,Hire Date",
            "2002,Rivera,Alex,pm@example.com,2024-01-01", // id is analyst@, email is pm@
            "9999,Chen,Val,viewer@example.com,2024-01-01", // viewer@ is already linked to 3003
            "7001,Dup,One,dup@example.com,2026-01-01",
            "7001,Dup,Two,dup2@example.com,2026-01-01", // id twice
            "7003,No,Email,,2026-01-01", // new, no email
            "") + "\r\n";
        var result = await paylocity.ImportRosterAsync(Req(csv, dryRun: false));
        var byId = result.Rows.ToDictionary(r => r.PaylocityId + r.Line);
        Assert.Contains("nothing was guessed", result.Rows[0].Errors.Single());
        Assert.Contains("already linked to Paylocity id 3003", result.Rows[1].Errors.Single());
        Assert.Equal(RosterAction.Create, result.Rows[2].Action);
        Assert.Contains("more than once", result.Rows[3].Errors.Single());
        Assert.Contains("needs an email", result.Rows[4].Errors.Single());
        Assert.Equal(4, result.Count(RosterAction.Error));

        await using var db = await kit.Factory.CreateAsync();
        Assert.True(await db.Set<Employee>().AnyAsync(e => e.EmployeeNumber == "7001"));
        Assert.False(await db.Set<Employee>().AnyAsync(e => e.EmployeeNumber == "7003"));
        Assert.Equal("viewer@example.com", (await db.Set<Employee>().SingleAsync(e => e.Id == kit.Payroll.Fx.ViewerId)).Email);
        Assert.NotEmpty(byId);
    }

    [Fact]
    public async Task Email_matches_link_the_id_and_an_employee_number_match_is_found()
    {
        await using var kit = await RosterKitAsync();
        var paylocity = kit.Get<IPaylocityService>();
        await using (var db = await kit.Factory.CreateAsync())
        {
            var outsider = await db.Set<Employee>().SingleAsync(e => e.Id == kit.Payroll.Fx.OutsiderId); // no payroll profile; number E4
            Assert.Equal("E4", outsider.EmployeeNumber);
        }

        // By email, no pay group chosen: matched, but there's nowhere to keep the id.
        var csv = "Employee Id,Name,Email\r\n8008,Olly Out,OUTSIDER@example.com\r\nE4,Olly Out,\r\n";
        var dry = await paylocity.ImportRosterAsync(Req(csv));
        Assert.Equal(("email", RosterAction.NoChange), (dry.Rows[0].MatchedBy, dry.Rows[0].Action));
        Assert.Contains("isn't linked", dry.Rows[0].Notices.Single());
        Assert.Equal(("employee number", RosterAction.NoChange), (dry.Rows[1].MatchedBy, dry.Rows[1].Action));
    }

    [Fact]
    public async Task Terminations_and_rehires_are_flagged_and_only_applied_when_asked()
    {
        await using var kit = await RosterKitAsync();
        var paylocity = kit.Get<IPaylocityService>();
        var termCsv = "Employee Id,Name,Termination Date,Status\r\n2002,Alex Rivera,10/09/2026,Terminated\r\n";

        // A time entry after the termination date is called out.
        await kit.Payroll.AddApprovedHoursAsync(kit.Payroll.Fx.AnalystId, kit.Payroll.Fx.Wbs1, (new DateOnly(2026, 10, 14), 8));
        var flagged = await paylocity.ImportRosterAsync(Req(termCsv, dryRun: false));
        var row = flagged.Rows.Single();
        Assert.Contains(row.Notices, n => n.Contains("still active in Workbench"));
        Assert.Contains(row.Notices, n => n.Contains("time entr") && n.Contains("after the termination date"));
        Assert.False(row.Changes.Single(c => c.Field == "Termination date").Applied);
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.Null((await db.Set<Employee>().SingleAsync(e => e.Id == kit.Payroll.Fx.AnalystId)).TerminationDate);
        }

        await paylocity.ImportRosterAsync(Req(termCsv, dryRun: false, terms: true));
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.Equal(new DateOnly(2026, 10, 9), (await db.Set<Employee>().SingleAsync(e => e.Id == kit.Payroll.Fx.AnalystId)).TerminationDate);
        }

        // Active in Paylocity again: a rehire, flagged and not applied until asked.
        var rehireCsv = "Employee Id,Name,Status,Rehire Date\r\n2002,Alex Rivera,Active,11/02/2026\r\n";
        var notApplied = await paylocity.ImportRosterAsync(Req(rehireCsv, dryRun: false));
        Assert.Contains(notApplied.Rows.Single().Notices, n => n.Contains("terminated in Workbench") && n.Contains("11/02/2026"));
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.NotNull((await db.Set<Employee>().SingleAsync(e => e.Id == kit.Payroll.Fx.AnalystId)).TerminationDate);
        }

        await paylocity.ImportRosterAsync(Req(rehireCsv, dryRun: false, rehires: true));
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.Null((await db.Set<Employee>().SingleAsync(e => e.Id == kit.Payroll.Fx.AnalystId)).TerminationDate);
        }
    }

    // ---- Permissions ----

    [Fact]
    public async Task Payroll_features_are_finance_and_the_roster_import_is_hr()
    {
        await using var kit = await RosterKitAsync();
        var paylocity = kit.Get<IPaylocityService>();
        var csv = "Employee Id,Name\r\n2002,Alex Rivera\r\n";

        kit.As(Finance);
        await paylocity.GetMappingAsync();
        await paylocity.SaveCostCentersAsync([]);
        await paylocity.ListExportsAsync();
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => paylocity.ImportRosterAsync(Req(csv)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => paylocity.GetRosterSettingsAsync());

        kit.As(HrKit.Hr);
        await paylocity.ImportRosterAsync(Req(csv));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => paylocity.GetMappingAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => paylocity.ListExportsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => paylocity.GetExportFileAsync(Guid.NewGuid()));

        kit.As(Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => paylocity.GetMappingAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => paylocity.ImportRosterAsync(Req(csv)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => paylocity.SaveCostCentersAsync([]));
    }
}
