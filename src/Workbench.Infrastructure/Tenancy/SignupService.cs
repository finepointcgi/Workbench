using System.Collections.Concurrent;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Tenancy;

namespace Workbench.Infrastructure.Tenancy;

/// <summary>What the visitor typed on the signup form, plus what the server knows about the request.</summary>
public sealed record SignupRequest(
    string CompanyName,
    string Subdomain,
    string AdminName,
    string AdminEmail,
    string? CompanyType,
    bool AcceptedTerms,
    string? Source,
    string? ClientAddress);

public enum SignupOutcome
{
    /// <summary>A new organization was created and the sign-in link emailed.</summary>
    Created,

    /// <summary>The same person asked again for the same address: nothing was created twice, and a new sign-in link was emailed.</summary>
    LinkResent,

    /// <summary>The same person asked again while the first request is still being set up.</summary>
    InProgress,
}

public sealed record SignupResult(SignupOutcome Outcome, string Slug, string Url);

/// <summary>A signup the visitor can fix (validation, taken subdomain) or must wait out (rate limit). The message is safe to show.</summary>
public sealed class SignupRefusedException(string message, bool rateLimited = false) : Exception(message)
{
    public bool RateLimited { get; } = rateLimited;
}

/// <summary>
/// Self-service signup: validates the form, makes the tenant through <see cref="TenantProvisioner"/> with a trial entitlement
/// and the consent recorded, and emails the administrator a sign-in link. Safe to repeat: the same email asking again for the
/// same subdomain never makes a second tenant; two different visitors racing for one subdomain get one winner (a per-subdomain
/// lock in this process and the catalog's unique index across processes). Off unless <c>Tenancy:Signup:Enabled</c>.
/// </summary>
public sealed partial class SignupService(
    TenantProvisioner provisioner,
    IDbContextFactory<CatalogDbContext> catalog,
    TenancyOptions tenancy,
    SignupRateLimiter limiter,
    TimeProvider clock,
    ILogger<SignupService> logger)
{
    /// <summary>The actor recorded in the host audit log for self-service actions.</summary>
    public const string Actor = "signup";

    private static readonly HashSet<string> ReservedSubdomains = new(StringComparer.Ordinal)
    {
        "www", "host", "admin", "administrator", "api", "app", "apps", "auth", "login", "logout", "signin", "signup", "register", "account", "accounts",
        "mail", "email", "smtp", "imap", "pop", "ftp", "sftp", "ns1", "ns2", "dns", "static", "assets", "cdn", "status", "support", "help", "docs", "doc",
        "blog", "news", "billing", "payments", "pay", "invoice", "portal", "license", "licenses", "licensing", "store", "shop", "secure", "security",
        "abuse", "postmaster", "hostmaster", "webmaster", "root", "noreply", "no-reply", "info", "sales", "legal", "privacy", "terms", "trust",
        "sso", "oauth", "id", "idp", "git", "vpn", "dev", "test", "testing", "staging", "stage", "demo", "sandbox", "preview", "prod", "production",
        "internal", "workbench", "scim", "mcp", "webhook", "webhooks", "metrics", "health", "monitor", "grafana",
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>The subdomain, validated: <see cref="TenantCatalog.ValidateSlug"/>'s rules plus the signup-only reserved words.</summary>
    public static string ValidateSubdomain(string? subdomain)
    {
        string value;
        try
        {
            value = TenantCatalog.ValidateSlug(subdomain);
        }
        catch (ArgumentException ex)
        {
            throw new SignupRefusedException(ex.Message);
        }

        if (ReservedSubdomains.Contains(value) || value.StartsWith("xn--", StringComparison.Ordinal) || value.Contains("--", StringComparison.Ordinal))
        {
            throw new SignupRefusedException($"'{value}' can't be used as an address. Choose another one.");
        }

        return value;
    }

    /// <summary>True when <paramref name="subdomain"/> is valid and no tenant (in any state) has it. For the form's availability check.</summary>
    public async Task<bool> IsAvailableAsync(string? subdomain, CancellationToken cancellationToken = default)
    {
        string slug;
        try
        {
            slug = ValidateSubdomain(subdomain);
        }
        catch (SignupRefusedException)
        {
            return false;
        }

        await using var db = await catalog.CreateDbContextAsync(cancellationToken);
        return !await db.Tenants.AnyAsync(t => t.Slug == slug, cancellationToken);
    }

    public async Task<SignupResult> SignUpAsync(SignupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = tenancy.Signup;
        if (!tenancy.IsMultiTenant || !options.Enabled)
        {
            throw new SignupRefusedException("Signup isn't available on this server.");
        }

        var company = (request.CompanyName ?? string.Empty).Trim();
        if (company.Length is < 2 or > 200)
        {
            throw new SignupRefusedException("Enter your company's name (2 to 200 characters).");
        }

        var slug = ValidateSubdomain(request.Subdomain);
        var name = (request.AdminName ?? string.Empty).Trim();
        if (name.Length is < 2 or > 200)
        {
            throw new SignupRefusedException("Enter your name.");
        }

        var email = NormalizeEmail(request.AdminEmail);
        if (!request.AcceptedTerms)
        {
            throw new SignupRefusedException("Accept the terms to continue.");
        }

        // Count the attempt before doing any work, so a script can't use the form to probe subdomains or flood an inbox.
        if (!limiter.TryAcquire(request.ClientAddress, email, options.MaxPerIpPerHour, options.MaxPerEmailPerDay))
        {
            throw new SignupRefusedException("Too many attempts. Wait a while and try again.", rateLimited: true);
        }

        var gate = _locks.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await FindExistingAsync(slug, cancellationToken);
            if (existing is not null)
            {
                return await ExistingAsync(existing.Value.Tenant, existing.Value.Entitlement, email, slug, options, cancellationToken);
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var entitlement = new SetEntitlementRequest(
                Plan: "trial",
                Modules: options.TrialModules,
                Seats: options.TrialSeats,
                LightSeats: options.TrialLightSeats,
                Trial: true,
                TrialEndsUtc: now.AddDays(options.TrialDays),
                Status: "active");
            var source = CleanSource(request.Source);
            try
            {
                await provisioner.CreateAsync(
                    new CreateTenantRequest(slug, company, email, name, "trial", null,
                        new TenantSignup(email, source, CleanCompanyType(request.CompanyType), now, options.TermsVersion, entitlement, options.MagicLinkMinutes)),
                    Actor, cancellationToken: cancellationToken);
            }
            catch (ArgumentException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
            {
                // Lost a race with another process for the same subdomain: if the winner is this same person, treat it as the repeat it is.
                var winner = await FindExistingAsync(slug, cancellationToken);
                if (winner is not null)
                {
                    return await ExistingAsync(winner.Value.Tenant, winner.Value.Entitlement, email, slug, options, cancellationToken);
                }

                throw new SignupRefusedException("That address was just taken. Choose another one.");
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "Signup provisioning of {Slug} failed", slug);
                throw new SignupRefusedException("We couldn't set up your organization. Please try again later or contact support.");
            }

            return new SignupResult(SignupOutcome.Created, slug, $"https://{slug}.{tenancy.BaseDomain}/");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SignupResult> ExistingAsync(Tenant tenant, TenantEntitlement? entitlement, string email, string slug, SignupOptions options, CancellationToken cancellationToken)
    {
        // Someone else owns it: say it's taken. Don't reveal whose.
        if (entitlement?.SignupEmail is not { } owner || !string.Equals(owner, email, StringComparison.Ordinal))
        {
            throw new SignupRefusedException("That address is already taken. Choose another one.");
        }

        var url = $"https://{slug}.{tenancy.BaseDomain}/";
        switch (tenant.Status)
        {
            case TenantStatus.Provisioning:
                return new SignupResult(SignupOutcome.InProgress, slug, url);
            case TenantStatus.Active:
                await provisioner.ResendSignInLinkAsync(new TenantInfo(tenant.Id, tenant.Slug, tenant.DisplayName), email, options.MagicLinkMinutes, cancellationToken);
                return new SignupResult(SignupOutcome.LinkResent, slug, url);
            default:
                throw new SignupRefusedException("We couldn't set up that organization. Contact support and quote the address you chose.");
        }
    }

    private async Task<(Tenant Tenant, TenantEntitlement? Entitlement)?> FindExistingAsync(string slug, CancellationToken cancellationToken)
    {
        await using var db = await catalog.CreateDbContextAsync(cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);
        if (tenant is null)
        {
            return null;
        }

        return (tenant, await db.Entitlements.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenant.Id, cancellationToken));
    }

    private static string NormalizeEmail(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length is 0 or > 320 || !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value || !value.Contains('.', StringComparison.Ordinal))
        {
            throw new SignupRefusedException("Enter a valid work email address.");
        }

        return value;
    }

    private static string CleanSource(string? source)
    {
        var value = (source ?? string.Empty).Trim().ToLowerInvariant();
        return value.Length == 0 || !SourcePattern().IsMatch(value) ? "direct" : value;
    }

    private static string? CleanCompanyType(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > 64 ? trimmed[..64] : trimmed;
    }

    [GeneratedRegex("^[a-z0-9._-]{1,64}$")]
    private static partial Regex SourcePattern();
}

/// <summary>
/// Per-address and per-email limits for the signup form, kept in memory (so each server instance counts for itself; put a
/// shared limit at the load balancer when you run several). Every call counts, whether or not the signup then succeeds.
/// </summary>
public sealed class SignupRateLimiter(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, List<DateTime>> _hits = new(StringComparer.Ordinal);

    public bool TryAcquire(string? address, string email, int perAddressPerHour, int perEmailPerDay)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var ok = Record("ip:" + (address ?? "unknown"), now, TimeSpan.FromHours(1), perAddressPerHour);
        return Record("email:" + email, now, TimeSpan.FromDays(1), perEmailPerDay) && ok;
    }

    private bool Record(string key, DateTime now, TimeSpan window, int limit)
    {
        var list = _hits.GetOrAdd(key, _ => []);
        lock (list)
        {
            list.RemoveAll(t => now - t >= window);
            if (list.Count >= limit)
            {
                return false;
            }

            list.Add(now);
            if (_hits.Count > 20_000)
            {
                foreach (var stale in _hits.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).Take(1000))
                {
                    _hits.TryRemove(stale, out _);
                }
            }

            return true;
        }
    }
}
