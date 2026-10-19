using System.Text;
using Workbench.Application.Imports;
using Workbench.Application.Migration;
using Workbench.Contracts.Migration;
using Workbench.Tests.Imports;

namespace Workbench.Tests.Migration;

/// <summary>Matching and tolerance rules of the reconciliation engine, on the sample Costpoint reports.</summary>
public sealed class ReconciliationEngineTests
{
    private static TabularFile Sample(ReconReportKind kind, string name) =>
        TabularFile.Read(name, File.ReadAllBytes(Samples.PathOf($"samples/reconcile/{name}")), ReconciliationReports.Get(kind).Fields.SelectMany(f => f.Aliases));

    private static TabularFile Csv(ReconReportKind kind, string content) =>
        TabularFile.Read("report.csv", Encoding.UTF8.GetBytes(content), ReconciliationReports.Get(kind).Fields.SelectMany(f => f.Aliases));

    private static ReconMappingDto Suggest(ReconReportKind kind, TabularFile file) => ReconciliationEngine.Suggest(ReconciliationReports.Get(kind), file, "test");

    private static WorkbenchFigure Figure(string key, params (string Field, decimal Value)[] values) =>
        new([key], null, values.ToDictionary(v => v.Field, v => v.Value));

    /// <summary>Workbench's trial balance after the sample opening balances, by account (ending, debit positive).</summary>
    private static List<WorkbenchFigure> TrialBalance() =>
    [
        Figure("1010", ("Ending", 412_350.17m)), Figure("1110", ("Ending", 186_420.55m)), Figure("1510", ("Ending", 48_900m)), Figure("1590", ("Ending", -12_225m)),
        Figure("2010", ("Ending", -38_617.42m)), Figure("2020", ("Ending", -2_500m)), Figure("2100", ("Ending", -61_880m)), Figure("3100", ("Ending", -250_000m)),
        Figure("3200", ("Ending", -291_480.84m)), Figure("4010", ("Ending", -171_600m)), Figure("5010", ("Ending", 73_040m)), Figure("5110", ("Ending", 3_412.88m)),
        Figure("5140", ("Ending", 25_000m)), Figure("6010", ("Ending", 6_150m)), Figure("6020", ("Ending", 7_825.12m)), Figure("6030", ("Ending", 9_500m)),
        Figure("6110", ("Ending", 4_200m)), Figure("6120", ("Ending", 12_500m)), Figure("6130", ("Ending", 1_804.54m)), Figure("6210", ("Ending", 8_900m)),
        Figure("6220", ("Ending", 3_500m)), Figure("6230", ("Ending", 24_800m)),
    ];

    [Fact]
    public void Trial_balance_sample_maps_debit_minus_credit_skips_the_title_and_total_rows_and_matches_to_the_penny()
    {
        var report = ReconciliationReports.Get(ReconReportKind.TrialBalance);
        var file = Sample(ReconReportKind.TrialBalance, "trial-balance.csv");
        var mapping = Suggest(ReconReportKind.TrialBalance, file);
        var ending = mapping.Columns.Single(c => c.Field == "Ending");
        Assert.Equal(("Debit Balance", "Credit Balance"), (ending.Column, ending.MinusColumn));
        Assert.Equal("Account", mapping.Columns.Single(c => c.Field == "Account").Column);

        var result = ReconciliationEngine.Compare(report, mapping, file, TrialBalance());
        Assert.True(result.AllMatched, string.Join("; ", result.Rows.Where(r => r.Status != ReconRowStatus.Matched).Select(r => r.Key)));
        Assert.Equal(22, result.Rows.Count);
        Assert.Equal((0m, 0m), (result.CostpointTotal, result.WorkbenchTotal));
        Assert.Equal("Retained earnings", result.Rows.Single(r => r.Key == "3200").Label);
        Assert.Equal([11], result.Rows.Single(r => r.Key == "3200").SourceRows);
    }

