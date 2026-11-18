using Workbench.Contracts.Approvals;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Compliance;
using Workbench.Contracts.Costpoint;
using Workbench.Contracts.Currencies;
using Workbench.Contracts.IndirectRates;
using Workbench.Contracts.Migration;
using Workbench.Contracts.Modules;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Security;
using Workbench.Contracts.Travel;

namespace Workbench.Contracts.Licensing;

/// <summary>The ids of the license modules. Mirrors <c>ModuleIds</c> in Workbench.Domain (which this project can't see); a test keeps them equal.</summary>
public static class ModuleKeys
{
    public const string Core = "core";
    public const string Billing = "billing";
    public const string Accounting = "accounting";
    public const string Capture = "capture";
    public const string Workforce = "workforce";
    public const string ProjectControls = "projectcontrols";
    public const string Materials = "materials";
    public const string GovCon = "govcon";
    public const string Enterprise = "enterprise";
    public const string Ai = "ai";
}

/// <summary>
/// <b>The one table that says which license module everything belongs to.</b> API paths, menu entries, permissions and service
/// interfaces each belong to exactly one module (<c>core</c> included), so a feature can't be added without a licensing decision:
/// tests fail for any API path, menu entry, permission or service interface that appears here nowhere.
/// <list type="bullet">
/// <item>The menu and the page guard read <see cref="ForNav"/>.</item>
/// <item>The service gates (<c>EditionGates</c>) wrap every service interface in <see cref="ForService"/> whose module isn't core, for changes only.</item>
/// <item>Module seats read <see cref="ForPermission"/>: a person uses a seat of a module when their roles grant any permission in it.</item>
/// <item>API paths are listed so a new top-level path can't be added unlicensed; the services behind them are what is gated.</item>
/// </list>
/// Whether a module is <i>enforced</i> is not decided here; that is <c>ModuleCatalog</c> in Workbench.Domain. Moving something from
/// one module to another here is a pricing decision.
/// </summary>
public static class ModuleMap
{
    // ───────────────────────────── API paths (first segment after /api/v1/) ─────────────────────────────

    private static Dictionary<string, string> Ignoring(IEnumerable<KeyValuePair<string, string>> pairs) =>
        new(pairs, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<KeyValuePair<string, string>> Segments(string module, params string[] segments) =>
        segments.Select(s => KeyValuePair.Create(s, module));

    /// <summary>The module of each first path segment of the REST API.</summary>
    public static IReadOnlyDictionary<string, string> ApiSegments { get; } = Ignoring(
    [
        .. Segments(ModuleKeys.Core,
            "sheets", "workspaces", "dashboards", "my-work", "my-tasks", "my-todo", "plan-template", "templates", "views", "forms", "home",
            "projects", "project-hub", "project-start", "project-plans", "contracts", "wbs", "assignments", "budgets", "clins", "modifications",
            "time", "expense-reports", "odc-approvals", "odcs", "leave", "per-diem", "travel", "receipts", "timesheet-flags",
            "reports", "setup", "customers", "org-units", "labor-categories", "approvals", "approval-delegations", "fiscal", "audit", "employees",
            "admin", "host", "users", "invites", "workspaces-admin", "imports", "migration", "reconciliation", "assistant", "me", "support", "help", "license",
            "email-issues", "security-events", "mcp", "company-profile", "accounting-export",
            "resourcing", // Staffing: allocations, assignments, capacity, utilization and skills (it stays core: the Plan, timesheets and schedule health read it).
            // The ledger, vendors, purchase orders and payables, currencies and indirect rates are free: too many core features and other modules read their records.
            "accounting", "currencies", "indirect-rates", "purchasing", "cards"),
        // Invoices, payments and receivables are free (the ledger records too many core features and modules read). "billing" is core.
        .. Segments(ModuleKeys.Core, "billing"),
        // billing: the revenue and accounting-live segments live in the billing module (its own rows).
        // accounting: the cash, assets, planning, sales-tax, bank-files and vendor-bank segments live in the accounting module (its own rows).
        // workforce: the hr and payroll segments live in the workforce module (its own rows); resourcing is core, above.
        // project controls: the evm, ceilings, ceiling-overrides, rate-ceilings, closeout and risks segments live in the project controls module (its own rows).
        .. Segments(ModuleKeys.Enterprise,
            "companies", "integrations", "customization", "automations", "approval-workflows", "access-reviews", "retention", "edi", "costpoint"),
    ]);

    /// <summary>The module of an API path segment: the built-in table, else the loaded plugins' rows (<see cref="ModuleRegistry"/>).</summary>
    public static string? ForApiSegment(string segment) => ApiSegments.GetValueOrDefault(segment) ?? ModuleRegistry.Current.ForApiSegment(segment);

    // ───────────────────────────── Permissions ─────────────────────────────

    /// <summary>
    /// Permission key prefixes (built-in and registered), longest match wins. A permission whose key matches none belongs to no
    /// module, which the coverage test refuses for every built-in permission. A feature-registered permission with no rule counts as core.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PermissionPrefixes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["admin."] = ModuleKeys.Core,
        ["admin.retention"] = ModuleKeys.Enterprise,
        ["admin.integrations"] = ModuleKeys.Enterprise,
        ["admin.customization"] = ModuleKeys.Enterprise,
        ["admin.automations"] = ModuleKeys.Enterprise,
        ["approvals."] = ModuleKeys.Core,
        ["approvals.workflows.manage"] = ModuleKeys.Enterprise,
        ["setup."] = ModuleKeys.Core,
        ["setup.currencies"] = ModuleKeys.Core,
        ["setup.companies"] = ModuleKeys.Enterprise,
        ["time."] = ModuleKeys.Core,
        ["labor."] = ModuleKeys.Core,
        ["leave."] = ModuleKeys.Core,
        ["projects."] = ModuleKeys.Core,
        ["gl."] = ModuleKeys.Core,
        // planning.*, tax.*, assets.* and cash.* are the accounting module's own rows.
        // revenue.* is the billing module's own row.
        ["compliance.indirect_rates"] = ModuleKeys.Core,
        ["billing."] = ModuleKeys.Core,
        ["ap."] = ModuleKeys.Core,
        ["purchasing."] = ModuleKeys.Core,
        // payroll.* and hr.* (but the two below) are the workforce module's own rows.
        ["travel."] = ModuleKeys.Core,
        ["cards."] = ModuleKeys.Core,
        ["hr.workforce."] = ModuleKeys.Core,
        ["hr.reports."] = ModuleKeys.Core,
        ["sheets."] = ModuleKeys.Core,
        ["fields."] = ModuleKeys.Core,
    };

