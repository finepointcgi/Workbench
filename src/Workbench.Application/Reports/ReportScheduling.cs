using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.Tenancy;
using Workbench.Contracts;
using Workbench.Contracts.Reports;
using Workbench.Domain.Identity;
using Workbench.Domain.Reports;

namespace Workbench.Application.Reports;

/// <summary>
/// Emails a scheduled report. The owner gets the file attached (or a link); additional recipients only ever get a link,
/// so nobody receives data run with someone else's permissions.
/// </summary>
public sealed class ReportDeliverer(IEmailSender email, NotificationOptions options, ILogger<ReportDeliverer> logger)
{
    /// <summary>Runs and sends; returns the status to record. Never throws for a report or delivery failure.</summary>
    public async Task<string> DeliverAsync(ReportSchedule schedule, SavedReport report, Func<CancellationToken, Task<ReportResultDto>> run, CancellationToken ct)
    {
        ReportResultDto result;
        try
        {
            result = await run(ct);
        }
        catch (Exception ex) when (ex is WorkbenchForbiddenException or WorkbenchNotFoundException or ArgumentException)
        {
            await SendAsync(Message(schedule.OwnerEmail, $"Scheduled report failed: {report.Name}",
                $"Workbench couldn't run \"{report.Name}\" for you: {ex.Message}", null, null), ct);
            return Clip($"Failed: {ex.Message}");
        }

        var link = Absolute($"reports/saved/{report.Id}");
        var details = result.Rows.Count(r => r.Kind == ReportRowKind.Detail);
        var summary = $"{result.Subtitle}{(result.Subtitle is null ? "" : " · ")}{details:N0} row{(details == 1 ? "" : "s")}, generated {result.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC.";
        var subject = $"Workbench report: {report.Name}";
        if (schedule.Delivery == ReportDelivery.Attachment)
        {
            var file = ReportFiles.Render(result, Enum.TryParse<ReportFileFormat>(schedule.Format, out var f) ? f : ReportFileFormat.Xlsx);
            await SendAsync(Message(schedule.OwnerEmail, subject, $"{summary}\nThe report is attached.", link, new EmailAttachment(file.FileName, file.ContentType, file.Content)), ct);
        }
        else
        {
            await SendAsync(Message(schedule.OwnerEmail, subject, summary, link, null), ct);
        }

        var others = (schedule.AdditionalRecipients ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Where(r => r != schedule.OwnerEmail).ToList();
        foreach (var to in others)
        {
            await SendAsync(Message(to, subject, $"{schedule.OwnerEmail} shares the report \"{report.Name}\" with you. It runs with your own access when you open it.", link, null), ct);
        }

        return Clip($"Sent to {schedule.OwnerEmail}{(others.Count == 0 ? "" : $" and {others.Count} other{(others.Count == 1 ? "" : "s")} (link)")}: {details:N0} rows.");
    }

    /// <summary>Splits emails into active Workbench users and everything else.</summary>
    public static async Task<(List<string> Valid, List<string> Invalid)> ValidRecipientsAsync(IWorkbenchDbContext db, IEnumerable<string> emails, CancellationToken ct)
    {
        var wanted = emails.Select(AccessControl.Normalize).Where(e => e.Length > 0).Distinct().ToList();
        var active = (await db.Set<AppUser>().AsNoTracking().Where(u => wanted.Contains(u.Email) && u.IsActive).Select(u => u.Email).ToListAsync(ct)).ToHashSet();
        return (wanted.Where(active.Contains).ToList(), wanted.Where(e => !active.Contains(e)).ToList());
    }

    private async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        try
        {
            await email.SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Scheduled report email to {Recipient} failed", message.To);
        }
    }

    private string? Absolute(string link) =>
        string.IsNullOrWhiteSpace(options.PublicBaseUrl) ? link : options.PublicBaseUrl.TrimEnd('/') + "/" + link;

