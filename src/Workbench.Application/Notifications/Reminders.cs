using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Application.Time;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Leave;
using Workbench.Domain.Notifications;

namespace Workbench.Application.Notifications;

/// <summary>A reminder job the hourly <see cref="ReminderBackgroundService{TCheck}"/> runs for every tenant.</summary>
public interface IReminderCheck
{
    string Name { get; }

    /// <summary>Sends whatever is due now. Returns how many reminders went out.</summary>
    Task<int> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What every reminder shares: the person's choices, "not on weekends, holidays, approved leave or in quiet hours", the log that makes
/// a reminder go out at most once (a restart or a second server can't double-send), and delivery through <see cref="Notifier"/>.
/// Delivery never throws: a down mail server or Teams only means that reminder isn't delivered there.
/// </summary>
public sealed class ReminderGate(IWorkbenchDbContextFactory dbFactory, Notifier notifier, TimekeepingClock clock, ILogger<ReminderGate>? logger = null)
{
    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;

    public DateOnly Today => clock.Today;

    public int LocalHour => clock.LocalTime(clock.UtcNow).Hour;

    /// <summary>Reminders go out on business days from the same morning hour as the daily missing-hours reminder (<see cref="TimekeepingOptions.MissingTimeReminderHour"/>).</summary>
    public bool IsReminderTime => TimekeepingClock.IsBusinessDay(Today) && LocalHour >= clock.Options.MissingTimeReminderHour;

    public static bool InQuietHours(ReminderPreference preference, int hour) =>
        preference is { QuietStartHour: { } start, QuietEndHour: { } end } && start != end && (start < end ? hour >= start && hour < end : hour >= start || hour < end);

    /// <summary>The saved choices for each email; anyone without a row gets the defaults.</summary>
    public async Task<Dictionary<string, ReminderPreference>> PreferencesAsync(IEnumerable<string> emails, CancellationToken cancellationToken)
    {
        var list = emails.Distinct().ToList();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var saved = await db.Set<ReminderPreference>().AsNoTracking().Where(p => list.Contains(p.UserEmail)).ToDictionaryAsync(p => p.UserEmail, cancellationToken);
        return list.ToDictionary(e => e, e => saved.GetValueOrDefault(e) ?? new ReminderPreference { UserEmail = e });
    }

    /// <summary>Emails of people who are on approved leave or deactivated today, so nobody is nagged on a day off or after leaving.</summary>
    public async Task<HashSet<string>> UnavailableTodayAsync(IReadOnlyCollection<string> emails, CancellationToken cancellationToken)
    {
        var today = Today;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var away = await (from d in db.Set<LeaveRequestDay>().AsNoTracking()
                          join r in db.Set<LeaveRequest>().AsNoTracking() on d.RequestId equals r.Id
                          join e in db.Set<Domain.ProjectAccounting.Employee>().AsNoTracking() on r.EmployeeId equals e.Id
                          where d.Date == today && r.Status == LeaveRequestStatus.Approved
                          select e.Email).ToListAsync(cancellationToken);
        var gone = await db.Set<Domain.Identity.AppUser>().AsNoTracking().Where(u => !u.IsActive).Select(u => u.Email).ToListAsync(cancellationToken);
        var holiday = await db.Set<Holiday>().AsNoTracking().AnyAsync(h => h.Date == today, cancellationToken);
        return holiday
            ? emails.Select(Normalize).ToHashSet()
            : away.Concat(gone).Select(Normalize).ToHashSet();
    }

    /// <summary>
    /// Records that this reminder is being sent. False when the same reminder (same kind and key) already went out, or one of this
    /// kind already went out to this person today, or another server just claimed it.
    /// </summary>
    public async Task<bool> ClaimAsync(string email, string kind, string key, CancellationToken cancellationToken)
    {
        var today = Today;
        try
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            if (await db.Set<ReminderLog>().AnyAsync(l => l.UserEmail == email && l.Kind == kind && (l.Key == key || l.SentOn == today), cancellationToken))
            {
                return false;
            }

            db.Set<ReminderLog>().Add(new ReminderLog { UserEmail = email, Kind = kind, Key = key, SentOn = today, SentAtUtc = clock.UtcNow });
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex)
        {
            _log.LogDebug(ex, "Reminder {Kind} {Key} for {Recipient} was already claimed.", kind, key, email);
            return false;
        }
    }

