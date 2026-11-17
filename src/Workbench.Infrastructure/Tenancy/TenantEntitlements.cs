using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Licensing;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Tenancy;
using Workbench.Domain.Licensing;

namespace Workbench.Infrastructure.Tenancy;

/// <summary>
/// Reads and writes the catalog's per-tenant entitlement. The in-product <see cref="EditionService"/> sees it through
/// <see cref="IHostedEntitlementSource"/> (cached for <see cref="CacheTtl"/>, and at once on the instance that wrote it);
/// the host admin and the license server change it through <see cref="ApplyAsync"/>. Every change is written to the host audit log.
/// </summary>
public sealed class TenantEntitlements(IDbContextFactory<CatalogDbContext> catalog, TenancyOptions tenancy, TimeProvider clock) : IHostedEntitlementSource
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, (HostedEntitlement? Value, DateTime FetchedAtUtc)> _cache = new();

    public async Task<HostedEntitlement?> GetAsync(CancellationToken cancellationToken)
    {
        if (TenantScope.Current is not { } tenant)
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (_cache.TryGetValue(tenant.Id, out var cached) && now - cached.FetchedAtUtc < CacheTtl)
        {
            return Reevaluate(cached.Value, now);
        }

        await using var db = await catalog.CreateDbContextAsync(cancellationToken);
        var row = await db.Entitlements.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenant.Id, cancellationToken);
        var value = row is null ? null : Evaluate(row, now, tenancy.Signup.GraceDays);
        _cache[tenant.Id] = (value, now);
        return value;
    }

    /// <summary>
    /// Why the current tenant is read-only, or null when it can change data (or has no entitlement, or there is no tenant). Synchronous
    /// because it runs inside <c>SaveChanges</c>; a catalog read happens at most every <see cref="CacheTtl"/> per tenant.
    /// </summary>
    public string? CurrentReadOnlyMessage()
    {
        if (TenantScope.Current is not { } tenant)
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (!_cache.TryGetValue(tenant.Id, out var cached) || now - cached.FetchedAtUtc >= CacheTtl)
        {
            using var db = catalog.CreateDbContext();
            var row = db.Entitlements.AsNoTracking().FirstOrDefault(e => e.TenantId == tenant.Id);
            cached = (row is null ? null : Evaluate(row, now, tenancy.Signup.GraceDays), now);
            _cache[tenant.Id] = cached;
        }

        return Reevaluate(cached.Value, now) is { ReadOnly: true } view ? view.Message : null;
    }

    /// <summary>True when the tenant is read-only for commercial reasons (does not read the catalog when the answer is cached).</summary>
    public bool IsReadOnly(Guid tenantId, TenantEntitlement? row) =>
        row is not null && Evaluate(row, clock.GetUtcNow().UtcDateTime, tenancy.Signup.GraceDays).ReadOnly;

    // The cached value was computed at fetch time; a trial can end inside the cache window, so the dates are checked again.
    private static HostedEntitlement? Reevaluate(HostedEntitlement? value, DateTime now) =>
        value is { ReadOnly: false, Trial: true, EndsUtc: { } ends } && ends < now
            ? value with { ReadOnly = true, Message = TrialEnded(ends) }
            : value;

    public async Task<TenantEntitlement?> FindAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await catalog.CreateDbContextAsync(cancellationToken);
        return await db.Entitlements.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, TenantEntitlement>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await catalog.CreateDbContextAsync(cancellationToken);
        return (await db.Entitlements.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(e => e.TenantId);
    }

    /// <summary>
    /// Applies <paramref name="request"/> (missing fields stay as they are), creating the entitlement when the tenant has none.
    /// An update whose <see cref="SetEntitlementRequest.EventAtUtc"/> is older than the last one applied is ignored (returns the
    /// current state), so the license server can retry and redeliver freely.
    /// </summary>
    public async Task<TenantEntitlement> ApplyAsync(Guid tenantId, SetEntitlementRequest request, string actor, string source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await catalog.CreateDbContextAsync(cancellationToken);
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw Contracts.WorkbenchNotFoundException.For("Tenant", tenantId);
        var row = await db.Entitlements.FirstOrDefaultAsync(e => e.TenantId == tenantId, cancellationToken);
        var isNew = row is null;
        if (row is not null && request.EventAtUtc is { } at && row.SourceEventAtUtc is { } last && at < last)
        {
            return row;
        }

        row ??= db.Entitlements.Add(new TenantEntitlement { TenantId = tenantId, Source = source, UpdatedAtUtc = now }).Entity;
        var before = Describe(row);

        if (request.Plan is { } plan)
        {
            row.Plan = Clean(plan, 64) ?? row.Plan;
        }

        if (request.Modules is { } modules)
        {
            row.Modules = string.Join(',', NormalizeModules(modules));
        }

        if (request.Seats is { } seats)
        {
            row.Seats = Math.Clamp(seats, 0, 100_000);
        }

        if (request.LightSeats is { } light)
        {
            row.LightSeats = Math.Clamp(light, 0, 100_000);
        }

        if (request.Status is { } status)
        {
            var parsed = ParseStatus(status);
            if (parsed == EntitlementStatus.PastDue && row.Status != EntitlementStatus.PastDue)
            {
                row.PastDueSinceUtc = now;
            }

            if (parsed == EntitlementStatus.Cancelled && row.Status != EntitlementStatus.Cancelled)
            {
                row.CancelledAtUtc = now;
            }

            if (parsed is EntitlementStatus.Active)
            {
                row.PastDueSinceUtc = null;
                row.CancelledAtUtc = null;
            }

            row.Status = parsed;
        }

        if (request.Trial is { } trial)
        {
            row.IsTrial = trial;
        }

        if (request.TrialEndsUtc is { } trialEnd)
        {
            row.TrialEndsUtc = DateTime.SpecifyKind(trialEnd, DateTimeKind.Utc);
        }

        if (request.PaidThroughUtc is { } paid)
        {
            row.PaidThroughUtc = DateTime.SpecifyKind(paid, DateTimeKind.Utc);
        }

        if (request.ExternalRef is { } reference)
        {
            row.ExternalRef = Clean(reference, 128);
        }

        row.SourceEventAtUtc = request.EventAtUtc ?? row.SourceEventAtUtc;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = Clean(actor, 320);
        if (!isNew)
        {
            row.Source = source;
        }

        var after = Describe(row);
        if (isNew || before != after)
        {
            db.HostAuditEvents.Add(new HostAuditEvent
            {
                AtUtc = now,
                Actor = actor,
                Action = isNew ? "entitlement.create" : "entitlement.update",
                TenantId = tenantId,
                Detail = Truncate($"{tenant.Slug}: {(isNew ? after : $"{before} -> {after}")}{(string.IsNullOrWhiteSpace(request.Reason) ? "" : $" ({request.Reason.Trim()})")} via {source}", 1000),
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        _cache.TryRemove(tenantId, out _);
        return row;
    }

    /// <summary>Moves a trial's end later by <paramref name="days"/> (counted from now when it has already ended) and puts an active trial back to Active.</summary>
    public async Task<TenantEntitlement> ExtendTrialAsync(Guid tenantId, int days, string actor, string? reason, CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 365)
        {
            throw new ArgumentException("Extend a trial by 1 to 365 days.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var current = await FindAsync(tenantId, cancellationToken);
        var from = current?.TrialEndsUtc is { } end && end > now ? end : now;
        return await ApplyAsync(tenantId, new SetEntitlementRequest(Trial: true, TrialEndsUtc: from.AddDays(days), Status: "active", Reason: $"trial extended {days} days{(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason.Trim()}")}"),
            actor, "host", cancellationToken);
    }

    /// <summary>What the product sees: read-only for a lapsed trial, a cancelled or suspended subscription, or a term that ended more than the grace days ago.</summary>
    public static HostedEntitlement Evaluate(TenantEntitlement e, DateTime now, int graceDays)
    {
        var modules = Modules(e);
        var plan = e.Plan;
        if (e.Status == EntitlementStatus.Cancelled)
        {
            return new HostedEntitlement(plan, modules, e.Seats, e.LightSeats, e.IsTrial, e.CancelledAtUtc, true, false,
                "This organization's subscription has been cancelled, so it is read-only. Your data is untouched and an administrator can export it. Contact your Workbench provider to resume.");
        }

        if (e.Status == EntitlementStatus.Suspended)
        {
            return new HostedEntitlement(plan, modules, e.Seats, e.LightSeats, e.IsTrial, e.TrialEndsUtc ?? e.PaidThroughUtc, true, false,
                "This organization's subscription is suspended, so it is read-only. Your data is untouched and an administrator can export it. Contact your Workbench provider.");
        }

        if (e.IsTrial)
        {
            if (e.TrialEndsUtc is { } ends && ends < now)
            {
                return new HostedEntitlement(plan, modules, e.Seats, e.LightSeats, true, ends, true, false, TrialEnded(ends));
            }

            return new HostedEntitlement(plan, modules, e.Seats, e.LightSeats, true, e.TrialEndsUtc, false, e.Status == EntitlementStatus.PastDue, null);
        }

        var lastGood = e.PaidThroughUtc;
        if (e.Status == EntitlementStatus.PastDue || (lastGood is { } paid && paid < now))
        {
            var since = e.PastDueSinceUtc ?? lastGood ?? now;
            var graceEnds = (lastGood is { } p && p > since ? p : since).AddDays(graceDays);
            if (now > graceEnds)
            {
                return new HostedEntitlement(plan, modules, e.Seats, e.LightSeats, false, lastGood, true, false,
                    "Payment for this organization is overdue, so it is read-only. Your data is untouched and an administrator can export it. Update the payment method to resume.");
            }

            return new HostedEntitlement(plan, modules, e.Seats, e.LightSeats, false, lastGood, false, true,
                $"Payment for this organization is overdue. Update the payment method by {graceEnds:d MMM yyyy} to keep full access; after that it becomes read-only.");
        }

        return new HostedEntitlement(plan, modules, e.Seats, e.LightSeats, false, lastGood, false, false, null);
    }

    public static string TrialEnded(DateTime ends) =>
        $"The trial ended on {ends:d MMM yyyy}, so this organization is read-only. Your data is untouched and an administrator can export it. Ask your Workbench provider to subscribe or extend the trial.";

    public static IReadOnlySet<string> Modules(TenantEntitlement e) =>
        new HashSet<string>(e.Modules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { ModuleIds.Core };

    public static IReadOnlyList<string> NormalizeModules(IEnumerable<string> modules) =>
        modules.Select(m => m.Trim().ToLowerInvariant()).Where(m => m.Length > 0 && m != ModuleIds.Core && ModuleCatalog.IsKnown(m)).Distinct().Order().ToList();

    public static EntitlementStatus ParseStatus(string status) => status.Trim().ToLowerInvariant().Replace("-", "_") switch
    {
        "active" => EntitlementStatus.Active,
        "past_due" or "pastdue" => EntitlementStatus.PastDue,
        "suspended" => EntitlementStatus.Suspended,
        "cancelled" or "canceled" => EntitlementStatus.Cancelled,
        _ => throw new ArgumentException("Status must be active, past_due, suspended or cancelled."),
    };

    public static string StatusName(EntitlementStatus status) => status switch
    {
        EntitlementStatus.PastDue => "past_due",
        EntitlementStatus.Suspended => "suspended",
        EntitlementStatus.Cancelled => "cancelled",
        _ => "active",
    };

    /// <summary>The host-facing view of a row. <paramref name="seats"/> is what the tenant's database says is in use, when known.</summary>
    public HostEntitlementDto ToDto(TenantEntitlement e, (int Full, int Light)? seats = null)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var view = Evaluate(e, now, tenancy.Signup.GraceDays);
        DateTime? retention = view.ReadOnly ? (e.CancelledAtUtc ?? e.TrialEndsUtc ?? e.PaidThroughUtc ?? e.UpdatedAtUtc).AddDays(tenancy.Signup.RetentionDays) : null;
        return new HostEntitlementDto(e.Plan, Modules(e).Where(m => m != ModuleIds.Core).Order().ToList(), e.Seats, e.LightSeats, StatusName(e.Status), e.IsTrial, e.TrialEndsUtc, e.PaidThroughUtc,
            view.ReadOnly, e.Source, e.ExternalRef, e.UpdatedAtUtc, e.UpdatedBy, seats?.Full, seats?.Light, retention, e.SignupEmail, e.SignupSource, e.TermsAcceptedAtUtc);
    }

    private static string Describe(TenantEntitlement e) =>
        $"{StatusName(e.Status)} {e.Plan}{(e.IsTrial && e.Plan != "trial" ? " (trial)" : "")} modules [{e.Modules}] seats {e.Seats}+{e.LightSeats} light, trial ends {e.TrialEndsUtc:yyyy-MM-dd}, paid through {e.PaidThroughUtc:yyyy-MM-dd}";

    private static string? Clean(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Truncate(trimmed, max);
    }

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}
