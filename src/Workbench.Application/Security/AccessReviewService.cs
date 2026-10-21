using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;

namespace Workbench.Application.Security;

/// <summary>
/// Quarterly access reviews (A.5.18). Starting one snapshots each active user's org roles, workspace roles, last
/// sign-in and linked employee, so the evidence shows what the reviewer saw. Revoke deactivates the user at once.
/// </summary>
public sealed class AccessReviewService(
    IWorkbenchDbContextFactory dbFactory,
    AccessControl access,
    UserLifecycle lifecycle,
    SecurityEventLog securityLog,
    TimeProvider clock) : IAccessReviewService
{
    /// <summary>Users with no sign-in for this long are flagged as dormant.</summary>
    public static readonly TimeSpan DormantAfter = TimeSpan.FromDays(90);

    public async Task<IReadOnlyList<AccessReviewSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminSecurity, "run access reviews", cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var reviews = await db.Set<AccessReview>().AsNoTracking().ToListAsync(cancellationToken);
        var counts = await db.Set<AccessReviewItem>().AsNoTracking()
            .GroupBy(i => i.AccessReviewId)
            .Select(g => new
            {
                Id = g.Key,
                Total = g.Count(),
                Decided = g.Count(i => i.Decision != AccessReviewDecision.Pending),
                Revoked = g.Count(i => i.Decision == AccessReviewDecision.Revoke),
            })
            .ToListAsync(cancellationToken);
        var byId = counts.ToDictionary(c => c.Id);
        return reviews
            .OrderByDescending(r => r.StartedAtUtc)
            .Select(r => byId.TryGetValue(r.Id, out var c) ? Summary(r, c.Total, c.Decided, c.Revoked) : Summary(r, 0, 0, 0))
            .ToList();
    }

    public async Task<AccessReviewDto> GetAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminSecurity, "run access reviews", cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await LoadDtoAsync(db, reviewId, cancellationToken);
    }

    public async Task<AccessReviewDto> StartAsync(string? name = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminSecurity, "run access reviews", cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<AccessReview>().AnyAsync(r => r.Status == AccessReviewStatus.InProgress, cancellationToken))
        {
            throw new ArgumentException("Finish the access review in progress before starting another.", nameof(name));
        }

        var review = new AccessReview
        {
            Name = UserLifecycle.CleanName(name) ?? DefaultName(now),
            Status = AccessReviewStatus.InProgress,
            StartedBy = access.Email,
            StartedAtUtc = now,
        };

        var users = await db.Set<AppUser>().AsNoTracking().Where(u => u.IsActive).ToListAsync(cancellationToken);
        var memberships = await db.Set<WorkspaceMember>().AsNoTracking()
            .Join(db.Set<Workspace>(), m => m.WorkspaceId, w => w.Id, (m, w) => new { m.UserEmail, Workspace = w.Name, m.Role })
            .ToListAsync(cancellationToken);
        var byUser = memberships.GroupBy(m => m.UserEmail).ToDictionary(g => g.Key, g => string.Join("; ", g.OrderBy(m => m.Workspace).Select(m => $"{m.Workspace}: {m.Role}")));
        var since = await PeriodStartAsync(db, now, cancellationToken);
        var changes = await RoleChangesByUserAsync(db, since, cancellationToken);
        foreach (var user in users.OrderBy(u => u.DisplayName))
        {
            var employee = await EmployeeLinks.FindForUserAsync(db, user.Email, cancellationToken);
            var lastSignIn = user.LastSignInAtUtc ?? user.LastSeenAtUtc;
            var held = await RoleStore.ResolveAsync(db, user.Email, cancellationToken);
            review.Items.Add(new AccessReviewItem
            {
                AccessReviewId = review.Id,
                UserEmail = user.Email,
                DisplayName = user.DisplayName,
                OrgRoles = Clip(RoleStore.Describe(await RoleStore.WithScopeTextAsync(db, held.Roles, cancellationToken)), 1000),
                Permissions = Clip(string.Join(", ", PermissionCatalog.All.Where(p => held.Permissions.Has(p.Key)).Select(p => p.Key)), 4000),
                RoleChanges = changes.TryGetValue(user.Id, out var c) ? Clip(string.Join("; ", c), 4000) : null,
                WorkspaceRoles = held.Permissions.Has(Permission.AdminAllWorkspaces)
                    ? held.BuiltIn.HasFlag(OrgRoles.Admin) ? "All workspaces (Admin)" : "All workspaces (Own every workspace)"
                    : byUser.GetValueOrDefault(user.Email) ?? "None",
                LastSignInAtUtc = lastSignIn,
                EmployeeName = employee is null ? null : $"{employee.EmployeeNumber} {employee.DisplayName}",
                Dormant = lastSignIn is null || now - lastSignIn.Value > DormantAfter,
            });
        }

        db.Set<AccessReview>().Add(review);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.AccessReviewStarted, review.Name, detail: $"{review.Items.Count} active users", cancellationToken: cancellationToken);
        return await LoadDtoAsync(db, review.Id, cancellationToken);
    }

    public async Task<AccessReviewItemDto> DecideAsync(Guid reviewId, Guid itemId, AccessReviewDecisionRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminSecurity, "run access reviews", cancellationToken);
        var comment = request.Comment?.Trim();
        if (request.Decision == AccessReviewDecisionKind.Revoke && string.IsNullOrEmpty(comment))
        {
            throw new ArgumentException("Say why access is revoked.", nameof(request));
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var review = await db.Set<AccessReview>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == reviewId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Access review", reviewId);
        if (review.Status == AccessReviewStatus.Completed)
        {
            throw new ArgumentException("This access review is completed and can't be changed.", nameof(reviewId));
        }

        var item = await db.Set<AccessReviewItem>().FirstOrDefaultAsync(i => i.Id == itemId && i.AccessReviewId == reviewId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Access review item", itemId);
        if (item.AppliedAtUtc is not null && request.Decision != AccessReviewDecisionKind.Revoke)
        {
            throw new ArgumentException("Access was already revoked. Reactivate the user from the Users tab if that was a mistake.", nameof(request));
        }

        if (request.Decision == AccessReviewDecisionKind.Revoke && item.AppliedAtUtc is null)
        {
            // Applies at once; the lifecycle refuses self-deactivation and removing the last active admin.
            await lifecycle.SetActiveAsync(item.UserEmail, false, access.Email, $"Access review {review.Name}: {comment}", cancellationToken);
            item.AppliedAtUtc = clock.GetUtcNow().UtcDateTime;
        }

        item.Decision = (AccessReviewDecision)(int)request.Decision;
        item.Comment = string.IsNullOrEmpty(comment) ? null : comment;
        item.DecidedBy = access.Email;
        item.DecidedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.AccessReviewDecision, item.UserEmail,
            detail: $"{review.Name}: {item.Decision}{(item.Comment is null ? "" : $" ({item.Comment})")}", cancellationToken: cancellationToken);
        return ToDto(item);
    }

    public async Task<AccessReviewDto> CompleteAsync(Guid reviewId, string? notes = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminSecurity, "run access reviews", cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var review = await db.Set<AccessReview>().FirstOrDefaultAsync(r => r.Id == reviewId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Access review", reviewId);
        if (review.Status == AccessReviewStatus.Completed)
        {
            throw new ArgumentException("This access review is already completed.", nameof(reviewId));
        }

        var pending = await db.Set<AccessReviewItem>().CountAsync(i => i.AccessReviewId == reviewId && i.Decision == AccessReviewDecision.Pending, cancellationToken);
        if (pending > 0)
        {
            throw new ArgumentException($"{pending} user(s) still need a Keep or Revoke decision.", nameof(reviewId));
        }

        review.Status = AccessReviewStatus.Completed;
        review.CompletedBy = access.Email;
        review.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
        review.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        await db.SaveChangesAsync(cancellationToken);
        var dto = await LoadDtoAsync(db, reviewId, cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.AccessReviewCompleted, review.Name,
            detail: $"{dto.Summary.UserCount} users reviewed, {dto.Summary.Revoked} revoked", cancellationToken: cancellationToken);
        return dto;
    }

    internal static string DefaultName(DateTime now) =>
        $"{now.Year.ToString(CultureInfo.InvariantCulture)} Q{(now.Month - 1) / 3 + 1}";

    private static async Task<AccessReviewDto> LoadDtoAsync(IWorkbenchDbContext db, Guid reviewId, CancellationToken cancellationToken)
    {
        var review = await db.Set<AccessReview>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == reviewId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Access review", reviewId);
        var items = await db.Set<AccessReviewItem>().AsNoTracking().Where(i => i.AccessReviewId == reviewId).ToListAsync(cancellationToken);
        var summary = Summary(review, items.Count, items.Count(i => i.Decision != AccessReviewDecision.Pending), items.Count(i => i.Decision == AccessReviewDecision.Revoke));
        var since = await PeriodStartAsync(db, review.StartedAtUtc, cancellationToken);
        var until = review.CompletedAtUtc ?? DateTime.MaxValue;
        var roleEvents = await db.Set<AuditEvent>().AsNoTracking()
            .Where(a => a.EntityType == RoleStore.AuditRole && a.OccurredAtUtc >= since && a.OccurredAtUtc <= until)
            .OrderBy(a => a.OccurredAtUtc)
            .ToListAsync(cancellationToken);
        var roleChanges = roleEvents.Select(a => $"{a.OccurredAtUtc:yyyy-MM-dd HH:mm} UTC, {a.Actor}: {a.Action} {RoleName(a)}").ToList();
        return new AccessReviewDto(summary, review.Notes, items.OrderByDescending(i => i.Dormant).ThenBy(i => i.DisplayName).Select(ToDto).ToList(), roleChanges);
    }

    /// <summary>Where the review period starts: the previous review's start, at most 90 days back.</summary>
    private static async Task<DateTime> PeriodStartAsync(IWorkbenchDbContext db, DateTime reviewStart, CancellationToken ct)
    {
        var previous = await db.Set<AccessReview>().AsNoTracking().Where(r => r.StartedAtUtc < reviewStart)
            .OrderByDescending(r => r.StartedAtUtc).Select(r => (DateTime?)r.StartedAtUtc).FirstOrDefaultAsync(ct);
        var floor = reviewStart - DormantAfter;
        return previous is { } p && p > floor ? p : floor;
    }

    /// <summary>Each user's role assignment changes since <paramref name="since"/>, from the audit log.</summary>
    private static async Task<Dictionary<Guid, List<string>>> RoleChangesByUserAsync(IWorkbenchDbContext db, DateTime since, CancellationToken ct)
    {
        var events = await db.Set<AuditEvent>().AsNoTracking()
            .Where(a => a.EntityType == RoleStore.AuditUserRoles && a.OccurredAtUtc >= since)
            .OrderBy(a => a.OccurredAtUtc)
            .ToListAsync(ct);
        return events.GroupBy(a => a.EntityId).ToDictionary(
            g => g.Key,
            g => g.Select(a => $"{a.OccurredAtUtc:yyyy-MM-dd} by {a.Actor}: {Roles(a.BeforeJson)} → {Roles(a.AfterJson)}").ToList());
    }

    private static string Roles(string? json)
    {
        try
        {
            var names = json is null ? [] : System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("roles").EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList();
            return names.Count == 0 ? "Employee" : string.Join(", ", names);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "?";
        }
    }

    private static string RoleName(AuditEvent a)
    {
        try
        {
            var json = a.AfterJson ?? a.BeforeJson;
            return json is null ? "" : $"role \"{System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("name").GetString()}\"";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "a role";
        }
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static AccessReviewSummaryDto Summary(AccessReview r, int total, int decided, int revoked) =>
        new(r.Id, r.Name, r.Status == AccessReviewStatus.Completed, r.StartedBy, r.StartedAtUtc, r.CompletedBy, r.CompletedAtUtc, total, decided, revoked);

    private static AccessReviewItemDto ToDto(AccessReviewItem i) =>
        new(i.Id, i.UserEmail, i.DisplayName, i.OrgRoles, i.WorkspaceRoles, i.LastSignInAtUtc, i.EmployeeName, i.Dormant,
            (AccessReviewDecisionKind)(int)i.Decision, i.Comment, i.DecidedBy, i.DecidedAtUtc, i.AppliedAtUtc, i.Permissions, i.RoleChanges);
}
