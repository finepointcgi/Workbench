using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Domain.Collaboration;

namespace Workbench.Application.Collaboration;

public sealed class AttachmentService(
    IWorkbenchDbContextFactory dbFactory,
    IBlobStore blobs,
    ActivityRecorder activity,
    ICurrentUser currentUser,
    TimeProvider clock) : IAttachmentService
{
    public async Task<IReadOnlyList<AttachmentDto>> ListAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EnsureItemAsync(db, sheetId, itemId, cancellationToken);
        var attachments = await db.Set<Attachment>()
            .AsNoTracking()
            .Where(a => a.SheetId == sheetId && a.ItemId == itemId)
            .OrderBy(a => a.UploadedAtUtc)
            .ToListAsync(cancellationToken);
        return attachments.Select(ToDto).ToList();
    }

    public async Task<AttachmentDto> UploadAsync(Guid sheetId, Guid itemId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        fileName = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/')).Trim();
        if (fileName.Length == 0 || fileName.Length > 255)
        {
            throw new ArgumentException("The file needs a name of at most 255 characters.", nameof(fileName));
        }

        if (!AttachmentRules.IsAllowed(fileName))
        {
            throw new ArgumentException(
                $"\"{Path.GetExtension(fileName)}\" files can't be attached. Allowed: {string.Join(", ", AttachmentRules.AllowedExtensions)}.", nameof(fileName));
        }

        if (content.CanSeek && content.Length - content.Position > AttachmentRules.MaxSizeBytes)
        {
            throw TooLarge();
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var title = await EnsureItemAsync(db, sheetId, itemId, cancellationToken);

        var attachment = new Attachment
        {
            SheetId = sheetId,
            ItemId = itemId,
            FileName = fileName,
            ContentType = AttachmentRules.ContentTypeFor(fileName),
            StorageKey = string.Empty,
            UploadedBy = currentUser.Email,
            UploadedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        attachment.StorageKey = $"{sheetId:N}/{attachment.Id:N}";

        try
        {
            await using var limited = new SizeLimitedStream(content, AttachmentRules.MaxSizeBytes);
            attachment.SizeBytes = await blobs.SaveAsync(attachment.StorageKey, limited, cancellationToken);
            db.Set<Attachment>().Add(attachment);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await blobs.DeleteAsync(attachment.StorageKey, CancellationToken.None);
            throw;
        }

        await activity.RecordAsync(sheetId, itemId, ActivityKinds.AttachmentAdded, $"Attached \"{fileName}\" to \"{title}\"", SheetChangeKind.AttachmentAdded);
        return ToDto(attachment);
    }

    public async Task<AttachmentContent> DownloadAsync(Guid sheetId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var attachment = await FindAsync(db, sheetId, attachmentId, cancellationToken);
        Stream stream;
        try
        {
            stream = await blobs.OpenReadAsync(attachment.StorageKey, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            throw new WorkbenchNotFoundException($"The file for attachment {attachmentId} is missing.");
        }

        return new AttachmentContent(ToDto(attachment), stream);
    }

    public string GetDownloadUrl(Guid sheetId, Guid attachmentId) => $"api/v1/sheets/{sheetId}/attachments/{attachmentId}/content";

    public async Task DeleteAsync(Guid sheetId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var attachment = await FindAsync(db, sheetId, attachmentId, cancellationToken);
        db.Set<Attachment>().Remove(attachment);
        await db.SaveChangesAsync(cancellationToken);
        await blobs.DeleteAsync(attachment.StorageKey, CancellationToken.None);

        var title = await db.Items.Where(i => i.Id == attachment.ItemId).Select(i => i.Title).FirstOrDefaultAsync(cancellationToken) ?? "a deleted item";
        await activity.RecordAsync(sheetId, attachment.ItemId, ActivityKinds.AttachmentDeleted, $"Removed \"{attachment.FileName}\" from \"{title}\"", SheetChangeKind.AttachmentDeleted);
    }

    internal static ArgumentException TooLarge() =>
        new($"Attachments can be at most {AttachmentRules.MaxSizeBytes / (1024 * 1024)} MB.");

    private static AttachmentDto ToDto(Attachment a) =>
        new(a.Id, a.ItemId, a.FileName, a.ContentType, a.SizeBytes, a.UploadedBy, a.UploadedAtUtc);

    private static async Task<string> EnsureItemAsync(IWorkbenchDbContext db, Guid sheetId, Guid itemId, CancellationToken cancellationToken) =>
        await db.Items.Where(i => i.Id == itemId && i.SheetId == sheetId).Select(i => i.Title).FirstOrDefaultAsync(cancellationToken)
        ?? throw WorkbenchNotFoundException.For("Item", itemId);

    private static async Task<Attachment> FindAsync(IWorkbenchDbContext db, Guid sheetId, Guid attachmentId, CancellationToken cancellationToken) =>
        await db.Set<Attachment>().FirstOrDefaultAsync(a => a.Id == attachmentId && a.SheetId == sheetId, cancellationToken)
        ?? throw WorkbenchNotFoundException.For("Attachment", attachmentId);

    /// <summary>Read-only pass-through that throws once more than the limit has been read, so oversized uploads stop early.</summary>
    private sealed class SizeLimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // The caller owns the inner stream.
        protected override void Dispose(bool disposing) { }

        private int Count(int read)
        {
            _read += read;
            return _read > limit ? throw TooLarge() : read;
        }
    }
}
