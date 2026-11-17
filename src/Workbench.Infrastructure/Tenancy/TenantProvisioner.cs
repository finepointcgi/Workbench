using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.Tenancy;
using Workbench.Domain.Identity;
using Workbench.Domain.Security;
using Workbench.Infrastructure.Persistence;

namespace Workbench.Infrastructure.Tenancy;

public sealed record CreateTenantRequest(string Slug, string DisplayName, string AdminEmail, string? AdminName = null, string? Plan = null, string? Notes = null, TenantSignup? Signup = null);

/// <summary>
/// A self-service signup (<see cref="SignupService"/>): the tenant starts with <paramref name="Entitlement"/> (a trial), its
/// first administrator gets an emailed sign-in link instead of a password, and the consent is recorded in the catalog.
/// </summary>
public sealed record TenantSignup(string Email, string Source, string? CompanyType, DateTime TermsAcceptedAtUtc, string TermsVersion, Contracts.Tenancy.SetEntitlementRequest Entitlement, int MagicLinkMinutes);

/// <summary>A tenant with what the host admin needs to see: database size and last activity (null when unreachable).</summary>
public sealed record TenantOverview(TenantSummary Tenant, long? DatabaseBytes, DateTime? LastActivityUtc, (int Full, int Light)? Seats = null);

