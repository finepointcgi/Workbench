using System.Collections.Frozen;

namespace Workbench.Contracts.Identity;

/// <summary>
/// One capability a role can grant. <see cref="GrantedTo"/> lists the built-in roles that hold it (Admin holds every
/// permission), which is exactly the access those roles had before roles became configurable.
/// </summary>
/// <param name="Key">Stable id, stored on custom roles (for example <c>gl.journals.post</c>). Never rename one.</param>
/// <param name="Area">The group it's listed under in Admin › Roles.</param>
/// <param name="Name">Short name, sentence case.</param>
/// <param name="Description">What it allows, in plain words.</param>
/// <param name="GrantedTo">Built-in roles (besides Admin) that hold it.</param>
/// <param name="Sensitive">Reveals pay, rates, tax ids, bank details or security settings: shown with a warning.</param>
/// <param name="Source">Null for the built-in catalog; otherwise the feature that registered it at run time
/// (<see cref="IPermissionRegistry"/>), such as <c>custom-fields</c>.</param>
public sealed record PermissionDefinition(string Key, string Area, string Name, string Description, OrgRoles GrantedTo, bool Sensitive = false, string? Source = null)
{
    public bool IsRegistered => Source is not null;
}

/// <summary>
/// The permission catalog: every capability a role can grant, grouped by area. Each permission check on the server (the
/// permission decorators, <c>AccessControl.RequireAsync</c>) and each menu entry (<c>NavCatalog</c>) names one of these.
/// Documented in <c>docs/security/permissions.md</c>; keep that page in step when you add one.
/// </summary>
public static class Permission
{
    // Administration
    public const string AdminUsers = "admin.users";
    public const string AdminRoles = "admin.roles";
    public const string AdminSecurity = "admin.security";
    public const string AdminRetention = "admin.retention";
    // The DCAA readiness checklist's permission (its definition ships with the government contracting module; the key stays here because the checklist service, which is core, checks it).
    public const string AdminCompliance = "admin.compliance";
    public const string AdminIntegrations = "admin.integrations";
    public const string AdminAssistant = "admin.assistant";
    public const string AdminSupport = "admin.support";
    public const string AdminOrganization = "admin.organization";
    public const string AdminCustomization = "admin.customization";
    public const string AdminAutomations = "admin.automations";
    public const string AdminAllWorkspaces = "admin.all_workspaces";

    // Approvals
    public const string ApprovalWorkflowsView = "approvals.workflows.view";
    public const string ApprovalWorkflowsManage = "approvals.workflows.manage";
    public const string ApprovalDelegationsManage = "approvals.delegations.manage";
    public const string ApprovalsActAsAdmin = "approvals.act_as_admin";

    // Organization setup
    public const string SetupOrganizationView = "setup.organization.view";
    public const string SetupPeople = "setup.people";
    public const string SetupFiscalCalendar = "setup.fiscal_calendar";
    public const string SetupContracts = "setup.contracts";
    public const string SetupBillRates = "setup.bill_rates";
    public const string SetupProjects = "setup.projects";
    public const string SetupImports = "setup.imports";
    public const string SetupAuditLog = "setup.audit_log";
    public const string SetupPerDiem = "setup.per_diem";
    public const string SetupCurrencies = "setup.currencies";
    public const string SetupCompanies = "setup.companies";

    // Time and labor
    public const string TimeViewAll = "time.view_all";
    public const string TimeTeam = "time.team";
    public const string LaborCostView = "labor.cost.view";
    public const string LaborCostRatesManage = "labor.cost_rates.manage";
    public const string LeaveBalancesManage = "leave.balances.manage";
    public const string LeavePoliciesManage = "leave.policies.manage";

    // Projects
    public const string ProjectsViewAll = "projects.view_all";
    public const string ProjectsCreate = "projects.create";
    public const string ProjectsManageOwn = "projects.manage_own";
    public const string ProjectsFinancialsView = "projects.financials.view";
    public const string ResourcingManage = "projects.resourcing.manage";
    public const string ResourcingView = "projects.resourcing.view";

