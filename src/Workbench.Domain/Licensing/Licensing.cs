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

public sealed record FeatureInfo(Feature Feature, string Name, string Description, Edition Needs);

public static class FeatureCatalog
{
    public static IReadOnlyList<FeatureInfo> All { get; } =
    [
        new(Feature.MultiCompany, "More than one company", "Intercompany accounts and consolidated statements for a firm with several legal entities.", Edition.Business),
        new(Feature.SingleSignOn, "Single sign-on", "Sign in with your own identity provider (Entra ID, Okta or any OpenID Connect provider), and SCIM provisioning.", Edition.Business),
        new(Feature.Integrations, "Integrations", "Jira, GitLab, Slack, Teams, Unanet, Costpoint and the other connections to outside systems.", Edition.Business),
        new(Feature.CustomerLinks, "Customer view links", "A read-only link a contracting officer opens without an account.", Edition.Business),
        new(Feature.SupplierPortal, "Supplier portal", "Suppliers quote and confirm orders themselves.", Edition.Business),
        new(Feature.ApprovalWorkflows, "Custom approval workflows", "Multi-step approvals you design yourself. The standard timesheet, leave and expense approvals stay free.", Edition.Business),
        new(Feature.IncurredCost, "Incurred cost submission", "Builds the annual incurred cost submission schedules for DCAA.", Edition.Business),
    ];

    public static FeatureInfo Find(Feature feature) => All.First(f => f.Feature == feature);

    public static bool IsIncluded(Edition edition, Feature feature) => edition >= Find(feature).Needs;
}

/// <summary>The organization's license key, one row per tenant database. Missing means Community.</summary>
public sealed class LicenseSettings : Entity
{
    public string? Key { get; set; }

    public DateTime? ActivatedAtUtc { get; set; }

    public string? ActivatedBy { get; set; }
}
