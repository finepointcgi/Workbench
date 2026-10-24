using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Identity;
using Workbench.Domain.Notifications;
using DomainKind = Workbench.Domain.Notifications.EmailIssueKind;

namespace Workbench.Application.Notifications;

/// <summary>A bounce, complaint or failed send the mail provider reported for one address.</summary>
public sealed record EmailDeliveryEvent(string Address, DomainKind Kind, string Provider, string? Detail, DateTime OccurredAtUtc);

/// <summary>
/// Keeps <see cref="EmailAddressIssue"/> rows in the current tenant's database: records provider events and answers
/// whether non-essential mail to an address should stop. Events older than the last time someone cleared the address
/// are ignored, so a replayed or late webhook can't undo a fix.
/// </summary>
public sealed class EmailDeliverabilityRecorder(IWorkbenchDbContextFactory dbFactory)
{
    public const int DetailMaxLength = 500;

    /// <summary>Records the events (several for one address collapse into one row) and returns how many changed a row.</summary>
    public async Task<int> RecordAsync(IReadOnlyCollection<EmailDeliveryEvent> events, CancellationToken cancellationToken = default)
    {
        var byAddress = events
            .Select(e => e with { Address = Normalize(e.Address) ?? "" })
            .Where(e => e.Address.Length > 0)
            .GroupBy(e => e.Address)
            .ToList();
        if (byAddress.Count == 0)
        {
            return 0;
        }

        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var addresses = byAddress.Select(g => g.Key).ToList();
            var rows = await db.Set<EmailAddressIssue>().Where(i => addresses.Contains(i.Address)).ToDictionaryAsync(i => i.Address, cancellationToken);
            var changed = 0;
            foreach (var group in byAddress)
            {
                rows.TryGetValue(group.Key, out var row);
                foreach (var e in group.OrderBy(e => e.OccurredAtUtc))
                {
                    if (Apply(db, ref row, e))
                    {
                        changed++;
                    }
                }

                if (row is not null)
                {
                    rows[group.Key] = row;
                }
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return changed;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Another server added the same address at the same moment (unique index): read it and apply again.
            }
        }
    }

    /// <summary>True while non-essential mail to <paramref name="address"/> is held back (an open bounce or complaint).</summary>
    public async Task<bool> IsSuppressedAsync(string address, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(address);
        if (normalized is null)
        {
            return false;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<EmailAddressIssue>().AsNoTracking()
            .AnyAsync(i => i.Address == normalized && i.ClearedAtUtc == null && i.Kind != DomainKind.SendFailure, cancellationToken);
    }

    public static string? Normalize(string? address)
    {
        var trimmed = (address ?? "").Trim().ToLowerInvariant();
        return trimmed.Length is > 2 and <= 320 && trimmed.IndexOf('@') > 0 ? trimmed : null;
    }

    private static bool Apply(IWorkbenchDbContext db, ref EmailAddressIssue? row, EmailDeliveryEvent e)
    {
        var detail = Clean(e.Detail);
        if (row is null)
        {
            row = new EmailAddressIssue
            {
                Address = e.Address, Kind = e.Kind, Provider = e.Provider, Detail = detail, FirstAtUtc = e.OccurredAtUtc, LastAtUtc = e.OccurredAtUtc, EventCount = 1,
            };
            db.Set<EmailAddressIssue>().Add(row);
            return true;
        }

        if (row.ClearedAtUtc is { } cleared)
        {
            if (e.OccurredAtUtc <= cleared)
            {
                return false;
            }

            // A new problem after someone cleared it: open it again from scratch.
            row.ClearedAtUtc = null;
            row.ClearedBy = null;
            row.Kind = e.Kind;
            row.FirstAtUtc = e.OccurredAtUtc;
            row.EventCount = 0;
        }

        row.Kind = e.Kind > row.Kind ? e.Kind : row.Kind;
        row.Provider = e.Provider;
        row.Detail = detail ?? row.Detail;
        row.LastAtUtc = e.OccurredAtUtc > row.LastAtUtc ? e.OccurredAtUtc : row.LastAtUtc;
        row.EventCount++;
        return true;
    }

    private static string? Clean(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var text = new string(detail.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();
        return text.Length <= DetailMaxLength ? text : text[..DetailMaxLength];
    }
}

/// <summary>The admin list, the person's own notice, and clearing an address once its mailbox works again.</summary>
public sealed class EmailDeliverabilityService(IWorkbenchDbContextFactory dbFactory, AccessControl access, ICurrentUser currentUser, TimeProvider clock)
    : IEmailDeliverabilityService
{
    public async Task<IReadOnlyList<EmailAddressIssueDto>> ListAsync(bool includeCleared = false, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(IsAdmin, "You need the \"Administer users\" or \"Manage sign-in and security\" permission to see bouncing email addresses.", cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<EmailAddressIssue>().AsNoTracking();
        if (!includeCleared)
        {
            query = query.Where(i => i.ClearedAtUtc == null);
        }

        var rows = await query.ToListAsync(cancellationToken);
        var addresses = rows.Select(r => r.Address).ToList();
        var names = await db.Set<AppUser>().AsNoTracking().Where(u => addresses.Contains(u.Email))
            .ToDictionaryAsync(u => u.Email, u => u.DisplayName, cancellationToken);
        return rows
            .OrderBy(r => r.IsOpen ? 0 : 1).ThenByDescending(r => r.Kind).ThenByDescending(r => r.LastAtUtc)
            .Select(r => ToDto(r, names.GetValueOrDefault(r.Address)))
            .ToList();
    }

    public async Task<EmailAddressIssueDto?> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var me = EmailDeliverabilityRecorder.Normalize(currentUser.Email);
        if (me is null)
        {
            return null;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<EmailAddressIssue>().AsNoTracking().FirstOrDefaultAsync(i => i.Address == me && i.ClearedAtUtc == null, cancellationToken);
        return row is null ? null : ToDto(row, currentUser.DisplayName);
    }

    public async Task ClearAsync(string address, CancellationToken cancellationToken = default)
    {
        var normalized = EmailDeliverabilityRecorder.Normalize(address) ?? throw new ArgumentException("That isn't an email address.", nameof(address));
        if (normalized != EmailDeliverabilityRecorder.Normalize(currentUser.Email))
        {
            await access.RequireAsync(IsAdmin, "You need the \"Administer users\" or \"Manage sign-in and security\" permission to clear another person's email address.", cancellationToken);
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<EmailAddressIssue>().FirstOrDefaultAsync(i => i.Address == normalized && i.ClearedAtUtc == null, cancellationToken)
            ?? throw new WorkbenchNotFoundException($"{normalized} has no open email problem.");
        row.ClearedAtUtc = clock.GetUtcNow().UtcDateTime;
        row.ClearedBy = currentUser.Email;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsAdmin(PermissionSet permissions) => permissions.Has(Permission.AdminUsers) || permissions.Has(Permission.AdminSecurity);

    private static EmailAddressIssueDto ToDto(EmailAddressIssue r, string? userName) => new(
        r.Address, (Contracts.Notifications.EmailIssueKind)r.Kind, r.Provider, r.Detail, r.FirstAtUtc, r.LastAtUtc, r.EventCount, r.IsSuppressed,
        r.ClearedAtUtc, r.ClearedBy, userName);
}