    // General ledger and accounting
    public const string GlAccountsManage = "gl.accounts.manage";
    public const string GlJournalsPost = "gl.journals.post";
    public const string GlPostingManage = "gl.posting.manage";
    public const string GlClose = "gl.close";
    public const string GlReportsView = "gl.reports.view";
    public const string GlExport = "gl.export";
    public const string GlRevaluation = "gl.revaluation";
    public const string IndirectRatesManage = "compliance.indirect_rates";

    // Billing and receivables
    public const string BillingInvoicesManage = "billing.invoices.manage";
    public const string BillingPaymentsRecord = "billing.payments.record";
    public const string BillingReportsView = "billing.reports.view";

    // Payables and purchasing
    public const string VendorsManage = "ap.vendors.manage";
    public const string VendorTaxIds = "ap.vendors.tax_ids";
    public const string VendorBankDetails = "ap.vendors.bank";
    public const string ApBillsManage = "ap.bills.manage";
    public const string ApPaymentsManage = "ap.payments.manage";
    public const string ApReportsView = "ap.reports.view";
    public const string PurchaseOrdersManage = "purchasing.orders.manage";
    public const string SourcingManage = "purchasing.sourcing.manage";

    // Cash, fixed assets, corporate budgets, recurring journals and sales tax: the accounting module defines these permissions (cash.*, assets.*, planning.*, tax.*, gl.recurring.*), see the module repository.

    // Payroll: the workforce module defines its permissions (payroll.*), see the module repository.

    // Travel and expenses
    public const string TravelManage = "travel.manage";
    public const string CardsManage = "cards.manage";

    // People (HR): the workforce module defines the personnel, position, compensation, benefit and onboarding permissions (hr.*); these two stay core.
    public const string HrWorkforceView = "hr.workforce.view";
    public const string HrRestrictedReports = "hr.reports.restricted";

    // Business development and pricing: the capture module defines its permissions (bd.*, pricing.*), see the module repository.

    // Materials, quality and sales: the materials module defines its permissions (materials.*, quality.*, sales.*), see the module repository.

    // Sheets
    public const string RestrictedColumnsView = "sheets.restricted_columns.view";
    public const string SheetFormsPublic = "sheets.forms.public";

    // Sensitive fields: withheld fields are masked in reads, exports, reports, the API and the assistant
    // ([SensitiveField] on contract properties). LaborCostView, PayRatesView, VendorTaxIds and VendorBankDetails are fields too.
    public const string FieldsSalaries = "fields.salaries";
    public const string FieldsBurdenedCost = "fields.burdened_cost";
    public const string FieldsEeo = "fields.eeo";
    public const string FieldsPricingFee = "fields.pricing_fee";
}

/// <summary>The catalog itself: definitions, built-in grants and separation-of-duties conflicts.</summary>
public static class PermissionCatalog
{
    private const OrgRoles F = OrgRoles.Finance;
    private const OrgRoles A = OrgRoles.Admin;
    private const OrgRoles H = OrgRoles.Hr;

    public const string AreaAdministration = "Administration";
    public const string AreaApprovals = "Approvals";
    public const string AreaSetup = "Organization setup";
    public const string AreaTime = "Time and labor";
    public const string AreaProjects = "Projects";
    public const string AreaAccounting = "Accounting";
    public const string AreaBilling = "Billing and receivables";
    public const string AreaPayables = "Payables and purchasing";
    public const string AreaCash = "Cash";
    public const string AreaPayroll = "Payroll";
    public const string AreaTravel = "Travel and cards";
    public const string AreaPeople = "People (HR)";
    public const string AreaGrowth = "Business development";
    public const string AreaMaterials = "Materials";
    public const string AreaSheets = "Sheets";
    public const string AreaFields = "Sensitive fields";

    /// <summary>Areas in display order.</summary>
    public static IReadOnlyList<string> Areas => Union().Areas;

