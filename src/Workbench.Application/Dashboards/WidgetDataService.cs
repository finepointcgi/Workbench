using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Dashboards;

/// <summary>
/// Computes widget data. Sheet data comes only from <see cref="ISheetService"/>, <see cref="IItemService"/>,
/// <see cref="IDependencyService"/>, <see cref="IContractService"/> and <see cref="IProjectService"/>, never EF, so
/// visibility rules apply.
/// A sheet the user can't see is reported as missing.
/// </summary>
public sealed class WidgetDataService(
    IDashboardService dashboards,
    ISheetService sheets,
    IItemService items,
    IDependencyService dependencies,
    IContractService contracts,
    IProjectService projects,
    TimeProvider clock) : IWidgetDataService
{
    public async Task<WidgetDataDto> GetAsync(Guid dashboardId, Guid widgetId, CancellationToken cancellationToken = default)
    {
        var dashboard = await dashboards.GetAsync(dashboardId, cancellationToken);
        var widget = dashboard.Widgets.FirstOrDefault(w => w.Id == widgetId)
            ?? throw WorkbenchNotFoundException.For("Widget", widgetId);
        return await ComputeAsync(widget, cancellationToken);
    }

    public async Task<WidgetDataDto> ComputeAsync(WidgetDto widget, CancellationToken cancellationToken = default)
    {
        var config = widget.Config;
        switch (widget.Type)
        {
            case WidgetType.RichText:
                return new WidgetDataDto(widget.Id, widget.Type, null, null, null, Markdown: config.Markdown ?? string.Empty);
            case WidgetType.ContractFunding:
                return await ContractFundingAsync(widget, cancellationToken);
            case WidgetType.ProjectFinancials:
                // TODO(financials): build from the native project financials contracts (budget, actuals, burn for the
                // project whose plan sheet is config.SheetId) once that session lands. Never from ICostpointService.
                return Error(widget, config.SheetId, null, "Project financials widgets aren't available yet.");
        }

        if (config.SheetId is not { } sheetId)
        {
            return Error(widget, null, null, "Pick a sheet for this widget.");
        }

        var sheet = await sheets.GetAsync(sheetId, cancellationToken);
        if (sheet is null)
        {
            return Error(widget, sheetId, null, "The sheet for this widget no longer exists or isn't shared with you.");
        }

        var name = sheet.Summary.Name;
        try
        {
            var data = new WidgetDataDto(widget.Id, widget.Type, sheetId, name, null);
            switch (widget.Type)
            {
                case WidgetType.Metric:
                    return data with { Metric = WidgetCalculator.Metric(sheet, await items.ListAsync(sheetId, cancellationToken), config) };
                case WidgetType.Chart:
                    return data with { Chart = WidgetCalculator.Chart(sheet, await items.ListAsync(sheetId, cancellationToken), config) };
                case WidgetType.ItemList:
                    return data with { ItemList = WidgetCalculator.ItemList(sheet, await items.ListAsync(sheetId, cancellationToken), config) };
                case WidgetType.ScheduleHealth:
                    if (!sheet.Summary.SchedulingEnabled)
                    {
                        return Error(widget, sheetId, name, $"\"{name}\" doesn't have scheduling turned on.");
                    }

                    var analysis = await dependencies.AnalyzeAsync(sheetId, cancellationToken);
                    var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
                    return data with
                    {
                        Schedule = WidgetCalculator.ScheduleHealth(sheet, await items.ListAsync(sheetId, cancellationToken), analysis, today),
                    };
                default:
                    return Error(widget, sheetId, name, $"Unknown widget type {widget.Type}.");
            }
        }
        catch (WidgetConfigException ex)
        {
            return Error(widget, sheetId, name, ex.Message);
        }
        catch (WorkbenchNotFoundException)
        {
            return Error(widget, sheetId, name, "The sheet for this widget no longer exists or isn't shared with you.");
        }
    }

    private async Task<WidgetDataDto> ContractFundingAsync(WidgetDto widget, CancellationToken cancellationToken)
    {
        if (widget.Config.ContractId is not { } contractId)
        {
            return Error(widget, null, null, "Pick a contract for this widget.");
        }

        var contract = await contracts.GetContractAsync(contractId, cancellationToken);
        if (contract is null)
        {
            return Error(widget, null, null, "The contract for this widget no longer exists or isn't shared with you.");
        }

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var data = WidgetCalculator.ContractFunding(contract, await projects.ListProjectsAsync(cancellationToken), today);
        return new WidgetDataDto(widget.Id, widget.Type, null, null, null, ContractFunding: data);
    }

    private static WidgetDataDto Error(WidgetDto widget, Guid? sheetId, string? sheetName, string message) =>
        new(widget.Id, widget.Type, sheetId, sheetName, message);
}

public static class DashboardRegistration
{
    public static IServiceCollection AddWorkbenchDashboards(this IServiceCollection services)
    {
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IWidgetDataService, WidgetDataService>();
        return services;
    }
}