    /// <summary>The module <paramref name="permissionKey"/> belongs to, or null when no rule covers it.</summary>
    public static string? ForPermission(string permissionKey)
    {
        string? best = null;
        var bestLength = -1;
        foreach (var (prefix, module) in PermissionPrefixes)
        {
            if (prefix.Length > bestLength && permissionKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                (best, bestLength) = (module, prefix.Length);
            }
        }

        var (plugin, pluginLength) = ModuleRegistry.Current.ForPermission(permissionKey);
        return pluginLength > bestLength ? plugin : best;
    }

    // ───────────────────────────── Menu ─────────────────────────────

    /// <summary>Sections whose every entry belongs to one module (an entry or a group below can still say otherwise).</summary>
    public static IReadOnlyDictionary<string, string> NavSections { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["home"] = ModuleKeys.Core,
        ["my-work"] = ModuleKeys.Core,
        ["approvals"] = ModuleKeys.Core,
        ["projects"] = ModuleKeys.Core,
        ["people"] = ModuleKeys.Core,
        // "growth" (Business development): the capture module adds the rows (the section itself stays in the menu and is hidden while it is empty).
        ["sheets"] = ModuleKeys.Core,
        // "materials": the materials module adds the rows (the section itself stays in the menu and is hidden while it is empty).
        ["reports"] = ModuleKeys.Core,
        ["admin"] = ModuleKeys.Core,
        // "finance" has no default: every group, or page, there must say which module it is.
    };

    /// <summary>Groups within a section, as "section/group". Wins over the section.</summary>
    public static IReadOnlyDictionary<string, string> NavGroups { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["finance/Accounting"] = ModuleKeys.Core,
        ["finance/Billing & AR"] = ModuleKeys.Core,
        ["finance/Payables & purchasing"] = ModuleKeys.Core,
        // finance/Cash, finance/Assets and finance/Planning are the accounting module's (its own rows).
        ["admin/Customization"] = ModuleKeys.Enterprise,
    };