    /// <summary>The built-in areas, without any a loaded module adds.</summary>
    public static IReadOnlyList<string> BuiltInAreas { get; } =
        [AreaAdministration, AreaApprovals, AreaSetup, AreaTime, AreaProjects, AreaAccounting, AreaBilling, AreaPayables, AreaCash, AreaPayroll, AreaTravel, AreaPeople, AreaGrowth, AreaMaterials, AreaSheets, AreaFields];

    /// <summary>Every permission: the built-in catalog, then those the loaded modules add (<see cref="Modules.ModuleRegistry"/>).</summary>
    public static IReadOnlyList<PermissionDefinition> All => Union().All;

    /// <summary>The built-in catalog alone.</summary>
    public static IReadOnlyList<PermissionDefinition> BuiltIn { get; } =
    [
        new(Permission.AdminUsers, AreaAdministration, "Administer users", "Invite people, deactivate and reactivate accounts, sign people out everywhere, link users to employees and assign roles.", A, Sensitive: true),
        new(Permission.AdminRoles, AreaAdministration, "Manage roles", "Create, edit, clone and delete custom roles and choose their permissions.", A, Sensitive: true),
        new(Permission.AdminSecurity, AreaAdministration, "Manage sign-in and security", "Sign-in settings (single sign-on, MFA, SCIM, sessions), the security event log and access reviews.", A, Sensitive: true),
        new(Permission.AdminRetention, AreaAdministration, "Manage retention and legal holds", "Record retention policies and legal holds.", A),
        new(Permission.AdminIntegrations, AreaAdministration, "Manage integrations", "API keys, webhooks, connectors and the Costpoint sync.", A, Sensitive: true),
        new(Permission.AdminAssistant, AreaAdministration, "Administer the assistant", "AI assistant settings, usage and audit.", A),
        new(Permission.AdminSupport, AreaAdministration, "Handle support requests", "See and answer everyone's support requests, and hear about new ones.", A),
        new(Permission.AdminOrganization, AreaAdministration, "Change organization settings", "Organization-wide settings such as the timesheet cadence.", A),
        new(Permission.AdminCustomization, AreaAdministration, "Manage customization", "Custom fields, picklists, status labels, document numbering, form layouts, shared list views and role home pages (Admin › Customization).", A),
        new(Permission.AdminAutomations, AreaAdministration, "Manage automations", "Build, test, turn on and off the automations under Admin › Automation, and see their run log.", A),
        new(Permission.AdminAllWorkspaces, AreaAdministration, "Own every workspace", "Owner access to every workspace and sheet, and the right to change anyone's dashboards, saved reports and schedules.", A),

        new(Permission.ApprovalWorkflowsView, AreaApprovals, "View approval workflows", "See approval workflows and the approvers set for each org unit.", F),
        new(Permission.ApprovalWorkflowsManage, AreaApprovals, "Change approval workflows", "Change how documents are routed for approval.", A, Sensitive: true),
        new(Permission.ApprovalDelegationsManage, AreaApprovals, "Manage anyone's delegations", "Create and revoke approval delegations for other people.", A),
        new(Permission.ApprovalsActAsAdmin, AreaApprovals, "Approver of last resort", "Decide approval steps that allow administrators, and approve timesheets and leave for people with no supervisor.", A),

        new(Permission.SetupOrganizationView, AreaSetup, "View organization setup", "The setup checklist and organization settings (read only).", F),
        new(Permission.SetupPeople, AreaSetup, "Manage employees and org units", "Add and change employees, org units and labor categories.", F),
        new(Permission.SetupFiscalCalendar, AreaSetup, "Manage the fiscal calendar", "Set up fiscal years and close or reopen fiscal periods.", F),
        new(Permission.SetupContracts, AreaSetup, "Manage customers and contracts", "Customers, contracts, CLINs, contract modifications and funding, and contracts from won opportunities and proposals.", F),
        new(Permission.SetupBillRates, AreaSetup, "View and change bill rates", "Contract bill rates.", F, Sensitive: true),
        new(Permission.SetupProjects, AreaSetup, "Set up every project", "Projects and WBS, charge assignments and plan sheets for every project.", F),
        new(Permission.SetupImports, AreaSetup, "Import data and run the cut-over", "Imports, the go-live checklist and reconciliation with Costpoint.", F),
        new(Permission.SetupAuditLog, AreaSetup, "Read the audit log", "The audit trail of changes to rates, setup, budgets and approvals.", F),
        new(Permission.SetupPerDiem, AreaSetup, "Manage per diem rates", "Import and change per diem rate tables.", F),
        new(Permission.SetupCurrencies, AreaSetup, "Manage currencies", "Currencies and exchange rates.", F),
        new(Permission.SetupCompanies, AreaSetup, "Manage companies", "Companies, intercompany accounts and company periods.", F),

        new(Permission.TimeViewAll, AreaTime, "See everyone's time", "Everyone's timesheets, hours, daily entries, leave requests, utilization and charge codes.", OrgRoles.Timekeeper | F),
        new(Permission.TimeTeam, AreaTime, "Team time for everyone", "The Team time page and timesheet approvals list for the whole organization.", OrgRoles.Timekeeper),
        new(Permission.LaborCostView, AreaFields, "See cost rates and labor cost", "Employee cost rates, labor cost in reports and the labor distribution.", F, Sensitive: true),
        new(Permission.LaborCostRatesManage, AreaTime, "Change cost rates", "Add employee cost rates.", F, Sensitive: true),
        new(Permission.LeaveBalancesManage, AreaTime, "Manage leave balances", "See everyone's leave balances, adjust them, run accruals and the leave year end.", F),
        new(Permission.LeavePoliciesManage, AreaTime, "Manage leave policies and holidays", "Leave policies, eligibility and the holiday calendar.", A),

        new(Permission.ProjectsViewAll, AreaProjects, "See every project", "Every project in lists, the projects hub, search and reports, not just the ones you're on (project visibility).", OrgRoles.ResourceManager | F),
        new(Permission.ProjectsCreate, AreaProjects, "Create projects", "Create new projects (project requests).", OrgRoles.ProjectManager | F),
        new(Permission.ProjectsManageOwn, AreaProjects, "Run the projects you manage", "Set up the projects you're the manager of: WBS, charge assignments, plan sheet, draft budgets and estimates at completion.", OrgRoles.ProjectManager),
        new(Permission.ProjectsFinancialsView, AreaProjects, "See every project's financials", "Financials, budgets, project costs and financial exports of every project, and contract funding. Project managers always see their own.", F),
        new(Permission.ResourcingManage, AreaProjects, "Staff every project", "Change anyone's allocations on every project and see capacity and utilization.", OrgRoles.ResourceManager),
        new(Permission.ResourcingView, AreaProjects, "See every project's staffing", "Allocations and open demand on every project.", F),

        new(Permission.GlAccountsManage, AreaAccounting, "Manage the chart of accounts", "Add and change general ledger accounts.", F),
        new(Permission.GlJournalsPost, AreaAccounting, "Post journal entries", "Draft, post and reverse manual journal entries.", F),
        new(Permission.GlPostingManage, AreaAccounting, "Manage posting rules", "General ledger posting setup.", F),
        new(Permission.GlClose, AreaAccounting, "Close fiscal periods", "The period close checklist and closing accounting periods, including closing anyway (with a reason) when a check fails.", F),
        new(Permission.GlReportsView, AreaAccounting, "See financial statements", "Balance sheet, income statement, trial balance, consolidated statements, intercompany balances and the general ledger in the report writer.", F),
        new(Permission.GlExport, AreaAccounting, "Export to accounting software", "Make QuickBooks, Xero and generic accounting files from journal entries, invoices, payments, bills and the chart of accounts, map account names, and see the export history.", F),
        new(Permission.GlRevaluation, AreaAccounting, "Revalue foreign currency", "Currency revaluation and the foreign currency report.", F),
        new(Permission.IndirectRatesManage, AreaAccounting, "Manage indirect rates", "Fringe, overhead and G&A pools and rates.", F),

        new(Permission.BillingInvoicesManage, AreaBilling, "Bill customers", "Invoices, billing milestones, government billing, progress payments, the unbilled accrual and customer statements.", F),
        new(Permission.BillingPaymentsRecord, AreaBilling, "Record customer payments", "Record and apply customer payments.", F),
        new(Permission.BillingReportsView, AreaBilling, "See receivables", "AR aging and receivables reports.", F),

        new(Permission.VendorsManage, AreaPayables, "Manage vendors", "Add and change vendors and subcontracts.", F),
        new(Permission.VendorTaxIds, AreaFields, "See and set vendor tax ids", "Set a vendor's TIN, see the full TIN and vendor W-9s.", F, Sensitive: true),
        new(Permission.VendorBankDetails, AreaFields, "Vendor bank details", "See and change where vendors are paid.", F, Sensitive: true),
        new(Permission.ApBillsManage, AreaPayables, "Enter and approve AP bills", "Every AP bill, credit and debit memo: enter, approve, post, void and apply them, and release subcontract retainage.", F),
        new(Permission.ApPaymentsManage, AreaPayables, "Pay vendors", "Payment runs (which net vendor credits), vendor refunds and bank payment files.", F),
        new(Permission.ApReportsView, AreaPayables, "See AP reports", "Payables aging and 1099 reports.", F),
        new(Permission.PurchaseOrdersManage, AreaPayables, "Manage purchase orders", "Every purchase order and requisition, receiving and the three-way match (and its overrides).", F),
        new(Permission.SourcingManage, AreaPayables, "Sourcing", "Requests for quote, the supplier portal and EDI.", F),



        new(Permission.TravelManage, AreaTravel, "Manage everyone's travel", "Everyone's trips, travel advances and per diem.", F),
        new(Permission.CardsManage, AreaTravel, "Manage corporate cards", "Card programs, statement imports, everyone's card charges and card reconciliation.", F),

        new(Permission.HrWorkforceView, AreaPeople, "See workforce and pay data", "Positions with salary ranges, compensation (and deciding compensation changes), benefits administration and deductions, and workforce reports.", H | F, Sensitive: true),
        new(Permission.HrRestrictedReports, AreaPeople, "Restricted HR reports", "EEO-1, VETS-4212, clearance and other restricted HR reports.", H, Sensitive: true),



        new(Permission.RestrictedColumnsView, AreaSheets, "See restricted columns", "Sheet columns marked \"Finance and owners only\" in every workspace.", F, Sensitive: true),
        new(Permission.SheetFormsPublic, AreaSheets, "Publish public forms",
            "Share a sheet's form by public link, so anyone with the link can add a row without signing in. Only in workspaces the person owns.",
            F | OrgRoles.ProjectManager),

        new(Permission.FieldsSalaries, AreaFields, "See salaries", "Salary ranges on positions and the amounts in compensation history and pay changes. Without it they show as hidden.", H | F, Sensitive: true),
        new(Permission.FieldsBurdenedCost, AreaFields, "See burdened cost", "Fringe, overhead and G&A applied to project cost (burdened cost and burdened financials). Project managers always see their own projects'.", F, Sensitive: true),
        new(Permission.FieldsEeo, AreaFields, "See EEO data", "Employees' EEO and veteran self-identification and the EEO-1 and VETS-4212 reports. Employees always see their own.", H, Sensitive: true),
        new(Permission.FieldsPricingFee, AreaFields, "See fee and margin", "Fee, fee rate, margin and price in proposal pricing, and the cost volume export. Without it pricing shows cost only.", OrgRoles.Pricing | F, Sensitive: true),
    ];