    /// <summary>Delivers a claimed reminder: the bell always, then email, Teams and Slack as the person chose. Never throws.</summary>
    public async Task SendAsync(string email, ReminderPreference preference, string kind, string title, string body, string link)
    {
        try
        {
            await notifier.NotifyAsync([email], new NotificationRequest(kind, title, body, null, null, link, new DeliveryChoice(preference.ViaEmail, preference.ViaTeams, preference.ViaSlack)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Couldn't deliver the {Kind} reminder to {Recipient}.", kind, email);
        }
    }

    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}

/// <summary>Everyone's own reminder choices. A person only ever reads and changes their own.</summary>
public sealed class ReminderSettingsService(IWorkbenchDbContextFactory dbFactory, Identity.AccessControl access) : IReminderSettingsService
{
    public async Task<ReminderSettingsDto> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var me = Me();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<ReminderPreference>().AsNoTracking().FirstOrDefaultAsync(p => p.UserEmail == me, cancellationToken)
            ?? new ReminderPreference { UserEmail = me };
        var connected = await db.Set<ChatChannel>().AsNoTracking().Where(c => c.IsActive).Select(c => c.Kind).ToListAsync(cancellationToken);
        return new ReminderSettingsDto(row.TimesheetEnabled, row.ApprovalsEnabled, row.ViaEmail, row.ViaTeams, row.QuietStartHour, row.QuietEndHour,
            connected.Contains(ChatChannelKinds.Teams), row.ViaSlack, connected.Contains(ChatChannelKinds.Slack));
    }

    public async Task<ReminderSettingsDto> SetMineAsync(SaveReminderSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var me = Me();
        ArgumentNullException.ThrowIfNull(request);
        if (request.QuietStartHour is < 0 or > 23 || request.QuietEndHour is < 0 or > 23)
        {
            throw new ArgumentException("Quiet hours are hours of the day, 0 to 23.");
        }

        if (request.QuietStartHour is null != request.QuietEndHour is null)
        {
            throw new ArgumentException("Choose both the start and the end of your quiet hours, or neither.");
        }

        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var row = await db.Set<ReminderPreference>().FirstOrDefaultAsync(p => p.UserEmail == me, cancellationToken);
            if (row is null)
            {
                row = new ReminderPreference { UserEmail = me };
                db.Set<ReminderPreference>().Add(row);
            }

            row.TimesheetEnabled = request.TimesheetEnabled;
            row.ApprovalsEnabled = request.ApprovalsEnabled;
            row.ViaEmail = request.ViaEmail;
            row.ViaTeams = request.ViaTeams;
            row.ViaSlack = request.ViaSlack;
            row.QuietStartHour = request.QuietStartHour;
            row.QuietEndHour = request.QuietEndHour;
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetMineAsync(cancellationToken);
    }

    private string Me()
    {
        access.RequireSignedIn();
        return access.Email;
    }
}

/// <summary>Runs one <see cref="IReminderCheck"/> hourly for every tenant. The check decides what is due and the log makes it idempotent.</summary>
public sealed class ReminderBackgroundService<TCheck>(IServiceScopeFactory scopes, Tenancy.ITenantJobRunner tenants, TimeProvider clock, ILogger<ReminderBackgroundService<TCheck>> logger)
    : BackgroundService
    where TCheck : class, IReminderCheck
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                await tenants.ForEachTenantAsync(async ct =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var check = scope.ServiceProvider.GetRequiredService<TCheck>();
                    var sent = await check.RunAsync(ct);
                    if (sent > 0)
                    {
                        logger.LogInformation("Sent {Count} {Reminder} reminders", sent, check.Name);
                    }
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reminder check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
