using MudBlazor;
using P = Workbench.Contracts.Identity.Permission;

namespace Workbench.Ui.Navigation;

/// <summary>How a "+ New" item opens: a page, or a create dialog the menu shows itself.</summary>
public enum NewKind
{
    Page,
    ProjectDialog,
    SheetDialog,
    ExpenseReportDialog,
    ReceiptUpload,
}

/// <summary>
/// One thing the "+ New" menu (and Ctrl+K) can start. <see cref="Mine"/> items are the user's own paperwork and come
/// first; the rest create shared records. <see cref="Href"/> is the page it opens, or for a dialog the page that
/// does the same. Hiding is cosmetic: the page or service checks the same permission on the server.
/// </summary>
public sealed record NewAction(string Key, string Title, string PaletteTitle, string Icon, NavRule Rule, string Href, NewKind Kind = NewKind.Page, bool Mine = false, string? Keywords = null)
{
    public bool IsVisible(NavAccess access) => Rule.Allows(access);
}

/// <summary>What the "+ New" menu offers, in task order: everyday paperwork first, then records for the team.</summary>
public static class NewCatalog
{
    public static IReadOnlyList<NewAction> Actions { get; } =
    [
        new("time", "Log time", "Log time", Icons.Material.Filled.AccessTime, NavRule.Everyone, "time", Mine: true, Keywords: "timesheet hours enter"),
        new("expense-report", "Expense report", "New expense report", Icons.Material.Filled.Receipt, NavRule.Everyone, "expenses", NewKind.ExpenseReportDialog, Mine: true, Keywords: "expenses reimbursement"),
        new("receipt", "Upload a receipt", "Upload a receipt", Icons.Material.Filled.ReceiptLong, NavRule.Everyone, "receipts", NewKind.ReceiptUpload, Mine: true, Keywords: "receipt folio photo scan"),
        new("travel", "Travel request", "New travel request", Icons.Material.Filled.FlightTakeoff, NavRule.Everyone, "travel/new", Mine: true, Keywords: "trip authorization per diem advance"),
        new("leave", "Leave request", "Request leave", Icons.Material.Filled.BeachAccess, NavRule.Everyone, "leave", Mine: true, Keywords: "vacation pto time off"),
        new("requisition", "Purchase request", "New purchase request", Icons.Material.Filled.PostAdd, NavRule.Everyone, "purchasing/requisitions/new", Mine: true, Keywords: "requisition buy order"),

        new("project", "Project", "New project", Icons.Material.Filled.CreateNewFolder, NavRule.Need(P.ProjectsCreate, P.SetupProjects), "setup/projects/new", NewKind.ProjectDialog, Keywords: "wbs plan request"),
        new("sheet", "Sheet", "New sheet", Icons.Material.Filled.TableChart, NavRule.Everyone, "sheets", NewKind.SheetDialog, Keywords: "grid tracker list"),
        new("opportunity", "Opportunity", "New opportunity", Icons.Material.Filled.Flag, NavRule.Need(P.CaptureManage), "bd/opportunities/new", Keywords: "pursuit capture rfp bd"),
        new("invoice", "Invoice", "New invoice", Icons.Material.Filled.NoteAdd, NavRule.Need(P.BillingInvoicesManage), "billing/new", Keywords: "bill customer billing"),
        new("journal", "Journal entry", "New journal entry", Icons.Material.Filled.MenuBook, NavRule.Need(P.GlJournalsPost), "accounting/journal/new", Keywords: "je gl general ledger"),
        new("bill", "Vendor bill", "New vendor bill", Icons.Material.Filled.ReceiptLong, NavRule.Need(P.ApBillsManage), "purchasing/bills/new", Keywords: "ap payable vendor invoice"),
        new("contract", "Contract", "New contract", Icons.Material.Filled.Description, NavRule.Need(P.SetupContracts), "setup/contracts/new", Keywords: "award funding bill rates"),
        new("rfq", "Request for quote", "New request for quote", Icons.Material.Filled.RequestQuote, NavRule.Need(P.SourcingManage), "purchasing/rfqs/new", Keywords: "rfq sourcing quotes"),
        new("engineering-change", "Engineering change", "New engineering change", Icons.Material.Filled.ChangeCircle, NavRule.Need(P.MaterialsManage), "materials/engineering-changes/new", Keywords: "eco ecn revision"),
        new("automation", "Automation", "New automation", Icons.Material.Filled.Bolt, NavRule.Need(P.AdminAutomations), "admin/automation/new", Keywords: "rule workflow trigger"),
    ];

    /// <summary>The items <paramref name="access"/> may start, in menu order.</summary>
    public static IReadOnlyList<NewAction> For(NavAccess access) =>
        access.IsSignedIn ? Actions.Where(a => a.IsVisible(access)).ToList() : [];

    public static NewAction? Find(string key) => Actions.FirstOrDefault(a => a.Key == key);
}
