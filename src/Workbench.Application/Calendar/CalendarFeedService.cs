using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.Tasks;
using Workbench.Application.Time;
using Workbench.Contracts.Calendar;
using Workbench.Domain;
using Workbench.Domain.Calendar;
using Workbench.Domain.Leave;
using Workbench.Domain.Tasks;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Calendar;

/// <summary>
/// A person's private calendar feed: turn it on, get a new address, turn it off. The address is
/// <c>calendar/{token}.ics</c> on the app's public address; the token is 256 random bits, kept as a hash for lookup and encrypted so
/// the owner can copy the link again.
/// </summary>
public sealed class CalendarFeedService(
    IWorkbenchDbContextFactory dbFactory,
    AccessControl access,
    IDataProtectionProvider dataProtection,
    NotificationOptions options,
    TimeProvider clock) : ICalendarFeedService
{
    public const string PathPrefix = "calendar/";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("Workbench.Calendar.FeedToken.v1");

    public async Task<CalendarFeedDto> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var me = Me();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<CalendarFeedToken>().AsNoTracking().FirstOrDefaultAsync(t => t.UserEmail == me, cancellationToken);
        return row is null ? new CalendarFeedDto(false, null) : new CalendarFeedDto(true, UrlFor(_protector.Unprotect(row.TokenProtected)));
    }

    public async Task<CalendarFeedDto> EnableAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetMineAsync(cancellationToken);
        return current.Enabled ? current : await RotateAsync(cancellationToken);
    }

    public async Task<CalendarFeedDto> RotateAsync(CancellationToken cancellationToken = default)
    {
        var me = Me();
        var token = NewToken();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<CalendarFeedToken>().FirstOrDefaultAsync(t => t.UserEmail == me, cancellationToken);
        if (row is null)
        {
            row = new CalendarFeedToken { UserEmail = me, TokenHash = "", TokenProtected = "" };
            db.Set<CalendarFeedToken>().Add(row);
        }

        row.TokenHash = Hash(token);
        row.TokenProtected = _protector.Protect(token);
        row.CreatedAtUtc = clock.GetUtcNow().UtcDateTime;
        row.LastFetchedAtUtc = null;
        await db.SaveChangesAsync(cancellationToken);
        return new CalendarFeedDto(true, UrlFor(token));
    }

    public async Task RevokeAsync(CancellationToken cancellationToken = default)
    {
        var me = Me();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await db.Set<CalendarFeedToken>().Where(t => t.UserEmail == me).ExecuteDeleteAsync(cancellationToken);
    }

    internal static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private string UrlFor(string token)
    {
        var path = $"{PathPrefix}{token}.ics";
        return NotificationEmails.AbsoluteLink(path, options) ?? path;
    }

    private string Me()
    {
        access.RequireSignedIn();
        return access.Email;
    }
}

/// <summary>
/// Builds the feed behind a token, without a signed-in user (calendar apps can't sign in): the token names the person, and the feed holds
/// only what is theirs. It carries approved leave, company holidays, tasks assigned to them with a due date, milestones they own (assigned,
/// or on a project they manage) and the next two timesheet due dates. Entries cover the last 30 days and the next year.
/// </summary>
public sealed class CalendarFeedBuilder(IWorkbenchDbContextFactory dbFactory, NotificationOptions options, TimekeepingClock timekeeping)
{
    public const int DaysBack = 30;
    public const int DaysAhead = 365;

