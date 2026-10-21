using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Reports;
using Workbench.Contracts.Resourcing;

namespace Workbench.Application.Customization;

/// <summary>
/// Data for the home page widget types. KPI tiles and report charts read only through the permission-checked contract
/// services (resolved from the caller's scope), so a tile shows what the viewer's own pages would; a figure they may not see
/// comes back as an <see cref="WidgetDataDto.Error"/>. The personal widgets (to-dos, this week, ...) carry no server data:
/// the page renders them for the viewer.
/// </summary>
public sealed class HomeWidgetData(IServiceProvider services, TimeProvider clock)
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static bool Handles(WidgetType type) => type >= WidgetType.MyTodo;

    public async Task<WidgetDataDto> ComputeAsync(WidgetDto widget, CancellationToken ct)
    {
        var data = new WidgetDataDto(widget.Id, widget.Type, null, null, null);
        try
        {
            return widget.Type switch
            {
                WidgetType.Kpi when widget.Config.Kpi is { } kind => data with { Kpi = await KpiAsync(kind, ct) },
                WidgetType.Kpi => data with { Error = "Pick the figure to show." },
                WidgetType.ReportChart => await ReportChartAsync(data, widget.Config, ct),
                WidgetType.Announcement => data with { Markdown = widget.Config.Markdown ?? "" },
                _ => data,
            };
        }
        catch (Exception ex) when (ex is WorkbenchForbiddenException or UnauthorizedAccessException)
        {
            return data with { Error = "You don't have access to this figure." };
        }
        catch (WorkbenchNotFoundException)
        {
            return data with { Error = "What this widget shows no longer exists or isn't shared with you." };
        }
    }

    private async Task<KpiData> KpiAsync(KpiKind kind, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        switch (kind)
        {
            case KpiKind.FundingRemaining:
            {
                var contracts = (await services.GetRequiredService<IContractService>().ListContractsAsync(ct))
                    .Where(c => c.Status == ContractState.Active).ToList();
                var invoices = services.GetRequiredService<IInvoiceService>();
                decimal remaining = 0;
                var counted = 0;
                foreach (var contract in contracts)
                {
                    try
                    {
                        var summary = await invoices.GetContractSummaryAsync(contract.Id, ct);
                        remaining += summary.FundedValue - summary.BilledToDate;
                        counted++;
                    }
                    catch (Exception ex) when (ex is WorkbenchForbiddenException or WorkbenchNotFoundException or UnauthorizedAccessException)
                    {
                        // Not one of theirs: leave it out.
                    }
                }

                if (counted == 0 && contracts.Count > 0)
                {
                    throw new WorkbenchForbiddenException("Contract funding isn't available to you.");
                }

                return new KpiData(kind, "Funding remaining", remaining, true, false,
                    $"Funded, not yet billed, on {counted} active contract{(counted == 1 ? "" : "s")}", "billing/contracts");
            }

            case KpiKind.Utilization:
            {
                var from = new DateOnly(today.Year, today.Month, 1);
                var utilization = await services.GetRequiredService<IResourcePlanningService>().GetUtilizationAsync(from, today, ct);
                var direct = utilization.Total.DirectUtilization ?? 0;
                return new KpiData(kind, "Direct utilization", direct, false, true,
                    $"Month to date · target {utilization.TargetUtilization.ToString("P0", Us)}", "resourcing");
            }

            case KpiKind.ArAging:
            {
                var totals = (await services.GetRequiredService<IArReportService>().GetAgingAsync(today, ct)).Totals;
                var open = totals.Current + totals.Days1To30 + totals.Days31To60 + totals.Days61To90 + totals.Over90;
                var over90 = open == 0 ? 0 : totals.Over90 / open;
                return new KpiData(kind, "Open receivables", open, true, false, $"{over90.ToString("P0", Us)} more than 90 days past due", "billing/aging");
            }

            case KpiKind.OpenPurchaseOrders:
            {
                var orders = await services.GetRequiredService<IPurchaseOrderService>().ListAsync(new PurchaseOrderQuery(OpenOnly: true), ct);
                var open = orders.Sum(o => o.Total - o.Billed);
                return new KpiData(kind, "Open purchase orders", open, true, false,
                    $"{orders.Count} open order{(orders.Count == 1 ? "" : "s")}, value not yet billed", "purchasing/orders");
            }

            case KpiKind.Pipeline:
            {
                var forecast = await services.GetRequiredService<IPipelineService>().GetForecastAsync(ct);
                return new KpiData(kind, "Weighted pipeline", forecast.WeightedValue, true, false,
                    $"{forecast.TotalValue.ToString("C0", Us)} unweighted", "bd/forecast");
            }

            default:
                throw new ArgumentException($"Unknown KPI {kind}.");
        }
    }

    private async Task<WidgetDataDto> ReportChartAsync(WidgetDataDto data, WidgetConfig config, CancellationToken ct)
    {
        if (config.SavedReportId is not { } reportId)
        {
            return data with { Error = "Pick a saved report." };
        }

        var result = await services.GetRequiredService<ISavedReportService>().RunAsync(reportId, ct);
        return data with { Chart = ToChart(result, config.Chart), SheetName = result.Title };
    }

    /// <summary>The first text column as labels and the first numeric column as values, over the detail rows (at most 25).</summary>
    public static ChartData ToChart(ReportResultDto result, ChartKind kind)
    {
        var label = result.Columns.ToList().FindIndex(c => c.Type is ReportColumnType.Text or ReportColumnType.Date);
        var value = result.Columns.ToList().FindIndex(c => c.Type is ReportColumnType.Number or ReportColumnType.Money or ReportColumnType.Hours or ReportColumnType.Percent);
        if (label < 0 || value < 0)
        {
            return new ChartData(kind, [], [], result.Title);
        }

        var rows = result.Rows.Where(r => r.Kind == ReportRowKind.Detail)
            .GroupBy(r => r.Values[label] ?? "(none)")
            .Select(g => (Label: g.Key, Value: g.Sum(r => double.TryParse(r.Values[value], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0)))
            .Take(25)
            .ToList();
        return new ChartData(kind, rows.Select(r => r.Label).ToList(), rows.Select(r => r.Value).ToList(), result.Columns[value].Label);
    }
}