    [Fact]
    public void A_penny_off_is_a_difference_unless_the_tolerance_allows_it()
    {
        var report = ReconciliationReports.Get(ReconReportKind.TrialBalance);
        var file = Sample(ReconReportKind.TrialBalance, "trial-balance.csv");
        var mapping = Suggest(ReconReportKind.TrialBalance, file);
        var figures = TrialBalance();
        figures[0] = Figure("1010", ("Ending", 412_350.18m));

        var strict = ReconciliationEngine.Compare(report, mapping, file, figures);
        var cash = strict.Rows.Single(r => r.Key == "1010");
        Assert.Equal(ReconRowStatus.Different, cash.Status);
        Assert.Equal(0.01m, cash.Values.Single().Difference);
        Assert.False(strict.AllMatched);

        var penny = ReconciliationEngine.Compare(report, mapping with { Tolerances = [new ReconToleranceDto("Ending", 0.01m)] }, file, figures);
        Assert.True(penny.AllMatched);

        // A percentage tolerance is a share of the Costpoint amount.
        figures[0] = Figure("1010", ("Ending", 412_350.17m + 40m));
        Assert.False(ReconciliationEngine.Compare(report, mapping with { Tolerances = [new ReconToleranceDto("Ending", 0m, 0.009m)] }, file, figures).AllMatched);
        Assert.True(ReconciliationEngine.Compare(report, mapping with { Tolerances = [new ReconToleranceDto("Ending", 0m, 0.01m)] }, file, figures).AllMatched);
    }

    [Fact]
    public void Keys_on_one_side_only_are_reported_and_zero_rows_are_not_noise()
    {
        var report = ReconciliationReports.Get(ReconReportKind.TrialBalance);
        var file = Csv(ReconReportKind.TrialBalance, "Account,Ending Balance\n1010,100.00\n1020,0.00\n1030,5.00\n");
        var mapping = Suggest(ReconReportKind.TrialBalance, file);
        var result = ReconciliationEngine.Compare(report, mapping, file,
            [Figure("1010", ("Ending", 100m)), Figure("2000", ("Ending", -7m)), Figure("2001", ("Ending", 0m))]);

        Assert.Equal(ReconRowStatus.Matched, result.Rows.Single(r => r.Key == "1010").Status);
        Assert.Equal(ReconRowStatus.Matched, result.Rows.Single(r => r.Key == "1020").Status);
        Assert.Equal(ReconRowStatus.OnlyInCostpoint, result.Rows.Single(r => r.Key == "1030").Status);
        Assert.Equal(ReconRowStatus.OnlyInWorkbench, result.Rows.Single(r => r.Key == "2000").Status);
        Assert.DoesNotContain(result.Rows, r => r.Key == "2001");
    }

    [Fact]
    public void Summary_roll_ups_are_compared_only_when_the_report_lists_them()
    {
        var report = ReconciliationReports.Get(ReconReportKind.TrialBalance);
        var file = Csv(ReconReportKind.TrialBalance, "Account,Ending Balance\n1000,150.00\n1010,100.00\n1020,50.00\n");
        var figures = new List<WorkbenchFigure>
        {
            Figure("1010", ("Ending", 100m)), Figure("1020", ("Ending", 50m)),
            new(["1000"], "Assets", new Dictionary<string, decimal> { ["Ending"] = 150m }, OnlyIfInFile: true),
            new(["9000"], "Expenses", new Dictionary<string, decimal> { ["Ending"] = 1m }, OnlyIfInFile: true),
        };
        var result = ReconciliationEngine.Compare(report, Suggest(ReconReportKind.TrialBalance, file), file, figures);
        Assert.True(result.AllMatched);
        Assert.Equal(["1000", "1010", "1020"], result.Rows.Select(r => r.Key));
    }