    private sealed record UnionView(Modules.ModuleSet Set, IReadOnlyList<string> Areas, IReadOnlyList<PermissionDefinition> All, FrozenDictionary<string, PermissionDefinition> ByKey);

    private static UnionView? _union;

    /// <summary>The built-in catalog with the loaded modules' permissions, rebuilt only when the module set changes.</summary>
    private static UnionView Union()
    {
        var set = Modules.ModuleRegistry.Current;
        if (_union is { } cached && ReferenceEquals(cached.Set, set))
        {
            return cached;
        }

        var all = set.IsEmpty ? BuiltIn : [.. BuiltIn, .. set.Modules.SelectMany(m => m.Permissions)];
        var areas = set.IsEmpty ? BuiltInAreas : [.. BuiltInAreas, .. set.Modules.SelectMany(m => m.PermissionAreas).Where(a => !BuiltInAreas.Contains(a)).Distinct()];
        return _union = new UnionView(set, areas, all, all.ToFrozenDictionary(p => p.Key, StringComparer.Ordinal));
    }

    public static PermissionDefinition? Find(string key) => Union().ByKey.GetValueOrDefault(key);

    public static bool Exists(string key) => Union().ByKey.ContainsKey(key);

    /// <summary>The permission's name, or the key itself for one that no longer exists.</summary>
    public static string NameOf(string key) => Find(key)?.Name ?? key;

