namespace Workbench.Domain.Licensing;

/// <summary>What an organization has paid for. Community is free to self-host; Business unlocks <see cref="Feature"/>s that larger firms need.</summary>
public enum Edition
{
    Community = 0,
    Business = 1,
}

/// <summary>
/// A capability that needs the Business edition. Gates apply to changing things (connecting, creating, saving), never to
/// reading data that is already there, so a lapsed license never locks anyone out of their own records.
/// </summary>
public enum Feature
{
    MultiCompany,
    SingleSignOn,
    Integrations,
    CustomerLinks,
    SupplierPortal,
    ApprovalWorkflows,
    IncurredCost,
}

/// <summary>
/// The ids of the modules a license can include. <see cref="Core"/> is free forever and always on. The same ids are repeated as
/// <c>ModuleKeys</c> in Workbench.Contracts (the contracts project can't see this one); a test keeps the two in step.
/// </summary>
public static class ModuleIds
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

    /// <summary>Reserved for a future hosted-credits product. Not enforced and not built: the assistant is free when the organization brings its own API key.</summary>
    public const string Ai = "ai";

    /// <summary>
    /// Ids that earlier keys (WB2 payloads issued before the module catalog was split) may still carry, and what each one now means.
    /// Applied when a payload is parsed, so keys already issued, and the license server's output, keep working.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Legacy { get; } = new Dictionary<string, string[]>
    {
        ["finance"] = [Billing, Accounting],
        ["controls"] = [Enterprise],
    };
}

/// <param name="Enforced">
/// False: everyone has the module, whatever their license says, so nothing changes for anyone. True: only licenses that list
/// the module have it. Flipping a flag is the whole of a pricing change.
/// </param>
/// <param name="WholeModule">
/// Only read when <paramref name="Enforced"/> is true. True (the default): the module's pages leave the menu and every change to
/// its services is refused without the module. False: only the named <see cref="Feature"/>s that belong to the module need a key
/// that lists it, as before the module map existed; its other pages and services stay open. <c>govcon</c> and <c>enterprise</c>
/// are enforced this way today, which keeps everything that was free before the split free. Setting <c>WholeModule</c> true on
/// them is a pricing decision that has not been made.
/// </param>
public sealed record ModuleInfo(string Id, string Name, string Description, bool Enforced, bool WholeModule = true);

/// <summary>
/// What a license can include. <b>The <see cref="ModuleInfo.Enforced"/> flags below are the only place pricing is tuned.</b>
/// Only <c>govcon</c> and <c>enterprise</c> are enforced today, and only for the <see cref="Feature"/>s listed below; every
/// other module ships free until the price list is decided. Which page, API path, permission and service belongs to which
/// module is the one table in <c>ModuleMap</c> (Workbench.Contracts).
/// </summary>
public static class ModuleCatalog
{
    public static IReadOnlyList<ModuleInfo> Default { get; } =
    [
        new(ModuleIds.Core, "Core", "Free forever: time, timesheets, expenses, leave, customers, contracts, projects, the Plan, sheets, dashboards, search, standard reports, users, roles, MFA, the audit log and importers; invoicing, ready to bill, customer payments, AR aging, unbilled accrual, milestones, retainers and statements; the general ledger, period close, payables, purchasing, payment runs, multi-currency and indirect rates; accounting exports as files; custom fields and layouts, the automation builder, the integration outbox with webhooks, API keys and connectors, audit retention and access reviews. Always included.", false),
        new(ModuleIds.Billing, "Billing", "Revenue recognition (setup by method, estimates at completion, the monthly revenue run and the revenue reports) and live push to QuickBooks Online and Xero.", false),
        new(ModuleIds.Accounting, "Accounting", "Cash and bank reconciliation, bank payment files, fixed assets with tax books and depreciation, corporate budgets with planning cycles and recurring journals, and sales and use tax.", false),
        new(ModuleIds.Capture, "Capture", "The business development pipeline: leads, opportunities, accounts, forecast, gates, proposals and pricing.", false),
        new(ModuleIds.Workforce, "Workforce", "HR records, positions, compensation, benefits, onboarding, payroll and the Paylocity file exchange.", false),
        new(ModuleIds.ProjectControls, "Project controls", "Earned value, the risk register, ceilings and closeout, schedule health and the monthly narrative.", false),
        new(ModuleIds.Materials, "Materials", "Parts, inventory, bills of materials, work orders, MRP, quality, engineering changes, sales orders and shipping.", false),
        new(ModuleIds.GovCon, "Government contracting", "DCAA readiness, CPSR, CAS disclosure, CMMC, incurred cost, CLIN funding reports, WAWF, total time accounting and the Costpoint parallel run.", true, WholeModule: false),
        new(ModuleIds.Enterprise, "Enterprise", "The add-on code that loads from the enterprise module: Jira and GitLab sync of project plans, the Costpoint sync (the read-only mirror and plan versus actual) and customer view links. A key that lists enterprise also unlocks four gates that stay in the product: a second company, single sign-on, adding connectors and Slack or Teams connections, and custom approval workflows. Custom fields and layouts, the automation builder, webhooks, API keys, audit retention and access reviews are in the product, and free.", true, WholeModule: false),
        new(ModuleIds.Ai, "AI credits", "Reserved for hosted AI credits. The assistant is free when your organization supplies its own API key.", false),
    ];

