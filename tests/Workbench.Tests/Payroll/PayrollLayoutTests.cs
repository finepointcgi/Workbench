using Workbench.Application.Payroll;
using Workbench.Contracts.Payroll;
using Workbench.Tests.Imports;

namespace Workbench.Tests.Payroll;

/// <summary>Configurable Paylocity hours export and register layouts, their "test against a sample file", and the run using them.</summary>
public sealed class PayrollLayoutTests
{
    private static readonly PayrollHoursExport Export = new("W1234", "BW", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 23),
    [
        new("2002", "Alex Rivera", "REG", 72m, "ENG"),
        new("2002", "Alex Rivera", "PTO", 8m, "ENG"),
        new("1001", "Pat Morgan", "OT", 2.5m, null),
        new("1001", "Pat Morgan", "REG", 0m, null),
    ]);

    private static RegisterLayoutDto DetailLayout => PayrollLayouts.DefaultRegister with
    {
        Columns = [new(RegisterField.EmployeeId, ["Emp #"]), new(RegisterField.EmployeeName, ["Employee"]), new(RegisterField.CheckDate, ["Pay Date"])],
        CodeColumn = "Det Code",
        AmountColumn = "Amount",
        CodeMap =
        [
            new("REG", RegisterAmountTarget.GrossPay), new("OT", RegisterAmountTarget.GrossPay),
            new("FITW", RegisterAmountTarget.EmployeeTaxes), new("SS", RegisterAmountTarget.EmployeeTaxes), new("MED", RegisterAmountTarget.EmployeeTaxes),
            new("ER-SS", RegisterAmountTarget.EmployerTaxes), new("ER-MED", RegisterAmountTarget.EmployerTaxes),
            new("401K", RegisterAmountTarget.Deductions), new("ER-MED-PLAN", RegisterAmountTarget.EmployerBenefits),
        ],
    };

    [Fact]
    public void The_default_layouts_are_todays_paylocity_formats()
    {
        Assert.Equal(PaylocityFormats.WriteHours(Export), PayrollLayouts.WriteHours(Export, PayrollLayouts.DefaultHours));

        var sample = File.ReadAllText(Samples.PathOf("samples/payroll/paylocity-register-sample.csv"));
        var fixedFormat = PaylocityFormats.ParseRegister(sample);
        var configurable = PayrollLayouts.ParseRegister(sample, PayrollLayouts.DefaultRegister);
        Assert.Equal(fixedFormat.Rows, configurable.Rows);
        Assert.Equal(3, configurable.Rows.Count);
    }

    [Fact]
    public void A_custom_hours_layout_changes_columns_order_delimiter_and_date_format()
    {
        var layout = new HoursLayoutDto(
        [
            new("EMP_ID", HoursColumnValue.EmployeeId),
            new("LAST", HoursColumnValue.LastName),
            new("FIRST", HoursColumnValue.FirstName),
            new("EARN", HoursColumnValue.EarningCode),
            new("HRS", HoursColumnValue.Hours),
            new("WEEK_END", HoursColumnValue.PeriodEnd),
            new("SOURCE", HoursColumnValue.Constant, "WORKBENCH"),
            new("DIV", HoursColumnValue.CostCenter),
        ], "yyyy-MM-dd", "|", HoursFormat: "0.000");
        PayrollLayouts.Validate(layout, PayrollLayouts.DefaultRegister);

        var lines = PayrollLayouts.WriteHours(Export, layout).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
        [
            "EMP_ID|LAST|FIRST|EARN|HRS|WEEK_END|SOURCE|DIV",
            "1001|Morgan|Pat|OT|2.500|2026-10-18|WORKBENCH|",
            "2002|Rivera|Alex|PTO|8.000|2026-10-18|WORKBENCH|ENG",
            "2002|Rivera|Alex|REG|72.000|2026-10-18|WORKBENCH|ENG",
        ], lines);

        var headerless = PayrollLayouts.WriteHours(Export, layout with { IncludeHeader = false, Delimiter = "\\t" });
        Assert.StartsWith("1001\tMorgan\tPat\tOT", headerless);
    }

    [Fact]
    public void Layouts_that_cant_work_are_refused_with_every_problem()
    {
        var bad = new HoursLayoutDto([new("Name", HoursColumnValue.EmployeeName)], "nonsense?", "#");
        var ex = Assert.Throws<ArgumentException>(() => PayrollLayouts.Validate(bad, PayrollLayouts.DefaultRegister with { DateFormats = [] }));
        Assert.True(ex.Message.Contains("delimiter"), ex.Message);
        Assert.Contains("EmployeeId", ex.Message);
        Assert.Contains("earning code", ex.Message);
        Assert.Contains("delimiter", ex.Message);
        Assert.Contains("date format", ex.Message);
        Assert.Contains("gross pay", Assert.Throws<ArgumentException>(() => PayrollLayouts.Validate(PayrollLayouts.DefaultHours, DetailLayout with { CodeMap = [] })).Message);
    }

    [Fact]
    public void A_register_with_its_own_headers_and_date_format_parses_with_a_custom_layout()
    {
        const string register = """
            EE Number|Full Name|Paid On|Total Gross|Fed+State Tax|Company Tax|Ded|Co Benefits|Take Home
            2002|Alex Rivera|20261023|400.00|60.00|30.60|20.00|50.00|320.00
            1001|Pat Morgan|20261023|4000.00|800.00|306.00|150.00|400.00|3050.00
            """;
        Assert.Throws<ArgumentException>(() => PayrollLayouts.ParseRegister(register, PayrollLayouts.DefaultRegister));

        var layout = new RegisterLayoutDto(
        [
            new(RegisterField.EmployeeId, ["EE Number"]), new(RegisterField.EmployeeName, ["Full Name"]), new(RegisterField.CheckDate, ["Paid On"]),
            new(RegisterField.GrossPay, ["Total Gross"]), new(RegisterField.EmployeeTaxes, ["Fed+State Tax"]), new(RegisterField.EmployerTaxes, ["Company Tax"]),
            new(RegisterField.Deductions, ["Ded"]), new(RegisterField.EmployerBenefits, ["Co Benefits"]), new(RegisterField.NetPay, ["Take Home"]),
        ], ["yyyyMMdd"], "|");
        var parsed = PayrollLayouts.ParseRegister(register, layout);
        Assert.Equal(2, parsed.Rows.Count);
        var alex = parsed.Rows.Single(r => r.ProviderEmployeeId == "2002");
        Assert.Equal((new DateOnly(2026, 10, 23), 400m, 60m, 30.6m, 20m, 50m, 320m),
            (alex.CheckDate, alex.GrossWages, alex.EmployeeTaxes, alex.EmployerTaxes, alex.Deductions, alex.EmployerBenefits, alex.NetPay));
    }

    [Fact]
    public void A_detail_register_adds_codes_into_totals_through_the_code_map()
    {
        var detail = File.ReadAllText(Samples.PathOf("samples/payroll/paylocity-register-detail-sample.csv"));
        var test = PayrollLayouts.TestRegister(detail, DetailLayout);
        Assert.True(test.Ok, test.Error);
        Assert.Equal(2, test.Checks);
        Assert.Equal(["MEMO"], test.UnmappedCodes);
        Assert.Contains(test.Warnings, w => w.Contains("net pay is gross"));
        Assert.Equal(["Det"], test.UnusedHeaders);

        // The same checks as the summary register sample, to the penny.
        var summary = PaylocityFormats.ParseRegister(File.ReadAllText(Samples.PathOf("samples/payroll/paylocity-register-sample.csv")));
        var parsed = PayrollLayouts.ParseRegister(detail, DetailLayout);
        foreach (var id in new[] { "1001", "1002" })
        {
            var a = summary.Rows.Single(r => r.ProviderEmployeeId == id);
            var b = parsed.Rows.Single(r => r.ProviderEmployeeId == id);
            Assert.Equal((a.CheckDate, a.GrossWages, a.EmployeeTaxes, a.EmployerTaxes, a.Deductions, a.EmployerBenefits, a.NetPay),
                (b.CheckDate, b.GrossWages, b.EmployeeTaxes, b.EmployerTaxes, b.Deductions, b.EmployerBenefits, b.NetPay));
        }
    }

    [Fact]
    public void Testing_a_sample_shows_what_parses_and_what_doesnt()
    {
        var missing = PayrollLayouts.TestRegister("Employee Id,Check Date,Net Pay\n7,10/23/2026,90\n", PayrollLayouts.DefaultRegister);
        Assert.False(missing.Ok);
        Assert.Contains("Gross Pay", missing.Error);
        Assert.Null(missing.Columns.Single(c => c.Field == RegisterField.GrossPay).MatchedHeader);
        Assert.Equal("Net Pay", missing.Columns.Single(c => c.Field == RegisterField.NetPay).MatchedHeader);

        var test = PayrollLayouts.TestRegister("""
            Payroll Register,,,,,
            Employee Id,Check Date,Gross Pay,Net Pay,Check Number,Department
            1,10/23/2026,100.00,80.00,555,ENG
            2,23/10/2026,100.00,80.00,556,ENG
            3,10/23/2026,lots,80.00,557,ENG
            ,,200.00,160.00,,
            """, PayrollLayouts.DefaultRegister);
        Assert.False(test.Ok);
        Assert.Equal(1, test.Checks);
        Assert.Equal(["Check Number", "Department"], test.UnusedHeaders);
        Assert.Equal([3, 4, 5], test.Rows.Select(r => r.Line));
        Assert.True(test.Rows[0].Parsed);
        Assert.Contains("check date", test.Rows[1].Message);
        Assert.Contains("isn't an amount", test.Rows[2].Message);
        var refused = Assert.Throws<ArgumentException>(() => PayrollLayouts.ParseRegister(test.Rows.Count > 0 ? """
            Employee Id,Check Date,Gross Pay,Net Pay
            2,23/10/2026,100.00,80.00
            """ : "", PayrollLayouts.DefaultRegister));
        Assert.StartsWith("Line 2:", refused.Message);
    }

    [Fact]
    public void Testing_the_hours_layout_against_an_import_template_lines_up_the_columns()
    {
        var template = File.ReadAllText(Samples.PathOf("samples/payroll/paylocity-hours-template-sample.csv"));
        var test = PayrollLayouts.TestTemplate(template, PayrollLayouts.DefaultHours);
        Assert.False(test.Ok);
        Assert.Equal(6, test.Columns.Count);
        var hours = test.Columns.Single(c => c.TemplateHeader == "Hours");
        Assert.Equal((4, 9, false), (hours.Position, hours.LayoutPosition, hours.SamePosition));
        Assert.Contains("Company Code", test.ExtraLayoutColumns);
        Assert.StartsWith("Company Code,Employee Id", test.Example);

        var matching = new HoursLayoutDto(
        [
            new("Employee Id", HoursColumnValue.EmployeeId), new("Det", HoursColumnValue.Det), new("Det Code", HoursColumnValue.EarningCode),
            new("Hours", HoursColumnValue.Hours), new("Cost Center 1", HoursColumnValue.CostCenter), new("Check Date", HoursColumnValue.CheckDate),
        ]);
        var ok = PayrollLayouts.TestTemplate(template, matching);
        Assert.True(ok.Ok);
        Assert.Empty(ok.ExtraLayoutColumns);
    }

    [Fact]
    public async Task A_saved_layout_is_used_by_the_hours_export_and_the_register_import()
    {
        await using var kit = await PayrollKit.CreateAsync();
        await kit.AddApprovedHoursAsync(kit.Fx.ViewerId, kit.Fx.Wbs1, (PayrollKit.PeriodStart.AddDays(7), 4));
        var layouts = kit.Get<IPayrollLayoutService>();
        Assert.True((await layouts.GetAsync("paylocity")).HoursIsDefault);
        await Assert.ThrowsAsync<ArgumentException>(() => layouts.GetAsync("adp"));

        var hours = new HoursLayoutDto(
        [
            new("Employee Id", HoursColumnValue.EmployeeId), new("Det", HoursColumnValue.Det), new("Det Code", HoursColumnValue.EarningCode),
            new("Hours", HoursColumnValue.Hours), new("Cost Center 1", HoursColumnValue.CostCenter), new("Check Date", HoursColumnValue.CheckDate),
        ], "yyyy-MM-dd", "|");
        var saved = await layouts.SaveAsync("paylocity", new SavePayrollLayoutRequest(hours, DetailLayout with { Columns = [.. DetailLayout.Columns], DateFormats = ["MM/dd/yyyy"] }));
        Assert.False(saved.HoursIsDefault);
        Assert.Equal(Workbench.Tests.Identity.SeededHost.Finance, saved.UpdatedBy);

        var run = await kit.Runs.StartAsync(kit.Group.Id, kit.Period.Id);
        var file = await kit.Runs.ExportHoursAsync(run.Id, new ExportHoursRequest(AcknowledgeIssues: true));
        var lines = file.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Employee Id|Det|Det Code|Hours|Cost Center 1|Check Date", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("3003|E|REG|") && l.EndsWith("|2026-10-23"));

        const string detail = """
            Emp #,Employee,Pay Date,Det Code,Amount
            2002,Alex Rivera,10/23/2026,REG,400.00
            2002,Alex Rivera,10/23/2026,FITW,60.00
            2002,Alex Rivera,10/23/2026,ER-SS,30.60
            2002,Alex Rivera,10/23/2026,401K,20.00
            2002,Alex Rivera,10/23/2026,ER-MED-PLAN,50.00
            """;
        var imported = await kit.Runs.ImportRegisterAsync(run.Id, new ImportRegisterRequest("detail.csv", detail));
        var line = imported.Lines.Single();
        Assert.Equal((kit.Fx.AnalystId, 400m, 60m, 30.6m, 20m, 50m, 320m),
            (line.EmployeeId, line.GrossWages, line.EmployeeTaxes, line.EmployerTaxes, line.Deductions, line.EmployerBenefits, line.NetPay));

        var reset = await layouts.ResetAsync("paylocity");
        Assert.True(reset.HoursIsDefault && reset.RegisterIsDefault);
        var test = await layouts.TestRegisterAsync("paylocity", new TestRegisterSampleRequest(PayrollKit.Register));
        Assert.True(test.Ok);
        Assert.Equal(4_400m, test.TotalGross);
    }
}