    /// <summary>Permissions held by a combination of built-in roles (Admin holds them all; Employee holds none).</summary>
    public static PermissionSet ForBuiltInRoles(OrgRoles roles) =>
        roles.HasFlag(OrgRoles.Admin)
            ? PermissionSet.All
            : PermissionSet.Of(All.Where(p => (p.GrantedTo & roles) != 0).Select(p => p.Key));

    /// <summary>
    /// Pairs that one person shouldn't normally hold together (segregation of duties). Admin › Roles warns about them;
    /// nothing refuses them, since small organizations sometimes have to combine duties.
    /// </summary>
    public static IReadOnlyList<PermissionConflict> Conflicts { get; } =
    [
        new(Permission.VendorsManage, Permission.ApPaymentsManage, "Someone who can create vendors and pay them could pay a vendor they made up."),
        new(Permission.VendorBankDetails, Permission.ApPaymentsManage, "Someone who can change vendor bank details and send payments could redirect payments."),
        new(Permission.ApBillsManage, Permission.ApPaymentsManage, "Entering or approving bills and paying them should be separate."),
        new(Permission.PurchaseOrdersManage, Permission.ApBillsManage, "Ordering goods and approving the vendor's bill should be separate."),
        new("payroll.profiles.manage", "payroll.run", "Someone who can change pay and run payroll could pay themselves more."),
        new("hr.compensation.manage", "payroll.run", "Deciding compensation and running payroll should be separate."),
        new(Permission.BillingInvoicesManage, Permission.BillingPaymentsRecord, "Billing customers and recording their payments should be separate."),
        new(Permission.GlJournalsPost, "cash.manage", "Posting journal entries and reconciling the bank should be separate."),
        new(Permission.LaborCostRatesManage, "payroll.run", "Setting cost rates and running payroll should be separate."),
    ];

