using Microsoft.EntityFrameworkCore;
using Workbench.Application.Collaboration;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Domain.Files;

namespace Workbench.Application.Files;

/// <summary>
/// Stores files that belong to any record (receipts, invoices) in the <see cref="IBlobStore"/>, with a <see cref="StoredFile"/>
/// row per file. Uses the same extension and size rules as sheet attachments (<see cref="AttachmentRules"/>).
/// Files are never deleted by replacing them: the owner just points at the newer one, so history stays intact.
/// </summary>
public sealed class FileStorage(IBlobStore blobs, ICurrentUser currentUser, TimeProvider clock)
{
    /// <summary>
    /// Writes the bytes and adds a <see cref="StoredFile"/> to <paramref name="db"/>; the caller saves it. If the caller's
    /// save fails, call <see cref="DiscardAsync"/> so the blob doesn't linger.
    /// </summary>
    public async Task<StoredFile> SaveAsync(IWorkbenchDbContext db, string ownerType, Guid ownerId, string fileName, Stream content, CancellationToken cancellationToken = default)
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
                $"\"{Path.GetExtension(fileName)}\" files can't be uploaded. Allowed: {string.Join(", ", AttachmentRules.AllowedExtensions)}.", nameof(fileName));
        }

        if (content.CanSeek && content.Length - content.Position > AttachmentRules.MaxSizeBytes)
        {
            throw TooLarge();
        }

        var file = new StoredFile
        {
            OwnerType = ownerType,
            OwnerId = ownerId,
            FileName = fileName,
            ContentType = AttachmentRules.ContentTypeFor(fileName),
            StorageKey = string.Empty,
            UploadedBy = currentUser.Email,
            UploadedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        file.StorageKey = $"files/{ownerType}/{file.Id:N}";

        // Read into memory with a hard cap so an oversized upload stops early; receipts are small.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > AttachmentRules.MaxSizeBytes)
            {
                throw TooLarge();
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        file.SizeBytes = await blobs.SaveAsync(file.StorageKey, buffer, cancellationToken);
        db.Set<StoredFile>().Add(file);
        return file;
    }

    public Task DiscardAsync(StoredFile file) => blobs.DeleteAsync(file.StorageKey, CancellationToken.None);

    public async Task<(StoredFile File, Stream Content)> OpenAsync(IWorkbenchDbContext db, Guid fileId, CancellationToken cancellationToken = default)
    {
        var file = await db.Set<StoredFile>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("File", fileId);
        try
        {
            return (file, await blobs.OpenReadAsync(file.StorageKey, cancellationToken));
        }
        catch (FileNotFoundException)
        {
            throw new WorkbenchNotFoundException($"The content of file {fileId} is missing.");
        }
    }

    private static ArgumentException TooLarge() => new($"Files can be at most {AttachmentRules.MaxSizeBytes / (1024 * 1024)} MB.");
}
