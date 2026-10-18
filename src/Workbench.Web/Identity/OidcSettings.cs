namespace Workbench.Web.Identity;

/// <summary>
/// Config section <c>Authentication:Oidc</c>. Works with Entra ID (Authority
/// <c>https://login.microsoftonline.com/{tenant}/v2.0</c>) and any standards-compliant OpenID Connect provider.
/// </summary>
public sealed class OidcSettings
{
    public const string SectionName = "Authentication:Oidc";

    /// <summary>The issuer URL. Leave it empty in Development to use the dev sign-in page instead.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    /// <summary>Confidential web client secret. Prefer user secrets or environment variables over appsettings.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// The audience of bearer tokens the desktop app sends to <c>/api</c> and <c>/hubs</c>. Defaults to
    /// <see cref="ClientId"/>. For Entra ID this is usually the API's application ID URI.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>Extra scopes for the web sign-in, on top of <c>openid profile email</c>.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>Set false only for a development identity provider served over plain HTTP.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority);
}
