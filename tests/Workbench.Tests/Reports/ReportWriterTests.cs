using Workbench.Contracts;
using Workbench.Contracts.Reports;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Reports;

public sealed class ReportWriterTests
{
    private static ReportDefinition Time(params string[] columns) =>
        new(ReportSourceKeys.TimeEntries, columns.Select(c => new ReportColumnSpec(c)).ToList(), Range: ReportDateRange.All);

    [Fact]
    public async Task Sources_and_fields_follow_each_users_access()
    {
        await using var kit = await ReportsKit.CreateAsync();
        var analyst = await kit.As(Editor).Writer.ListSourcesAsync();
        Assert.Equal([ReportSourceKeys.TimeEntries, ReportSourceKeys.Odcs, ReportSourceKeys.ProjectsWbs, ReportSourceKeys.Employees], analyst.Select(s => s.Key));
        Assert.DoesNotContain(analyst.Single(s => s.Key == ReportSourceKeys.TimeEntries).Fields, f => f.Key is "cost_rate" or "labor_cost");
        Assert.DoesNotContain(analyst.Single(s => s.Key == ReportSourceKeys.ProjectsWbs).Fields, f => f.Key == "contract_ceiling");
        Assert.DoesNotContain(analyst.Single(s => s.Key == ReportSourceKeys.Employees).Fields, f => f.Key == "cost_rate");

        var pm = await kit.As(Owner).Writer.ListSourcesAsync();
        Assert.Contains(pm, s => s.Key == ReportSourceKeys.Invoices);
        Assert.Contains(pm.Single(s => s.Key == ReportSourceKeys.ProjectsWbs).Fields, f => f.Key == "contract_ceiling");
        Assert.DoesNotContain(pm, s => s.Key is ReportSourceKeys.GlLines or ReportSourceKeys.ApBills);

        var finance = await kit.As(Finance).Writer.ListSourcesAsync();
        Assert.Equal(7, finance.Count);
        Assert.Contains(finance.Single(s => s.Key == ReportSourceKeys.TimeEntries).Fields, f => f.Key == "labor_cost" && f.Restricted);
    }

    [Fact]
    public async Task Time_entries_show_employees_only_their_own_time_and_no_cost()
    {
        await using var kit = await ReportsKit.CreateAsync();
        var mine = await kit.As(Editor).Writer.RunAsync(Time("employee", "hours", "cost_rate", "labor_cost"));
        Assert.Equal(["employee", "hours"], mine.Columns.Select(c => c.Key));
        Assert.Contains(mine.Notes, n => n.Contains("don't have access"));
        var row = Assert.Single(mine.Details());
        Assert.Equal("Alex Rivera", row.Text(mine, "employee"));

        var finance = await kit.As(Finance).Writer.RunAsync(Time("employee", "hours", "cost_rate", "labor_cost"));
        Assert.Equal(3, finance.Details().Count());
        Assert.Equal(700m, finance.Details().Single(r => r.Text(finance, "employee") == "Alex Rivera").Num(finance, "labor_cost"));

        // A filter on a hidden field is dropped rather than used to infer it.
        var probe = await kit.As(Editor).Writer.RunAsync(Time("employee", "hours") with { Filters = [new("cost_rate", ReportFilterOperator.GreaterThan, "1000")] });
        Assert.Single(probe.Details());
    }

