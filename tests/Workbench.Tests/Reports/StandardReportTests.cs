using Workbench.Contracts;
using Workbench.Contracts.Reports;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Reports;

public sealed class StandardReportTests
{
    [Fact]
    public async Task Catalog_marks_what_each_user_can_run()
    {
        await using var kit = await ReportsKit.CreateAsync();
        var analyst = await kit.As(Editor).Standard.ListAsync();
        Assert.Equal(StandardReportKeys.All.Count, analyst.Count(r => !r.PageOnly)); // plus the pages listed to open (EAC, incurred cost)
        Assert.False(analyst.Single(r => r.Key == StandardReportKeys.ProjectStatus).Available);
        Assert.False(analyst.Single(r => r.Key == StandardReportKeys.ArAging).Available);
        Assert.True(analyst.Single(r => r.Key == StandardReportKeys.LaborDistribution).Available);

        var pm = await kit.As(Owner).Standard.ListAsync();
        Assert.True(pm.Single(r => r.Key == StandardReportKeys.ProjectStatus).Available);
        Assert.True(pm.Single(r => r.Key == StandardReportKeys.FundingStatus).Available);
        Assert.False(pm.Single(r => r.Key == StandardReportKeys.IndirectRateVariance).Available);

        Assert.All(await kit.As(Finance).Standard.ListAsync(), r => Assert.True(r.Available));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Standard.RunAsync("nope", new ReportParameters()));
    }

    [Fact]
    public async Task Labor_distribution_totals_and_cost_follow_the_hours_and_cost_rules()
    {
        await using var kit = await ReportsKit.CreateAsync();

        var finance = await kit.As(Finance).Standard.RunAsync(StandardReportKeys.LaborDistribution, ReportsKit.Day);
        Assert.Equal(14m, finance.Total().Num(finance, "hours"));
        Assert.Equal(700m, finance.Total().Num(finance, "cost"));
        Assert.Equal(3, finance.Details().Count());

        var analyst = await kit.As(Editor).Standard.RunAsync(StandardReportKeys.LaborDistribution, ReportsKit.Day);
        Assert.DoesNotContain(analyst.Columns, c => c.Key == "cost");
        Assert.Equal(8m, analyst.Total().Num(analyst, "hours"));
        Assert.All(analyst.Details(), r => Assert.Equal("Alex Rivera", r.Text(analyst, "employee")));

        // pm@ supervises analyst@ and viewer@.
        var pm = await kit.As(Owner).Standard.RunAsync(StandardReportKeys.LaborDistribution, ReportsKit.Day);
        Assert.Equal(12m, pm.Total().Num(pm, "hours"));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            kit.As(Editor).Standard.RunAsync(StandardReportKeys.LaborDistribution, ReportsKit.Day with { EmployeeId = kit.Data.ViewerId }));

        // Drill to the entries: cost only for Finance.
        var row = finance.Details().First(r => r.Text(finance, "employee") == "Alex Rivera");
        var drill = await kit.As(Finance).Standard.DrillAsync(StandardReportKeys.LaborDistribution, ReportsKit.Day, row.DrillKey!);
        Assert.Equal(700m, drill.Details().Single().Num(drill, "cost"));
        var own = await kit.As(Editor).Standard.DrillAsync(StandardReportKeys.LaborDistribution, ReportsKit.Day, analyst.Details().First().DrillKey!);
        Assert.DoesNotContain(own.Columns, c => c.Key == "cost");
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Viewer).Standard.DrillAsync(StandardReportKeys.LaborDistribution, ReportsKit.Day, row.DrillKey!));
    }

    [Fact]
    public async Task Project_status_report_totals_match_the_project_financials()
    {
        await using var kit = await ReportsKit.CreateAsync();
        var p = ReportsKit.Day with { ProjectId = kit.Data.Project1 };
        var psr = await kit.As(Owner).Standard.RunAsync(StandardReportKeys.ProjectStatus, p);
        var project = psr.Rows.Single(r => r.Kind == ReportRowKind.Subtotal);
        Assert.Equal("1001", project.Text(psr, "project"));
        Assert.Equal(8m, project.Num(psr, "itd_hours"));
        Assert.Equal(700m, project.Num(psr, "itd_direct"));
        Assert.Equal(700m, project.Num(psr, "period_direct"));
        Assert.Equal(400_000m, project.Num(psr, "funded"));
        Assert.Equal(700m, psr.Details().Single(r => r.Text(psr, "wbs") == "1001.01").Num(psr, "itd_direct"));

        // Finance sees both open projects and a total.
        var all = await kit.As(Finance).Standard.RunAsync(StandardReportKeys.ProjectStatus, ReportsKit.Day);
        Assert.Equal(2, all.Rows.Count(r => r.Kind == ReportRowKind.Subtotal));
        Assert.Equal(14m, all.Total().Num(all, "itd_hours"));

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Editor).Standard.RunAsync(StandardReportKeys.ProjectStatus, p));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.As(Owner).Standard.RunAsync(StandardReportKeys.ProjectStatus, ReportsKit.Day with { ProjectId = kit.Data.Project2 })); // not on 2002

        // Drill: the PM sees hours, not per-entry labor cost.
        var wbsRow = psr.Details().Single(r => r.Text(psr, "wbs") == "1001.01");
        var drill = await kit.As(Owner).Standard.DrillAsync(StandardReportKeys.ProjectStatus, p, wbsRow.DrillKey!);
        var labor = drill.Details().Single(r => r.Text(drill, "kind") == "Labor");
        Assert.Equal(8m, labor.Num(drill, "hours"));
        Assert.Null(labor.Text(drill, "amount"));
        var financeDrill = await kit.As(Finance).Standard.DrillAsync(StandardReportKeys.ProjectStatus, p, wbsRow.DrillKey!);
        Assert.Equal(700m, financeDrill.Details().Single(r => r.Text(financeDrill, "kind") == "Labor").Num(financeDrill, "amount"));
    }

    [Fact]
    public async Task Funding_status_lists_only_readable_contracts()
    {
        await using var kit = await ReportsKit.CreateAsync();
        var finance = await kit.As(Finance).Standard.RunAsync(StandardReportKeys.FundingStatus, new ReportParameters());
        Assert.Equal(["C1", "C2"], finance.Rows.Where(r => r.Kind == ReportRowKind.Subtotal).Select(r => r.Text(finance, "contract")));
        Assert.Equal(1_500_000m, finance.Total().Num(finance, "ceiling"));
        Assert.Equal(500_000m, finance.Total().Num(finance, "funded"));

        var pm = await kit.As(Owner).Standard.RunAsync(StandardReportKeys.FundingStatus, new ReportParameters());
        var only = Assert.Single(pm.Rows);
        Assert.Equal("C1", only.Text(pm, "contract"));
        Assert.Equal(1_000_000m, only.Num(pm, "ceiling"));

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Viewer).Standard.RunAsync(StandardReportKeys.FundingStatus, new ReportParameters()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Standard.RunAsync(StandardReportKeys.FundingStatus, new ReportParameters(ContractId: kit.Data.Contract2)));
    }

    [Fact]
    public async Task Timesheet_compliance_flags_missing_and_follows_visibility()
    {
        await using var kit = await ReportsKit.CreateAsync();
        var p = new ReportParameters(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 18));
        var finance = await kit.As(Finance).Standard.RunAsync(StandardReportKeys.TimesheetCompliance, p);
        // Four employees × two weekly periods; the week of Oct 5 has no timesheets, so it's missing for everyone.
        Assert.Equal(8, finance.Details().Count());
        var missing = finance.Details().Where(r => r.Text(finance, "missing") == "true").ToList();
        Assert.Equal(5, missing.Count); // Oct 5 for all four, Oct 12 for pm@ (no timesheet).
        var analystWeek = finance.Details().Single(r => r.Text(finance, "employee") == "Alex Rivera" && r.Text(finance, "period_start") == "2026-10-12");
        Assert.Equal(8m, analystWeek.Num(finance, "hours"));

        var analyst = await kit.As(Editor).Standard.RunAsync(StandardReportKeys.TimesheetCompliance, p);
        Assert.All(analyst.Details(), r => Assert.Equal("Alex Rivera", r.Text(analyst, "employee")));
        Assert.Equal(2, analyst.Details().Count());
    }

    [Fact]
    public async Task Aging_and_indirect_rates_are_finance_only()
    {
        await using var kit = await ReportsKit.CreateAsync();
        foreach (var key in new[] { StandardReportKeys.ArAging, StandardReportKeys.ApAging, StandardReportKeys.IndirectRateVariance })
        {
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Standard.RunAsync(key, new ReportParameters()));
        }

        var ar = await kit.As(Finance).Standard.RunAsync(StandardReportKeys.ArAging, new ReportParameters());
        Assert.Equal(0m, ar.Total().Num(ar, "total"));
        var ap = await kit.Standard.RunAsync(StandardReportKeys.ApAging, new ReportParameters());
        Assert.Equal(0m, ap.Total().Num(ap, "total"));
        var rates = await kit.Standard.RunAsync(StandardReportKeys.IndirectRateVariance, new ReportParameters(FiscalYear: 2027));
        Assert.Contains("2027", rates.Subtitle);
    }

    [Fact]
    public async Task Exports_render_xlsx_and_csv()
    {
        await using var kit = await ReportsKit.CreateAsync();
        var xlsx = await kit.As(Finance).Standard.ExportAsync(StandardReportKeys.LaborDistribution, ReportsKit.Day, ReportFileFormat.Xlsx);
        Assert.EndsWith(".xlsx", xlsx.FileName);
        Assert.Equal((byte)'P', xlsx.Content[0]);
        using (var book = new ClosedXML.Excel.XLWorkbook(new MemoryStream(xlsx.Content)))
        {
            Assert.Equal("Labor distribution", book.Worksheet(1).Cell(1, 1).GetString());
        }

        var csv = await kit.Standard.ExportAsync(StandardReportKeys.LaborDistribution, ReportsKit.Day, ReportFileFormat.Csv);
        var text = System.Text.Encoding.UTF8.GetString(csv.Content).TrimStart('﻿');
        Assert.StartsWith("Employee,Employee #,Project,Project name,Period,Hours,Labor cost", text);
        Assert.Contains("Alex Rivera", text);
    }
}