    private static EmailMessage Message(string to, string subject, string body, string? link, EmailAttachment? attachment)
    {
        var text = new StringBuilder().AppendLine(subject).AppendLine().AppendLine(body);
        if (link is not null)
        {
            text.AppendLine().AppendLine($"Open: {link}");
        }

        var html = new StringBuilder()
            .Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(subject)).Append("</title></head>")
            .Append("<body style=\"margin:0;padding:24px;font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:15px;line-height:1.5;color:#1f1f1f\"><main style=\"max-width:600px\">")
            .Append("<h1 style=\"font-size:20px;margin:0 0 12px\">").Append(WebUtility.HtmlEncode(subject)).Append("</h1>")
            .Append("<p style=\"margin:0 0 16px;white-space:pre-wrap\">").Append(WebUtility.HtmlEncode(body)).Append("</p>");
        if (link is not null)
        {
            html.Append("<p style=\"margin:0 0 16px\"><a href=\"").Append(WebUtility.HtmlEncode(link)).Append("\" style=\"color:#0b57d0\">Open in Workbench</a></p>");
        }

        html.Append("<p style=\"margin:0;font-size:13px;color:#444746\">You get this email because of a report schedule in Workbench. Change it on the Reports page.</p></main></body></html>");
        return new EmailMessage(to, subject, text.ToString(), html.ToString(), attachment is null ? null : [attachment]);
    }

    private static string Clip(string text) => text.Length <= 500 ? text : text[..500];
}

/// <summary>
/// Sends the due schedules of the current tenant. Each schedule's next run is advanced and saved before it is sent, so
/// a run never repeats; the report then runs as the schedule's owner (<see cref="ReportRunAs"/>) in its own scope.
/// Owners who were deactivated have their schedules switched off.
/// </summary>
public sealed class ReportScheduleRunner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ReportScheduleRunner> logger)
{
    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        List<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync(ct);
            due = await db.Set<ReportSchedule>().AsNoTracking().Where(s => s.Enabled && s.NextRunUtc <= now).OrderBy(s => s.NextRunUtc).Select(s => s.Id).ToListAsync(ct);
        }

        var sent = 0;
        foreach (var id in due)
        {
            try
            {
                if (await RunOneAsync(id, now, ct))
                {
                    sent++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduled report {ScheduleId} failed", id);
            }
        }

        return sent;
    }

    private async Task<bool> RunOneAsync(Guid scheduleId, DateTime now, CancellationToken ct)
    {
        ReportSchedule schedule;
        SavedReport? report;
        AppUser? owner;
        await using (var scope = scopes.CreateAsyncScope())
        {
            await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync(ct);
            schedule = await db.Set<ReportSchedule>().FirstAsync(s => s.Id == scheduleId, ct);
            if (!schedule.Enabled || schedule.NextRunUtc > now)
            {
                return false;
            }

            report = await db.Set<SavedReport>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == schedule.SavedReportId, ct);
            owner = await db.Set<AppUser>().AsNoTracking().FirstOrDefaultAsync(u => u.Email == schedule.OwnerEmail, ct);
            schedule.NextRunUtc = ReportScheduleMath.Next(schedule, now);
            if (report is null || owner is { IsActive: false })
            {
                schedule.Enabled = false;
                schedule.LastStatus = report is null ? "Stopped: the report was deleted." : "Stopped: the owner's account is deactivated.";
                await db.SaveChangesAsync(ct);
                return false;
            }

            // Claim the run before sending: a crash after this point skips one email rather than sending it twice.
            await db.SaveChangesAsync(ct);
        }

        string status;
        await using (var scope = scopes.CreateAsyncScope())
        using (ReportRunAs.Enter(scope.ServiceProvider, schedule.OwnerEmail, owner?.DisplayName ?? schedule.OwnerEmail))
        {
            var reports = scope.ServiceProvider.GetRequiredService<SavedReportService>();
            var deliverer = scope.ServiceProvider.GetRequiredService<ReportDeliverer>();
            status = await deliverer.DeliverAsync(schedule, report, c => reports.RunReportAsync(report, c), ct);
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync(ct);
            var row = await db.Set<ReportSchedule>().FirstAsync(s => s.Id == scheduleId, ct);
            row.LastRunUtc = now;
            row.LastStatus = status;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }
}

/// <summary>Checks report schedules every 15 minutes in every tenant.</summary>
public sealed class ReportScheduleBackgroundService(IServiceScopeFactory scopes, ITenantJobRunner tenants, TimeProvider clock, ILogger<ReportScheduleBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15), clock);
        do
        {
            try
            {
                await tenants.ForEachTenantAsync(async ct =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var sent = await scope.ServiceProvider.GetRequiredService<ReportScheduleRunner>().RunDueAsync(ct);
                    if (sent > 0)
                    {
                        logger.LogInformation("Sent {Count} scheduled reports", sent);
                    }
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Report schedule run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
