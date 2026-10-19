using MudBlazor;

namespace Workbench.Ui.Navigation;

/// <summary>A top-level menu section. With <see cref="Href"/>, a section with only that one entry shows as a plain link.</summary>
public sealed record NavSection(string Key, string Title, string Icon, string? Href = null);

/// <summary>
/// One page in the menu and the command palette. <see cref="Group"/> nests it under a sub-group of its section.
/// <see cref="Detail"/> names one record on the pages below it (for breadcrumbs, e.g. "Bill" for purchasing/bills/{id}).
/// <see cref="InMenu"/> false keeps it out of the menu but findable with Ctrl+K (used for pages their parent page
/// already links to, such as New invoice from Invoices).
/// </summary>
public sealed record NavEntry(
    string Section,
    string? Group,
    string Title,
    string Href,
    string Icon,
    Func<NavAccess, bool> IsVisible,
    bool MatchAll = false,
    string? Detail = null,
    string? Keywords = null,
    bool InMenu = true);

/// <summary>
/// The whole navigation, shaped by task and role: the menu, the command palette and the breadcrumbs all read it.
/// Every page route stays where it was; only how it's reached changes.
/// </summary>
public static class NavCatalog
{
    public const string Home = "home";
    public const string MyWork = "my-work";
    public const string Approvals = "approvals";
    public const string Projects = "projects";
    public const string Growth = "growth";
    public const string Sheets = "sheets";
    public const string Finance = "finance";
    public const string Reports = "reports";
    public const string Admin = "admin";

    public static IReadOnlyList<NavSection> Sections { get; } =
    [
        new(Home, "Home", Icons.Material.Filled.Home, ""),
        new(MyWork, "My work", Icons.Material.Filled.Inbox),
        new(Approvals, "Approvals", Icons.Material.Filled.Approval, "approvals"),
        new(Projects, "Projects", Icons.Material.Filled.Work, "projects"),
        new(Growth, "Business development", Icons.Material.Filled.TrendingUp),
        new(Sheets, "Sheets & dashboards", Icons.Material.Filled.TableChart),
        new(Finance, "Finance", Icons.Material.Filled.AccountBalance),
        new(Reports, "Reports", Icons.Material.Filled.Assessment),
        new(Admin, "Admin", Icons.Material.Filled.AdminPanelSettings),
    ];

    private static bool Everyone(NavAccess a) => a.IsSignedIn;

    private static bool FinanceOnly(NavAccess a) => a.IsFinance;

    private static bool AdminOnly(NavAccess a) => a.IsAdmin;

    // Project managers who aren't Finance (Finance reaches the same pages under Setup and Finance).
    private static bool PmNotFinance(NavAccess a) => a.IsPm && !a.IsFinance;

