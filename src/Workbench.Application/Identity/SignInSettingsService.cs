using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Security;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;

namespace Workbench.Application.Identity;

public sealed partial class SignInSettingsService(
    IWorkbenchDbContextFactory dbFactory,
    AccessControl access,
    ReauthenticationGuard reauthentication,
    SignInSettingsStore store,
    SsoSecretProtector secrets,
    SecurityEventLog securityLog,
    ISignInContext signIn,
    SignInServerInfo server,
    TimeProvider clock,
    Tenancy.TenancyOptions? tenancy = null) : ISignInSettingsService
{
    private const int MaxGroupMappings = 200;

    public async Task<SignInSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await ToDtoAsync(db, cancellationToken);
    }

    public async Task<SignInSettingsDto> SaveProviderAsync(SaveSsoProviderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAdminRecentAsync(cancellationToken);

        var authority = Clean(request.Authority, 512, "The issuer URL");
        var clientId = Clean(request.ClientId, 200, "The client id");
        var displayName = Clean(request.DisplayName, 100, "The button label");
        var scopes = Clean(request.Scopes, 500, "Scopes");
        if (authority is not null)
        {
            ValidateAuthority(authority);
        }

        if (scopes is not null && SignInRules.SplitList(scopes).Any(s => !ScopePattern().IsMatch(s)))
        {
            throw new ArgumentException("Scopes are words like offline_access or api://workbench/user, separated by spaces.", nameof(request));
        }

        var newSecret = string.IsNullOrWhiteSpace(request.NewClientSecret) ? null : request.NewClientSecret.Trim();
        if (newSecret is { Length: > 1000 })
        {
            throw new ArgumentException("The client secret is too long.", nameof(request));
        }

        if (request.Enabled && (authority is null || clientId is null))
        {
            throw new ArgumentException("Enter the issuer URL and the client id before turning single sign-on on.", nameof(request));
        }

        if (request.RequireOrganizationIdp && !request.Enabled)
        {
            throw new ArgumentException("Turn single sign-on on before requiring it.", nameof(request));
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var settings = await LoadOrCreateAsync(db, cancellationToken);
        var authorityChanged = !string.Equals(settings.Authority, authority, StringComparison.Ordinal) || !string.Equals(settings.ClientId, clientId, StringComparison.Ordinal);
        if (request.RequireOrganizationIdp && !settings.RequireOrganizationIdp && signIn.Current is { } facts && (facts.Method != "sso" || authorityChanged))
        {
            // Lock-out guard: prove the organization's IdP works (with these settings) before refusing every other way in.
            throw new ArgumentException(
                "Sign in through your organization's identity provider first (save these settings, sign out, choose your provider), then require it. That proves it works before other sign-ins are refused.",
                nameof(request));
        }

        var changes = new List<string>();
        Track(changes, "enabled", settings.SsoEnabled, request.Enabled);
        Track(changes, "provider", settings.ProviderKind, (SsoProviderKind)request.ProviderKind);
        Track(changes, "issuer", settings.Authority, authority);
        Track(changes, "client id", settings.ClientId, clientId);
        Track(changes, "label", settings.DisplayName, displayName);
        Track(changes, "scopes", settings.Scopes, scopes);
        Track(changes, "only this IdP", settings.RequireOrganizationIdp, request.RequireOrganizationIdp);
        settings.SsoEnabled = request.Enabled;
        settings.ProviderKind = (SsoProviderKind)request.ProviderKind;
        settings.Authority = authority;
        settings.ClientId = clientId;
        settings.DisplayName = displayName;
        settings.Scopes = scopes;
        settings.RequireOrganizationIdp = request.RequireOrganizationIdp;
        if (newSecret is not null)
        {
            settings.ProtectedClientSecret = secrets.Protect(newSecret);
            settings.ClientSecretSetAtUtc = clock.GetUtcNow().UtcDateTime;
            changes.Add("client secret replaced");
        }
        else if (request.ClearClientSecret && settings.ProtectedClientSecret is not null)
        {
            settings.ProtectedClientSecret = null;
            settings.ClientSecretSetAtUtc = null;
            changes.Add("client secret removed");
        }

        return await SaveAsync(db, settings, "Identity provider", changes, cancellationToken);
    }

    public async Task<SignInSettingsDto> SaveMfaPolicyAsync(SaveMfaPolicyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAdminRecentAsync(cancellationToken);
        if (!Enum.IsDefined(request.Mfa))
        {
            throw new ArgumentException("Choose who needs multi-factor authentication.", nameof(request));
        }

        var amr = Clean(request.AcceptedAmrValues, 500, "Accepted amr values") ?? SignInSettings.DefaultAcceptedAmr;
        var acr = Clean(request.AcceptedAcrValues, 500, "Accepted acr values");
        var requested = Clean(request.RequestedAcrValues, 500, "Requested acr values");
        if (request.Mfa != MfaRequirement.Off && SignInRules.SplitList(amr).Count == 0 && SignInRules.SplitList(acr).Count == 0)
        {
            throw new ArgumentException("List at least one amr or acr value that proves MFA.", nameof(request));
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var settings = await LoadOrCreateAsync(db, cancellationToken);
        var stricter = (int)request.Mfa > (int)settings.MfaRequirement;
        if (stricter && signIn.Current is { } facts && facts.Method != "dev" && !facts.MfaPerformed)
        {
            // Lock-out guard: the administrator's own sign-in must show the IdP reports MFA in a way these settings accept.
            throw new ArgumentException(
                "Your own sign-in didn't report multi-factor authentication, so requiring it now could lock administrators out. Turn on MFA at your identity provider, sign in again, then require it here.",
                nameof(request));
        }

        var changes = new List<string>();
        Track(changes, "MFA", settings.MfaRequirement, (MfaRequirementLevel)request.Mfa);
        Track(changes, "amr", settings.AcceptedAmrValues, amr);
        Track(changes, "acr", settings.AcceptedAcrValues, acr);
        Track(changes, "acr_values", settings.RequestedAcrValues, requested);
        settings.MfaRequirement = (MfaRequirementLevel)request.Mfa;
        settings.AcceptedAmrValues = amr;
        settings.AcceptedAcrValues = acr;
        settings.RequestedAcrValues = requested;
        return await SaveAsync(db, settings, "MFA", changes, cancellationToken);
    }

    public async Task<SignInSettingsDto> SaveProvisioningAsync(SaveProvisioningRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAdminRecentAsync(cancellationToken);
        var domains = Clean(request.AllowedEmailDomains, 1000, "Allowed domains");
        var domainList = SignInRules.SplitList(domains).Select(d => d.TrimStart('@').ToLowerInvariant()).Distinct().ToList();
        if (domainList.Any(d => !DomainPattern().IsMatch(d)))
        {
            throw new ArgumentException("Allowed domains look like example.com, separated by spaces or commas.", nameof(request));
        }

        if (request.Mode == JitProvisioningMode.AllowedDomains && domainList.Count == 0)
        {
            throw new ArgumentException("List at least one allowed email domain.", nameof(request));
        }

        var groupsClaim = Clean(request.GroupsClaim, 100, "The groups claim") ?? "groups";
        var mappings = (request.Mappings ?? []).Select(m => (Group: m.GroupValue?.Trim() ?? string.Empty, m.Roles, Description: Clean(m.Description, 200, "A description"))).ToList();
        if (mappings.Count > MaxGroupMappings)
        {
            throw new ArgumentException($"At most {MaxGroupMappings} group mappings.", nameof(request));
        }

        if (mappings.Any(m => m.Group.Length is 0 or > 256))
        {
            throw new ArgumentException("Every mapping needs a group id or name (at most 256 characters).", nameof(request));
        }

        if (mappings.GroupBy(m => m.Group, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Each group can be mapped once; combine its roles in one row.", nameof(request));
        }

        const OrgRoles allRoles = OrgRoles.Admin | OrgRoles.Finance | OrgRoles.Timekeeper | OrgRoles.ResourceManager;
        if ((request.DefaultRoles & ~allRoles) != 0 || mappings.Any(m => (m.Roles & ~allRoles) != 0))
        {
            throw new ArgumentException("Unknown role.", nameof(request));
        }

        if (request.DefaultRoles.IsAdmin())
        {
            throw new ArgumentException("New people can't become administrators by default; map an administrators group instead.", nameof(request));
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var settings = await LoadOrCreateAsync(db, cancellationToken);
        var changes = new List<string>();
        Track(changes, "JIT", settings.JitMode, (JitProvisioning)request.Mode);
        Track(changes, "domains", settings.AllowedEmailDomains, domainList.Count == 0 ? null : string.Join(' ', domainList));
        Track(changes, "default roles", UserLifecycle.RoleText((OrgRoles)settings.JitDefaultRoles), UserLifecycle.RoleText(request.DefaultRoles));
        Track(changes, "groups claim", settings.GroupsClaim, groupsClaim);
        Track(changes, "sync roles", settings.SyncRolesFromGroups, request.SyncRolesFromGroups);
        settings.JitMode = (JitProvisioning)request.Mode;
        settings.AllowedEmailDomains = domainList.Count == 0 ? null : string.Join(' ', domainList);
        settings.JitDefaultRoles = (int)request.DefaultRoles;
        settings.GroupsClaim = groupsClaim;
        settings.SyncRolesFromGroups = request.SyncRolesFromGroups;

        var existing = await db.Set<SsoGroupMapping>().ToListAsync(cancellationToken);
        var wanted = mappings.ToDictionary(m => m.Group, StringComparer.Ordinal);
        foreach (var old in existing.Where(e => !wanted.ContainsKey(e.GroupValue)))
        {
            db.Set<SsoGroupMapping>().Remove(old);
            changes.Add($"unmapped {old.GroupValue}");
        }

        foreach (var (group, roles, description) in mappings)
        {
            var row = existing.FirstOrDefault(e => e.GroupValue == group);
            if (row is null)
            {
                db.Set<SsoGroupMapping>().Add(new SsoGroupMapping { GroupValue = group, Roles = (int)roles, Description = description });
                changes.Add($"mapped {group} → {UserLifecycle.RoleText(roles)}");
            }
            else if (row.Roles != (int)roles || row.Description != description)
            {
                if (row.Roles != (int)roles)
                {
                    changes.Add($"{group}: {UserLifecycle.RoleText((OrgRoles)row.Roles)} → {UserLifecycle.RoleText(roles)}");
                }

                row.Roles = (int)roles;
                row.Description = description;
            }
        }

        return await SaveAsync(db, settings, "Provisioning", changes, cancellationToken);
    }

    public async Task<SignInSettingsDto> SaveSessionPolicyAsync(SaveSessionPolicyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAdminRecentAsync(cancellationToken);
        if (request.ReauthenticationMinutes is < 1 or > 240)
        {
            throw new ArgumentException("Re-authentication must be between 1 and 240 minutes.", nameof(request));
        }

        if (request.SessionMaxHours is { } hours && (hours < 1 || hours > server.AbsoluteLifetimeHours))
        {
            throw new ArgumentException($"The session limit must be between 1 and {server.AbsoluteLifetimeHours} hours (the server's limit).", nameof(request));
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var settings = await LoadOrCreateAsync(db, cancellationToken);
        var changes = new List<string>();
        Track(changes, "re-authentication minutes", settings.ReauthenticationMinutes, request.ReauthenticationMinutes);
        Track(changes, "session max hours", settings.SessionMaxHours, request.SessionMaxHours);
        settings.ReauthenticationMinutes = request.ReauthenticationMinutes;
        settings.SessionMaxHours = request.SessionMaxHours;
        return await SaveAsync(db, settings, "Sessions", changes, cancellationToken);
    }

    private async Task RequireAdminRecentAsync(CancellationToken cancellationToken)
    {
        await access.RequireAdminAsync(cancellationToken);
        await reauthentication.RequireRecentAsync(SensitiveActions.SignInSettingsChange, cancellationToken);
    }

    private async Task<SignInSettingsDto> SaveAsync(IWorkbenchDbContext db, SignInSettings settings, string area, List<string> changes, CancellationToken cancellationToken)
    {
        settings.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        settings.UpdatedBy = access.Email;
        await db.SaveChangesAsync(cancellationToken);
        store.Invalidate();
        if (changes.Count > 0)
        {
            await securityLog.RecordAsync(SecurityEventTypes.SignInSettingsChanged, area, detail: string.Join("; ", changes), cancellationToken: cancellationToken);
        }

        return await ToDtoAsync(db, cancellationToken);
    }

    private static async Task<SignInSettings> LoadOrCreateAsync(IWorkbenchDbContext db, CancellationToken cancellationToken)
    {
        var settings = await db.Set<SignInSettings>().OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new SignInSettings();
            db.Set<SignInSettings>().Add(settings);
        }

        return settings;
    }

    private async Task<SignInSettingsDto> ToDtoAsync(IWorkbenchDbContext db, CancellationToken cancellationToken)
    {
        var s = await db.Set<SignInSettings>().AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(cancellationToken) ?? new SignInSettings();
        var mappings = await db.Set<SsoGroupMapping>().AsNoTracking().OrderBy(m => m.GroupValue).ToListAsync(cancellationToken);
        var facts = signIn.Current;
        return new SignInSettingsDto(
            s.SsoEnabled, (IdentityProviderKind)s.ProviderKind, s.DisplayName, s.Authority, s.ClientId, s.ProtectedClientSecret is not null, s.ClientSecretSetAtUtc,
            s.Scopes, s.RequireOrganizationIdp,
            (MfaRequirement)s.MfaRequirement, s.AcceptedAmrValues, s.AcceptedAcrValues, s.RequestedAcrValues,
            (JitProvisioningMode)s.JitMode, s.AllowedEmailDomains, (OrgRoles)s.JitDefaultRoles, s.GroupsClaim, s.SyncRolesFromGroups,
            mappings.Select(m => new SsoGroupMappingDto(m.Id, m.GroupValue, (OrgRoles)m.Roles, m.Description)).ToList(),
            s.ReauthenticationMinutes, s.SessionMaxHours, s.UpdatedAtUtc, s.UpdatedBy,
            new SignInEnvironmentDto(server.ServerOidcConfigured, tenancy?.IsMultiTenant == true, server.IdleTimeoutMinutes, server.AbsoluteLifetimeHours,
                facts?.Method, facts?.MfaPerformed ?? false));
    }

    private static void ValidateAuthority(string authority)
    {
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("The issuer URL must be an absolute address such as https://login.microsoftonline.com/<tenant-id>/v2.0.", nameof(authority));
        }

        // Plain HTTP only for an identity provider on this machine (development and tests).
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            throw new ArgumentException("The issuer URL must use https.", nameof(authority));
        }
    }

    private static string? Clean(string? value, int max, string what)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length > max ? throw new ArgumentException($"{what} is too long (at most {max} characters).") : value;
    }

    private static void Track<T>(List<string> changes, string name, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
        {
            changes.Add($"{name}: {Show(before)} → {Show(after)}");
        }
    }

    private static string Show<T>(T value) => value switch
    {
        null => "(none)",
        string s when s.Length > 80 => s[..80] + "…",
        _ => value.ToString() ?? "(none)",
    };

    [GeneratedRegex(@"^[A-Za-z0-9:._/\-]+$")]
    private static partial Regex ScopePattern();

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainPattern();
}
