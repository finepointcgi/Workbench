using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Hr;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Projects;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Time;

namespace Workbench.Ui.Customization;

/// <summary>
/// A list page's rows and columns outside the page, so a saved list view can be charted on a home page. Rows are read
/// through the same permission-checked services the page uses, with the page's own definition, so a chart never shows more
/// than the list would.
/// </summary>
public interface IListSource
{
    string ListKey { get; }

    string Title { get; }

    /// <summary>Rows counted by the view's group-by column (or the first column that groups), after its filters.</summary>
    Task<ChartData> ChartAsync(IServiceProvider services, ListViewDefinition view, ChartKind kind, CancellationToken ct = default);
}

public sealed class ListSource<T>(string listKey, string title, Func<IServiceProvider, CancellationToken, Task<(IReadOnlyList<T> Rows, ListDefinition<T> Definition)>> load)
    : IListSource
{
    public string ListKey { get; } = listKey;

    public string Title { get; } = title;

    public async Task<ChartData> ChartAsync(IServiceProvider services, ListViewDefinition view, ChartKind kind, CancellationToken ct = default)
    {
        var (rows, definition) = await load(services, ct);
        var available = await ListPermissions.AvailableAsync(services, definition);
        var restricted = ListViewEngine.Restrict(available, view);
        // The view's grouping, else its first column that groups, else any column that groups.
        var group = available.FirstOrDefault(c => c.Key == restricted.GroupBy)
            ?? ListViewEngine.Visible(available, restricted).FirstOrDefault(c => c.Groupable)
            ?? available.FirstOrDefault(c => c.Groupable);
        if (group is null)
        {
            return new ChartData(kind, [], [], Title);
        }

        var shown = ListViewEngine.Apply(rows, available, restricted with { GroupBy = null, Search = null });
        var groups = shown.GroupBy(r => ListViewEngine.GroupKey(group, r)).OrderByDescending(g => g.Count()).Take(20).ToList();
        return new ChartData(kind, groups.Select(g => g.Key).ToList(), groups.Select(g => (double)g.Count()).ToList(), $"{Title} by {group.Title.ToLowerInvariant()}");
    }
}

/// <summary>Columns of a definition the viewer may see (built-in only: custom field values aren't charted).</summary>
public static class ListPermissions
{
    public static async Task<IReadOnlyList<ListColumn<T>>> AvailableAsync<T>(IServiceProvider services, ListDefinition<T> definition)
    {
        var keys = definition.Columns.Select(c => c.PermissionKey).OfType<string>().Distinct().ToList();
        IReadOnlySet<string> granted = new HashSet<string>();
        if (keys.Count > 0 && services.GetService<ICustomizationAccess>() is { } access)
        {
            granted = await access.GrantedAsync(keys);
        }

        return definition.Columns.Where(c => c.PermissionKey is null || granted.Contains(c.PermissionKey)).ToList();
    }
}

/// <summary>Every list page that can be charted.</summary>
public static class ListSources
{
    private static T Get<T>(IServiceProvider sp) where T : notnull => sp.GetRequiredService<T>();

    public static IReadOnlyList<IListSource> All { get; } =
    [
        new ListSource<ProjectHubRowDto>(CustomizableLists.Projects, "Projects", async (sp, ct) =>
        {
            var list = await Get<IProjectHubService>(sp).ListProjectsAsync(ct);
            return (list.Projects, list.ShowsMoney ? Projects.ProjectListPage.MoneyDefinition : Projects.ProjectListPage.PlainDefinition);
        }),
        new ListSource<ContractDto>(CustomizableLists.Contracts, "Contracts", async (sp, ct) =>
            (await Get<IContractService>(sp).ListContractsAsync(ct), Setup.ContractsPage._definition)),
        new ListSource<EmployeeSummaryDto>(CustomizableLists.People, "People", async (sp, ct) =>
            (await Get<IHrPersonnelService>(sp).ListAsync(new EmployeeQuery(null, false), ct), Hr.PeoplePage._definition)),
        new ListSource<VendorDto>(CustomizableLists.Vendors, "Vendors", async (sp, ct) =>
            (await Get<IVendorService>(sp).ListAsync(false, ct), Purchasing.VendorsPage._definition)),
        new ListSource<PurchaseOrderSummaryDto>(CustomizableLists.PurchaseOrders, "Purchase orders", async (sp, ct) =>
            (await Get<IPurchaseOrderService>(sp).ListAsync(new PurchaseOrderQuery(), ct), Purchasing.Procurement.PurchaseOrdersPage._definition)),
        new ListSource<BillSummaryDto>(CustomizableLists.Bills, "AP bills", async (sp, ct) =>
            (await Get<IApBillService>(sp).ListAsync(new BillQuery(), ct), Purchasing.BillsPage._definition)),
        new ListSource<InvoiceSummaryDto>(CustomizableLists.Invoices, "Invoices", async (sp, ct) =>
            (await Get<IInvoiceService>(sp).ListAsync(new InvoiceQuery(), ct), Billing.InvoicesPage._definition)),
        new ListSource<TeamMemberStatusDto>(CustomizableLists.Timesheets, "Team time", async (sp, ct) =>
            ((await Get<ITimesheetApprovalService>(sp).GetTeamStatusAsync(DateOnly.FromDateTime(DateTime.Today), ct)).Members, Time.TeamPage._definition)),
        new ListSource<ExpenseReportDto>(CustomizableLists.ExpenseReports, "Expense reports", async (sp, ct) =>
            (await Get<IExpenseReportService>(sp).ListMyReportsAsync(ct), Pages.Odcs.Expenses._definition)),
        new ListSource<OpportunityDto>(CustomizableLists.Opportunities, "Opportunities", async (sp, ct) =>
            (await Get<IOpportunityService>(sp).ListAsync(new OpportunityFilter(), ct), Crm.OpportunityListPage._definition)),
        new ListSource<PartSummaryDto>(CustomizableLists.Parts, "Parts", async (sp, ct) =>
            (await Get<IPartService>(sp).ListAsync(new PartQuery(), ct), Materials.PartsPage._definition)),
        new ListSource<SalesOrderSummaryDto>(CustomizableLists.SalesOrders, "Sales orders", async (sp, ct) =>
            (await Get<ISalesOrderService>(sp).ListAsync(new SalesOrderQuery(), ct), Sales.SalesOrdersPage._definition)),
    ];

    public static IListSource? Find(string listKey) => All.FirstOrDefault(s => s.ListKey == listKey);
}