    /// <summary>Single pages (by href) that differ from their group or section. Wins over both.</summary>
    public static IReadOnlyDictionary<string, string> NavPages { get; } = Ignoring(
    [
        .. Segments(ModuleKeys.Core,
            "purchasing/requisitions", "purchasing/receiving", "purchasing/approvals", "cards",
            "accounting/indirect-rates", "accounting/reports", "purchasing/reports", "setup/currencies", "setup/card-programs"),
        // accounting: planning/my-budget, planning/submissions, accounting/recurring, accounting/sales-tax, assets/reports and setup/sales-tax are the accounting module's rows.
        .. Segments(ModuleKeys.Core, "billing/invoices", "billing/statements"),
        // billing: accounting/revenue, accounting/revenue/estimates, /setup, /projects and /reports are the billing module's rows.
        .. Segments(ModuleKeys.GovCon, "reports/standard/funding-status"),
        .. Segments(ModuleKeys.Enterprise, "setup/companies", "accounting/consolidated", "admin/integrations", "admin/integrations/edi", "setup/approval-workflows"),
    ]);

    /// <summary>
    /// The module of a menu entry: its page, else its group, else its section. Null when none says (the coverage test fails for it).
    /// </summary>
    public static string? ForNav(string section, string? group, string href)
    {
        var plugins = ModuleRegistry.Current;
        if (NavPages.TryGetValue(href, out var page) || (page = plugins.ForNavPage(href)) is not null)
        {
            return page;
        }

        if (group is not null && (NavGroups.TryGetValue($"{section}/{group}", out var grouped) || (grouped = plugins.ForNavGroup($"{section}/{group}")) is not null))
        {
            return grouped;
        }

        return NavSections.GetValueOrDefault(section) ?? plugins.ForNavSection(section);
    }

    // ───────────────────────────── Services ─────────────────────────────

    /// <summary>The module of every service interface in a Contracts namespace (without the <c>Workbench.Contracts.</c> prefix), unless <see cref="ServiceTypes"/> says otherwise.</summary>
    public static IReadOnlyDictionary<string, string> ServiceNamespaces { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // core
        ["Approvals"] = ModuleKeys.Core, ["AccountingExport"] = ModuleKeys.Core, ["Company"] = ModuleKeys.Core, ["Assistant"] = ModuleKeys.Core, ["Calendar"] = ModuleKeys.Core, ["Collaboration"] = ModuleKeys.Core,
        ["Contracts"] = ModuleKeys.Core, ["Dashboards"] = ModuleKeys.Core, ["Financials"] = ModuleKeys.Core, ["Forms"] = ModuleKeys.Core,
        ["Identity"] = ModuleKeys.Core, ["Imports"] = ModuleKeys.Core, ["Leave"] = ModuleKeys.Core, ["Licensing"] = ModuleKeys.Core,
        ["Migration"] = ModuleKeys.Core, ["MyTodo"] = ModuleKeys.Core, ["MyWork"] = ModuleKeys.Core, ["Notifications"] = ModuleKeys.Core,
        ["Odcs"] = ModuleKeys.Core, ["PerDiem"] = ModuleKeys.Core, ["ProjectAccounting"] = ModuleKeys.Core, ["Projects"] = ModuleKeys.Core,
        ["Receipts"] = ModuleKeys.Core, ["Reports"] = ModuleKeys.Core, ["Scheduling"] = ModuleKeys.Core, ["Security"] = ModuleKeys.Core,
        ["Sheets"] = ModuleKeys.Core, ["Support"] = ModuleKeys.Core, ["Tasks"] = ModuleKeys.Core, ["Tenancy"] = ModuleKeys.Core, ["Time"] = ModuleKeys.Core,
        ["Automation"] = ModuleKeys.Core, // sheet rules
        // billing
        // billing: invoices, payments and receivables (the Billing namespace) are core, and free. The Revenue and AccountingLive namespaces live in the billing module (its own rows).
        ["Billing"] = ModuleKeys.Core,
        // accounting
        // accounting: the Assets, BankFiles, Cash, Planning and SalesTax namespaces live in the accounting module (its own rows).
        // The ledger, currencies, indirect rates and purchasing (vendors, purchase orders, payables) are core, and free.
        ["Accounting"] = ModuleKeys.Core, ["Currencies"] = ModuleKeys.Core, ["IndirectRates"] = ModuleKeys.Core,
        ["Purchasing"] = ModuleKeys.Core, ["Travel"] = ModuleKeys.Core,
        // capture: the Crm and Pricing namespaces live in the capture module (its own rows).
        // workforce
        ["Resourcing"] = ModuleKeys.Core, // staffing stays core
        // workforce: the Hr and Payroll namespaces live in the workforce module (its own rows).
        // project controls: the Ceilings, Closeout, Evm and Risks namespaces, and the monthly narrative, live in the project controls module (its own rows).
        // materials: the Materials and Quality namespaces live in the materials module (its own rows).
        // govcon: the Compliance, IncurredCost and TotalTime namespaces and the parallel run live in the government contracting module (its own rows).
        // enterprise
        ["Automations"] = ModuleKeys.Enterprise, ["Companies"] = ModuleKeys.Enterprise, ["Customization"] = ModuleKeys.Enterprise,
        ["Integrations"] = ModuleKeys.Enterprise, ["Integrations.GitLab"] = ModuleKeys.Enterprise, ["Integrations.Jira"] = ModuleKeys.Enterprise,
        ["Costpoint"] = ModuleKeys.Enterprise,
    };