    public static IReadOnlyList<NavEntry> Entries { get; } =
    [
        new(Home, null, "Home", "", Icons.Material.Filled.Home, _ => true, MatchAll: true, Keywords: "my work start"),

        // My work: what everyone does for themselves.
        new(MyWork, null, "Time", "time", Icons.Material.Filled.AccessTime, Everyone, MatchAll: true, Detail: "Timesheet", Keywords: "timesheet hours"),
        new(MyWork, null, "Team time", "time/team", Icons.Material.Filled.Groups, a => a.ShowTeamTime, Keywords: "supervisor timesheets"),
        new(MyWork, null, "Expenses", "expenses", Icons.Material.Filled.Receipt, Everyone, Detail: "Expense report", Keywords: "expense report travel receipts"),
        new(MyWork, null, "Leave", "leave", Icons.Material.Filled.BeachAccess, Everyone, Keywords: "vacation pto time off"),
        new(MyWork, null, "My requisitions", "purchasing/requisitions", Icons.Material.Filled.PostAdd, Everyone, Detail: "Requisition", Keywords: "purchase request buy"),
        new(MyWork, null, "Receiving", "purchasing/receiving", Icons.Material.Filled.Inventory, a => a.IsReceiver && !a.IsFinance, Keywords: "receive goods purchase order"),

        // One inbox for every approval; the old approval pages keep their routes and are reached from it.
        new(Approvals, null, "Approvals", "approvals", Icons.Material.Filled.Approval, a => a.ShowApprovals,
            Keywords: "approve reject inbox timesheet expense odc bill requisition delegation"),

        new(Projects, null, "All projects", "projects", Icons.Material.Filled.Work, Everyone, Keywords: "contracts wbs"),
        new(Projects, null, "New project", "setup/projects/new", Icons.Material.Filled.CreateNewFolder, a => a.HasProjectManagerRole && !a.IsFinance, MatchAll: true, Keywords: "create request project wbs plan"),
        new(Projects, null, "Resourcing", "resourcing", Icons.Material.Filled.GroupWork, a => a.IsResourceManager || a.IsPm, Keywords: "allocations staffing capacity utilization"),
        new(Projects, null, "Project costs", "odcs", Icons.Material.Filled.RequestQuote, a => a.IsFinance || a.IsPm, MatchAll: true, Keywords: "other direct costs vendor"),

        // Business development: the capture pipeline (BD, Finance, Admin); capture and proposal managers see their own.
        new(Growth, null, "Pipeline", "bd", Icons.Material.Filled.ViewKanban, a => a.ShowBusinessDevelopment, MatchAll: true, Keywords: "capture board opportunities crm"),
        new(Growth, null, "Opportunities", "bd/opportunities", Icons.Material.Filled.Flag, a => a.ShowBusinessDevelopment, Detail: "Opportunity", Keywords: "pursuits rfp solicitation capture"),
        new(Growth, null, "Accounts and contacts", "bd/accounts", Icons.Material.Filled.Contacts, a => a.SeesPipeline, Detail: "Account", Keywords: "agencies offices primes partners competitors crm"),
        new(Growth, null, "Leads", "bd/leads", Icons.Material.Filled.TravelExplore, a => a.SeesPipeline, Keywords: "sam.gov saved searches import notices"),
        new(Growth, null, "Forecast and win/loss", "bd/forecast", Icons.Material.Filled.Insights, a => a.SeesPipeline, Keywords: "pwin pgo weighted pipeline b&p spend"),
        new(Growth, null, "Gates and settings", "bd/settings", Icons.Material.Filled.Rule, a => a.SeesPipeline, Keywords: "go no-go bid decision checklist b&p charge code"),

        new(Sheets, null, "Sheets", "sheets", Icons.Material.Filled.TableChart, Everyone, Detail: "Sheet", Keywords: "grid gantt board calendar plan"),
        new(Sheets, null, "Dashboards", "dashboards", Icons.Material.Filled.Dashboard, Everyone, Detail: "Dashboard", Keywords: "charts widgets"),

        // Finance (Finance and administrators).
        new(Finance, "Accounting", "Journal entries", "accounting/journal", Icons.Material.Filled.MenuBook, FinanceOnly, Detail: "Journal entry", Keywords: "gl general ledger je"),
        new(Finance, "Accounting", "Chart of accounts", "accounting/accounts", Icons.Material.Filled.AccountTree, FinanceOnly, Keywords: "gl accounts"),
        new(Finance, "Accounting", "Posting rules", "accounting/posting-rules", Icons.Material.Filled.Rule, FinanceOnly),
        new(Finance, "Accounting", "Period close", "accounting/close", Icons.Material.Filled.EventAvailable, FinanceOnly, Keywords: "fiscal month end"),

        new(Finance, "Billing & AR", "Invoices", "billing", Icons.Material.Filled.Receipt, FinanceOnly, MatchAll: true, Keywords: "billing receivables"),
        new(Finance, "Billing & AR", "New invoice", "billing/new", Icons.Material.Filled.NoteAdd, FinanceOnly, InMenu: false),
        new(Finance, "Billing & AR", "Contracts and milestones", "billing/contracts", Icons.Material.Filled.Flag, FinanceOnly, Detail: "Contract"),
        new(Finance, "Billing & AR", "Customer payments", "billing/payments", Icons.Material.Filled.Payments, FinanceOnly, Keywords: "cash receipts"),
        new(Finance, "Billing & AR", "AR aging", "billing/aging", Icons.Material.Filled.HourglassBottom, FinanceOnly, Keywords: "receivables overdue"),
        new(Finance, "Billing & AR", "Unbilled accrual", "billing/accrual", Icons.Material.Filled.EventRepeat, FinanceOnly),
        new(Finance, null, "Invoice", "billing/invoices", Icons.Material.Filled.Receipt, FinanceOnly, Detail: "Invoice", InMenu: false),
        new(Finance, null, "Customer statement", "billing/statements", Icons.Material.Filled.Receipt, FinanceOnly, Detail: "Statement", InMenu: false),

        new(Finance, "Payables & purchasing", "Bills", "purchasing/bills", Icons.Material.Filled.ReceiptLong, FinanceOnly, Detail: "Bill", Keywords: "ap accounts payable vendor invoices"),
        new(Finance, "Payables & purchasing", "Payment runs", "purchasing/payments", Icons.Material.Filled.Payments, FinanceOnly, Detail: "Payment run", Keywords: "pay vendors checks ach"),
        new(Finance, "Payables & purchasing", "Vendors", "purchasing/vendors", Icons.Material.Filled.Storefront, FinanceOnly, Detail: "Vendor", Keywords: "suppliers 1099"),
        new(Finance, "Payables & purchasing", "Subcontracts", "purchasing/subcontracts", Icons.Material.Filled.Handshake, FinanceOnly, Detail: "Subcontract"),
        new(Finance, "Payables & purchasing", "Purchase orders", "purchasing/orders", Icons.Material.Filled.ShoppingBasket, FinanceOnly, Detail: "Purchase order", Keywords: "po"),
        new(Finance, "Payables & purchasing", "Receiving", "purchasing/receiving", Icons.Material.Filled.Inventory, FinanceOnly),
        new(Finance, "Payables & purchasing", "Match exceptions", "purchasing/match-exceptions", Icons.Material.Filled.CompareArrows, FinanceOnly, Keywords: "three way match"),

        new(Finance, "Cash", "Cash and bank accounts", "cash", Icons.Material.Filled.AccountBalanceWallet, FinanceOnly, MatchAll: true),
        new(Finance, "Cash", "Import statements", "cash/import", Icons.Material.Filled.UploadFile, FinanceOnly, Keywords: "bank statement", InMenu: false),
        new(Finance, "Cash", "Reconcile", "cash/reconcile", Icons.Material.Filled.FactCheck, FinanceOnly, Detail: "Reconciliation", Keywords: "bank reconciliation"),
        new(Finance, "Cash", "Reconciliation history", "cash/reconciliations", Icons.Material.Filled.History, FinanceOnly),
        new(Finance, "Cash", "Cash forecast", "cash/forecast", Icons.Material.Filled.ShowChart, FinanceOnly),

        new(Finance, "Assets", "Asset register", "assets", Icons.Material.Filled.ListAlt, FinanceOnly, MatchAll: true, Detail: "Asset", Keywords: "fixed assets"),
        new(Finance, "Assets", "Capitalize", "assets/capitalize", Icons.Material.Filled.AddBox, FinanceOnly, InMenu: false),
        new(Finance, "Assets", "Depreciation", "assets/depreciation", Icons.Material.Filled.TrendingDown, FinanceOnly),
        new(Finance, "Assets", "Asset categories", "assets/categories", Icons.Material.Filled.Category, FinanceOnly, Keywords: "fixed asset classes useful life depreciation method"),

        new(Finance, "Payroll", "Payroll", "payroll", Icons.Material.Filled.Payments, FinanceOnly, Detail: "Payroll run", Keywords: "paylocity pay runs"),

        new(Finance, "Revenue & compliance", "Revenue", "accounting/revenue", Icons.Material.Filled.TrendingUp, FinanceOnly, MatchAll: true, Keywords: "revenue recognition run"),
        new(Finance, "Revenue & compliance", "Revenue estimates", "accounting/revenue/estimates", Icons.Material.Filled.Calculate, FinanceOnly, Keywords: "eac etc", InMenu: false),
        new(Finance, "Revenue & compliance", "Revenue setup", "accounting/revenue/setup", Icons.Material.Filled.Tune, FinanceOnly, InMenu: false),
        new(Finance, "Revenue & compliance", "Incurred cost", "accounting/incurred-cost", Icons.Material.Filled.Gavel, FinanceOnly, Detail: "Submission", Keywords: "ice dcaa submission"),
        new(Finance, "Revenue & compliance", "Indirect rates", "accounting/indirect-rates", Icons.Material.Filled.Percent, FinanceOnly, Keywords: "fringe overhead g&a pools"),
        new(Finance, null, "Revenue by project", "accounting/revenue/projects", Icons.Material.Filled.TrendingUp, FinanceOnly, Detail: "Project", InMenu: false),

        // Reports: one place for every report.
        new(Reports, null, "Report catalog", "reports", Icons.Material.Filled.LibraryBooks, Everyone, MatchAll: true, Keywords: "reports"),
        new(Reports, null, "Report writer", "reports/writer", Icons.Material.Filled.Build, Everyone, Detail: "Report", Keywords: "custom report builder"),
        new(Reports, "Project reports", "Project status", "reports/standard/project-status", Icons.Material.Filled.Assessment, PmNotFinance, Keywords: "eac etc budget variance pm"),
        new(Reports, "Project reports", "Contract funding", "reports/standard/funding-status", Icons.Material.Filled.Assessment, PmNotFinance, Keywords: "ceiling funded clin"),
        new(Reports, null, "Financial statements", "accounting/reports", Icons.Material.Filled.Assessment, FinanceOnly, Keywords: "balance sheet income statement trial balance p&l"),
        new(Reports, null, "AP reports", "purchasing/reports", Icons.Material.Filled.Assessment, FinanceOnly, Keywords: "payables aging 1099"),
        new(Reports, null, "Asset reports", "assets/reports", Icons.Material.Filled.Assessment, FinanceOnly),
        new(Reports, null, "Revenue reports", "accounting/revenue/reports", Icons.Material.Filled.Assessment, FinanceOnly),
        new(Reports, null, "Saved report", "reports/saved", Icons.Material.Filled.Assessment, Everyone, Detail: "Report", InMenu: false),
        new(Reports, null, "Standard report", "reports/standard", Icons.Material.Filled.Assessment, Everyone, Detail: "Report", InMenu: false),

        // Admin (administrators; Finance sees the setup parts, titled Setup).
        new(Admin, "People and access", "People", "setup/people", Icons.Material.Filled.People, FinanceOnly, Keywords: "employees cost rates supervisors"),
        new(Admin, "People and access", "Users and security", "admin/users", Icons.Material.Filled.ManageAccounts, AdminOnly, Keywords: "sign in accounts security invite deactivate"),
        new(Admin, "Compliance", "DCAA readiness", "admin/dcaa-readiness", Icons.Material.Filled.Gavel, AdminOnly, Keywords: "sf 1408 pre-award survey accounting system compliance cpa"),
        new(Admin, "People and access", "Audit log", "setup/audit", Icons.Material.Filled.History, FinanceOnly, Keywords: "history security"),
        new(Admin, "Setup", "Getting started", "setup", Icons.Material.Filled.Checklist, FinanceOnly, MatchAll: true),
        new(Admin, "Setup", "Org units", "setup/org-units", Icons.Material.Filled.AccountTree, FinanceOnly, Keywords: "departments"),
        new(Admin, "Setup", "Labor categories", "setup/labor-categories", Icons.Material.Filled.Badge, FinanceOnly, Keywords: "plc"),
        new(Admin, "Setup", "Customers", "setup/customers", Icons.Material.Filled.Business, FinanceOnly),
        new(Admin, "Setup", "Contracts", "setup/contracts", Icons.Material.Filled.Description, FinanceOnly, Detail: "Contract", Keywords: "bill rates funding"),
        new(Admin, "Setup", "Projects and WBS", "setup/projects", Icons.Material.Filled.Work, FinanceOnly, Detail: "Project", Keywords: "charge codes"),
        new(Admin, "Setup", "Fiscal calendar", "setup/fiscal-calendar", Icons.Material.Filled.CalendarMonth, FinanceOnly, Keywords: "periods year"),
        new(Admin, "Setup", "Per diem rates", "setup/per-diem", Icons.Material.Filled.Hotel, FinanceOnly, Keywords: "gsa travel"),
        new(Admin, null, "Approval workflows", "setup/approval-workflows", Icons.Material.Filled.AltRoute, FinanceOnly, Detail: "Workflow", Keywords: "routing approvers delegation"),
        new(Admin, null, "Import", "setup/import", Icons.Material.Filled.UploadFile, FinanceOnly, Detail: "Import", Keywords: "costpoint smartsheet unanet csv"),
        new(Admin, null, "Go-live migration", "setup/migration", Icons.Material.Filled.Flag, FinanceOnly, Detail: "Reconciliation", Keywords: "cutover cut-over costpoint parallel reconcile opening balances"),
        new(Admin, null, "Organization", "setup/organization", Icons.Material.Filled.Tune, FinanceOnly, Keywords: "settings timesheet cadence company"),

        // Findable with Ctrl+K only.
        new(Home, null, "Notifications", "notifications", Icons.Material.Filled.Notifications, Everyone, Keywords: "alerts email digest settings", InMenu: false),
        new(Home, null, "Mobile view", "m", Icons.Material.Filled.PhoneIphone, Everyone, MatchAll: true, Keywords: "phone app", InMenu: false),
        new(Approvals, null, "Timesheet approvals", "time/approvals", Icons.Material.Filled.FactCheck, a => a.ShowTeamTime, InMenu: false),
        new(Approvals, null, "ODC approvals", "odcs/approvals", Icons.Material.Filled.FactCheck, a => a.ShowApprovals, InMenu: false),
        new(Approvals, null, "Bill approvals", "purchasing/approvals", Icons.Material.Filled.FactCheck, a => a.ShowApprovals, InMenu: false),
        new(Home, null, "Help center", "help", Icons.Material.Filled.MenuBook, _ => true, Detail: "Article", Keywords: "how to guide support faq", InMenu: false),
        new(Home, null, "Glossary", "help/glossary", Icons.Material.Filled.Translate, _ => true, Keywords: "terms definitions acronyms", InMenu: false),
        new(Home, null, "What's new", "help/whats-new", Icons.Material.Filled.NewReleases, _ => true, Keywords: "changes release notes changelog", InMenu: false),
        new(Home, null, "My support requests", "support", Icons.Material.Filled.SupportAgent, Everyone, Keywords: "report problem feedback bug help", InMenu: false),
        new(Admin, null, "Support requests", "admin/support", Icons.Material.Filled.SupportAgent, AdminOnly, Keywords: "help desk problems feedback bug reports", InMenu: false),
        new(MyWork, null, "My allocations", "resourcing", Icons.Material.Filled.GroupWork, a => a.IsSignedIn && !a.IsResourceManager && !a.IsPm, Keywords: "resourcing staffing", InMenu: false),
    ];

