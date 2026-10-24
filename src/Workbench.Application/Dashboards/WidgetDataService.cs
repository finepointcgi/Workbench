using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Financials;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Dashboards;

/// <summary>
/// Computes widget data. Sheet data comes only from <see cref="ISheetService"/>, <see cref="IItemService"/>,
/// <see cref="IDependencyService"/>, <see cref="IContractService"/> and <see cref="IProjectService"/>, never EF, so
/// visibility rules apply. Financial figures come from <see cref="IProjectFinancialsService"/> and contract funding is
/// checked with <see cref="IFinancialAccessService"/>, so they follow the same rules as the financials pages.
/// A sheet the user can't see is reported as missing.
/// </summary>
public sealed class WidgetDataService(
    IDashboardService dashboards,
    ISheetService sheets,
    IItemService items,
    IDependencyService dependencies,
    IContractService contracts,
    IProjectService projects,
    IProjectFinancialsService financials,
    IFinancialAccessService financialAccess,
    TimeProvider clock,
    Contracts.Evm.IEvmService? evm = null,
    Customization.HomeWidgetData? home = null,
    Companies.CompanyScope? companies = null) : IWidgetDataService
{
    public Task<WidgetDataDto> GetAsync(Guid dashboardId, Guid widgetId, CancellationToken cancellationToken = default) =>
        GetAsync(dashboardId, widgetId, null, cancellationToken);

    /// <summary>
    /// The widget's data within the dashboard's company choice: the company decorators, project visibility and the
    /// ledger reports all follow it while the widget is computed (<see cref="Companies.CompanyScope.Narrow"/>).
    /// </summary>
    public async Task<WidgetDataDto> GetAsync(Guid dashboardId, Guid widgetId, Guid? companyId, CancellationToken cancellationToken = default)
    {
        var dashboard = await dashboards.GetAsync(dashboardId, cancellationToken);
        var widget = dashboard.Widgets.FirstOrDefault(w => w.Id == widgetId)
            ?? throw WorkbenchNotFoundException.For("Widget", widgetId);
        var company = companies is null ? null : await companies.CheckFilterAsync(companyId, cancellationToken);
        if (company is not null && await InAnotherCompanyAsync(widget, company.Id, cancellationToken) is { } elsewhere)
        {
            return Error(widget, widget.Config.SheetId, null, $"{elsewhere} isn't in {company.Code}. Pick \"All my companies\" or its company to see this widget.");
        }

        using var narrowed = Companies.CompanyScope.Narrow(company?.Id);
        return await ComputeAsync(widget, cancellationToken);
    }

    /// <summary>
    /// The project (by its plan sheet) or contract a widget is about, when the viewer can see it but it belongs to another
    /// company than <paramref name="companyId"/>; null otherwise (company-neutral widgets, sheets that aren't plans).
    /// </summary>
    private async Task<string?> InAnotherCompanyAsync(WidgetDto widget, Guid companyId, CancellationToken ct)
    {
        if (widget.Type == WidgetType.ContractFunding && widget.Config.ContractId is { } contractId)
        {
            var contract = await contracts.GetContractAsync(contractId, ct);
            if (contract is null)
            {
                return null;
            }

            using (Companies.CompanyScope.Narrow(companyId))
            {
                return await contracts.GetContractAsync(contractId, ct) is null ? $"Contract {contract.ContractNumber}" : null;
            }
        }

        if (Customization.HomeWidgetData.Handles(widget.Type) || widget.Type == WidgetType.RichText || widget.Config.SheetId is not { } sheetId)
        {
            return null;
        }

        var project = await projects.GetProjectForSheetAsync(sheetId, ct);
        if (project is null)
        {
            return null;
        }

        using (Companies.CompanyScope.Narrow(companyId))
        {
            return await projects.GetProjectForSheetAsync(sheetId, ct) is null ? $"Project {project.Code}" : null;
        }
    }

    public async Task<WidgetDataDto> ComputeAsync(WidgetDto widget, CancellationToken cancellationToken = default)
    {
        var config = widget.Config;
        if (Customization.HomeWidgetData.Handles(widget.Type))
        {
            return home is null
                ? new WidgetDataDto(widget.Id, widget.Type, null, null, "This widget isn't available here.")
                : await home.ComputeAsync(widget, cancellationToken);
        }

        switch (widget.Type)
        {
            case WidgetType.RichText:
                return new WidgetDataDto(widget.Id, widget.Type, null, null, null, Markdown: config.Markdown ?? string.Empty);
            case WidgetType.ContractFunding:
                return await ContractFundingAsync(widget, cancellationToken);
            case WidgetType.ProjectFinancials:
                return await ProjectFinancialsAsync(widget, cancellationToken);
            case WidgetType.EarnedValue:
                return await EarnedValueAsync(widget, cancellationToken);
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

        if (!await financialAccess.CanReadContractFundingAsync(contractId, cancellationToken))
        {
            return Error(widget, null, null, "Only Finance and the managers of this contract's projects can see its funding.");
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

    private async Task<WidgetDataDto> ProjectFinancialsAsync(WidgetDto widget, CancellationToken cancellationToken)
    {
        if (widget.Config.SheetId is not { } sheetId)
        {
            return Error(widget, null, null, "Pick a project plan sheet for this widget.");
        }

        var sheet = await sheets.GetAsync(sheetId, cancellationToken);
        if (sheet is null)
        {
            return Error(widget, sheetId, null, "The sheet for this widget no longer exists or isn't shared with you.");
        }

        var name = sheet.Summary.Name;
        var project = await projects.GetProjectForSheetAsync(sheetId, cancellationToken);
        if (project is null)
        {
            return Error(widget, sheetId, name, $"\"{name}\" isn't a project plan sheet.");
        }

        if (!await financialAccess.CanReadProjectFinancialsAsync(project.Id, cancellationToken))
        {
            return Error(widget, sheetId, name, $"Only Finance and the project's manager can see {project.Code}'s financials.");
        }

        try
        {
            var summary = await financials.GetProjectSummaryAsync(project.Id, cancellationToken: cancellationToken);
            return new WidgetDataDto(widget.Id, widget.Type, sheetId, name, null, ProjectFinancials: summary);
        }
        catch (WorkbenchForbiddenException ex)
        {
            return Error(widget, sheetId, name, ex.Message);
        }
    }

    private async Task<WidgetDataDto> EarnedValueAsync(WidgetDto widget, CancellationToken cancellationToken)
    {
        if (widget.Config.SheetId is not { } sheetId)
        {
            return Error(widget, null, null, "Pick a project plan sheet for this widget.");
        }

        var sheet = await sheets.GetAsync(sheetId, cancellationToken);
        if (sheet is null)
        {
            return Error(widget, sheetId, null, "The sheet for this widget no longer exists or isn't shared with you.");
        }

        var name = sheet.Summary.Name;
        var project = await projects.GetProjectForSheetAsync(sheetId, cancellationToken);
        if (project is null || evm is null)
        {
            return Error(widget, sheetId, name, $"\"{name}\" isn't a project plan sheet.");
        }

        if (!await financialAccess.CanReadProjectFinancialsAsync(project.Id, cancellationToken))
        {
            return Error(widget, sheetId, name, $"Only Finance and the project's manager can see {project.Code}'s earned value.");
        }

        try
        {
            if (await evm.GetBaselineAsync(project.Id, cancellationToken) is null)
            {
                return Error(widget, sheetId, name, $"{project.Code} has no performance measurement baseline yet.");
            }

            var status = await evm.GetStatusAsync(project.Id, cancellationToken: cancellationToken);
            var series = await evm.GetSeriesAsync(project.Id, cancellationToken);
            return new WidgetDataDto(widget.Id, widget.Type, sheetId, name, null, EarnedValue: new Contracts.Evm.EvmWidgetData(
                project.Id, project.Code, project.Name, status.Period.Name, status.Total, series.Points, status.ControlAccounts.Count(c => c.NeedsVar)));
        }
        catch (Exception ex) when (ex is WorkbenchForbiddenException or ArgumentException)
        {
            return Error(widget, sheetId, name, ex.Message);
        }
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
        services.AddScoped<Customization.HomeWidgetData>();
        return services;
    }
}
