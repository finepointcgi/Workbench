namespace Workbench.Contracts.Support;

public enum SupportKind
{
    Problem,
    Feedback,
}

public enum SupportStatus
{
    New,
    InProgress,
    Resolved,
    Closed,
}

public sealed record SupportRequestDto(
    Guid Id,
    int Number,
    SupportKind Kind,
    SupportStatus Status,
    string Summary,
    string? Details,
    string? PagePath,
    string? AppVersion,
    bool FromError,
    string ReporterEmail,
    string? ReporterName,
    string? ScreenshotFileName,
    string? Resolution,
    string? HandledBy,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    /// <summary>The number people quote: SR-000042.</summary>
    public string Reference => SupportRules.Reference(Number);

    public bool HasScreenshot => ScreenshotFileName is not null;
}

/// <summary>
/// A new report. <see cref="PagePath"/> is cleaned to an app-relative path (no host, query string or fragment) and
/// <see cref="AppVersion"/> is what the client reports; nothing else about the page is sent.
/// </summary>
public sealed record SubmitSupportRequest(
    SupportKind Kind,
    string Summary,
    string? Details = null,
    string? PagePath = null,
    string? AppVersion = null,
    bool FromError = false);

/// <summary>An optional image the reporter chose to attach.</summary>
public sealed record SupportScreenshot(string FileName, Stream Content);

public sealed record UpdateSupportRequest(SupportStatus Status, string? Resolution = null);

/// <summary>An open screenshot. Dispose it to release the stream.</summary>
public sealed record SupportScreenshotContent(string FileName, string ContentType, Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// "Report a problem" and "Send feedback". Everyone signed in can send one and see their own; administrators see and
/// handle every request in the organization. A request someone else sent is "not found" for non-admins.
/// </summary>
public interface ISupportService
{
    Task<SupportRequestDto> SubmitAsync(SubmitSupportRequest request, SupportScreenshot? screenshot = null, CancellationToken cancellationToken = default);

    /// <summary>The signed-in user's own requests, newest first.</summary>
    Task<IReadOnlyList<SupportRequestDto>> ListMineAsync(CancellationToken cancellationToken = default);

    /// <summary>Every request in the organization, newest first (administrators).</summary>
    Task<IReadOnlyList<SupportRequestDto>> ListAllAsync(SupportStatus? status = null, CancellationToken cancellationToken = default);

    /// <summary>One request: the reporter's own, or any for administrators.</summary>
    Task<SupportRequestDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Changes the status and the note back to the reporter (administrators). The reporter is notified.</summary>
    Task<SupportRequestDto> UpdateAsync(Guid id, UpdateSupportRequest request, CancellationToken cancellationToken = default);

    Task<SupportScreenshotContent> OpenScreenshotAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>A URL the browser can open to download the screenshot.</summary>
    string GetScreenshotUrl(Guid id);
}

/// <summary>A request as the host sees it, with the tenant it came from.</summary>
public sealed record HostSupportRequestDto(string TenantSlug, string TenantName, SupportRequestDto Request);

/// <summary>Support requests across every tenant (MultiTenant mode, host administrators on the host site only).</summary>
public interface IHostSupportService
{
    Task<IReadOnlyList<HostSupportRequestDto>> ListAsync(SupportStatus? status = null, CancellationToken cancellationToken = default);
}

/// <summary>The signed-in user's help state, kept on the server so it follows them between devices.</summary>
public sealed record HelpStateDto(IReadOnlyList<string> CompletedTours, string? WhatsNewSeen)
{
    public bool HasCompleted(string tourKey) => CompletedTours.Contains(tourKey, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Welcome tours finished or dismissed, and the last "What's new" entry seen, per user.</summary>
public interface IHelpStateService
{
    Task<HelpStateDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks a tour finished or dismissed, so it doesn't show again.</summary>
    Task<HelpStateDto> CompleteTourAsync(string tourKey, CancellationToken cancellationToken = default);

    /// <summary>Forgets every finished tour, so the welcome tour shows again on the next page load.</summary>
    Task<HelpStateDto> RestartToursAsync(CancellationToken cancellationToken = default);

    Task<HelpStateDto> MarkWhatsNewSeenAsync(string version, CancellationToken cancellationToken = default);
}

/// <summary>Limits and cleaning shared by the server (which enforces them) and the UI (which checks early).</summary>
public static class SupportRules
{
    public const int SummaryMaxLength = 200;
    public const int DetailsMaxLength = 4_000;
    public const int ResolutionMaxLength = 2_000;
    public const int PagePathMaxLength = 500;
    public const int TourKeyMaxLength = 32;
    public const long MaxScreenshotBytes = 10 * 1024 * 1024;

    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    /// <summary>The <c>accept</c> attribute value for the screenshot input.</summary>
    public static string ScreenshotAccept => string.Join(",", ImageTypes.Keys);

    public static bool IsAllowedScreenshot(string fileName) => ImageTypes.ContainsKey(Path.GetExtension(fileName));

    public static string ScreenshotContentType(string fileName) =>
        ImageTypes.TryGetValue(Path.GetExtension(fileName), out var type) ? type : "application/octet-stream";

    public static string Reference(int number) => $"SR-{number:000000}";

    /// <summary>
    /// The app-relative path of a page, from a full URL or a path: no scheme, host, query string or fragment (those can
    /// carry search terms, tokens or form state), and no leading slash. Null when nothing is left.
    /// </summary>
    public static string? CleanPagePath(string? uriOrPath)
    {
        if (string.IsNullOrWhiteSpace(uriOrPath))
        {
            return null;
        }

        var value = uriOrPath.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            value = absolute.AbsolutePath;
        }
        else if (value.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        value = value.Split('?', '#')[0].Trim().TrimStart('/');
        value = new string(value.Where(c => !char.IsControl(c)).ToArray());
        if (value.Length > PagePathMaxLength - 1)
        {
            value = value[..(PagePathMaxLength - 1)];
        }

        return "/" + value;
    }
}
