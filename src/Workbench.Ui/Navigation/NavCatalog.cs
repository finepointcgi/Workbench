using MudBlazor;
using Workbench.Contracts.Identity;
using P = Workbench.Contracts.Identity.Permission;

namespace Workbench.Ui.Navigation;

/// <summary>A top-level menu section. With <see cref="Href"/>, a section with only that one entry shows as a plain link.</summary>
public sealed record NavSection(string Key, string Title, string Icon, string? Href = null);

/// <summary>
/// Who sees a page. <see cref="Permissions"/> are the permissions that reveal it (any one of them), as Admin › Roles lists
/// them; <see cref="Test"/>, when set, decides instead (for pages that also open through a relationship, like the
/// manager of a project), and <see cref="Note"/> says what else lets someone in. No permissions and no test: everyone
/// signed in. Hiding is cosmetic: the page's services check the same permissions on the server.
/// </summary>
public sealed record NavRule(IReadOnlyList<string> Permissions, Func<NavAccess, bool>? Test = null, string? Note = null)
{
    /// <summary>Everyone who is signed in.</summary>
    public static NavRule Everyone { get; } = new([]);

    /// <summary>Anyone, signed in or not (help pages).</summary>
    public static NavRule Public { get; } = new([], _ => true, "Anyone");

    /// <summary>Holders of any of <paramref name="permissions"/>.</summary>
    public static NavRule Need(params string[] permissions) => new(permissions);

    /// <summary>Decided by <paramref name="test"/>; <paramref name="permissions"/> are the ones that play a part.</summary>
    public static NavRule Custom(Func<NavAccess, bool> test, string note, params string[] permissions) => new(permissions, test, note);

    public bool Allows(NavAccess access) =>
        Test is not null ? Test(access) : Permissions.Count == 0 ? access.IsSignedIn : Permissions.Any(access.Can);
}

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
    NavRule Rule,
    bool MatchAll = false,
    string? Detail = null,
    string? Keywords = null,
    bool InMenu = true)
{
    public bool IsVisible(NavAccess access) => Rule.Allows(access);
}

/// <summary>One menu item: a page, or a group of pages (<see cref="IsGroup"/>) reached as one link, with tabs on its pages.</summary>
public sealed record NavItem(string? Group, List<NavEntry> Pages)
{
    public bool IsGroup => Group is not null && Pages.Count > 1;

