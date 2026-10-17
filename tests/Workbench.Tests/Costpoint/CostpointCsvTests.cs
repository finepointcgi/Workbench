using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Costpoint;

namespace Workbench.Tests.Costpoint;

public sealed class CostpointCsvTests
{
    [Fact]
    public void Parses_projects_with_quotes_any_column_order_and_defaults()
    {
        const string csv = "name,project_id,start_date,extra\r\n\"Logistics, Phase \"\"I\"\"\",1002.01,2026-01-01,ignored\r\nSupport,1001.01,,x\r\n";

        var result = CostpointCsv.ParseProjects(new StringReader(csv));

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new CpProjectRecord("1002.01", "Logistics, Phase \"I\"", null, new DateOnly(2026, 1, 1), null, true), result.Rows[0]);
        Assert.Null(result.Rows[1].Start);
    }

    [Fact]
    public void Bad_rows_are_skipped_and_reported_with_line_numbers()
    {
        const string csv = """
            project_id,wbs_id,fiscal_period,hours,cost
            1001.01,1001.01.01,2026-09,6,870
            1001.01,1001.01.01,2026-13,4,580
            1001.01,,2026-10,4,580
            1001.01,1001.01.02,2026-10,abc,580
            1001.01,1001.01.02,2026-10,4

            1001.01,1001.01.03,2026-10,1.5,"1,200.50"
            """;

        var result = CostpointCsv.ParseActuals(new StringReader(csv));

        Assert.Equal([("1001.01.01", 6m, 870m), ("1001.01.03", 1.5m, 1200.50m)], result.Rows.Select(r => (r.WbsId, r.Hours, r.Cost)));
        Assert.Equal(4, result.Errors.Count);
        Assert.Contains("line 3", result.Errors[0]);
        Assert.Contains("fiscal_period '2026-13'", result.Errors[0]);
        Assert.Contains("wbs_id is required", result.Errors[1]);
        Assert.Contains("hours 'abc' is not a number", result.Errors[2]);
        Assert.Contains("expected 5 fields but found 4", result.Errors[3]);
    }

    [Fact]
    public void Missing_required_header_fails_the_file()
    {
        var ex = Assert.Throws<FormatException>(() => CostpointCsv.ParseActuals(new StringReader("project_id,wbs_id,hours\n1,2,3\n")));

        Assert.Contains("fiscal_period", ex.Message);
        Assert.Contains("cost", ex.Message);
    }

    [Fact]
    public void Budget_period_blank_or_total_means_whole_life()
    {
        const string csv = "project_id,wbs_id,fiscal_period,hours,amount\n1001.01,,TOTAL,10,100\n1001.01,1001.02,,1,2\n1001.01,1001.02,2026-10,3,4\n1001.01,1001.02,FY26,3,4\n";

        var result = CostpointCsv.ParseBudgets(new StringReader(csv));

        Assert.Equal([(null, null), ("1001.02", null), ("1001.02", "2026-10")], result.Rows.Select(r => (r.WbsId, r.FiscalPeriod)));
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Booleans_accept_y_n()
    {
        const string csv = "employee_id,name,active\nE1,Pat,N\nE2,Sam,Y\nE3,Lee,maybe\n";

        var result = CostpointCsv.ParseEmployees(new StringReader(csv));

        Assert.Equal([false, true], result.Rows.Select(r => r.Active));
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task Folder_source_reads_the_samples_and_filters_by_project_and_period()
    {
        var source = new CsvFolderCostpointSource(CsvFolderCostpointSource.ResolveFolder("samples/costpoint"), NullLogger<CsvFolderCostpointSource>.Instance);

        var projects = await source.GetProjectsAsync();
        var wbs = await source.GetWbsAsync("1001.01");
        var actuals = await source.GetActualsAsync("1001.01", "2026-10", "2026-10");

        Assert.Contains(projects, p => p.ProjectId == "1001.01");
        Assert.Equal(["1001.01.01", "1001.01.02", "1001.01.03", "1001.02"], wbs.Select(w => w.WbsId));
        Assert.All(actuals, a => Assert.Equal("2026-10", a.FiscalPeriod));
        Assert.Empty(source.TakeWarnings());
    }

    [Fact]
    public async Task Folder_source_fails_when_a_file_is_missing()
    {
        var folder = Directory.CreateTempSubdirectory("cp-csv").FullName;
        try
        {
            var source = new CsvFolderCostpointSource(folder, NullLogger<CsvFolderCostpointSource>.Instance);
            await Assert.ThrowsAsync<FileNotFoundException>(() => source.GetProjectsAsync());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