    [Fact]
    public void Rows_with_the_same_key_add_up_and_unreadable_amounts_are_reported_by_row()
    {
        var report = ReconciliationReports.Get(ReconReportKind.TrialBalance);
        var file = Csv(ReconReportKind.TrialBalance, "Account,Ending Balance\n1010,60.00\n1010,(10.00)\n1010,\"50.00\"\n2010,lots\n,99\n");
        var result = ReconciliationEngine.Compare(report, Suggest(ReconReportKind.TrialBalance, file), file, [Figure("1010", ("Ending", 100m))]);
        var cash = result.Rows.Single(r => r.Key == "1010");
        Assert.Equal((ReconRowStatus.Matched, 100m), (cash.Status, cash.Values.Single().Costpoint));
        Assert.Equal([2, 3, 4], cash.SourceRows);
        Assert.Contains(result.Problems, p => p.Row == 5 && p.Message.Contains("lots"));
        Assert.Contains(result.Problems, p => p.Row == 6 && p.Message.Contains("No Account"));
        Assert.False(result.AllMatched);
    }

    [Fact]
    public void Indirect_rates_translate_pool_names_and_scale_percentages()
    {
        var report = ReconciliationReports.Get(ReconReportKind.IndirectRates);
        var file = Sample(ReconReportKind.IndirectRates, "indirect-rates.csv");
        var mapping = Suggest(ReconReportKind.IndirectRates, file);
        Assert.Equal(0.01m, mapping.Columns.Single(c => c.Field == "Rate").Scale);
        Assert.Equal(ReconRange.YearToDate, mapping.Options.Range);

        var figures = new List<WorkbenchFigure>
        {
            new(["Fringe"], "Fringe", new Dictionary<string, decimal> { ["PoolCost"] = 23_475.12m, ["Base"] = 73_040m, ["Rate"] = 0.32140m }),
            new(["Overhead"], "Overhead", new Dictionary<string, decimal> { ["PoolCost"] = 18_504.54m, ["Base"] = 96_515.12m, ["Rate"] = 0.19173m }),
            new(["G&A"], "G&A", new Dictionary<string, decimal> { ["PoolCost"] = 37_200m, ["Base"] = 143_927.66m, ["Rate"] = 0.25846m }),
        };
        var result = ReconciliationEngine.Compare(report, mapping, file, figures);
        Assert.True(result.AllMatched, string.Join("; ", result.Rows.Select(r => $"{r.Key} {r.Status} {string.Join(",", r.Values.Select(v => v.Difference))}")));
        Assert.Equal(["Fringe", "G&A", "OVERHEAD"], result.Rows.Select(r => r.KeyParts[0]).Order(StringComparer.Ordinal));

        // A rate 0.0002 off is outside the default 0.0001 tolerance.
        figures[0] = figures[0] with { Values = new Dictionary<string, decimal> { ["PoolCost"] = 23_475.12m, ["Base"] = 73_040m, ["Rate"] = 0.3216m } };
        var off = ReconciliationEngine.Compare(report, mapping, file, figures);
        Assert.Equal(ReconRowStatus.Different, off.Rows.Single(r => r.KeyParts[0] == "Fringe").Status);
        Assert.False(off.Rows.Single(r => r.KeyParts[0] == "Fringe").Values.Single(v => v.Field == "Rate").WithinTolerance);
    }

