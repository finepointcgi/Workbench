using Microsoft.EntityFrameworkCore;
using Workbench.Application.Files;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Quality;
using Workbench.Domain.Quality;

namespace Workbench.Application.Quality;

/// <summary>Certificates of conformance, test reports and defect photos on inspections, NCRs and corrective actions (kept, never replaced).</summary>
public sealed class QualityFileService(IWorkbenchDbContextFactory dbFactory, FileStorage files, AuditRecorder audit, TimeProvider clock) : IQualityFileService
{
    public async Task<QualityFileDto> AttachAsync(QualityFileOwner owner, Guid ownerId, string kind, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var label = PurchasingSupport.Required(kind, "What the document is", 100);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var exists = owner switch
        {
            QualityFileOwner.Inspection => await db.Set<Inspection>().AnyAsync(i => i.Id == ownerId, cancellationToken),
            QualityFileOwner.Nonconformance => await db.Set<Nonconformance>().AnyAsync(n => n.Id == ownerId, cancellationToken),
            QualityFileOwner.CorrectiveAction => await db.Set<CorrectiveAction>().AnyAsync(c => c.Id == ownerId, cancellationToken),
            _ => false,
        };
        if (!exists)
        {
            throw WorkbenchNotFoundException.For(owner.ToString(), ownerId);
        }

        var stored = await files.SaveAsync(db, $"Quality{owner}", ownerId, fileName, content, cancellationToken);
        var attachment = new QualityAttachment
        {
            OwnerType = owner.ToString(), OwnerId = ownerId, FileId = stored.Id, Kind = label, FileName = stored.FileName, UploadedBy = audit.Actor,
            UploadedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.Set<QualityAttachment>().Add(attachment);
        audit.Record(db, owner.ToString(), ownerId, "FileAttached", after: new { attachment.Kind, attachment.FileName });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await files.DiscardAsync(stored);
            throw;
        }

        return Map(attachment);
    }

    public async Task<ReceiptContent> OpenAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var attachment = await db.Set<QualityAttachment>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("File", attachmentId);
        var (file, content) = await files.OpenAsync(db, attachment.FileId, cancellationToken);
        return new ReceiptContent(file.FileName, file.ContentType, content);
    }

    public string GetUrl(Guid attachmentId) => $"api/v1/quality/files/{attachmentId}";

    internal static async Task<IReadOnlyList<QualityFileDto>> ListAsync(IWorkbenchDbContext db, QualityFileOwner owner, Guid ownerId, CancellationToken ct)
    {
        var type = owner.ToString();
        return (await db.Set<QualityAttachment>().AsNoTracking().Where(a => a.OwnerType == type && a.OwnerId == ownerId).ToListAsync(ct))
            .OrderBy(a => a.UploadedAtUtc).Select(Map).ToList();
    }

    private static QualityFileDto Map(QualityAttachment a) => new(a.Id, a.FileId, a.Kind, a.FileName, a.UploadedBy, a.UploadedAtUtc);
}