    /// <summary>The feed for a token, or null when the token is unknown, revoked or its owner is deactivated.</summary>
    public async Task<string?> BuildAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 20 or > 100)
        {
            return null;
        }

        var hash = CalendarFeedService.Hash(token);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<CalendarFeedToken>().FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (row is null || !await db.Set<Domain.Identity.AppUser>().AnyAsync(u => u.Email == row.UserEmail && u.IsActive, cancellationToken))
        {
            return null;
        }

        row.LastFetchedAtUtc = timekeeping.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var today = timekeeping.Today;
        var from = today.AddDays(-DaysBack);
        var to = today.AddDays(DaysAhead);
        var me = row.UserEmail;
        var entries = new List<CalendarEntry>();
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Email == me, cancellationToken);

        // Company holidays.
        foreach (var h in await db.Set<Holiday>().AsNoTracking().Where(h => h.Date >= from && h.Date <= to).ToListAsync(cancellationToken))
        {
            entries.Add(new($"holiday-{h.Id}@workbench", h.Date, h.Date, $"Holiday: {h.Name}", null, Absolute("leave")));
        }

        if (employee is not null)
        {
            // Approved leave.
            var policies = await db.Set<LeavePolicy>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
            foreach (var l in await db.Set<LeaveRequest>().AsNoTracking()
                         .Where(l => l.EmployeeId == employee.Id && l.Status == LeaveRequestStatus.Approved && l.EndDate >= from && l.StartDate <= to).ToListAsync(cancellationToken))
            {
                entries.Add(new($"leave-{l.Id}@workbench", l.StartDate, l.EndDate, $"Out: {policies.GetValueOrDefault(l.PolicyId) ?? "Leave"}", l.Comment, Absolute("leave")));
            }

            // Timesheet due dates: the end of this period and the next, unless this one is already in.
            var (_, end) = await timekeeping.PeriodForAsync(db, today, cancellationToken);
            var current = await db.Set<Timesheet>().AsNoTracking().FirstOrDefaultAsync(t => t.EmployeeId == employee.Id && t.PeriodEnd == end, cancellationToken);
            if (current is null || current.Status is TimesheetStatus.Draft or TimesheetStatus.Rejected)
            {
                entries.Add(new($"timesheet-{end:yyyyMMdd}@workbench", end, end, "Timesheet due", "Enter your hours and submit your timesheet.", Absolute("time")));
            }

            var (_, nextEnd) = await timekeeping.PeriodForAsync(db, end.AddDays(1), cancellationToken);
            entries.Add(new($"timesheet-{nextEnd:yyyyMMdd}@workbench", nextEnd, nextEnd, "Timesheet due", "Enter your hours and submit your timesheet.", Absolute("time")));
        }

        // Tasks assigned to them, and milestones they own.
        var sheetIds = new HashSet<Guid>();
        var itemIds = new HashSet<Guid>();
        var assigned = await db.Set<ItemAssignee>().AsNoTracking().Where(a => a.Email == me).Select(a => a.ItemId).ToListAsync(cancellationToken);
        itemIds.UnionWith(assigned);
        var managed = employee is null ? [] : await db.Set<Project>().AsNoTracking()
            .Where(p => p.ProjectManagerId == employee.Id && p.SheetId != null).Select(p => p.SheetId!.Value).ToListAsync(cancellationToken);

        var items = await db.Items.AsNoTracking()
            .Where(i => itemIds.Contains(i.Id) || (i.IsMilestone && managed.Contains(i.SheetId)))
            .ToListAsync(cancellationToken);
        sheetIds.UnionWith(items.Select(i => i.SheetId));
        var sheetNames = await db.Sheets.AsNoTracking().Where(s => sheetIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        var columns = (await db.Columns.AsNoTracking().Where(c => sheetIds.Contains(c.SheetId)).ToListAsync(cancellationToken)).GroupBy(c => c.SheetId)
            .ToDictionary(g => g.Key, g => TaskRules.StatusColumn(g) is { IsRestricted: false } c ? c.Id.ToString() : null);
        foreach (var item in items)
        {
            var status = columns.GetValueOrDefault(item.SheetId) is { } key ? item.Values.GetValueOrDefault(key) : null;
            var date = item.Finish ?? item.Start;
            if (date is not { } due || due < from || due > to || TaskRules.IsDone(status))
            {
                continue;
            }

            var where = sheetNames.GetValueOrDefault(item.SheetId);
            var link = Absolute($"sheets/{item.SheetId}?item={item.Id}");
            var mine = assigned.Contains(item.Id);
            if (item.IsMilestone)
            {
                entries.Add(new($"milestone-{item.Id}@workbench", due, due, $"Milestone: {item.Title}", where, link));
            }
            else if (mine)
            {
                entries.Add(new($"task-{item.Id}@workbench", due, due, $"Due: {item.Title}", where, link));
            }
        }

        return IcsWriter.Write("Workbench", entries.OrderBy(e => e.Start).ThenBy(e => e.Summary, StringComparer.Ordinal), timekeeping.UtcNow);
    }

    private string? Absolute(string link) =>
        NotificationEmails.AbsoluteLink(link, options) is { } url && Uri.IsWellFormedUriString(url, UriKind.Absolute) ? url : null;
}
