using Workbench.Contracts.Approvals;
using Workbench.Contracts.Assets;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.BankFiles;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Cash;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Closeout;
using Workbench.Contracts.Compliance;
using Workbench.Contracts.Costpoint;
using Workbench.Contracts.Currencies;
using Workbench.Contracts.Evm;
using Workbench.Contracts.Hr;
using Workbench.Contracts.IncurredCost;
using Workbench.Contracts.IndirectRates;
using Workbench.Contracts.Migration;
using Workbench.Contracts.Modules;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Risks;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Security;
using Workbench.Contracts.TotalTime;
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
            "email-issues", "security-events", "mcp", "company-profile", "accounting-export"),
        .. Segments(ModuleKeys.Billing, "billing", "revenue", "accounting-live"),
        .. Segments(ModuleKeys.Accounting,
            "accounting", "cash", "assets", "currencies", "planning", "indirect-rates", "sales-tax", "purchasing", "cards", "vendor-bank", "bank-files"),
        .. Segments(ModuleKeys.Capture, "crm", "pricing"),
        .. Segments(ModuleKeys.Workforce, "hr", "payroll", "resourcing"),
        .. Segments(ModuleKeys.ProjectControls, "evm", "ceilings", "ceiling-overrides", "rate-ceilings", "closeout", "risks"),
        .. Segments(ModuleKeys.Materials, "materials", "quality", "engineering-changes", "sales-orders", "shipping"),
        .. Segments(ModuleKeys.GovCon, "incurred-cost", "total-time", "compliance", "parallel-run"),
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
        ["admin.compliance"] = ModuleKeys.GovCon,
        ["admin.integrations"] = ModuleKeys.Enterprise,
        ["admin.customization"] = ModuleKeys.Enterprise,
        ["admin.automations"] = ModuleKeys.Enterprise,
        ["approvals."] = ModuleKeys.Core,
        ["approvals.workflows.manage"] = ModuleKeys.Enterprise,
        ["setup."] = ModuleKeys.Core,
        ["setup.currencies"] = ModuleKeys.Accounting,
        ["setup.companies"] = ModuleKeys.Enterprise,
        ["time."] = ModuleKeys.Core,
        ["time.total_time"] = ModuleKeys.GovCon,
        ["labor."] = ModuleKeys.Core,
        ["leave."] = ModuleKeys.Core,
        ["projects."] = ModuleKeys.Core,
        ["projects.resourcing."] = ModuleKeys.Workforce,
        ["projects.evm."] = ModuleKeys.ProjectControls,
        ["projects.ceilings."] = ModuleKeys.ProjectControls,
        ["gl."] = ModuleKeys.Accounting,
        ["planning."] = ModuleKeys.Accounting,
        ["revenue."] = ModuleKeys.Billing,
        ["compliance."] = ModuleKeys.GovCon,
        ["compliance.indirect_rates"] = ModuleKeys.Accounting,
        ["tax."] = ModuleKeys.Accounting,
        ["assets."] = ModuleKeys.Accounting,
        ["billing."] = ModuleKeys.Billing,
        ["ap."] = ModuleKeys.Accounting,
        ["purchasing."] = ModuleKeys.Accounting,
        ["cash."] = ModuleKeys.Accounting,
        ["payroll."] = ModuleKeys.Workforce,
        ["travel."] = ModuleKeys.Core,
        ["cards."] = ModuleKeys.Accounting,
        ["hr."] = ModuleKeys.Workforce,
        ["hr.workforce."] = ModuleKeys.Core,
        ["hr.reports."] = ModuleKeys.Core,
        ["bd."] = ModuleKeys.Capture,
        ["pricing."] = ModuleKeys.Capture,
        ["materials."] = ModuleKeys.Materials,
        ["quality."] = ModuleKeys.Materials,
        ["sales."] = ModuleKeys.Materials,
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
        ["growth"] = ModuleKeys.Capture,
        ["sheets"] = ModuleKeys.Core,
        ["materials"] = ModuleKeys.Materials,
        ["reports"] = ModuleKeys.Core,
        ["admin"] = ModuleKeys.Core,
        // "finance" has no default: every group, or page, there must say which module it is.
    };

    /// <summary>Groups within a section, as "section/group". Wins over the section.</summary>
    public static IReadOnlyDictionary<string, string> NavGroups { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["people/HR administration"] = ModuleKeys.Workforce,
        ["finance/Accounting"] = ModuleKeys.Accounting,
        ["finance/Billing & AR"] = ModuleKeys.Billing,
        ["finance/Payables & purchasing"] = ModuleKeys.Accounting,
        ["finance/Cash"] = ModuleKeys.Accounting,
        ["finance/Assets"] = ModuleKeys.Accounting,
        ["finance/Payroll"] = ModuleKeys.Workforce,
        ["finance/Planning"] = ModuleKeys.Accounting,
        ["admin/Customization"] = ModuleKeys.Enterprise,
        ["admin/Compliance"] = ModuleKeys.GovCon,
    };

    /// <summary>Single pages (by href) that differ from their group or section. Wins over both.</summary>
    public static IReadOnlyDictionary<string, string> NavPages { get; } = Ignoring(
    [
        .. Segments(ModuleKeys.Accounting,
            "purchasing/requisitions", "purchasing/receiving", "purchasing/approvals", "planning/my-budget", "planning/submissions",
            "accounting/indirect-rates", "accounting/reports", "purchasing/reports", "assets/reports", "setup/currencies", "setup/card-programs", "setup/sales-tax"),
        .. Segments(ModuleKeys.Billing,
            "billing/invoices", "billing/statements", "accounting/revenue", "accounting/revenue/estimates", "accounting/revenue/setup",
            "accounting/revenue/projects", "accounting/revenue/reports"),
        .. Segments(ModuleKeys.Workforce, "resourcing", "hr/benefits/me", "payroll"),
        .. Segments(ModuleKeys.Capture, "pricing"),
        .. Segments(ModuleKeys.GovCon, "accounting/incurred-cost", "accounting/total-time", "reports/standard/funding-status", "setup/migration/parallel-run"),
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
        ["Billing"] = ModuleKeys.Billing, ["Revenue"] = ModuleKeys.Billing, ["AccountingLive"] = ModuleKeys.Billing,
        // accounting
        ["Accounting"] = ModuleKeys.Accounting, ["Assets"] = ModuleKeys.Accounting, ["BankFiles"] = ModuleKeys.Accounting, ["Cash"] = ModuleKeys.Accounting,
        ["Currencies"] = ModuleKeys.Accounting, ["IndirectRates"] = ModuleKeys.Accounting, ["Planning"] = ModuleKeys.Accounting,
        ["Purchasing"] = ModuleKeys.Accounting, ["SalesTax"] = ModuleKeys.Accounting, ["Travel"] = ModuleKeys.Core,
        // capture
        ["Crm"] = ModuleKeys.Capture, ["Pricing"] = ModuleKeys.Capture,
        // workforce
        ["Hr"] = ModuleKeys.Workforce, ["Payroll"] = ModuleKeys.Workforce, ["Resourcing"] = ModuleKeys.Workforce,
        // project controls
        ["Ceilings"] = ModuleKeys.ProjectControls, ["Closeout"] = ModuleKeys.ProjectControls, ["Evm"] = ModuleKeys.ProjectControls, ["Risks"] = ModuleKeys.ProjectControls,
        // materials
        ["Materials"] = ModuleKeys.Materials, ["Quality"] = ModuleKeys.Materials,
        // govcon
        ["Compliance"] = ModuleKeys.GovCon, ["IncurredCost"] = ModuleKeys.GovCon, ["TotalTime"] = ModuleKeys.GovCon,
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
        [typeof(IProjectNarrativeService)] = ModuleKeys.ProjectControls,
        [typeof(IScheduleHealthService)] = ModuleKeys.ProjectControls,
        [typeof(IBurdenedCostService)] = ModuleKeys.Accounting,
        [typeof(ICustomerShareLinkService)] = ModuleKeys.Enterprise,
        [typeof(ICustomerViewService)] = ModuleKeys.Enterprise,
        [typeof(IProgressPaymentService)] = ModuleKeys.GovCon,
        [typeof(IPerformanceBasedPaymentService)] = ModuleKeys.GovCon,
        [typeof(IGovernmentBillingService)] = ModuleKeys.GovCon,
        [typeof(IHrReportService)] = ModuleKeys.Core,
        [typeof(IHrAccessService)] = ModuleKeys.Core,
        [typeof(IParallelRunService)] = ModuleKeys.GovCon,
        [typeof(IAccessReviewService)] = ModuleKeys.Enterprise,
        [typeof(IRetentionService)] = ModuleKeys.Enterprise,
        [typeof(IEdiService)] = ModuleKeys.Enterprise,
        [typeof(ICardProgramService)] = ModuleKeys.Accounting,
        [typeof(ICardChargeService)] = ModuleKeys.Accounting,
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