    public NavEntry First => Pages[0];
}

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
    public const string People = "people";
    public const string Growth = "growth";
    public const string Sheets = "sheets";
    public const string Finance = "finance";
    public const string Reports = "reports";
    public const string Materials = "materials";
    public const string Admin = "admin";

    public static IReadOnlyList<NavSection> Sections { get; } =
    [
        new(Home, "Home", Icons.Material.Filled.Home, ""),
        new(MyWork, "My work", Icons.Material.Filled.Inbox),
        new(Approvals, "Approvals", Icons.Material.Filled.Approval, "approvals"),
        new(Projects, "Projects", Icons.Material.Filled.Work, "projects"),
        new(People, "People", Icons.Material.Filled.People),
        new(Growth, "Business development", Icons.Material.Filled.TrendingUp),
        new(Sheets, "Sheets & dashboards", Icons.Material.Filled.TableChart),
        new(Finance, "Finance", Icons.Material.Filled.AccountBalance),
        new(Materials, "Materials", Icons.Material.Filled.PrecisionManufacturing),
        new(Reports, "Reports", Icons.Material.Filled.Assessment),
        new(Admin, "Admin", Icons.Material.Filled.AdminPanelSettings),
    ];

    public static IReadOnlyList<NavEntry> Entries { get; } =
    [
        new(Home, null, "Home", "", Icons.Material.Filled.Home, NavRule.Public, MatchAll: true, Keywords: "my work start"),

        // My work: what everyone does for themselves.
        new(MyWork, null, "Time", "time", Icons.Material.Filled.AccessTime, NavRule.Everyone, MatchAll: true, Detail: "Timesheet", Keywords: "timesheet hours"),
        new(MyWork, null, "Team time", "time/team", Icons.Material.Filled.Groups, NavRule.Custom(a => a.ShowTeamTime, "Supervisors also see their own team", P.TimeTeam), Keywords: "supervisor timesheets"),
        new(MyWork, "Expenses and travel", "Expenses", "expenses", Icons.Material.Filled.Receipt, NavRule.Everyone, Detail: "Expense report", Keywords: "expense report travel receipts"),
        new(MyWork, "Expenses and travel", "My receipts", "receipts", Icons.Material.Filled.ReceiptLong, NavRule.Everyone, Keywords: "receipt folio hotel upload attachments files library"),
        new(MyWork, "Expenses and travel", "Travel", "travel", Icons.Material.Filled.FlightTakeoff, NavRule.Everyone, Detail: "Trip", Keywords: "travel authorization trip request per diem advance"),
        new(MyWork, null, "Leave", "leave", Icons.Material.Filled.BeachAccess, NavRule.Everyone, Keywords: "vacation pto time off"),
        new(MyWork, null, "My requisitions", "purchasing/requisitions", Icons.Material.Filled.PostAdd, NavRule.Everyone, Detail: "Requisition", Keywords: "purchase request buy"),
        new(MyWork, null, "My budgets", "planning/my-budget", Icons.Material.Filled.TableChart, NavRule.Custom(a => a.OwnsBudget, "Owners of an org unit budget"), Keywords: "org unit budget planning submit"),
        new(MyWork, null, "Org unit budget", "planning/submissions", Icons.Material.Filled.TableChart, NavRule.Everyone, Detail: "Budget", InMenu: false),
        new(MyWork, null, "Receiving", "purchasing/receiving", Icons.Material.Filled.Inventory, NavRule.Custom(a => a.IsReceiver && !a.Can(P.PurchaseOrdersManage), "People named as the receiver on an open purchase order"), Keywords: "receive goods purchase order"),

        // One inbox for every approval; the old approval pages keep their routes and are reached from it.
        new(Approvals, null, "Approvals", "approvals", Icons.Material.Filled.Approval, NavRule.Custom(a => a.ShowApprovals, "Also anyone with something waiting for their approval", P.ApBillsManage, P.TimeTeam),
            Keywords: "approve reject inbox timesheet expense odc bill requisition delegation"),

        new(Projects, null, "All projects", "projects", Icons.Material.Filled.Work, NavRule.Everyone, Keywords: "contracts wbs"),
        new(Projects, null, "New project", "setup/projects/new", Icons.Material.Filled.CreateNewFolder, NavRule.Custom(a => a.Can(P.ProjectsCreate) && !a.Can(P.SetupProjects), "Under Admin › Setup instead for those who set up every project", P.ProjectsCreate), MatchAll: true, Keywords: "create request project wbs plan", InMenu: false), // + New › Project.
        new(Projects, null, "Resourcing", "resourcing", Icons.Material.Filled.GroupWork, NavRule.Custom(a => a.Can(P.ResourcingManage) || a.IsPm, "Also managers of a project", P.ResourcingManage, P.ProjectsManageOwn), Keywords: "staffing allocations capacity utilization heat map"),
        new(Projects, null, "Project costs", "odcs", Icons.Material.Filled.RequestQuote, NavRule.Custom(a => a.Can(P.ProjectsFinancialsView) || a.IsPm, "Also managers of a project", P.ProjectsFinancialsView, P.ProjectsManageOwn), MatchAll: true, Keywords: "other direct costs vendor"),
        new(Projects, null, "Proposal pricing", "pricing", Icons.Material.Filled.PriceCheck, NavRule.Need(P.PricingManage), Detail: "Proposal", Keywords: "bid proposal price cost volume wrap rate fee scenario propricer"),

        // People (HR): HR's own work first; everyone's directory (with the org chart) and their own profile (with benefits).
        new(People, "HR administration", "Positions", "hr/positions", Icons.Material.Filled.WorkOutline, NavRule.Need(P.HrWorkforceView, P.HrPositionsManage), Keywords: "approved positions headcount open requisitions salary range"),
        new(People, "HR administration", "Onboarding and offboarding", "hr/onboarding", Icons.Material.Filled.Checklist, NavRule.Need(P.HrOnboardingManage), Detail: "Checklist", Keywords: "new hire checklist termination exit"),
        new(People, "HR administration", "Personnel requests", "hr/requests", Icons.Material.Filled.MarkEmailUnread, NavRule.Need(P.HrRecordsManage), Keywords: "change requests employee updates"),
        new(People, "HR administration", "Compensation", "hr/compensation", Icons.Material.Filled.Paid, NavRule.Need(P.HrWorkforceView, P.HrCompensationManage), Keywords: "salary raise merit cycle pay change"),
        new(People, "HR administration", "Benefits administration", "hr/benefits", Icons.Material.Filled.MedicalServices, NavRule.Need(P.HrWorkforceView, P.HrBenefitsManage), Keywords: "plans tiers open enrollment deductions paylocity fringe"),
        new(People, "Directory", "Directory", "hr", Icons.Material.Filled.People, NavRule.Everyone, MatchAll: true, Detail: "Employee", Keywords: "employees personnel hr records"),
        new(People, "Directory", "Org chart", "hr/org-chart", Icons.Material.Filled.AccountTree, NavRule.Everyone, Keywords: "reporting structure supervisors"),
        new(People, "My profile", "My profile", "hr/me", Icons.Material.Filled.Badge, NavRule.Everyone, Keywords: "personal details emergency contacts address self identification eeo"),
        new(People, "My profile", "My benefits", "hr/benefits/me", Icons.Material.Filled.HealthAndSafety, NavRule.Everyone, Keywords: "open enrollment medical dental vision 401k fsa hsa dependents life event"),
        // Business development: the capture pipeline (BD, Finance, Admin); capture and proposal managers see their own.
        new(Growth, "Business development", "Pipeline", "bd", Icons.Material.Filled.ViewKanban, NavRule.Custom(a => a.ShowBusinessDevelopment, "Also capture and proposal managers (their own opportunities)", P.PipelineView), MatchAll: true, Keywords: "capture board opportunities crm win themes swot pwin color team"),
        new(Growth, "Business development", "Opportunities", "bd/opportunities", Icons.Material.Filled.Flag, NavRule.Custom(a => a.ShowBusinessDevelopment, "Also capture and proposal managers (their own opportunities)", P.PipelineView), Detail: "Opportunity", Keywords: "pursuits rfp solicitation capture"),
        new(Growth, "Business development", "Accounts and contacts", "bd/accounts", Icons.Material.Filled.Contacts, NavRule.Need(P.PipelineView), Detail: "Account", Keywords: "agencies offices primes partners competitors crm"),
        new(Growth, "Business development", "Leads", "bd/leads", Icons.Material.Filled.TravelExplore, NavRule.Need(P.PipelineView), Keywords: "sam.gov saved searches import notices"),
        new(Growth, "Business development", "Forecast and win/loss", "bd/forecast", Icons.Material.Filled.Insights, NavRule.Need(P.PipelineView), Keywords: "pwin pgo weighted pipeline b&p spend"),
        new(Growth, "Business development", "Gates and settings", "bd/settings", Icons.Material.Filled.Rule, NavRule.Need(P.PipelineView), Keywords: "go no-go bid decision checklist b&p charge code capture exec captureexec import"),

        new(Sheets, null, "Sheets", "sheets", Icons.Material.Filled.TableChart, NavRule.Everyone, Detail: "Sheet", Keywords: "grid gantt board calendar plan"),
        new(Sheets, null, "Dashboards", "dashboards", Icons.Material.Filled.Dashboard, NavRule.Everyone, Detail: "Dashboard", Keywords: "charts widgets"),

        // Finance (Finance and administrators): in each group the everyday pages first, the setup pages last.
        new(Finance, "Accounting", "Journal entries", "accounting/journal", Icons.Material.Filled.MenuBook, NavRule.Need(P.GlJournalsPost), Detail: "Journal entry", Keywords: "gl general ledger je"),
        new(Finance, "Accounting", "Period close", "accounting/close", Icons.Material.Filled.EventAvailable, NavRule.Need(P.GlClose), Keywords: "fiscal month end"),
        new(Finance, "Accounting", "Month in review", "accounting/month-review", Icons.Material.Filled.CompareArrows, NavRule.Need(P.GlReportsView), Keywords: "what changed since last month variance controller revenue net income"),
        new(Finance, "Accounting", "Recurring journals", "accounting/recurring", Icons.Material.Filled.EventRepeat, NavRule.Need(P.GlRecurringManage), Keywords: "recurring journal entries rent accrual amortization auto reverse"),
        new(Finance, "Accounting", "Currency revaluation", "accounting/revaluation", Icons.Material.Filled.CurrencyExchange, NavRule.Need(P.GlRevaluation), Keywords: "foreign exchange fx unrealized gain loss month end period close multicurrency"),
        new(Finance, "Accounting", "Foreign currency report", "accounting/foreign-currency", Icons.Material.Filled.Language, NavRule.Need(P.GlRevaluation), Keywords: "fx exposure realized unrealized gain loss multicurrency"),
        new(Finance, "Accounting", "Chart of accounts", "accounting/accounts", Icons.Material.Filled.AccountTree, NavRule.Need(P.GlAccountsManage), Keywords: "gl accounts"),
        new(Finance, "Accounting", "Posting rules", "accounting/posting-rules", Icons.Material.Filled.Rule, NavRule.Need(P.GlPostingManage)),
        new(Finance, "Accounting", "Sales and use tax return", "accounting/sales-tax", Icons.Material.Filled.ReceiptLong, NavRule.Need(P.SalesTaxManage), InMenu: false, Keywords: "sales tax use tax return jurisdiction state county filing"),


        new(Finance, "Billing & AR", "Invoices", "billing", Icons.Material.Filled.Receipt, NavRule.Need(P.BillingInvoicesManage), MatchAll: true, Keywords: "billing receivables"),
        new(Finance, "Billing & AR", "Ready to bill", "billing/ready", Icons.Material.Filled.PlaylistAddCheck, NavRule.Need(P.BillingInvoicesManage), Keywords: "invoice unbilled draft start"),
        new(Finance, "Billing & AR", "New invoice", "billing/new", Icons.Material.Filled.NoteAdd, NavRule.Need(P.BillingInvoicesManage), InMenu: false),
        new(Finance, "Billing & AR", "Customer payments", "billing/payments", Icons.Material.Filled.Payments, NavRule.Need(P.BillingPaymentsRecord), Keywords: "cash receipts"),
        new(Finance, "Billing & AR", "AR aging", "billing/aging", Icons.Material.Filled.HourglassBottom, NavRule.Need(P.BillingReportsView), Keywords: "receivables overdue"),
        new(Finance, "Billing & AR", "Unbilled accrual", "billing/accrual", Icons.Material.Filled.EventRepeat, NavRule.Need(P.BillingInvoicesManage)),
        new(Finance, "Billing & AR", "Contracts and milestones", "billing/contracts", Icons.Material.Filled.Flag, NavRule.Need(P.BillingInvoicesManage), Detail: "Contract"),
        new(Finance, null, "Invoice", "billing/invoices", Icons.Material.Filled.Receipt, NavRule.Need(P.BillingInvoicesManage), Detail: "Invoice", InMenu: false),
        new(Finance, null, "Customer statement", "billing/statements", Icons.Material.Filled.Receipt, NavRule.Need(P.BillingInvoicesManage), Detail: "Statement", InMenu: false),

        new(Finance, "Payables & purchasing", "Bills", "purchasing/bills", Icons.Material.Filled.ReceiptLong, NavRule.Need(P.ApBillsManage), Detail: "Bill", Keywords: "ap accounts payable vendor invoices"),
        new(Finance, "Payables & purchasing", "Payment runs", "purchasing/payments", Icons.Material.Filled.Payments, NavRule.Need(P.ApPaymentsManage), Detail: "Payment run", Keywords: "pay vendors checks ach"),
        new(Finance, "Payables & purchasing", "Purchase orders", "purchasing/orders", Icons.Material.Filled.ShoppingBasket, NavRule.Need(P.PurchaseOrdersManage), Detail: "Purchase order", Keywords: "po"),
        new(Finance, "Payables & purchasing", "Receiving", "purchasing/receiving", Icons.Material.Filled.Inventory, NavRule.Need(P.PurchaseOrdersManage)),
        new(Finance, "Payables & purchasing", "Match exceptions", "purchasing/match-exceptions", Icons.Material.Filled.CompareArrows, NavRule.Need(P.PurchaseOrdersManage), Keywords: "three way match"),
        new(Finance, "Payables & purchasing", "Vendors", "purchasing/vendors", Icons.Material.Filled.Storefront, NavRule.Need(P.VendorsManage), Detail: "Vendor", Keywords: "suppliers 1099"),
        new(Finance, "Payables & purchasing", "Subcontracts", "purchasing/subcontracts", Icons.Material.Filled.Handshake, NavRule.Need(P.VendorsManage), Detail: "Subcontract"),
        new(Finance, "Payables & purchasing", "Requests for quote", "purchasing/rfqs", Icons.Material.Filled.RequestQuote, NavRule.Need(P.SourcingManage), Detail: "RFQ", Keywords: "rfq quotes sourcing competition bids award"),
        new(Finance, "Payables & purchasing", "Supplier portal", "purchasing/supplier-portal", Icons.Material.Filled.Storefront, NavRule.Need(P.SourcingManage), Keywords: "vendor portal contacts acknowledgements ship notices asn", InMenu: false), // Linked from Requests for quote.

        new(Finance, "Cash", "Cash and bank accounts", "cash", Icons.Material.Filled.AccountBalanceWallet, NavRule.Need(P.CashManage), MatchAll: true),
        new(Finance, "Cash", "Import statements", "cash/import", Icons.Material.Filled.UploadFile, NavRule.Need(P.CashManage), Keywords: "bank statement", InMenu: false),
        new(Finance, "Cash", "Reconcile", "cash/reconcile", Icons.Material.Filled.FactCheck, NavRule.Need(P.CashManage), Detail: "Reconciliation", Keywords: "bank reconciliation"),
        new(Finance, "Cash", "Reconciliation history", "cash/reconciliations", Icons.Material.Filled.History, NavRule.Need(P.CashManage)),
        new(Finance, "Cash", "Cash forecast", "cash/forecast", Icons.Material.Filled.ShowChart, NavRule.Need(P.CashForecastView)),
        new(Finance, "Cash", "Corporate cards", "cards", Icons.Material.Filled.CreditCard, NavRule.Need(P.CardsManage), Keywords: "card statement import reconciliation aging p-card travel card"),

        new(Finance, "Assets", "Asset register", "assets", Icons.Material.Filled.ListAlt, NavRule.Need(P.AssetsManage), MatchAll: true, Detail: "Asset", Keywords: "fixed assets"),
        new(Finance, "Assets", "Capitalize", "assets/capitalize", Icons.Material.Filled.AddBox, NavRule.Need(P.AssetsManage), InMenu: false),
        new(Finance, "Assets", "Depreciation", "assets/depreciation", Icons.Material.Filled.TrendingDown, NavRule.Need(P.AssetsDepreciationRun)),
        new(Finance, "Assets", "Asset categories", "assets/categories", Icons.Material.Filled.Category, NavRule.Need(P.AssetsManage), Keywords: "fixed asset classes useful life depreciation method"),
        new(Finance, "Assets", "Tax depreciation", "assets/tax", Icons.Material.Filled.RequestQuote, NavRule.Need(P.AssetsManage), Keywords: "tax books macrs section 179 bonus form 4562 mid-quarter book to tax deferred tax federal state amt"),
        new(Finance, "Assets", "Asset transfers", "assets/transfers", Icons.Material.Filled.SwapHoriz, NavRule.Need(P.AssetsManage), Keywords: "move asset location custodian organization company intercompany history", InMenu: false),

        new(Finance, "Payroll", "Payroll", "payroll", Icons.Material.Filled.Payments, NavRule.Need(P.PayrollRun), Detail: "Payroll run", Keywords: "paylocity pay runs"),

        new(Finance, "Revenue & compliance", "Revenue", "accounting/revenue", Icons.Material.Filled.TrendingUp, NavRule.Need(P.RevenueManage), MatchAll: true, Keywords: "revenue recognition run"),
        new(Finance, "Revenue & compliance", "Revenue estimates", "accounting/revenue/estimates", Icons.Material.Filled.Calculate, NavRule.Need(P.RevenueManage), Keywords: "eac etc", InMenu: false),
        new(Finance, "Revenue & compliance", "Revenue setup", "accounting/revenue/setup", Icons.Material.Filled.Tune, NavRule.Need(P.RevenueManage), InMenu: false),
        new(Finance, "Revenue & compliance", "Incurred cost", "accounting/incurred-cost", Icons.Material.Filled.Gavel, NavRule.Need(P.IncurredCostManage), Detail: "Submission", Keywords: "ice dcaa submission"),
        new(Finance, "Revenue & compliance", "Indirect rates", "accounting/indirect-rates", Icons.Material.Filled.Percent, NavRule.Need(P.IndirectRatesManage), Keywords: "fringe overhead g&a pools"),
        new(Finance, "Revenue & compliance", "Total time accounting", "accounting/total-time", Icons.Material.Filled.MoreTime, NavRule.Need(P.TimeTotalTime), Keywords: "tta uncompensated overtime exempt salaried effective rate labor cost method"),
        new(Finance, "Planning", "Budgets", "planning/budgets", Icons.Material.Filled.TableChart, NavRule.Need(P.BudgetsManage), Detail: "Budget version", Keywords: "corporate gl budget versions original revised forecast spread"),
        new(Finance, "Planning", "Budget vs actual", "planning/budget-vs-actual", Icons.Material.Filled.CompareArrows, NavRule.Need(P.BudgetsManage), Keywords: "variance budget actual income statement org unit pool"),
        new(Finance, "Planning", "Planning cycles", "planning/cycles", Icons.Material.Filled.Groups, NavRule.Need(P.BudgetsManage), Detail: "Planning cycle", Keywords: "annual plan budget submissions org unit owners"),
        new(Finance, null, "Revenue by project", "accounting/revenue/projects", Icons.Material.Filled.TrendingUp, NavRule.Need(P.RevenueManage), Detail: "Project", InMenu: false),

        // Materials: the Materials role (and administrators) runs it; Finance reads parts, inventory and the inventory reports
        // (the other pages still open for Finance from links on those).
        new(Materials, "Inventory", "Parts", "materials", Icons.Material.Filled.Category, NavRule.Need(P.MaterialsView), MatchAll: true, Keywords: "part master item inventory make buy"),
        new(Materials, null, "Part", "materials/parts", Icons.Material.Filled.Category, NavRule.Need(P.MaterialsView), Detail: "Part", InMenu: false),
        new(Materials, "Inventory", "Inventory", "materials/inventory", Icons.Material.Filled.Inventory2, NavRule.Need(P.MaterialsView), Keywords: "stock on hand bins lots serials gfm"),
        new(Materials, "Inventory", "Receiving", "materials/receiving", Icons.Material.Filled.MoveToInbox, NavRule.Need(P.MaterialsManage), Keywords: "receive po government furnished gfm"),
        new(Materials, "Inventory", "Transactions", "materials/transactions", Icons.Material.Filled.SwapVert, NavRule.Need(P.MaterialsManage), Keywords: "issue transfer adjustment cycle count"),
        new(Materials, "Manufacturing", "Bills of materials", "materials/boms", Icons.Material.Filled.AccountTree, NavRule.Need(P.MaterialsManage), Detail: "BOM", Keywords: "bom routing where used"),
        new(Materials, "Manufacturing", "Work orders", "materials/work-orders", Icons.Material.Filled.Build, NavRule.Need(P.MaterialsManage), Detail: "Work order", Keywords: "wo shop floor kit wip"),
        new(Materials, "Manufacturing", "MRP", "materials/mrp", Icons.Material.Filled.EventNote, NavRule.Need(P.MaterialsManage), Keywords: "material requirements planning planned orders forecast"),
        new(Materials, "Quality & engineering", "Quality", "materials/quality", Icons.Material.Filled.VerifiedUser, NavRule.Need(P.MaterialsManage), Detail: "Quality record", Keywords: "inspection queue ncr mrb nonconformance disposition corrective action car supplier quality aql coc"),
        new(Materials, "Quality & engineering", "Engineering changes", "materials/engineering-changes", Icons.Material.Filled.ChangeCircle, NavRule.Need(P.MaterialsManage), Detail: "Engineering change", Keywords: "eco ecn revision effectivity configuration as built as designed"),
        new(Materials, null, "Receive on a phone", "m/receive", Icons.Material.Filled.PhoneIphone, NavRule.Need(P.MaterialsManage), MatchAll: true, Keywords: "mobile receiving", InMenu: false),
        new(Materials, "Inventory", "Inventory reports", "materials/reports", Icons.Material.Filled.Assessment, NavRule.Need(P.MaterialsView), Keywords: "valuation aging gfm wip trace"),
        new(Materials, "Sales and shipping", "Sales orders", "materials/sales-orders", Icons.Material.Filled.ReceiptLong, NavRule.Need(P.SalesOrdersView), Detail: "Sales order", Keywords: "so customer order clin delivery"),
        new(Materials, "Sales and shipping", "Shipping", "materials/shipping", Icons.Material.Filled.LocalShipping, NavRule.Need(P.MaterialsManage), Keywords: "pick pack ship shipment dd250 wawf piee tracking"),
        new(Materials, null, "Pick list", "materials/pick-lists", Icons.Material.Filled.PlaylistAddCheck, NavRule.Need(P.MaterialsView), Detail: "Pick list", InMenu: false),
        new(Materials, null, "Shipment", "materials/shipments", Icons.Material.Filled.LocalShipping, NavRule.Need(P.MaterialsView), Detail: "Shipment", InMenu: false),
        new(Materials, null, "Pick on a phone", "m/pick", Icons.Material.Filled.PhoneIphone, NavRule.Need(P.MaterialsManage), MatchAll: true, Keywords: "mobile picking", InMenu: false),
        new(Materials, "Manufacturing", "Kits", "materials/kits", Icons.Material.Filled.Inventory, NavRule.Need(P.MaterialsManage), Detail: "Kit", Keywords: "kitting pick list stage shortages"),
        new(Materials, "Manufacturing", "Backflush", "materials/backflush", Icons.Material.Filled.AutoMode, NavRule.Need(P.MaterialsManage), Keywords: "issue on completion backflushing shortages"),
        new(Materials, "Manufacturing", "Capacity schedule", "materials/capacity", Icons.Material.Filled.ViewTimeline, NavRule.Need(P.MaterialsManage), Keywords: "finite capacity scheduling load chart work center calendar queue"),
        new(Materials, "Inventory", "Physical inventory", "materials/physical-inventory", Icons.Material.Filled.FactCheck, NavRule.Need(P.MaterialsManage), Detail: "Physical inventory", Keywords: "wall to wall count tags freeze recount"),
        new(Materials, "Inventory", "Landed cost", "materials/landed-cost", Icons.Material.Filled.LocalShipping, NavRule.Need(P.MaterialsManage), Detail: "Landed cost", Keywords: "freight duty handling allocation receipts"), // Finance: linked from Inventory reports (its menu is at its link budget).
        new(Materials, "Inventory", "Materials setup", "materials/setup", Icons.Material.Filled.Warehouse, NavRule.Need(P.MaterialsManage), Keywords: "warehouses bins work centers"),
        new(Materials, null, "Count on a phone", "m/count", Icons.Material.Filled.PhoneIphone, NavRule.Need(P.MaterialsManage), MatchAll: true, Keywords: "mobile physical inventory count tags", InMenu: false),

        // Reports: one place for every report.
        new(Reports, null, "Report catalog", "reports", Icons.Material.Filled.LibraryBooks, NavRule.Everyone, MatchAll: true, Keywords: "reports"),
        new(Reports, null, "Report writer", "reports/writer", Icons.Material.Filled.Build, NavRule.Everyone, Detail: "Report", Keywords: "custom report builder"),
        new(Reports, "Standard reports", "Project status", "reports/standard/project-status", Icons.Material.Filled.Assessment, NavRule.Custom(a => a.IsPm && !a.Can(P.ProjectsFinancialsView), "Project managers; everyone else opens it from the report catalog", P.ProjectsManageOwn), Keywords: "eac etc budget variance pm"),
        new(Reports, "Standard reports", "Contract funding", "reports/standard/funding-status", Icons.Material.Filled.Assessment, NavRule.Custom(a => a.IsPm && !a.Can(P.ProjectsFinancialsView), "Project managers; everyone else opens it from the report catalog", P.ProjectsManageOwn), Keywords: "ceiling funded clin"),
        new(Reports, "Standard reports", "HR reports", "hr/reports", Icons.Material.Filled.Assessment, NavRule.Need(P.HrWorkforceView, P.HrRestrictedReports), Keywords: "headcount turnover eeo-1 vets-4212 clearances expirations benefits"),
        new(Reports, "Standard reports", "Timesheet flags", "time/flags", Icons.Material.Filled.Flag, NavRule.Custom(a => a.ShowTeamTime || a.Can(P.TimeViewAll), "Supervisors also see their own team", P.TimeTeam, P.TimeViewAll), Keywords: "anomalies odd timesheets dcaa review weekend late revisions"),
        new(Reports, "Standard reports", "Financial statements", "accounting/reports", Icons.Material.Filled.Assessment, NavRule.Need(P.GlReportsView), Keywords: "balance sheet income statement trial balance p&l"),
        new(Reports, "Standard reports", "Consolidated statements", "accounting/consolidated", Icons.Material.Filled.Assessment, NavRule.Need(P.GlReportsView), Keywords: "consolidation eliminations multicompany intercompany companies"),
        new(Reports, "Standard reports", "AP reports", "purchasing/reports", Icons.Material.Filled.Assessment, NavRule.Need(P.ApReportsView), Keywords: "payables aging 1099"),
        new(Reports, "Standard reports", "Asset reports", "assets/reports", Icons.Material.Filled.Assessment, NavRule.Need(P.AssetsManage)),
        new(Reports, "Standard reports", "Revenue reports", "accounting/revenue/reports", Icons.Material.Filled.Assessment, NavRule.Need(P.RevenueManage)),
        new(Reports, null, "Saved report", "reports/saved", Icons.Material.Filled.Assessment, NavRule.Everyone, Detail: "Report", InMenu: false),
        new(Reports, null, "Standard report", "reports/standard", Icons.Material.Filled.Assessment, NavRule.Everyone, Detail: "Report", InMenu: false),

        // Admin (administrators; Finance sees the setup parts, titled Setup). Setup first: it is what most people here come for.
        new(Admin, "Setup", "Getting started", "setup", Icons.Material.Filled.Checklist, NavRule.Need(P.SetupOrganizationView), MatchAll: true),
        new(Admin, "Setup", "Org units", "setup/org-units", Icons.Material.Filled.AccountTree, NavRule.Need(P.SetupPeople), Keywords: "departments"),
        new(Admin, "Setup", "Labor categories", "setup/labor-categories", Icons.Material.Filled.Badge, NavRule.Need(P.SetupPeople), Keywords: "plc"),
        new(Admin, "Setup", "Customers", "setup/customers", Icons.Material.Filled.Business, NavRule.Need(P.SetupContracts)),
        new(Admin, "Setup", "Contracts", "setup/contracts", Icons.Material.Filled.Description, NavRule.Need(P.SetupContracts), Detail: "Contract", Keywords: "bill rates funding"),
        new(Admin, "Setup", "Projects and WBS", "setup/projects", Icons.Material.Filled.Work, NavRule.Need(P.SetupProjects), Detail: "Project", Keywords: "charge codes"),
        new(Admin, "Setup", "Fiscal calendar", "setup/fiscal-calendar", Icons.Material.Filled.CalendarMonth, NavRule.Need(P.SetupFiscalCalendar), Keywords: "periods year"),
        new(Admin, "Setup", "Per diem rates", "setup/per-diem", Icons.Material.Filled.Hotel, NavRule.Need(P.SetupPerDiem), Keywords: "gsa travel"),
        new(Admin, "Setup", "Currencies and rates", "setup/currencies", Icons.Material.Filled.CurrencyExchange, NavRule.Need(P.SetupCurrencies), Keywords: "exchange rates fx multicurrency euro pound functional currency"),
        new(Admin, "Setup", "Card programs", "setup/card-programs", Icons.Material.Filled.CreditCard, NavRule.Need(P.CardsManage), Keywords: "corporate card issuer cardholder"),
        new(Admin, "Setup", "Sales and use tax", "setup/sales-tax", Icons.Material.Filled.ReceiptLong, NavRule.Need(P.SalesTaxManage), InMenu: false, Keywords: "sales tax rates jurisdictions tax codes exemption certificates resale avalara"),
        new(Admin, "Setup", "Organization", "setup/organization", Icons.Material.Filled.Tune, NavRule.Need(P.SetupOrganizationView), Keywords: "settings timesheet cadence company"),
        new(Admin, "Setup", "Companies", "setup/companies", Icons.Material.Filled.Domain, NavRule.Need(P.SetupCompanies), Keywords: "multicompany legal entity intercompany ein due to due from"),
        new(Admin, "Setup", "License", "setup/license", Icons.Material.Filled.WorkspacePremium, NavRule.Need(P.AdminOrganization), Keywords: "edition business community seats upgrade subscription pricing key plan"),
        new(Admin, "People and access", "People", "setup/people", Icons.Material.Filled.People, NavRule.Need(P.SetupPeople), Keywords: "employees cost rates supervisors"),
        new(Admin, "People and access", "Users and security", "admin/users", Icons.Material.Filled.ManageAccounts, NavRule.Need(P.AdminUsers, P.AdminSecurity), Keywords: "sign in accounts security invite deactivate"),
        new(Admin, "People and access", "Roles", "admin/roles", Icons.Material.Filled.AdminPanelSettings, NavRule.Need(P.AdminRoles), Detail: "Role", Keywords: "permissions security groups access rights custom role"),
        new(Admin, "People and access", "Assistant", "admin/assistant", Icons.Material.Filled.AutoAwesome, NavRule.Need(P.AdminAssistant), Keywords: "ai claude assistant usage audit bedrock govcloud"),
        new(Admin, "People and access", "Audit log", "setup/audit", Icons.Material.Filled.History, NavRule.Need(P.SetupAuditLog), Keywords: "history security"),
        new(Admin, "People and access", "Support requests", "admin/support", Icons.Material.Filled.SupportAgent, NavRule.Need(P.AdminSupport), Keywords: "help desk problems feedback bug reports"),
        new(Admin, "Customization", "Customization", "admin/customization", Icons.Material.Filled.Tune, NavRule.Need(P.AdminCustomization), Keywords: "custom fields picklists lists dropdowns status labels sub-status numbering document numbers"),
        new(Admin, "Customization", "Layouts", "admin/customization/layouts", Icons.Material.Filled.ViewQuilt, NavRule.Need(P.AdminCustomization), Keywords: "forms fields sections required hidden home page widgets dashboard roles customize"),
        new(Admin, "Customization", "Automation", "admin/automation", Icons.Material.Filled.Bolt, NavRule.Need(P.AdminAutomations), Detail: "Automation", Keywords: "workflow rules triggers conditions actions notify escalate validation receipts funding thresholds"),
        new(Admin, "Customization", "Approval workflows", "setup/approval-workflows", Icons.Material.Filled.AltRoute, NavRule.Need(P.ApprovalWorkflowsView), Detail: "Workflow", Keywords: "routing approvers delegation"),
        new(Admin, "Integrations", "EDI", "admin/integrations/edi", Icons.Material.Filled.SwapHoriz, NavRule.Need(P.SourcingManage), Keywords: "x12 850 855 856 810 edi trading partners vendors"),
        new(Admin, "Integrations", "Integrations", "admin/integrations", Icons.Material.Filled.Hub, NavRule.Need(P.AdminIntegrations), Keywords: "webhooks api keys teams slack quickbooks sftp export connector"),
        new(Admin, "Integrations", "Import", "setup/import", Icons.Material.Filled.UploadFile, NavRule.Need(P.SetupImports), Detail: "Import", Keywords: "costpoint smartsheet unanet csv"),
        new(Admin, "Integrations", "Go-live migration", "setup/migration", Icons.Material.Filled.Flag, NavRule.Need(P.SetupImports), Detail: "Reconciliation", Keywords: "cutover cut-over costpoint parallel reconcile opening balances"),
        new(Admin, "Integrations", "Costpoint parallel run", "setup/migration/parallel-run", Icons.Material.Filled.FactCheck, NavRule.Need(P.SetupImports), Detail: "Costpoint", Keywords: "costpoint parallel month trial balance project cost reconcile differences cause"),
        new(Admin, "Compliance", "DCAA readiness", "admin/dcaa-readiness", Icons.Material.Filled.Gavel, NavRule.Need(P.AdminCompliance), Keywords: "sf 1408 pre-award survey accounting system compliance cpa"),
        new(Admin, "Compliance", "Purchasing system review", "admin/cpsr-readiness", Icons.Material.Filled.FactCheck, NavRule.Need(P.CasCpsrManage), Keywords: "cpsr purchasing system review procurement file competition quotes sole source flowdown debarment"),
        new(Admin, "Compliance", "CMMC assessment", "admin/cmmc", Icons.Material.Filled.VerifiedUser, NavRule.Need(P.AdminCompliance), Keywords: "cmmc nist 800-171 sprs score cui poam plan of action milestones ssp dfars 7012 security"),
        new(Admin, "Compliance", "CAS disclosure statement", "admin/cas-disclosure", Icons.Material.Filled.MenuBook, NavRule.Need(P.CasCpsrManage), Keywords: "cas ds-1 ds1 ds-2 disclosure statement 401 402 cost accounting standards consistency"),

        // Findable with Ctrl+K only.
        new(Home, null, "Notifications", "notifications", Icons.Material.Filled.Notifications, NavRule.Everyone, Keywords: "alerts email digest settings", InMenu: false),
        new(Home, null, "Mobile view", "m", Icons.Material.Filled.PhoneIphone, NavRule.Everyone, MatchAll: true, Keywords: "phone app", InMenu: false),
        new(Approvals, null, "Timesheet approvals", "time/approvals", Icons.Material.Filled.FactCheck, NavRule.Custom(a => a.ShowTeamTime, "Supervisors also see their own team", P.TimeTeam), InMenu: false),
        new(Approvals, null, "ODC approvals", "odcs/approvals", Icons.Material.Filled.FactCheck, NavRule.Custom(a => a.ShowApprovals, "Also anyone with something waiting for their approval", P.ApBillsManage, P.TimeTeam), InMenu: false),
        new(Approvals, null, "Bill approvals", "purchasing/approvals", Icons.Material.Filled.FactCheck, NavRule.Custom(a => a.ShowApprovals, "Also anyone with something waiting for their approval", P.ApBillsManage, P.TimeTeam), InMenu: false),
        new(Home, null, "Help center", "help", Icons.Material.Filled.MenuBook, NavRule.Public, Detail: "Article", Keywords: "how to guide support faq", InMenu: false),
        new(Home, null, "Glossary", "help/glossary", Icons.Material.Filled.Translate, NavRule.Public, Keywords: "terms definitions acronyms", InMenu: false),
        new(Home, null, "What's new", "help/whats-new", Icons.Material.Filled.NewReleases, NavRule.Public, Keywords: "changes release notes changelog", InMenu: false),
        new(Home, null, "My support requests", "support", Icons.Material.Filled.SupportAgent, NavRule.Everyone, Keywords: "report problem feedback bug help", InMenu: false),
        new(MyWork, null, "My staffing", "resourcing", Icons.Material.Filled.GroupWork, NavRule.Custom(a => a.IsSignedIn && !a.Can(P.ResourcingManage) && !a.IsPm, "Everyone who doesn't see Resourcing"), Keywords: "resourcing staffing allocations", InMenu: false),
    ];

    /// <summary>The section's title for this user: people without any Administration permission see Admin as "Setup".</summary>
    public static string TitleFor(NavSection section, NavAccess access) =>
        section.Key == Admin && !access.IsAdministrator ? "Setup" : section.Title;

    /// <summary>The pages a permission reveals (menu and Ctrl+K), for Admin › Roles.</summary>
    public static IReadOnlyList<NavEntry> PagesRevealedBy(string permission) =>
        Entries.Where(e => e.Rule.Permissions.Contains(permission)).DistinctBy(e => e.Href).ToList();

    /// <summary>The entry that owns <paramref name="path"/> (longest matching href), visible or not.</summary>
    public static NavEntry? OwnerOf(string path)
    {
        var segments = Segments(path);
        NavEntry? owner = null;
        var length = -1;
        foreach (var entry in Entries)
        {
            var href = Segments(entry.Href);
            if (href.Length > segments.Length || href.Length <= length || href.Length == 0)
            {
                continue;
            }

            if (href.Select((s, i) => string.Equals(s, segments[i], StringComparison.OrdinalIgnoreCase)).All(x => x))
            {
                owner = entry;
                length = href.Length;
            }
        }

        return owner;
    }

    /// <summary>"… needs the … permission" for the page at <paramref name="path"/>, or null when its entry names none.</summary>
    public static string? Explain(string path) =>
        OwnerOf(path) is { Rule.Permissions: { Count: > 0 } permissions } owner
            ? $"{owner.Title} needs the {string.Join(" or ", permissions.Select(p => $"\"{PermissionCatalog.NameOf(p)}\""))} permission. Ask an administrator if you need it."
            : null;

    /// <summary>
    /// Whether <paramref name="user"/> may open the page at <paramref name="path"/>, for layouts that guard a whole area:
    /// the catalog entry that owns the path decides (longest matching href, whether or not it's visible); a path no entry
    /// owns, or one whose entry also opens through a relationship, falls back to <paramref name="fallback"/>.
    /// </summary>
    public static bool PageAllowed(string path, CurrentUserInfo user, Func<PermissionSet, bool> fallback)
    {
        var owner = OwnerOf(path);
        if (owner is null || owner.Rule.Test is not null)
        {
            return fallback(user.Permissions) || (owner?.Rule.Permissions.Any(user.Can) ?? false);
        }

        return owner.Rule.Allows(new NavAccess(user));
    }

    public static IEnumerable<NavEntry> MenuEntries(NavAccess access) => Entries.Where(e => e.InMenu && e.IsVisible(access));

    /// <summary>
    /// A section's menu items in catalog order: a page, or a group of pages that shows as one link (to the first page, or
    /// the group's current page) and as tabs across the top of each of its pages. A group with one visible page is just
    /// that page.
    /// </summary>
    public static IReadOnlyList<NavItem> MenuItems(string section, NavAccess access)
    {
        var items = new List<NavItem>();
        foreach (var entry in MenuEntries(access).Where(e => e.Section == section))
        {
            if (entry.Group is not null && items.FirstOrDefault(i => i.Group == entry.Group) is { } existing)
            {
                existing.Pages.Add(entry);
            }
            else
            {
                items.Add(new NavItem(entry.Group, [entry]));
            }
        }

        return items;
    }

    /// <summary>The visible pages of <paramref name="entry"/>'s group (its tabs), or nothing when it isn't in a group of two or more.</summary>
    public static IReadOnlyList<NavEntry> GroupPages(NavEntry entry, NavAccess access) =>
        entry.Group is null || !entry.InMenu
            ? []
            : MenuItems(entry.Section, access).FirstOrDefault(i => i.Group == entry.Group) is { IsGroup: true } item ? item.Pages : [];

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
