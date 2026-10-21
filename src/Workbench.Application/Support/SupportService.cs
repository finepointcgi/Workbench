using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workbench.Application.Collaboration;
using Workbench.Application.Notifications;
using Workbench.Application.Tenancy;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Support;
using Workbench.Domain.Identity;
using Workbench.Domain.Support;

namespace Workbench.Application.Support;

/// <summary>
/// Stores problem reports and feedback, keeps the optional screenshot in the blob store, emails the support address and
/// tells the organization's administrators. Who may read or change what is enforced by
/// <see cref="Identity.PermissionCheckingSupportService"/>; this class only ever lists the caller's own requests in
/// <see cref="ListMineAsync"/>.
/// </summary>
public sealed class SupportService(
    IWorkbenchDbContextFactory dbFactory,
    IBlobStore blobs,
    ICurrentUser currentUser,
    IEmailSender email,
    INotificationPublisher notifications,
    IOptions<SupportOptions> options,
    TimeProvider clock,
    ILogger<SupportService> logger) : ISupportService
{
    public const string NotificationKind = "support.request";

    public async Task<SupportRequestDto> SubmitAsync(SubmitSupportRequest request, SupportScreenshot? screenshot = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reporter = (currentUser.Email ?? "").Trim().ToLowerInvariant();
        if (reporter.Length == 0)
        {
            throw new UnauthorizedAccessException("Sign in to send a report.");
        }

        var summary = (request.Summary ?? "").Trim();
        if (summary.Length == 0)
        {
            throw new ArgumentException("Say briefly what happened.", nameof(request));
        }

        if (summary.Length > SupportRules.SummaryMaxLength)
        {
            throw new ArgumentException($"Keep the summary to {SupportRules.SummaryMaxLength} characters; put the rest in the details.", nameof(request));
        }

        var details = string.IsNullOrWhiteSpace(request.Details) ? null : request.Details.Trim();
        if (details?.Length > SupportRules.DetailsMaxLength)
        {
            throw new ArgumentException($"The details can be at most {SupportRules.DetailsMaxLength} characters.", nameof(request));
        }

        byte[]? image = null;
        string? imageName = null;
        if (screenshot is not null)
        {
            imageName = Path.GetFileName((screenshot.FileName ?? "").Replace('\\', '/')).Trim();
            if (imageName.Length == 0 || imageName.Length > 255 || !SupportRules.IsAllowedScreenshot(imageName))
            {
                throw new ArgumentException("A screenshot must be a .png, .jpg, .gif or .webp image.", nameof(screenshot));
            }

            image = await ReadLimitedAsync(screenshot.Content, cancellationToken);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var entity = new SupportRequest
        {
            Kind = (SupportRequestKind)request.Kind,
            Summary = summary,
            Details = details,
            PagePath = SupportRules.CleanPagePath(request.PagePath),
            AppVersion = Truncate(request.AppVersion?.Trim(), 64),
            FromError = request.FromError,
            ReporterEmail = reporter,
            ReporterName = Truncate(currentUser.DisplayName is { Length: > 0 } name && name != reporter ? name : null, 200),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        if (image is not null)
        {
            entity.ScreenshotKey = $"support/{entity.Id:N}";
            entity.ScreenshotFileName = imageName;
            entity.ScreenshotContentType = SupportRules.ScreenshotContentType(imageName!);
            entity.ScreenshotBytes = await blobs.SaveAsync(entity.ScreenshotKey, new MemoryStream(image), cancellationToken);
        }

        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            entity.ReporterName ??= Truncate(
                await db.Set<AppUser>().AsNoTracking().Where(u => u.Email == reporter).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken), 200);
            try
            {
                await SaveWithNumberAsync(db, entity, cancellationToken);
            }
            catch
            {
                if (entity.ScreenshotKey is not null)
                {
                    await blobs.DeleteAsync(entity.ScreenshotKey, CancellationToken.None);
                }

                throw;
            }
        }

        var dto = ToDto(entity);
        await EmailSupportAsync(dto, image, cancellationToken);
        await NotifyAdminsAsync(dto, cancellationToken);
        return dto;
    }

    public async Task<IReadOnlyList<SupportRequestDto>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        var me = (currentUser.Email ?? "").Trim().ToLowerInvariant();
        if (me.Length == 0)
        {
            return [];
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<SupportRequest>().AsNoTracking().Where(r => r.ReporterEmail == me).ToListAsync(cancellationToken);
        return rows.OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Number).Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<SupportRequestDto>> ListAllAsync(SupportStatus? status = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await ListAsync(db, status, cancellationToken);
    }

    /// <summary>Every request in the database <paramref name="db"/> opens, newest first (also used by the host view).</summary>
    internal static async Task<IReadOnlyList<SupportRequestDto>> ListAsync(IWorkbenchDbContext db, SupportStatus? status, CancellationToken cancellationToken)
    {
        var query = db.Set<SupportRequest>().AsNoTracking();
        if (status is { } s)
        {
            var wanted = (SupportRequestStatus)s;
            query = query.Where(r => r.Status == wanted);
        }

        var rows = await query.ToListAsync(cancellationToken);
        return rows.OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Number).Select(ToDto).ToList();
    }

    public async Task<SupportRequestDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return ToDto(await FindAsync(db, id, cancellationToken));
    }

    public async Task<SupportRequestDto> UpdateAsync(Guid id, UpdateSupportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Status))
        {
            throw new ArgumentException("Unknown status.", nameof(request));
        }

        var resolution = string.IsNullOrWhiteSpace(request.Resolution) ? null : request.Resolution.Trim();
        if (resolution?.Length > SupportRules.ResolutionMaxLength)
        {
            throw new ArgumentException($"The note can be at most {SupportRules.ResolutionMaxLength} characters.", nameof(request));
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entity = await FindAsync(db, id, cancellationToken);
        var statusChanged = entity.Status != (SupportRequestStatus)request.Status;
        entity.Status = (SupportRequestStatus)request.Status;
        entity.Resolution = resolution;
        entity.HandledBy = Truncate(currentUser.Email?.ToLowerInvariant(), 320);
        entity.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        var dto = ToDto(entity);
        if (statusChanged && !string.Equals(entity.ReporterEmail, currentUser.Email, StringComparison.OrdinalIgnoreCase))
        {
            await notifications.PublishAsync(
                entity.ReporterEmail, NotificationKind,
                $"Your report {dto.Reference} is {StatusText(dto.Status).ToLowerInvariant()}",
                resolution ?? dto.Summary,
                link: "support");
        }

        return dto;
    }

    public async Task<SupportScreenshotContent> OpenScreenshotAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entity = await FindAsync(db, id, cancellationToken);
        if (entity.ScreenshotKey is null)
        {
            throw new WorkbenchNotFoundException($"{SupportRules.Reference(entity.Number)} has no screenshot.");
        }

        try
        {
            var stream = await blobs.OpenReadAsync(entity.ScreenshotKey, cancellationToken);
            return new SupportScreenshotContent(entity.ScreenshotFileName ?? "screenshot.png", entity.ScreenshotContentType ?? "application/octet-stream", stream);
        }
        catch (FileNotFoundException)
        {
            throw new WorkbenchNotFoundException($"The screenshot for {SupportRules.Reference(entity.Number)} is missing.");
        }
    }

    public string GetScreenshotUrl(Guid id) => $"api/v1/support/requests/{id}/screenshot";

    public static string StatusText(SupportStatus status) => status switch
    {
        SupportStatus.InProgress => "In progress",
        SupportStatus.Resolved => "Resolved",
        SupportStatus.Closed => "Closed",
        _ => "New",
    };

    internal static SupportRequestDto ToDto(SupportRequest r) => new(
        r.Id, r.Number, (SupportKind)r.Kind, (SupportStatus)r.Status, r.Summary, r.Details, r.PagePath, r.AppVersion, r.FromError,
        r.ReporterEmail, r.ReporterName, r.ScreenshotKey is null ? null : r.ScreenshotFileName ?? "screenshot", r.Resolution, r.HandledBy,
        r.CreatedAtUtc, r.UpdatedAtUtc);

    private static async Task<SupportRequest> FindAsync(IWorkbenchDbContext db, Guid id, CancellationToken cancellationToken) =>
        await db.Set<SupportRequest>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
        ?? throw WorkbenchNotFoundException.For("Support request", id);

    /// <summary>Numbers are max + 1; two reports at the same instant collide on the unique index, so retry a few times.</summary>
    private static async Task SaveWithNumberAsync(IWorkbenchDbContext db, SupportRequest entity, CancellationToken cancellationToken)
    {
        db.Set<SupportRequest>().Add(entity);
        for (var attempt = 0; ; attempt++)
        {
            entity.Number = (await db.Set<SupportRequest>().Select(r => (int?)r.Number).MaxAsync(cancellationToken) ?? 0) + 1;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < 4)
            {
            }
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > SupportRules.MaxScreenshotBytes)
            {
                throw new ArgumentException($"A screenshot can be at most {SupportRules.MaxScreenshotBytes / (1024 * 1024)} MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            throw new ArgumentException("The screenshot is empty.");
        }

        return buffer.ToArray();
    }

    private async Task EmailSupportAsync(SupportRequestDto dto, byte[]? image, CancellationToken cancellationToken)
    {
        var to = options.Value.Email?.Trim();
        if (string.IsNullOrEmpty(to))
        {
            return;
        }

        var tenant = TenantScope.Current;
        var kind = dto.Kind == SupportKind.Feedback ? "Feedback" : "Problem";
        var subject = $"[Workbench{(tenant is null ? "" : $" {tenant.Slug}")}] {kind} {dto.Reference}: {dto.Summary}";
        var fields = new List<(string Label, string Value)>
        {
            ("Reference", dto.Reference),
            ("Kind", kind + (dto.FromError ? " (from an error screen)" : "")),
        };
        if (tenant is not null)
        {
            fields.Add(("Organization", $"{tenant.DisplayName} ({tenant.Slug})"));
        }

        fields.Add(("From", dto.ReporterName is null ? dto.ReporterEmail : $"{dto.ReporterName} <{dto.ReporterEmail}>"));
        fields.Add(("Page", dto.PagePath ?? "(not captured)"));
        fields.Add(("App version", dto.AppVersion ?? "(unknown)"));
        fields.Add(("Sent", dto.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture)));

        var text = string.Join("\n", fields.Select(f => $"{f.Label}: {f.Value}")) + $"\n\nWhat happened:\n{dto.Summary}\n\n{dto.Details}";
        var html = "<table>" + string.Concat(fields.Select(f => $"<tr><th align=\"left\">{WebUtility.HtmlEncode(f.Label)}</th><td>{WebUtility.HtmlEncode(f.Value)}</td></tr>")) + "</table>"
            + $"<p><strong>{WebUtility.HtmlEncode(dto.Summary)}</strong></p>"
            + (dto.Details is null ? "" : $"<p style=\"white-space:pre-wrap\">{WebUtility.HtmlEncode(dto.Details)}</p>");
        IReadOnlyList<EmailAttachment>? attachments = image is null ? null : [new EmailAttachment(dto.ScreenshotFileName!, SupportRules.ScreenshotContentType(dto.ScreenshotFileName!), image)];

        try
        {
            await email.SendAsync(new EmailMessage(to, subject, text, html, attachments), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The request is saved and administrators see it in the app; a mail outage mustn't lose it.
            logger.LogWarning(ex, "Could not email support request {Reference} to the support address", dto.Reference);
        }
    }

    private async Task NotifyAdminsAsync(SupportRequestDto dto, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var admins = await Identity.RoleStore.EmailsWithPermissionAsync(db, Permission.AdminSupport, cancellationToken);
        var recipients = admins.Where(a => !string.Equals(a, dto.ReporterEmail, StringComparison.OrdinalIgnoreCase)).ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        var who = dto.ReporterName ?? dto.ReporterEmail;
        await notifications.PublishAsync(
            recipients, NotificationKind,
            $"{who} {(dto.Kind == SupportKind.Feedback ? "sent feedback" : "reported a problem")} ({dto.Reference})",
            dto.Summary,
            link: $"admin/support?id={dto.Id}");
    }

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