    /// <summary>The conflicts present in <paramref name="permissions"/>.</summary>
    public static IReadOnlyList<PermissionConflict> ConflictsIn(IEnumerable<string> permissions)
    {
        var set = permissions as IReadOnlySet<string> ?? permissions.ToHashSet(StringComparer.Ordinal);
        return Conflicts.Where(c => set.Contains(c.First) && set.Contains(c.Second)).ToList();
    }
}

/// <summary>Two permissions one person shouldn't normally hold together, and why.</summary>
public sealed record PermissionConflict(string First, string Second, string Why);

/// <summary>A user's effective permissions: the union of every role they hold.</summary>
public sealed class PermissionSet : IReadOnlyCollection<string>
{
    private readonly FrozenSet<string> _keys;
    private readonly bool _everything;

    private PermissionSet(FrozenSet<string> keys, bool everything = false) => (_keys, _everything) = (keys, everything);

    public static PermissionSet Empty { get; } = new(FrozenSet<string>.Empty);

    /// <summary>
    /// Every permission (built-in Admin, and the background system user), including ones features register at run time
    /// (<see cref="IPermissionRegistry"/>). Enumerating it lists the built-in catalog.
    /// </summary>
    public static PermissionSet All { get; } = new(PermissionCatalog.All.Select(p => p.Key).ToFrozenSet(StringComparer.Ordinal), everything: true);