    public static IReadOnlyList<string> AllIds { get; } = Default.Select(m => m.Id).ToList();

    public static bool IsKnown(string id) => Default.Any(m => m.Id == id);
}

public sealed record FeatureInfo(Feature Feature, string Name, string Description, Edition Needs, string Module);

public static class FeatureCatalog
{
    public static IReadOnlyList<FeatureInfo> All { get; } =
    [
        new(Feature.MultiCompany, "More than one company", "Intercompany accounts and consolidated statements for a firm with several legal entities.", Edition.Business, ModuleIds.Enterprise),
        new(Feature.SingleSignOn, "Single sign-on", "Sign in with your own identity provider (Entra ID, Okta or any OpenID Connect provider), and SCIM provisioning.", Edition.Business, ModuleIds.Enterprise),
        new(Feature.Integrations, "Integrations", "Adding connectors and Slack or Teams connections (in the product), and the Jira and GitLab sync (in the enterprise module).", Edition.Business, ModuleIds.Enterprise),
        new(Feature.CustomerLinks, "Customer view links", "A read-only link a contracting officer opens without an account (in the enterprise module).", Edition.Business, ModuleIds.Enterprise),
        new(Feature.SupplierPortal, "Supplier portal", "Suppliers quote and confirm orders themselves.", Edition.Business, ModuleIds.Core),
        new(Feature.ApprovalWorkflows, "Custom approval workflows", "Multi-step approvals you design yourself. The standard timesheet, leave and expense approvals stay free.", Edition.Business, ModuleIds.Enterprise),
        new(Feature.IncurredCost, "Incurred cost submission", "Builds the annual incurred cost submission schedules for DCAA.", Edition.Business, ModuleIds.GovCon),
    ];

    public static FeatureInfo Find(Feature feature) => All.First(f => f.Feature == feature);

    public static bool IsIncluded(Edition edition, Feature feature) => edition >= Find(feature).Needs;

    /// <summary>The module <paramref name="feature"/> belongs to.</summary>
    public static string ModuleOf(Feature feature) => Find(feature).Module;
}

/// <summary>The organization's license key, one row per tenant database. Missing means Community.</summary>
public sealed class LicenseSettings : Entity
{
    public string? Key { get; set; }

    public DateTime? ActivatedAtUtc { get; set; }

    public string? ActivatedBy { get; set; }

    /// <summary>A random id for this install, made once and sent to the license server (connected installs only). Not derived from anything about the machine.</summary>
    public string? InstallId { get; set; }

    /// <summary>The license the license server last activated this install for; a different key means activating again.</summary>
    public Guid? ServerLicenseId { get; set; }

    public DateTime? LastRefreshAttemptUtc { get; set; }

    public DateTime? LastRefreshSuccessUtc { get; set; }

    /// <summary>One of refreshed, refused, offline or failed.</summary>
    public string? LastRefreshOutcome { get; set; }

    /// <summary>What the last attempt said, for Setup &gt; License: the server's own reason when it refused.</summary>
    public string? LastRefreshMessage { get; set; }

    /// <summary>Attempts in a row that could not reach the server or got no usable answer. Drives the backoff.</summary>
    public int ConsecutiveRefreshFailures { get; set; }
}