    /// <summary>Service interfaces whose module differs from their namespace's. Wins over <see cref="ServiceNamespaces"/>.</summary>
    public static IReadOnlyDictionary<Type, string> ServiceTypes { get; } = new Dictionary<Type, string>
    {
        // Sign-in settings (MFA, sessions) stay core; the single sign-on provider save is a Feature gate on enterprise.
        [typeof(IApprovalWorkflowService)] = ModuleKeys.Enterprise, // Custom workflows; the standard approvals don't change them.
        [typeof(IBurdenedCostService)] = ModuleKeys.Core,
        [typeof(ICustomerShareLinkService)] = ModuleKeys.Enterprise,
        [typeof(ICustomerViewService)] = ModuleKeys.Enterprise,
        [typeof(IAccessReviewService)] = ModuleKeys.Enterprise,
        [typeof(IRetentionService)] = ModuleKeys.Enterprise,
        [typeof(IEdiService)] = ModuleKeys.Enterprise,
        [typeof(ICardProgramService)] = ModuleKeys.Core,
        [typeof(ICardChargeService)] = ModuleKeys.Core,
    };

    /// <summary>The module <paramref name="service"/> (a Contracts service interface) belongs to, or null when nothing says.</summary>
    public static string? ForService(Type service)
    {
        if (ServiceTypes.TryGetValue(service, out var module) || (module = ModuleRegistry.Current.ForService(service)) is not null)
        {
            return module;
        }

        const string Prefix = "Workbench.Contracts.";
        var ns = service.Namespace ?? "";
        return ns.StartsWith(Prefix, StringComparison.Ordinal) ? ServiceNamespaces.GetValueOrDefault(ns[Prefix.Length..]) : null;
    }

    /// <summary>The service interfaces of the contracts project (public interfaces named <c>I…Service</c>), without any module's.</summary>
    public static IReadOnlyList<Type> BuiltInServiceInterfaces() => ModuleContribution.ServiceInterfacesIn(typeof(ModuleMap).Assembly);

    /// <summary>Every service interface: the contracts project's, then the loaded modules' (<see cref="ModuleContribution.ServiceInterfaces"/>). What the service gates and the coverage test walk.</summary>
    public static IReadOnlyList<Type> ServiceInterfaces() =>
        [.. BuiltInServiceInterfaces(), .. ModuleRegistry.Current.ServiceInterfaces.OrderBy(t => t.FullName, StringComparer.Ordinal)];

    /// <summary>The asynchronous methods of a service interface, including those it inherits (ordinary methods only: no property accessors).</summary>
    public static IReadOnlyList<System.Reflection.MethodInfo> AsyncMethods(Type service) =>
        service.GetMethods().Concat(service.GetInterfaces().SelectMany(i => i.GetMethods()))
            .Where(m => !m.IsSpecialName && (m.ReturnType == typeof(Task) || (m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))))
            .ToList();

    // ───────────────────────────── What counts as a change ─────────────────────────────

    private static readonly string[] ReadVerbs =
    [
        "Get", "List", "Find", "Search", "Preview", "Export", "Check", "Is", "Can", "Has", "Lookup", "Validate", "Explain", "Dry", "Reveal",
        "Download", "Calculate", "Compute", "Summarize", "Suggest", "Count", "Read", "Load", "Granted", "Test", "Resolve", "Describe", "Show", "Query",
    ];

    /// <summary>
    /// True when a service method changes something: anything whose name doesn't start with a reading verb (<c>Get</c>, <c>List</c>,
    /// <c>Export</c>, <c>Preview</c>…). Reads are never gated, so a lapsed license can't lock anyone out of their own records.
    /// </summary>
    public static bool IsChange(string methodName) =>
        !ReadVerbs.Any(v => methodName.StartsWith(v, StringComparison.Ordinal) && (methodName.Length == v.Length || char.IsUpper(methodName[v.Length])));
}
