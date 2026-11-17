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

/// <summary>The ids of the modules a license can include. <see cref="Core"/> is always on.</summary>
public static class ModuleIds
{
    public const string Core = "core";
    public const string Finance = "finance";
    public const string Capture = "capture";
    public const string Workforce = "workforce";
    public const string Controls = "controls";
    public const string GovCon = "govcon";
    public const string Enterprise = "enterprise";
}

/// <param name="Enforced">
/// False: everyone has the module, whatever their license says, so nothing changes for anyone. True: only licenses that list
/// the module have it. Flipping a flag is the whole of a pricing change.
/// </param>
public sealed record ModuleInfo(string Id, string Name, string Description, bool Enforced);

/// <summary>
/// What a license can include. <b>The <see cref="ModuleInfo.Enforced"/> flags below are the only place pricing is tuned.</b>
/// Only the modules whose features are gated today are enforced; the rest ship free until the price list is decided.
/// </summary>
public static class ModuleCatalog
{
    public static IReadOnlyList<ModuleInfo> Default { get; } =
    [
        new(ModuleIds.Core, "Core", "Time, expenses, leave, projects, the Plan, sheets and dashboards. Always included.", false),
        new(ModuleIds.Finance, "Finance", "Billing, the general ledger, payables, purchasing and the supplier portal.", false),
        new(ModuleIds.Capture, "Capture", "The business development pipeline: leads, opportunities, accounts, forecast and proposal pricing.", false),
        new(ModuleIds.Workforce, "Workforce", "HR records, positions, benefits, onboarding, resourcing and payroll.", false),
        new(ModuleIds.Controls, "Controls", "Audit log, access reviews, retention, legal holds and compliance evidence.", false),
        new(ModuleIds.GovCon, "Government contracting", "Incurred cost submissions, indirect rates and the other DCAA/CAS work.", true),
        new(ModuleIds.Enterprise, "Enterprise", "More than one company, single sign-on, integrations, customer links and custom approval workflows.", true),
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
        new(Feature.Integrations, "Integrations", "Jira, GitLab, Slack, Teams, Unanet, Costpoint and the other connections to outside systems.", Edition.Business, ModuleIds.Enterprise),
        new(Feature.CustomerLinks, "Customer view links", "A read-only link a contracting officer opens without an account.", Edition.Business, ModuleIds.Enterprise),
        new(Feature.SupplierPortal, "Supplier portal", "Suppliers quote and confirm orders themselves.", Edition.Business, ModuleIds.Finance),
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
}
