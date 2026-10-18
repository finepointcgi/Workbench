using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Contracts.Time;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Time;

/// <summary>
/// The organization's timesheet period setting. A change takes effect at the next period boundary (the day after
/// today's period ends), so open timesheets keep their period; saving again before then replaces the pending change.
/// Every change writes an audit row and a security event.
/// </summary>
public sealed class OrganizationSettingsService(
    IWorkbenchDbContextFactory dbFactory,
    AccessControl access,
    AuditRecorder audit,
    SecurityEventLog securityLog,
    TimekeepingClock clock,
    IOptions<TimekeepingOptions> options) : IOrganizationSettingsService
{
    public const string AuditEntity = "OrganizationSetting";

    public async Task<OrganizationSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("view organization settings", cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await BuildAsync(db, cancellationToken);
    }

    public async Task<TimesheetCadencePreviewDto> PreviewTimesheetCadenceAsync(ChangeTimesheetCadenceRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("view organization settings", cancellationToken);
        var settings = Validate(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (_, effective, schedule) = await PlanAsync(db, settings, cancellationToken);
        var first = TimesheetPeriods.For(effective, schedule);
        var second = TimesheetPeriods.For(first.End.AddDays(1), schedule);
        return new TimesheetCadencePreviewDto(effective, [new(first.Start, first.End), new(second.Start, second.End)], Describe(settings));
    }

    public async Task<OrganizationSettingsDto> ChangeTimesheetCadenceAsync(ChangeTimesheetCadenceRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        var settings = Validate(request);
        var reason = request.Reason.Required("Reason");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (current, effective, _) = await PlanAsync(db, settings, cancellationToken);
        var now = clock.UtcNow;
        var rules = db.Set<TimesheetPeriodRule>();

        // The first saved setting also records the configured one it replaces, so history shows what was in force.
        if (!await rules.AnyAsync(cancellationToken))
        {
            var initial = options.Value.DefaultPeriodSettings.Normalized();
            rules.Add(new TimesheetPeriodRule
            {
                Cadence = initial.Cadence, WeekStartDay = initial.WeekStartDay, BiweeklyAnchor = initial.BiweeklyAnchor, EffectiveFrom = DateOnly.MinValue,
                ChangedBy = "configuration", ChangedAtUtc = now, Reason = "The setting in force before it was first saved (Timekeeping:Cadence)",
            });
        }

        // A change that hasn't taken effect yet is replaced, never stacked.
        var pending = await rules.Where(r => r.CancelledAtUtc == null && r.EffectiveFrom > clock.Today).ToListAsync(cancellationToken);
        foreach (var old in pending)
        {
            old.CancelledAtUtc = now;
            audit.Record(db, AuditEntity, old.Id, "TimesheetCadenceCancelled", Snapshot(old), null, reason);
        }

        if (settings == current && pending.Count == 0)
        {
            throw new ArgumentException($"Timesheet periods are already {Describe(settings)}.");
        }

        if (settings != current)
        {
            var rule = new TimesheetPeriodRule
            {
                Cadence = settings.Cadence, WeekStartDay = settings.WeekStartDay, BiweeklyAnchor = settings.BiweeklyAnchor, EffectiveFrom = effective,
                ChangedBy = access.Email, ChangedAtUtc = now, Reason = reason,
            };
            rules.Add(rule);
            audit.Record(db, AuditEntity, rule.Id, "TimesheetCadenceChanged", Describe(current), Snapshot(rule), reason);
        }

        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.SettingChanged, "Timekeeping:Cadence",
            detail: settings == current
                ? $"Pending change cancelled; stays {Describe(current)}"
                : $"{Describe(current)} → {Describe(settings)} from {effective:yyyy-MM-dd}",
            cancellationToken: cancellationToken);
        return await BuildAsync(db, cancellationToken);
    }

    /// <summary>The setting in force today, where a change would start, and the schedule with the change applied.</summary>
    private async Task<(TimesheetPeriodSettings Current, DateOnly Effective, IReadOnlyList<TimesheetPeriodRuleEntry> Schedule)> PlanAsync(
        IWorkbenchDbContext db, TimesheetPeriodSettings settings, CancellationToken cancellationToken)
    {
        var today = clock.Today;
        var inForce = (await LoadScheduleAsync(db, cancellationToken)).Where(r => r.EffectiveFrom <= today).ToList();
        var current = Active(inForce, today).Settings.Normalized();
        var effective = TimesheetPeriods.NextBoundary(today, inForce);
        return (current, effective, [.. inForce, new TimesheetPeriodRuleEntry(settings, effective)]);
    }

    private async Task<OrganizationSettingsDto> BuildAsync(IWorkbenchDbContext db, CancellationToken cancellationToken)
    {
        var today = clock.Today;
        var schedule = await LoadScheduleAsync(db, cancellationToken);
        var rows = await db.Set<TimesheetPeriodRule>().AsNoTracking().Where(r => r.CancelledAtUtc == null).ToListAsync(cancellationToken);
        var history = rows.OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.ChangedAtUtc).Select(ToDto).ToList();
        var currentRule = rows.Where(r => r.EffectiveFrom <= today).OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.ChangedAtUtc).FirstOrDefault();
        var current = currentRule is not null ? ToDto(currentRule) : ToDto(options.Value.DefaultPeriodSettings.Normalized(), null, null, null, null);
        var pendingRule = rows.Where(r => r.EffectiveFrom > today).OrderBy(r => r.EffectiveFrom).FirstOrDefault();
        var (start, end) = TimesheetPeriods.For(today, schedule);
        var inForce = schedule.Where(r => r.EffectiveFrom <= today).ToList();
        var canEdit = (await access.GetOrgRolesAsync(cancellationToken)).IsAdmin();
        return new OrganizationSettingsDto(current, pendingRule is null ? null : ToDto(pendingRule), start, end, TimesheetPeriods.NextBoundary(today, inForce), history, canEdit);
    }

    private Task<IReadOnlyList<TimesheetPeriodRuleEntry>> LoadScheduleAsync(IWorkbenchDbContext db, CancellationToken cancellationToken) =>
        TimesheetPeriods.LoadScheduleAsync(db, options.Value.DefaultPeriodSettings, cancellationToken);

    private static TimesheetPeriodRuleEntry Active(IReadOnlyList<TimesheetPeriodRuleEntry> schedule, DateOnly date) =>
        schedule.Where(r => r.EffectiveFrom <= date).OrderBy(r => r.EffectiveFrom).LastOrDefault() ?? schedule.OrderBy(r => r.EffectiveFrom).First();

    private static TimesheetPeriodSettings Validate(ChangeTimesheetCadenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Cadence))
        {
            throw new ArgumentException("Choose Weekly, Biweekly, Semi-monthly or Monthly.");
        }

        var cadence = (TimesheetCadence)Enum.Parse(typeof(TimesheetCadence), request.Cadence.ToString());
        switch (cadence)
        {
            case TimesheetCadence.Weekly:
                var day = request.WeekStartDay ?? DayOfWeek.Monday;
                if (!Enum.IsDefined(day))
                {
                    throw new ArgumentException("Choose the day weekly periods start on.");
                }

                return new TimesheetPeriodSettings(cadence, day);
            case TimesheetCadence.Biweekly:
                var anchor = request.BiweeklyAnchor ?? throw new ArgumentException("Biweekly periods need an anchor date: the first day of any period.");
                if (anchor.Year is < 2000 or > 2100)
                {
                    throw new ArgumentException("Choose an anchor date between 2000 and 2100.");
                }

                return new TimesheetPeriodSettings(cadence, anchor.DayOfWeek, anchor);
            default:
                return new TimesheetPeriodSettings(cadence);
        }
    }

    private static TimesheetPeriodSettingDto ToDto(TimesheetPeriodRule r) =>
        ToDto(TimesheetPeriodSettings.Of(r).Normalized(), r.EffectiveFrom == DateOnly.MinValue ? null : r.EffectiveFrom, r.ChangedBy, r.ChangedAtUtc, r.Reason);

    private static TimesheetPeriodSettingDto ToDto(TimesheetPeriodSettings s, DateOnly? effectiveFrom, string? by, DateTime? at, string? reason) =>
        new(Enum.Parse<TimesheetCadenceKind>(s.Cadence.ToString()), s.WeekStartDay, s.BiweeklyAnchor, effectiveFrom, by, at, reason, Describe(s));

    private static object Snapshot(TimesheetPeriodRule r) =>
        new { Cadence = r.Cadence.ToString(), WeekStartDay = r.WeekStartDay.ToString(), r.BiweeklyAnchor, r.EffectiveFrom };

    public static string Describe(TimesheetPeriodSettings s) => s.Cadence switch
    {
        TimesheetCadence.Weekly => $"weekly ({s.WeekStartDay}–{(DayOfWeek)(((int)s.WeekStartDay + 6) % 7)})",
        TimesheetCadence.Biweekly => $"biweekly (every other {s.BiweeklyAnchor?.DayOfWeek}, from {s.BiweeklyAnchor?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})",
        TimesheetCadence.SemiMonthly => "semi-monthly (1st–15th, 16th–end of month)",
        _ => "monthly",
    };
}
