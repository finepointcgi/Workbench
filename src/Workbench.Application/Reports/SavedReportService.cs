using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Time;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Reports;
using Workbench.Domain.Identity;
using Workbench.Domain.Reports;

namespace Workbench.Application.Reports;

/// <summary>
/// Saved reports and their schedules. A report is visible to its owner, administrators and, when shared, to holders of
/// the roles it's shared with; only the owner (or an administrator) changes it. Running always uses the caller's own
/// permissions through <see cref="IStandardReportService"/> / <see cref="IReportWriterService"/>.
/// </summary>
public sealed class SavedReportService(
    IWorkbenchDbContextFactory dbFactory,
    ReportAccess access,
    IStandardReportService standard,
    IReportWriterService writer,
    ReportDeliverer deliverer,
    TimekeepingClock clock) : ISavedReportService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task<IReadOnlyList<SavedReportDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var context = await access.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var reports = (await db.Set<SavedReport>().AsNoTracking().ToListAsync(cancellationToken)).Where(r => CanSee(r, context)).ToList();
        var ids = reports.Select(r => r.Id).ToList();
        var schedules = await db.Set<ReportSchedule>().AsNoTracking().Where(s => ids.Contains(s.SavedReportId)).ToListAsync(cancellationToken);
        return reports.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => ToDto(r, schedules, context)).ToList();
    }

    public async Task<SavedReportDto> GetAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        var context = await access.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await FindVisibleAsync(db, reportId, context, cancellationToken);
        var schedules = await db.Set<ReportSchedule>().AsNoTracking().Where(s => s.SavedReportId == reportId).ToListAsync(cancellationToken);
        return ToDto(report, schedules, context);
    }

    public async Task<SavedReportDto> CreateAsync(SaveReportRequest request, CancellationToken cancellationToken = default)
    {
        var context = await access.GetAsync(cancellationToken);
        var now = clock.UtcNow;
        var report = new SavedReport { Name = "", OwnerEmail = context.Email, DefinitionJson = "{}", CreatedAtUtc = now };
        Apply(report, request, now);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        db.Set<SavedReport>().Add(report);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(report, [], context);
    }

    public async Task<SavedReportDto> UpdateAsync(Guid reportId, SaveReportRequest request, CancellationToken cancellationToken = default)
    {
        var context = await access.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await FindEditableAsync(db, reportId, context, cancellationToken);
        Apply(report, request, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        var schedules = await db.Set<ReportSchedule>().AsNoTracking().Where(s => s.SavedReportId == reportId).ToListAsync(cancellationToken);
        return ToDto(report, schedules, context);
    }

    public async Task DeleteAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        var context = await access.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await FindEditableAsync(db, reportId, context, cancellationToken);
        db.Set<ReportSchedule>().RemoveRange(await db.Set<ReportSchedule>().Where(s => s.SavedReportId == reportId).ToListAsync(cancellationToken));
        db.Set<SavedReport>().Remove(report);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ReportResultDto> RunAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        var context = await access.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await FindVisibleAsync(db, reportId, context, cancellationToken);
        return await RunReportAsync(report, cancellationToken);
    }

    public async Task<ReportFileDto> ExportAsync(Guid reportId, ReportFileFormat format, CancellationToken cancellationToken = default) =>
        ReportFiles.Render(await RunAsync(reportId, cancellationToken), format);

    /// <summary>Runs a saved report as whoever is current (the caller, or a schedule's owner inside <see cref="ReportRunAs"/>).</summary>
    internal async Task<ReportResultDto> RunReportAsync(SavedReport report, CancellationToken ct)
    {
        ReportResultDto result;
        if (report.StandardKey is { } key)
        {
            result = await standard.RunAsync(key, JsonSerializer.Deserialize<ReportParameters>(report.DefinitionJson, Json) ?? new ReportParameters(), ct);
        }
        else
        {
            var definition = JsonSerializer.Deserialize<ReportDefinition>(report.DefinitionJson, Json) ?? throw new ArgumentException("The report's definition is empty.");
            result = await writer.RunAsync(definition with { Title = definition.Title ?? report.Name }, ct);
        }

        return result with { Title = report.Name };
    }

    public async Task<ReportScheduleDto> AddScheduleAsync(Guid reportId, SaveReportScheduleRequest request, CancellationToken cancellationToken = default)
    {
        var context = await access.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await FindVisibleAsync(db, reportId, context, cancellationToken);
        var schedule = new ReportSchedule { SavedReportId = reportId, OwnerEmail = context.Email, Format = "Xlsx", CreatedAtUtc = clock.UtcNow };
        await ApplyAsync(db, schedule, request, cancellationToken);
        db.Set<ReportSchedule>().Add(schedule);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(schedule, context);
    }

    public async Task<ReportScheduleDto> UpdateScheduleAsync(Guid scheduleId, SaveReportScheduleRequest request, CancellationToken cancellationToken = default)
    {
        var context = await access.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var schedule = await FindScheduleAsync(db, scheduleId, context, allowAdmin: true, cancellationToken);
        await ApplyAsync(db, schedule, request, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(schedule, context);
    }

    public async Task DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var context = await access.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        db.Set<ReportSchedule>().Remove(await FindScheduleAsync(db, scheduleId, context, allowAdmin: true, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ReportScheduleDto> SendNowAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var context = await access.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        // Only the owner: the report runs as the caller, so it must be the person the schedule runs as.
        var schedule = await FindScheduleAsync(db, scheduleId, context, allowAdmin: false, cancellationToken);
        var report = await db.Set<SavedReport>().AsNoTracking().FirstAsync(r => r.Id == schedule.SavedReportId, cancellationToken);
        schedule.LastStatus = await deliverer.DeliverAsync(schedule, report, ct => RunReportAsync(report, ct), cancellationToken);
        schedule.LastRunUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(schedule, context);
    }

    // ---- Rules ----

    internal static bool CanSee(SavedReport report, ReportAccessContext context) =>
        report.OwnerEmail == context.Email || context.IsAdmin
        || (report.Visibility == SavedReportVisibility.Shared && ParseRoles(report.SharedWith).Any(r => r == OrgRoles.Employee || context.Roles.HasFlag(r)));

    private static bool CanEdit(SavedReport report, ReportAccessContext context) => report.OwnerEmail == context.Email || context.IsAdmin;

    private static async Task<SavedReport> FindVisibleAsync(IWorkbenchDbContext db, Guid id, ReportAccessContext context, CancellationToken ct)
    {
        var report = await db.Set<SavedReport>().FirstOrDefaultAsync(r => r.Id == id, ct);
        return report is not null && CanSee(report, context) ? report : throw WorkbenchNotFoundException.For("Saved report", id);
    }

    private static async Task<SavedReport> FindEditableAsync(IWorkbenchDbContext db, Guid id, ReportAccessContext context, CancellationToken ct)
    {
        var report = await FindVisibleAsync(db, id, context, ct);
        return CanEdit(report, context) ? report : throw new WorkbenchForbiddenException("Only the report's owner can change it.");
    }

    private static async Task<ReportSchedule> FindScheduleAsync(IWorkbenchDbContext db, Guid id, ReportAccessContext context, bool allowAdmin, CancellationToken ct)
    {
        var schedule = await db.Set<ReportSchedule>().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Report schedule", id);
        var report = await db.Set<SavedReport>().AsNoTracking().FirstAsync(r => r.Id == schedule.SavedReportId, ct);
        if (schedule.OwnerEmail != context.Email && !CanSee(report, context))
        {
            throw WorkbenchNotFoundException.For("Report schedule", id);
        }

        return schedule.OwnerEmail == context.Email || (allowAdmin && context.IsAdmin)
            ? schedule
            : throw new WorkbenchForbiddenException("Only the schedule's owner can do that.");
    }

    private static void Apply(SavedReport report, SaveReportRequest request, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200)
        {
            throw new ArgumentException("Give the report a name (up to 200 characters).");
        }

        if (request.Description is { Length: > 1000 })
        {
            throw new ArgumentException("Keep the description under 1,000 characters.");
        }

        if ((request.StandardKey is null) == (request.Definition is null))
        {
            throw new ArgumentException("A saved report is either a standard report or a report-writer definition.");
        }

        string json;
        if (request.StandardKey is { } key)
        {
            if (!StandardReportKeys.All.Contains(key))
            {
                throw new ArgumentException($"There is no standard report '{key}'.");
            }

            json = JsonSerializer.Serialize(request.Parameters ?? new ReportParameters(), Json);
        }
        else
        {
            Validate(request.Definition!);
            json = JsonSerializer.Serialize(request.Definition, Json);
        }

        if (json.Length > 32000)
        {
            throw new ArgumentException("The report definition is too large.");
        }

        var roles = (request.SharedWith ?? []).Distinct().ToList();
        if (request.Visibility == ReportVisibility.Shared && roles.Count == 0)
        {
            throw new ArgumentException("Choose who to share the report with.");
        }

        report.Name = name;
        report.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        report.StandardKey = request.StandardKey;
        report.DefinitionJson = json;
        report.Visibility = request.Visibility == ReportVisibility.Shared ? SavedReportVisibility.Shared : SavedReportVisibility.Private;
        report.SharedWith = report.Visibility == SavedReportVisibility.Shared ? string.Join(',', roles) : null;
        report.UpdatedAtUtc = now;
    }

    /// <summary>The definition names a real source and only its fields (visibility is applied when it runs).</summary>
    internal static void Validate(ReportDefinition definition)
    {
        var source = ReportSource.Find(definition.Source ?? "") ?? throw new ArgumentException($"Unknown data source '{definition.Source}'.");
        var keys = source.Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var used = (definition.Columns ?? []).Select(c => c.Field)
            .Concat((definition.Filters ?? []).Select(f => f.Field))
            .Concat(definition.GroupBy ?? [])
            .Concat((definition.Sort ?? []).Select(s => s.Field));
        if (used.FirstOrDefault(f => !keys.Contains(f)) is { } unknown)
        {
            throw new ArgumentException($"{source.Name} has no field '{unknown}'.");
        }

        if (definition.Columns is not { Count: > 0 })
        {
            throw new ArgumentException("Choose at least one column.");
        }

        if ((definition.GroupBy?.Count ?? 0) > ReportEngine.MaxGroupLevels)
        {
            throw new ArgumentException($"Group by at most {ReportEngine.MaxGroupLevels} fields.");
        }
    }

    private async Task ApplyAsync(IWorkbenchDbContext db, ReportSchedule schedule, SaveReportScheduleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.HourUtc is < 0 or > 23)
        {
            throw new ArgumentException("The hour must be 0–23 (UTC).");
        }

        if (request.Frequency == ReportFrequency.Weekly && request.DayOfWeek is null)
        {
            throw new ArgumentException("Choose the day of the week.");
        }

        if (request.Frequency == ReportFrequency.Monthly && request.DayOfMonth is not (>= 1 and <= 28))
        {
            throw new ArgumentException("Choose a day of the month from 1 to 28.");
        }

        var recipients = await ReportDeliverer.ValidRecipientsAsync(db, request.AdditionalRecipients ?? [], ct);
        if (recipients.Invalid.Count > 0)
        {
            throw new ArgumentException($"Only active Workbench users can receive a report: {string.Join(", ", recipients.Invalid)}.");
        }

        schedule.Frequency = (ReportScheduleFrequency)(int)request.Frequency;
        schedule.DayOfWeek = request.Frequency == ReportFrequency.Weekly ? request.DayOfWeek : null;
        schedule.DayOfMonth = request.Frequency == ReportFrequency.Monthly ? request.DayOfMonth : null;
        schedule.HourUtc = request.HourUtc;
        schedule.Delivery = request.Delivery == ReportDeliveryMode.Link ? ReportDelivery.Link : ReportDelivery.Attachment;
        schedule.Format = request.Format.ToString();
        schedule.AdditionalRecipients = recipients.Valid.Count == 0 ? null : string.Join(',', recipients.Valid.Where(r => r != schedule.OwnerEmail));
        schedule.Enabled = request.Enabled;
        schedule.NextRunUtc = ReportScheduleMath.Next(schedule, clock.UtcNow);
    }

    internal static IReadOnlyList<OrgRoles> ParseRoles(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => Enum.TryParse<OrgRoles>(r, out var role) ? role : (OrgRoles?)null)
            .OfType<OrgRoles>()
            .ToList();

    private static SavedReportDto ToDto(SavedReport r, IReadOnlyList<ReportSchedule> schedules, ReportAccessContext context) => new(
        r.Id, r.Name, r.Description, r.OwnerEmail, r.OwnerEmail == context.Email, CanEdit(r, context),
        r.Visibility == SavedReportVisibility.Shared ? ReportVisibility.Shared : ReportVisibility.Private, ParseRoles(r.SharedWith), r.StandardKey,
        r.StandardKey is null ? null : JsonSerializer.Deserialize<ReportParameters>(r.DefinitionJson, Json),
        r.StandardKey is null ? JsonSerializer.Deserialize<ReportDefinition>(r.DefinitionJson, Json) : null,
        r.UpdatedAtUtc,
        schedules.Where(s => s.SavedReportId == r.Id && (s.OwnerEmail == context.Email || context.IsAdmin)).OrderBy(s => s.CreatedAtUtc).Select(s => ToDto(s, context)).ToList());

    internal static ReportScheduleDto ToDto(ReportSchedule s, ReportAccessContext context) => new(
        s.Id, s.SavedReportId, s.OwnerEmail, s.OwnerEmail == context.Email, (ReportFrequency)(int)s.Frequency, s.DayOfWeek, s.DayOfMonth, s.HourUtc,
        s.Delivery == ReportDelivery.Link ? ReportDeliveryMode.Link : ReportDeliveryMode.Attachment,
        Enum.TryParse<ReportFileFormat>(s.Format, out var f) ? f : ReportFileFormat.Xlsx,
        (s.AdditionalRecipients ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries), s.Enabled, s.NextRunUtc, s.LastRunUtc, s.LastStatus);
}

/// <summary>When a schedule next goes out: the first matching hour strictly after a moment.</summary>
public static class ReportScheduleMath
{
    public static DateTime Next(ReportSchedule s, DateTime afterUtc)
    {
        var day = DateOnly.FromDateTime(afterUtc);
        for (var i = 0; i < 400; i++, day = day.AddDays(1))
        {
            var matches = s.Frequency switch
            {
                ReportScheduleFrequency.Weekly => day.DayOfWeek == (s.DayOfWeek ?? DayOfWeek.Monday),
                ReportScheduleFrequency.Monthly => day.Day == (s.DayOfMonth ?? 1),
                _ => true,
            };
            var at = day.ToDateTime(new TimeOnly(s.HourUtc, 0), DateTimeKind.Utc);
            if (matches && at > afterUtc)
            {
                return at;
            }
        }

        throw new InvalidOperationException("No next run within a year.");
    }
}