    /// <summary>The section's title for this user: Finance people who aren't administrators see Admin as "Setup".</summary>
    public static string TitleFor(NavSection section, NavAccess access) =>
        section.Key == Admin && !access.IsAdmin ? "Setup" : section.Title;

    public static IEnumerable<NavEntry> MenuEntries(NavAccess access) => Entries.Where(e => e.InMenu && e.IsVisible(access));

    /// <summary>Every page the user may open, menu or not (for the command palette).</summary>
    public static IEnumerable<NavEntry> PaletteEntries(NavAccess access) => Entries.Where(e => e.IsVisible(access));

    /// <summary>
    /// The visible entry whose page <paramref name="path"/> is or sits below (the longest matching href), and the rest
    /// of the path.
    /// </summary>
    public static (NavEntry Entry, string[] Tail)? Match(string path, NavAccess access)
    {
        var segments = Segments(path);
        NavEntry? best = null;
        var bestLength = -1;
        foreach (var entry in Entries.Where(e => e.IsVisible(access)))
        {
            var hrefSegments = Segments(entry.Href);
            if (hrefSegments.Length > segments.Length || hrefSegments.Length <= bestLength)
            {
                continue;
            }

            if (hrefSegments.Select((s, i) => string.Equals(s, segments[i], StringComparison.OrdinalIgnoreCase)).All(x => x))
            {
                best = entry;
                bestLength = hrefSegments.Length;
            }
        }

        return best is null ? null : (best, segments[bestLength..]);
    }

    internal static string[] Segments(string path)
    {
        var clean = path.Split('?', '#')[0];
        return clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }
}
