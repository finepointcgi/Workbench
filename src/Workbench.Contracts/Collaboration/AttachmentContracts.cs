namespace Workbench.Contracts.Collaboration;

public sealed record AttachmentDto(
    Guid Id,
    Guid ItemId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string UploadedBy,
    DateTime UploadedAtUtc);

/// <summary>An open attachment. Dispose it to release the stream.</summary>
public sealed record AttachmentContent(AttachmentDto Attachment, Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>Upload limits, shared by the server (which enforces them) and the UI (which checks early).</summary>
public static class AttachmentRules
{
    public const long MaxSizeBytes = 25 * 1024 * 1024;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".csv"] = "text/csv",
        [".txt"] = "text/plain",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".zip"] = "application/zip",
        [".msg"] = "application/vnd.ms-outlook",
        [".eml"] = "message/rfc822",
        [".mpp"] = "application/vnd.ms-project",
    };

    public static IReadOnlyCollection<string> AllowedExtensions => ContentTypes.Keys;

    /// <summary>The <c>accept</c> attribute value for file inputs.</summary>
    public static string Accept => string.Join(",", ContentTypes.Keys);

    public static bool IsAllowed(string fileName) => ContentTypes.ContainsKey(Path.GetExtension(fileName));

    /// <summary>The content type for a file name. The server uses this instead of trusting the uploader's.</summary>
    public static string ContentTypeFor(string fileName) =>
        ContentTypes.TryGetValue(Path.GetExtension(fileName), out var type) ? type : "application/octet-stream";
}

public interface IAttachmentService
{
    Task<IReadOnlyList<AttachmentDto>> ListAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a file. Throws <see cref="ArgumentException"/> when the extension isn't allowed or the
    /// content is larger than <see cref="AttachmentRules.MaxSizeBytes"/>.
    /// </summary>
    Task<AttachmentDto> UploadAsync(Guid sheetId, Guid itemId, string fileName, Stream content, CancellationToken cancellationToken = default);

    Task<AttachmentContent> DownloadAsync(Guid sheetId, Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>A URL the browser can open to download the file (relative to the app in the web host, absolute in the desktop app).</summary>
    string GetDownloadUrl(Guid sheetId, Guid attachmentId);

    Task DeleteAsync(Guid sheetId, Guid attachmentId, CancellationToken cancellationToken = default);
}