    public static PermissionSet Of(IEnumerable<string> keys) => new(keys.ToFrozenSet(StringComparer.Ordinal));

    /// <summary>True for <see cref="All"/>: every permission, registered ones included.</summary>
    public bool IsEverything => _everything;

    public int Count => _keys.Count;

    public bool Has(string permission) => _everything || _keys.Contains(permission);

    public bool HasAny(params string[] permissions) => permissions.Any(Has);

    public PermissionSet Union(PermissionSet other) =>
        _everything ? this : other._everything ? other : other.Count == 0 ? this : Count == 0 ? other : Of(_keys.Concat(other._keys));

    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_keys).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// The built-in roles: the ten organization roles that existed before custom roles, with fixed ids so every tenant
/// database has the same ones. They can be viewed and cloned but not changed or deleted. Their permissions come from
/// <see cref="PermissionCatalog"/>, so a new permission reaches them on upgrade without a data change.
/// </summary>
public static class BuiltInRoles
{
    public sealed record Definition(OrgRoles Role, Guid Id, string Name, string Description)
    {
        public PermissionSet Permissions => PermissionCatalog.ForBuiltInRoles(Role);
    }

    public static Definition Admin { get; } = Make(OrgRoles.Admin, "Admin", "Everything, including users, roles and every workspace.");

    public static Definition Employee { get; } = Make(OrgRoles.Employee, "Employee", "Everyone: records their own time, expenses, travel and leave, and sees what's shared with them. Held by every user.");

    /// <summary>Every built-in role in display order, Employee last.</summary>
    public static IReadOnlyList<Definition> All { get; } =
    [
        Admin,
        Make(OrgRoles.Finance, "Finance", "Rates, contracts, budgets, accounting, billing, payables, payroll and project setup."),
        Make(OrgRoles.ProjectManager, "Project manager", "Runs the projects they manage: setup, budgets, staffing and approvals; creates projects."),
        Make(OrgRoles.Timekeeper, "Timekeeper", "Sees everyone's timesheets and the Team time page."),
        Make(OrgRoles.ResourceManager, "Resource manager", "Staffs every project and sees capacity and utilization (no cost rates)."),
        Make(OrgRoles.Hr, "HR", "Personnel records, EEO data, positions, compensation, benefits, onboarding and offboarding."),
        Make(OrgRoles.Materials, "Materials", "Parts, inventory, BOMs, work orders, MRP, quality and shipping."),
        Make(OrgRoles.BusinessDevelopment, "Business development", "The capture pipeline: leads, opportunities, accounts and forecast."),
        Make(OrgRoles.Pricing, "Pricing", "Builds, compares and locks proposal prices."),
        Employee,
    ];

    private static readonly FrozenDictionary<Guid, Definition> ById = All.ToFrozenDictionary(d => d.Id);

    public static Definition For(OrgRoles role) => All.Single(d => d.Role == role);

    public static Guid IdOf(OrgRoles role) => For(role).Id;

    public static Definition? Find(Guid id) => ById.GetValueOrDefault(id);

    public static bool IsBuiltIn(Guid id) => ById.ContainsKey(id);

    /// <summary>The ids of the built-in roles in <paramref name="roles"/> (Employee is implicit and never listed).</summary>
    public static IReadOnlyList<Guid> IdsOf(OrgRoles roles) =>
        All.Where(d => d.Role != OrgRoles.Employee && roles.HasFlag(d.Role)).Select(d => d.Id).ToList();

    /// <summary>The built-in flags among <paramref name="roleIds"/>.</summary>
    public static OrgRoles FlagsOf(IEnumerable<Guid> roleIds) =>
        roleIds.Select(Find).Where(d => d is not null).Aggregate(OrgRoles.Employee, (all, d) => all | d!.Role);

    // Fixed ids: the flag value in the last block, 0x1000 for Employee.
    private static Definition Make(OrgRoles role, string name, string description) =>
        new(role, new Guid($"7b1e0000-0000-4000-8000-{(role == OrgRoles.Employee ? 0x1000 : (int)role):x12}"), name, description);
}
