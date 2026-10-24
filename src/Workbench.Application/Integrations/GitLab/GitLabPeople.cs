using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Tenancy;
using Workbench.Domain;
using Workbench.Domain.Identity;
using Workbench.Domain.Integrations.GitLab;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Integrations.GitLab;

/// <summary>
/// Matches Workbench people to GitLab users: by email first (the GitLab user's email, visible to an administrator's token,
/// or their public email), then by GitLab user name when it is the email's local part and the names agree. Anything else
/// waits for someone to match it on the mapping screen. Misses are remembered for an hour so a sync doesn't search again.
/// </summary>
public sealed class GitLabPeople(IWorkbenchDbContextFactory dbFactory, TimeProvider clock)
{
    private static readonly TimeSpan MissFor = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _misses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tries to match <paramref name="emails"/> and <paramref name="gitLabUsers"/>; returns how many matches were added.</summary>
    public async Task<int> MatchAsync(
        GitLabClient client, IReadOnlyCollection<string> emails, IReadOnlyCollection<GitLabUser> gitLabUsers, IReadOnlyDictionary<string, string> names, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var maps = await db.Set<GitLabUserMap>().AsNoTracking().ToListAsync(ct);
        var mappedEmails = maps.Select(m => m.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappedIds = maps.Select(m => m.GitLabUserId).ToHashSet();
        var added = new List<GitLabUserMap>();

        void Add(string email, GitLabUser user, string by)
        {
            email = email.Trim().ToLowerInvariant();
            if (mappedEmails.Contains(email) || mappedIds.Contains(user.Id))
            {
                return;
            }

            mappedEmails.Add(email);
            mappedIds.Add(user.Id);
            added.Add(new GitLabUserMap
            {
                Email = email, GitLabUserId = user.Id, GitLabUsername = user.Username, GitLabName = user.Name, MatchedBy = by,
                CreatedBy = GitLabSyncEngine.Actor, CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            });
        }

        foreach (var email in emails.Where(e => !mappedEmails.Contains(e) && !RecentlyMissed("e|" + e)))
        {
            try
            {
                var found = (await client.SearchUsersAsync(email, ct))
                    .FirstOrDefault(u => Same(u.Email, email) || Same(u.PublicEmail, email));
                if (found is not null)
                {
                    Add(email, found, "email");
                    continue;
                }

                var local = email.Split('@')[0];
                var byName = await client.FindUserByUsernameAsync(local, ct);
                if (byName is not null && NamesAgree(byName.Name, names.GetValueOrDefault(email)))
                {
                    Add(email, byName, "username");
                    continue;
                }
            }
            catch (GitLabApiException ex) when (!ex.IsRetryable)
            {
                // A token that can't search users: leave it to the mapping screen.
            }

            Miss("e|" + email);
        }

        var unmatchedUsers = gitLabUsers.Where(u => !mappedIds.Contains(u.Id) && !RecentlyMissed($"u|{u.Id}")).ToList();
        if (unmatchedUsers.Count > 0)
        {
            var known = (await db.Set<AppUser>().AsNoTracking().Select(u => new { u.Email, u.DisplayName }).ToListAsync(ct))
                .Concat(await db.Set<Employee>().AsNoTracking().Select(e => new { e.Email, e.DisplayName }).ToListAsync(ct))
                .GroupBy(p => p.Email.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.OrdinalIgnoreCase);
            foreach (var user in unmatchedUsers)
            {
                var email = new[] { user.Email, user.PublicEmail }.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e) && known.ContainsKey(e!.Trim()));
                if (email is not null)
                {
                    Add(email, user, "email");
                    continue;
                }

                var byName = known.FirstOrDefault(k => string.Equals(k.Key.Split('@')[0], user.Username, StringComparison.OrdinalIgnoreCase) && NamesAgree(user.Name, k.Value));
                if (byName.Key is not null)
                {
                    Add(byName.Key, user, "username");
                    continue;
                }

                Miss($"u|{user.Id}");
            }
        }

        if (added.Count == 0)
        {
            return 0;
        }

        db.Set<GitLabUserMap>().AddRange(added);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return 0; // matched at the same time elsewhere
        }

        return added.Count;
    }

    /// <summary>Forgets misses (after someone matches people by hand, or for tests).</summary>
    public void Forget() => _misses.Clear();

    internal static bool NamesAgree(string? a, string? b)
    {
        static string Norm(string? s) => new((s ?? "").Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
        var x = Norm(a);
        var y = Norm(b);
        return x.Length > 1 && x == y;
    }

    private static bool Same(string? a, string b) => a is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private bool RecentlyMissed(string key) => _misses.TryGetValue(TenantScope.Qualify(key), out var until) && until > clock.GetUtcNow();

    private void Miss(string key) => _misses[TenantScope.Qualify(key)] = clock.GetUtcNow() + MissFor;
}

/// <summary>The plan columns the sync reads and writes.</summary>
public static class GitLabColumns
{
    public const string NotesName = "Notes";
    public const string PercentName = "% Complete";

    /// <summary>The plan's notes column: a plain text column named Notes or Description.</summary>
    public static Column? Notes(IEnumerable<Column> columns) =>
        columns.Where(c => c.Type == ColumnType.Text && c.Formula is null)
            .FirstOrDefault(c => string.Equals(c.Name, NotesName, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Name, "Description", StringComparison.OrdinalIgnoreCase));

    /// <summary>The plan's % complete: the number column the schedule rolls up (<see cref="Tasks.TaskRules.PercentColumn"/>), else a Progress column.</summary>
    public static Column? Percent(IEnumerable<Column> columns)
    {
        var list = columns.ToList();
        return Tasks.TaskRules.PercentColumn(list) ?? list.FirstOrDefault(c => c.Type == ColumnType.Progress && c.Formula is null);
    }

    /// <summary>Adds "% Complete" and "Notes" to the plan when missing (progress and the issue description need a home). Returns the names added.</summary>
    public static async Task<IReadOnlyList<string>> EnsureAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken ct)
    {
        var columns = await db.Columns.Where(c => c.SheetId == sheetId).ToListAsync(ct);
        var order = columns.Select(c => c.Order).DefaultIfEmpty(-1).Max();
        var added = new List<string>();
        if (Percent(columns) is null)
        {
            db.Columns.Add(new Column { SheetId = sheetId, Name = PercentName, Type = ColumnType.Number, Order = ++order });
            added.Add(PercentName);
        }

        if (Notes(columns) is null)
        {
            db.Columns.Add(new Column { SheetId = sheetId, Name = NotesName, Type = ColumnType.Text, Order = ++order });
            added.Add(NotesName);
        }

        if (added.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return added;
    }
}
