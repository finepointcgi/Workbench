using System.Globalization;
using System.Text;
using Workbench.Contracts.Financials;

namespace Workbench.Application.Financials;

/// <summary>CSV exports of the WBS financials and the time-phased burn (invariant numbers, RFC 4180 quoting).</summary>
public sealed class FinancialExportService(IProjectFinancialsService financials, TimeProvider clock) : IFinancialExportService
{
    public async Task<FinancialExportFile> ExportWbsCsvAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default)
    {
        var summary = await financials.GetProjectSummaryAsync(projectId, budgetVersionId, forecastVersionId, cancellationToken);
        var rows = await financials.GetWbsFinancialsAsync(projectId, budgetVersionId, forecastVersionId, cancellationToken);
        var csv = new StringBuilder();
        Line(csv, "WBS", "Name", "Level", "Budget hours", "Actual hours", "ETC hours", "EAC hours", "VAC hours",
            "Budget", "Actual labor", "Actual ODC", "Actual cost", "Percent spent", "Remaining budget", "ETC", "EAC", "VAC", "Billable value");
        foreach (var row in rows)
        {
            Line(csv, [Text(row.Code), Text(row.Name), row.Depth.ToString(CultureInfo.InvariantCulture), .. Figures(row.Figures)]);
        }

        Line(csv, ["Total", Text(summary.ProjectName), "", .. Figures(summary.Totals)]);
        return new FinancialExportFile($"{summary.ProjectCode}-wbs-financials-{Stamp()}.csv", "text/csv", csv.ToString());
    }

    public async Task<FinancialExportFile> ExportTimePhasedCsvAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default)
    {
        var summary = await financials.GetProjectSummaryAsync(projectId, budgetVersionId, null, cancellationToken);
        var burn = await financials.GetTimePhasedBurnAsync(projectId, budgetVersionId, cancellationToken);
        var csv = new StringBuilder();
        Line(csv, "Period", "Start", "End", "Budget", "Actual hours", "Actual labor", "Actual ODC", "Actual cost", "Cumulative budget", "Cumulative actual");
        foreach (var p in burn.Periods)
        {
            Line(csv, p.PeriodName, Date(p.PeriodStart), Date(p.PeriodEnd), N(p.BudgetAmount), N(p.ActualHours), N(p.ActualLaborCost), N(p.ActualOdcCost),
                N(p.ActualCost), N(p.CumulativeBudget), p.CumulativeActual is { } c ? N(c) : "");
        }

        if (burn.UnphasedBudgetAmount != 0)
        {
            Line(csv, "Unphased", "", "", N(burn.UnphasedBudgetAmount), "", "", "", "", "", "");
        }

        return new FinancialExportFile($"{summary.ProjectCode}-time-phased-burn-{Stamp()}.csv", "text/csv", csv.ToString());
    }

    private static string[] Figures(FinancialFigures f) =>
    [
        N(f.BudgetHours), N(f.ActualHours), N(f.EtcHours), N(f.EacHours), N(f.VacHours),
        N(f.BudgetAmount), N(f.ActualLaborCost), N(f.ActualOdcCost), N(f.ActualCost),
        f.PercentSpent is { } p ? p.ToString("0.####", CultureInfo.InvariantCulture) : "",
        N(f.RemainingBudget), N(f.EtcAmount), N(f.EacAmount), N(f.VacAmount), N(f.BillableValue),
    ];

    private string Stamp() => clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string N(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Neutralizes spreadsheet formulas in free text (CSV injection).</summary>
    private static string Text(string value) => value.Length > 0 && "=+-@".Contains(value[0]) ? "'" + value : value;

    private static void Line(StringBuilder csv, params string[] fields)
    {
        csv.AppendJoin(',', fields.Select(Quote)).Append("\r\n");
    }

    private static string Quote(string field) =>
        field.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{field.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : field;
}
