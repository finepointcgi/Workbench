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

public sealed record CreateTenantRequest(string Slug, string DisplayName, string AdminEmail, string? AdminName = null, string? Plan = null, string? Notes = null);

/// <summary>A tenant with what the host admin needs to see: database size and last activity (null when unreachable).</summary>
public sealed record TenantOverview(TenantSummary Tenant, long? DatabaseBytes, DateTime? LastActivityUtc);

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
            await db.SaveChangesAsync(cancellationToken);
        }

        catalog.Invalidate(slug, tenant.Id);
        var info = new TenantInfo(tenant.Id, slug, name);
        try
        {
            using (TenantScope.Enter(info))
            {
                await services.GetRequiredService<DatabaseMigrator>().MigrateCurrentAsync(cancellationToken);
                await TenantDefaultsSeed.SeedAsync(services, tenancy.FiscalYearStartMonth, clock, cancellationToken);
                if (seedSampleData)
                {
                    await services.SeedSampleDataAsync(cancellationToken);
                }

                await InviteAdminAsync(info, adminEmail, Clean(request.AdminName, 200) ?? adminEmail.Split('@')[0], actor, cancellationToken);
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
            if (tenant.Status is TenantStatus.Active or TenantStatus.Suspended)
            {
                try
                {
                    using (TenantScope.Enter(new TenantInfo(tenant.Id, tenant.Slug, tenant.DisplayName)))
                    {
                        await using var db = await services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>().CreateDbContextAsync(cancellationToken);
                        bytes = await DatabaseSizeAsync(db, cancellationToken);
                        lastActivity = await db.Set<AppUser>().MaxAsync(u => u.LastSeenAtUtc, cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Couldn't read the database of tenant {Slug}", tenant.Slug);
                }
            }

            result.Add(new TenantOverview(tenant, bytes, lastActivity));
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

    private async Task InviteAdminAsync(TenantInfo tenant, string email, string name, string actor, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var scope = services.CreateAsyncScope();
        var lifetime = scope.ServiceProvider.GetService<SignInPolicyOptions>()?.InviteLifetime ?? TimeSpan.FromDays(7);
        var invite = new UserInvite
        {
            Email = email,
            DisplayName = name,
            IsAdmin = true,
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
        var text = $"Hello {name},\n\nYour Workbench organization {tenant.DisplayName} is ready. Sign in as its administrator with this email address ({email}) at {signIn} before {invite.ExpiresAtUtc:MMMM d, yyyy HH:mm} UTC.\n";
        var html = $"<p>Hello {WebUtility.HtmlEncode(name)},</p><p>Your Workbench organization {WebUtility.HtmlEncode(tenant.DisplayName)} is ready. "
            + $"Sign in as its administrator with this email address ({WebUtility.HtmlEncode(email)}) at {WebUtility.HtmlEncode(signIn)} before {invite.ExpiresAtUtc:MMMM d, yyyy HH:mm} UTC.</p>";
        try
        {
            await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(new EmailMessage(email, "Your Workbench organization is ready", text, html), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't email the first-admin invite for tenant {Slug}", tenant.Slug);
        }
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