    [Fact]
    public async Task Filters_grouping_subtotals_and_sort()
    {
        await using var kit = await ReportsKit.CreateAsync();
        kit.As(Finance);
        var grouped = await kit.Writer.RunAsync(Time("employee", "hours") with { GroupBy = ["project"] });
        Assert.Equal(["project", "employee", "hours"], grouped.Columns.Select(c => c.Key));
        var subtotals = grouped.Rows.Where(r => r.Kind == ReportRowKind.Subtotal).ToList();
        Assert.Equal(["1001", "2002"], subtotals.Select(r => r.Text(grouped, "project")));
        Assert.Equal([8m, 6m], subtotals.Select(r => r.Num(grouped, "hours")));
        Assert.Equal(14m, grouped.Total().Num(grouped, "hours"));

        var summary = await kit.Writer.RunAsync(Time("hours") with { GroupBy = ["project"], ShowDetails = false });
        Assert.Empty(summary.Details());

        var filtered = await kit.Writer.RunAsync(Time("employee", "hours") with { Filters = [new("hours", ReportFilterOperator.GreaterOrEqual, "4")], Sort = [new("hours", true)] });
        Assert.Equal(["Alex Rivera", "Val Chen"], filtered.Details().Select(r => r.Text(filtered, "employee")));

        var oneOf = await kit.Writer.RunAsync(Time("employee") with { Filters = [new("employee", ReportFilterOperator.OneOf, "val chen, Olly Out")] });
        Assert.Equal(2, oneOf.Details().Count());

        var contains = await kit.Writer.RunAsync(Time("wbs") with { Filters = [new("wbs", ReportFilterOperator.StartsWith, "2002")] });
        Assert.Equal(2, contains.Details().Count());

        var count = await kit.Writer.RunAsync(Time("project", "employee") with { GroupBy = ["project"], Columns = [new("employee", Aggregate: ReportAggregate.Count)] });
        Assert.Equal(3m, count.Total().Num(count, "employee"));

        var dated = await kit.Writer.RunAsync(Time("hours") with { Range = ReportDateRange.Custom, From = new DateOnly(2026, 10, 15) });
        Assert.Empty(dated.Details());

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Writer.RunAsync(Time("nope")));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Writer.RunAsync(Time("hours") with { Filters = [new("hours", ReportFilterOperator.Equals, "abc")] }));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Writer.RunAsync(new ReportDefinition("sql", [new("x")])));
    }

    [Fact]
    public async Task Every_data_source_applies_its_row_rules()
    {
        await using var kit = await ReportsKit.CreateAsync();

        // ODCs: Finance all; the PM their project's; an employee their own; others none.
        var odcs = new ReportDefinition(ReportSourceKeys.Odcs, [new("description"), new("amount")]);
        Assert.Equal(2, (await kit.As(Finance).Writer.RunAsync(odcs)).Details().Count());
        Assert.Equal(2, (await kit.As(Owner).Writer.RunAsync(odcs)).Details().Count());
        Assert.Equal(["Taxi"], (await kit.As(Editor).Writer.RunAsync(odcs)).Details().Select(r => r.Values[0]));
        Assert.Empty((await kit.As(Viewer).Writer.RunAsync(odcs)).Details());

        // GL and AP: Finance only. Invoices: Finance and PMs.
        var gl = new ReportDefinition(ReportSourceKeys.GlLines, [new("account"), new("net")]);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Writer.RunAsync(gl));
        await kit.As(Finance).Writer.RunAsync(gl);
        var ap = new ReportDefinition(ReportSourceKeys.ApBills, [new("number")]);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Writer.RunAsync(ap));
        var invoices = new ReportDefinition(ReportSourceKeys.Invoices, [new("number")]);
        await kit.As(Owner).Writer.RunAsync(invoices);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Viewer).Writer.RunAsync(invoices));

        // Projects: contract values only for Finance and the contract's PM.
        var projects = new ReportDefinition(ReportSourceKeys.ProjectsWbs, [new("project"), new("contract_ceiling")]);
        var pm = await kit.As(Owner).Writer.RunAsync(projects);
        Assert.Equal("1000000", pm.Details().First(r => r.Values[0] == "1001").Values[1]?.Split('.')[0]);
        Assert.Null(pm.Details().First(r => r.Values[0] == "2002").Values[1]);
        var viewer = await kit.As(Viewer).Writer.RunAsync(projects);
        Assert.Equal(["project"], viewer.Columns.Select(c => c.Key));

        // Employees: the directory for everyone, the cost rate for Finance.
        var employees = new ReportDefinition(ReportSourceKeys.Employees, [new("employee"), new("cost_rate")]);
        var directory = await kit.As(Viewer).Writer.RunAsync(employees);
        Assert.Equal(4, directory.Details().Count());
        Assert.Equal(["employee"], directory.Columns.Select(c => c.Key));
        var rates = await kit.As(Finance).Writer.RunAsync(employees);
        Assert.Equal(87.5m, rates.Details().Single(r => r.Values[0] == "Alex Rivera").Num(rates, "cost_rate"));
    }
}