/// <summary>
/// Host administration (MultiTenant mode): creates a tenant (its database, migrations, default chart of accounts and
/// fiscal calendar, and an invite for its first Admin), suspends and reactivates it, and reports on every tenant. The
/// callers check that the actor is a host admin; every action is written to the host audit log.
/// </summary>
public sealed class TenantProvisioner(
    IServiceProvider services,
    IDbContextFactory<CatalogDbContext> catalogFactory,
    TenantCatalog catalog,
    TenancyOptions tenancy,
    WorkbenchDatabase database,
    TimeProvider clock,
    ILogger<TenantProvisioner> logger)
{
    public async Task<TenantSummary> CreateAsync(CreateTenantRequest request, string actor, bool seedSampleData = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var slug = TenantCatalog.ValidateSlug(request.Slug);
        var name = (request.DisplayName ?? string.Empty).Trim();
        if (name.Length is 0 or > 200)
        {
            throw new ArgumentException("Enter the organization's name (up to 200 characters).");
        }

        var adminEmail = ValidEmail(request.AdminEmail);
        var connectionString = ConnectionStringFor(slug);
        var now = clock.GetUtcNow().UtcDateTime;

        var tenant = new Tenant
        {
            Slug = slug,
            DisplayName = name,
            ProtectedConnectionString = catalog.Protect(connectionString),
            Plan = Clean(request.Plan, 64),
            Notes = Clean(request.Notes, 2000),
            CreatedAtUtc = now,
            StatusChangedAtUtc = now,
            StatusChangedBy = actor,
        };
        await using (var db = await catalogFactory.CreateDbContextAsync(cancellationToken))
        {
            if (await db.Tenants.AnyAsync(t => t.Slug == slug, cancellationToken))
            {
                throw new ArgumentException($"A tenant with the id '{slug}' already exists. Tenant ids are never reused.");
            }

            db.Tenants.Add(tenant);
            db.HostAuditEvents.Add(Audit(actor, "tenant.create.started", tenant.Id, $"{slug} ({name}); first admin {adminEmail}"));
            if (request.Signup is { } signup)
            {
                db.Entitlements.Add(EntitlementFor(tenant.Id, signup, actor, now));
                db.HostAuditEvents.Add(Audit(actor, "signup.accepted", tenant.Id, $"{slug}: {signup.Email}, source {signup.Source}, terms {signup.TermsVersion}"));
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Two requests passed the check above together; the unique index let only one in.
                if (await SlugTakenAsync(slug, cancellationToken))
                {
                    throw new ArgumentException($"A tenant with the id '{slug}' already exists. Tenant ids are never reused.");
                }

                throw;
            }
        }

        catalog.Invalidate(slug, tenant.Id);
        var info = new TenantInfo(tenant.Id, slug, name);
        try
        {
            using (TenantScope.Enter(info))
            {
                await services.GetRequiredService<DatabaseMigrator>().MigrateCurrentAsync(cancellationToken);

                // The sample data brings its own chart of accounts and fiscal years; the defaults then add only what's missing.
                if (seedSampleData)
                {
                    await services.SeedSampleDataAsync(cancellationToken);
                }

                await TenantDefaultsSeed.SeedAsync(services, tenancy.FiscalYearStartMonth, clock, cancellationToken);

                await InviteAdminAsync(info, adminEmail, Clean(request.AdminName, 200) ?? adminEmail.Split('@')[0], actor, request.Signup, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Provisioning tenant {Slug} failed", slug);
            await SetStatusAsync(tenant.Id, TenantStatus.Failed, actor, "tenant.create.failed", ex.GetType().Name, cancellationToken);
            throw new InvalidOperationException($"Creating tenant '{slug}' failed: {ex.Message}", ex);
        }

        return await SetStatusAsync(tenant.Id, TenantStatus.Active, actor, "tenant.create.completed", null, cancellationToken);
    }

    public Task<TenantSummary> SuspendAsync(Guid tenantId, string actor, string? reason, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(tenantId, TenantStatus.Active, TenantStatus.Suspended, actor, "tenant.suspend", reason, cancellationToken);

    public Task<TenantSummary> ReactivateAsync(Guid tenantId, string actor, string? reason, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(tenantId, TenantStatus.Suspended, TenantStatus.Active, actor, "tenant.reactivate", reason, cancellationToken);

    /// <summary>Every tenant with its database size and last activity (the latest user sign-in or request).</summary>
    public async Task<IReadOnlyList<TenantOverview>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<TenantOverview>();
        foreach (var tenant in await catalog.ListAsync(cancellationToken))
        {
            long? bytes = null;
            DateTime? lastActivity = null;
            (int Full, int Light)? seats = null;
            if (tenant.Status is TenantStatus.Active or TenantStatus.Suspended)
            {
                try
                {
                    using (TenantScope.Enter(new TenantInfo(tenant.Id, tenant.Slug, tenant.DisplayName)))
                    {
                        await using var db = await services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>().CreateDbContextAsync(cancellationToken);
                        bytes = await DatabaseSizeAsync(db, cancellationToken);
                        lastActivity = await db.Set<AppUser>().MaxAsync(u => u.LastSeenAtUtc, cancellationToken);
                        seats = await Workbench.Application.Licensing.EditionService.CountPeopleAsync(db, cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Couldn't read the database of tenant {Slug}", tenant.Slug);
                }
            }

            result.Add(new TenantOverview(tenant, bytes, lastActivity, seats));
        }

        return result;
    }

    public async Task<IReadOnlyList<HostAuditEvent>> ListAuditAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        await using var db = await catalogFactory.CreateDbContextAsync(cancellationToken);
        return await db.HostAuditEvents.AsNoTracking().OrderByDescending(e => e.AtUtc).Take(take).ToListAsync(cancellationToken);
    }

    /// <summary>Development: creates the tenant with the sample data if it doesn't exist. First admin: dev@workbench.local.</summary>
    internal async Task EnsureDevelopmentTenantAsync(string slug, CancellationToken cancellationToken)
    {
        slug = TenantCatalog.ValidateSlug(slug);
        if (await catalog.FindAsync(slug, cancellationToken) is not null)
        {
            return;
        }

        var name = $"{char.ToUpperInvariant(slug[0])}{slug[1..]} (development)";
        await CreateAsync(new CreateTenantRequest(slug, name, "dev@workbench.local", "Dana Lee", "Development"), "startup", seedSampleData: true, cancellationToken);
    }

    internal string ConnectionStringFor(string slug)
    {
        var template = tenancy.TenantConnectionString;
        if (string.IsNullOrWhiteSpace(template) || !template.Contains("{tenant}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Set Tenancy:TenantConnectionString with a {tenant} placeholder to create tenants.");
        }

        return template.Replace("{tenant}", slug.Replace('-', '_'), StringComparison.Ordinal);
    }

    private async Task<long?> DatabaseSizeAsync(WorkbenchDbContext db, CancellationToken cancellationToken) => database.Provider switch
    {
        DatabaseProvider.PostgreSql => await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").SingleAsync(cancellationToken),
        _ => await db.Database.SqlQueryRaw<long>("SELECT page_count * page_size AS \"Value\" FROM pragma_page_count(), pragma_page_size()").SingleAsync(cancellationToken),
    };

    private static TenantEntitlement EntitlementFor(Guid tenantId, TenantSignup signup, string actor, DateTime now)
    {
        var e = signup.Entitlement;
        return new TenantEntitlement
        {
            TenantId = tenantId,
            Plan = e.Plan ?? "trial",
            Modules = string.Join(',', TenantEntitlements.NormalizeModules(e.Modules ?? [])),
            Seats = e.Seats ?? 0,
            LightSeats = e.LightSeats ?? 0,
            Status = EntitlementStatus.Active,
            IsTrial = e.Trial ?? true,
            TrialEndsUtc = e.TrialEndsUtc,
            Source = "signup",
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            SignupEmail = signup.Email,
            SignupSource = signup.Source,
            TermsAcceptedAtUtc = signup.TermsAcceptedAtUtc,
            TermsVersion = signup.TermsVersion,
            CompanyType = signup.CompanyType,
        };
    }

    private async Task<bool> SlugTakenAsync(string slug, CancellationToken cancellationToken)
    {
        await using var db = await catalogFactory.CreateDbContextAsync(cancellationToken);
        return await db.Tenants.AnyAsync(t => t.Slug == slug, cancellationToken);
    }

    private async Task InviteAdminAsync(TenantInfo tenant, string email, string name, string actor, TenantSignup? signup, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var scope = services.CreateAsyncScope();
        var lifetime = scope.ServiceProvider.GetService<SignInPolicyOptions>()?.InviteLifetime ?? TimeSpan.FromDays(7);
        var passwordsEnabled = scope.ServiceProvider.GetService<Workbench.Application.Identity.Passwords.PasswordOptions>()?.Enabled ?? true;
        string? acceptToken = null;
        var invite = new UserInvite
        {
            Email = email,
            DisplayName = name,
            Roles = (int)Workbench.Contracts.Identity.OrgRoles.Admin,
            InvitedBy = actor,
            CreatedAtUtc = now,
            ExpiresAtUtc = now + lifetime,
        };
        await using (var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>().CreateDbContextAsync(cancellationToken))
        {
            if (await db.Set<AppUser>().AnyAsync(u => u.Email == email, cancellationToken))
            {
                return;
            }

            db.Set<UserInvite>().Add(invite);
            if (signup is not null)
            {
                // Self-service: one link signs the administrator in (no password to choose first); the invite makes them an Admin.
                var (raw, token) = Workbench.Application.Identity.Passwords.PasswordAccounts.NewToken(email, AccountTokenPurpose.MagicLink, now.AddMinutes(signup.MagicLinkMinutes), now);
                token.InviteId = invite.Id;
                db.Set<AccountToken>().Add(token);
                acceptToken = raw;
            }
            else if (passwordsEnabled)
            {
                // The first admin chooses a password from the email, like any invite (or signs in with the organization's SSO later).
                var (raw, token) = Workbench.Application.Identity.Passwords.PasswordAccounts.NewToken(email, AccountTokenPurpose.Invite, invite.ExpiresAtUtc, now);
                token.InviteId = invite.Id;
                db.Set<AccountToken>().Add(token);
                acceptToken = raw;
            }

            db.Set<SecurityEvent>().Add(new SecurityEvent
            {
                EventType = Contracts.Security.SecurityEventTypes.InviteCreated,
                Actor = actor,
                Target = email,
                Outcome = Contracts.Security.SecurityOutcomes.Success,
                Detail = "First administrator, invited by the host when the tenant was created",
                OccurredAtUtc = now,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        var signIn = $"https://{tenant.Slug}.{tenancy.BaseDomain}/account/signin";
        var start = acceptToken is null ? signIn : $"https://{tenant.Slug}.{tenancy.BaseDomain}/account/{(signup is null ? "accept-invite" : "magic")}?token={acceptToken}";
        string text, html;
        if (signup is null)
        {
            var how = acceptToken is null ? "Sign in as its administrator with this email address" : "Choose your administrator password for this email address";
            text = $"Hello {name},\n\nYour Workbench organization {tenant.DisplayName} is ready. {how} ({email}) at {start} before {invite.ExpiresAtUtc:MMMM d, yyyy HH:mm} UTC.\n";
            html = $"<p>Hello {WebUtility.HtmlEncode(name)},</p><p>Your Workbench organization {WebUtility.HtmlEncode(tenant.DisplayName)} is ready. "
                + $"{how} ({WebUtility.HtmlEncode(email)}) at <a href=\"{WebUtility.HtmlEncode(start)}\">{WebUtility.HtmlEncode(start)}</a> before {invite.ExpiresAtUtc:MMMM d, yyyy HH:mm} UTC.</p>";
        }
        else
        {
            // Self-service signup: one link, no password to choose first.
            text = $"Hello {name},\n\nYour Workbench organization {tenant.DisplayName} is ready. Use this link to sign in as its administrator ({email}):\n\n{start}\n\n"
                + $"The link works once and expires in {signup.MagicLinkMinutes} minutes. Your {(signup.Entitlement.TrialEndsUtc is { } ends ? $"free trial runs until {ends:MMMM d, yyyy}" : "trial has started")}.\n";
            html = $"<p>Hello {WebUtility.HtmlEncode(name)},</p><p>Your Workbench organization {WebUtility.HtmlEncode(tenant.DisplayName)} is ready. "
                + $"Use this link to sign in as its administrator ({WebUtility.HtmlEncode(email)}):</p><p><a href=\"{WebUtility.HtmlEncode(start)}\">{WebUtility.HtmlEncode(start)}</a></p>"
                + $"<p>The link works once and expires in {signup.MagicLinkMinutes} minutes. Your {(signup.Entitlement.TrialEndsUtc is { } ends2 ? $"free trial runs until {ends2:MMMM d, yyyy}" : "trial has started")}.</p>";
        }

        try
        {
            await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(new EmailMessage(email, "Your Workbench organization is ready", text, html) { Essential = true }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't email the first-admin invite for tenant {Slug}", tenant.Slug);
        }
    }

    /// <summary>
    /// Emails the signup's administrator a new one-time sign-in link (the first one expired, or got lost). False when there is
    /// nobody to sign in: no such user and no unexpired invite. Older unused links stop working.
    /// </summary>
    public async Task<bool> ResendSignInLinkAsync(TenantInfo tenant, string email, int minutes, CancellationToken cancellationToken = default)
    {
        var address = ValidEmail(email);
        var now = clock.GetUtcNow().UtcDateTime;
        using var _ = TenantScope.Enter(tenant);
        await using var scope = services.CreateAsyncScope();
        string raw;
        string name;
        await using (var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>().CreateDbContextAsync(cancellationToken))
        {
            var user = await db.Set<AppUser>().AsNoTracking().FirstOrDefaultAsync(u => u.Email == address && u.IsActive, cancellationToken);
            var invite = user is null
                ? await db.Set<UserInvite>().AsNoTracking().Where(i => i.Email == address && i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > now)
                    .OrderByDescending(i => i.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken)
                : null;
            if (user is null && invite is null)
            {
                return false;
            }

            foreach (var old in await db.Set<AccountToken>().Where(t => t.Email == address && t.Purpose == AccountTokenPurpose.MagicLink && t.UsedAtUtc == null && t.RevokedAtUtc == null).ToListAsync(cancellationToken))
            {
                old.RevokedAtUtc = now;
            }

            var (token, row) = Workbench.Application.Identity.Passwords.PasswordAccounts.NewToken(address, AccountTokenPurpose.MagicLink, now.AddMinutes(minutes), now);
            row.InviteId = invite?.Id;
            db.Set<AccountToken>().Add(row);
            await db.SaveChangesAsync(cancellationToken);
            raw = token;
            name = user?.DisplayName ?? invite?.DisplayName ?? address;
        }

        var link = $"https://{tenant.Slug}.{tenancy.BaseDomain}/account/magic?token={raw}";
        var text = $"Hello {name},\n\nUse this link to sign in to {tenant.DisplayName} on Workbench: {link}\n\nIt works once and expires in {minutes} minutes. If you didn't ask for it, ignore this email.\n";
        var html = $"<p>Hello {WebUtility.HtmlEncode(name)},</p><p><a href=\"{WebUtility.HtmlEncode(link)}\">Sign in to {WebUtility.HtmlEncode(tenant.DisplayName)}</a> on Workbench. "
            + $"The link works once and expires in {minutes} minutes. If you didn't ask for it, ignore this email.</p>";
        try
        {
            await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(new EmailMessage(address, "Your Workbench sign-in link", text, html) { Essential = true }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't email the sign-in link for tenant {Slug}", tenant.Slug);
        }

        return true;
    }

    private async Task<TenantSummary> ChangeStatusAsync(Guid tenantId, TenantStatus from, TenantStatus to, string actor, string action, string? reason, CancellationToken cancellationToken)
    {
        await using (var db = await catalogFactory.CreateDbContextAsync(cancellationToken))
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
                ?? throw Contracts.WorkbenchNotFoundException.For("Tenant", tenantId);
            if (tenant.Status != from)
            {
                throw new ArgumentException($"Tenant {tenant.Slug} is {tenant.Status.ToString().ToLowerInvariant()}, not {from.ToString().ToLowerInvariant()}.");
            }
        }

        return await SetStatusAsync(tenantId, to, actor, action, Clean(reason, 500), cancellationToken);
    }

    private async Task<TenantSummary> SetStatusAsync(Guid tenantId, TenantStatus status, string actor, string action, string? detail, CancellationToken cancellationToken)
    {
        await using var db = await catalogFactory.CreateDbContextAsync(cancellationToken);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId, cancellationToken);
        tenant.Status = status;
        tenant.StatusChangedAtUtc = clock.GetUtcNow().UtcDateTime;
        tenant.StatusChangedBy = actor;
        db.HostAuditEvents.Add(Audit(actor, action, tenant.Id, detail is null ? tenant.Slug : $"{tenant.Slug}: {detail}"));
        await db.SaveChangesAsync(cancellationToken);
        catalog.Invalidate(tenant.Slug);
        return TenantCatalog.ToSummary(tenant);
    }

    private HostAuditEvent Audit(string actor, string action, Guid? tenantId, string? detail) =>
        new() { AtUtc = clock.GetUtcNow().UtcDateTime, Actor = actor, Action = action, TenantId = tenantId, Detail = detail };

    private static string ValidEmail(string? email)
    {
        var value = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(value, out var parsed) || parsed.Address != value)
        {
            throw new ArgumentException("Enter the first administrator's email address.");
        }

        return value;
    }

    private static string? Clean(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