    [Fact]
    public void Labor_hours_match_on_two_keys_at_project_or_wbs_level()
    {
        var report = ReconciliationReports.Get(ReconReportKind.LaborHours);
        var file = Sample(ReconReportKind.LaborHours, "labor-hours.csv");
        var mapping = Suggest(ReconReportKind.LaborHours, file);
        Assert.True(mapping.Options.ProjectLevel);
        var figures = new List<WorkbenchFigure>
        {
            new(["E2001", "2001"], "Robin Nguyen", new Dictionary<string, decimal> { ["Hours"] = 160m, ["LaborCost"] = 14_800m }),
            new(["E2002", "2001"], null, new Dictionary<string, decimal> { ["Hours"] = 168m, ["LaborCost"] = 13_104m }),
            new(["E2003", "2001"], null, new Dictionary<string, decimal> { ["Hours"] = 172m, ["LaborCost"] = 11_051m }),
            new(["E2004", "2001"], null, new Dictionary<string, decimal> { ["Hours"] = 152m, ["LaborCost"] = 10_792m }),
            new(["E2005", "9000"], null, new Dictionary<string, decimal> { ["Hours"] = 8m, ["LaborCost"] = 332m }),
        };
        var result = ReconciliationEngine.Compare(report, mapping, file, figures);
        var sam = result.Rows.Single(r => r.Key == "E2004 · 2001");
        Assert.Equal(ReconRowStatus.Different, sam.Status);
        Assert.Equal((-8m, -568m), (sam.Values.Single(v => v.Field == "Hours").Difference, sam.Values.Single(v => v.Field == "LaborCost").Difference));
        Assert.Equal(4, result.Count(ReconRowStatus.Matched));
        Assert.Equal("Garcia, Sam", sam.Label);

        // At WBS level the file's dotted ids are compared as they are.
        var wbs = ReconciliationEngine.Compare(report, mapping with { Options = mapping.Options with { ProjectLevel = false } }, file, figures);
        Assert.Contains(wbs.Rows, r => r.Key == "E2001 · 2001.01.01" && r.Status == ReconRowStatus.OnlyInCostpoint);
    }

    [Fact]
    public void Key_options_ignore_leading_zeros_punctuation_and_rewrite_keys()
    {
        var options = new ReconOptionsDto(IgnorePunctuation: true, TrimLeadingZeros: true);
        Assert.Equal(ReconciliationEngine.NormalizeKey("00-1010", "Account", options, new Dictionary<string, string>()).Normal,
            ReconciliationEngine.NormalizeKey("1010", "Account", options, new Dictionary<string, string>()).Normal);
        Assert.Equal("2001", ReconciliationEngine.NormalizeKey("2001.02.01", "Project", new ReconOptionsDto(), new Dictionary<string, string>()).Shown);
        Assert.Equal("Fringe", ReconciliationEngine.NormalizeKey(" Fringe  Benefits ", "Pool", new ReconOptionsDto(), new Dictionary<string, string> { ["FRINGEBENEFITS"] = "Fringe" }).Shown);
    }

    [Theory]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("$(1,234.56)", -1234.56)]
    [InlineData("1234.56-", -1234.56)]
    [InlineData("1234.56 CR", -1234.56)]
    [InlineData("1234.56DR", 1234.56)]
    [InlineData("32.14%", 32.14)]
    [InlineData("-", 0)]
    public void Amounts_parse_the_way_costpoint_exports_them(string text, double expected) =>
        Assert.Equal((decimal)expected, ReconciliationEngine.ParseAmount(text));

    [Fact]
    public void A_mapping_must_map_every_key_and_a_required_amount_to_columns_the_file_has()
    {
        var report = ReconciliationReports.Get(ReconReportKind.ArAging);
        var file = Sample(ReconReportKind.ArAging, "ar-aging.csv");
        var mapping = Suggest(ReconReportKind.ArAging, file);
        ReconciliationEngine.Validate(report, mapping, file.Headers);

        var broken = mapping with { Columns = [.. mapping.Columns.Where(c => c.Field != "Customer"), new ReconColumnMapDto("Customer", "Nope")] };
        var ex = Assert.Throws<ArgumentException>(() => ReconciliationEngine.Validate(report, broken, file.Headers));
        Assert.Contains("no column 'Nope'", ex.Message);
        var noTotal = mapping with { Columns = [.. mapping.Columns.Where(c => c.Field != "Total"), new ReconColumnMapDto("Total", null)] };
        Assert.Contains("Total open", Assert.Throws<ArgumentException>(() => ReconciliationEngine.Validate(report, noTotal, file.Headers)).Message);
        Assert.Contains("negative", Assert.Throws<ArgumentException>(() => ReconciliationEngine.Validate(report, mapping with { Tolerances = [new ReconToleranceDto("Total", -1m)] }, file.Headers)).Message);
    }
}
