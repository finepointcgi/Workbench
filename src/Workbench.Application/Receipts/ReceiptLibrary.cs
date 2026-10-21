using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Receipts;
using Workbench.Domain.Files;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Receipts;

/// <summary>
/// The receipt library's shared rules, used by the library service, expense lines and card charges: whose a file is,
/// attaching a library file to a line (one stored file, many lines), which lines use a file, and the DTOs.
/// </summary>
public static class ReceiptLibrary
{
    /// <summary><see cref="StoredFile.OwnerType"/> of a file uploaded to the library before it was attached to anything.</summary>
    public const string LibraryOwnerType = nameof(ReceiptDocument);

    /// <summary>Expense line receipts uploaded straight onto a line (the original upload path) are owned by the line.</summary>
    public const string LineOwnerType = nameof(OdcEntry);

    public const string AuditEntityType = nameof(ReceiptDocument);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ReceiptDocument NewDocument(StoredFile file, string ownerEmail, ReceiptExtractionDto reading, DateTime now)
    {
        var document = new ReceiptDocument { FileId = file.Id, OwnerEmail = AccessControl.Normalize(ownerEmail), CreatedAtUtc = now };
        ApplyReading(document, file.FileName, reading, now);
        return document;
    }

    /// <summary>A receipt uploaded straight onto an expense line also joins the uploader's library. The caller saves.</summary>
    public static void AddAttachedUpload(IWorkbenchDbContext db, StoredFile file, string ownerEmail, DateTime now, ReceiptExtractionDto reading)
    {
        var document = NewDocument(file, ownerEmail, reading, now);
        document.FirstAttachedAtUtc = now;
        db.Set<ReceiptDocument>().Add(document);
    }

    /// <summary>Reads an upload into memory, refusing more than <see cref="AttachmentRules.MaxSizeBytes"/>.</summary>
    public static async Task<byte[]> ReadAllAsync(Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > AttachmentRules.MaxSizeBytes)
            {
                throw new ArgumentException($"Files can be at most {AttachmentRules.MaxSizeBytes / (1024 * 1024)} MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public static void ApplyReading(ReceiptDocument document, string fileName, ReceiptExtractionDto reading, DateTime now)
    {
        document.Kind = fileName.Contains("statement", StringComparison.OrdinalIgnoreCase) && !reading.IsLodging
            ? ReceiptKind.CardStatement
            : (ReceiptKind)(int)reading.Kind;
        document.Vendor = Clip(reading.Vendor, 200);
        document.Location = Clip(reading.Location, 200);
        document.DocumentDate = reading.Date;
        document.CheckIn = reading.CheckIn;
        document.CheckOut = reading.CheckOut;
        document.Total = reading.Total;
        document.ReadSource = (ReceiptReadSource)(int)reading.Method;
        document.ReadAtUtc = now;
        var json = JsonSerializer.Serialize(reading, Json);
        if (json.Length > 16000)
        {
            json = JsonSerializer.Serialize(reading with { Charges = [] }, Json);
        }

        document.ReadingJson = json.Length > 16000 ? null : json;
    }

    public static ReceiptExtractionDto? StoredReading(ReceiptDocument document)
    {
        if (document.ReadingJson is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReceiptExtractionDto>(document.ReadingJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gives receipts that were uploaded straight onto expense lines (before the library existed) a library entry, so the
    /// owner sees every file they uploaded. Adds the entries to <paramref name="db"/>; the caller saves.
    /// </summary>
    public static async Task<IReadOnlyList<ReceiptDocument>> BackfillAsync(IWorkbenchDbContext db, string ownerEmail, CancellationToken ct)
    {
        var me = AccessControl.Normalize(ownerEmail);
        var known = db.Set<ReceiptDocument>().Select(d => d.FileId);
        var lineFiles = db.Set<OdcEntry>().Where(o => o.ExpenseReportId != null && o.ReceiptAttachmentId != null).Select(o => o.ReceiptAttachmentId!.Value);
        var orphans = await db.Set<StoredFile>()
            .Where(f => f.OwnerType == LineOwnerType && f.UploadedBy.ToLower() == me && !known.Contains(f.Id) && lineFiles.Contains(f.Id))
            .ToListAsync(ct);
        var added = orphans.Select(file => new ReceiptDocument
        {
            FileId = file.Id,
            OwnerEmail = me,
            Kind = ReceiptKind.Receipt,
            CreatedAtUtc = file.UploadedAtUtc,
            FirstAttachedAtUtc = file.UploadedAtUtc,
        }).ToList();
        db.Set<ReceiptDocument>().AddRange(added);
        return added;
    }

    /// <summary>The signed-in person's library entry for a file (backfilled for a receipt uploaded onto a line); not found otherwise.</summary>
    public static async Task<(StoredFile File, ReceiptDocument Document)> FindMineAsync(IWorkbenchDbContext db, Guid fileId, string actorEmail, CancellationToken ct)
    {
        var me = AccessControl.Normalize(actorEmail);
        var document = await db.Set<ReceiptDocument>().FirstOrDefaultAsync(d => d.FileId == fileId, ct)
            ?? (await BackfillAsync(db, me, ct)).FirstOrDefault(d => d.FileId == fileId);

        if (document is null || document.OwnerEmail != me || document.RemovedAtUtc is not null)
        {
            throw new WorkbenchNotFoundException($"Receipt {fileId} isn't in your receipts.");
        }

        var file = await db.Set<StoredFile>().FirstAsync(f => f.Id == fileId, ct);
        return (file, document);
    }

    /// <summary>
    /// Points <paramref name="line"/> at a file from the actor's library (no copy: one stored file can back several lines)
    /// and records it in the line's audit trail. The caller has checked the line can be changed (draft or rejected) and saves.
    /// </summary>
    public static async Task AttachAsync(IWorkbenchDbContext db, AuditRecorder audit, string actorEmail, OdcEntry line, Guid fileId, DateTime now, CancellationToken ct)
    {
        var (file, document) = await FindMineAsync(db, fileId, actorEmail, ct);
        if (line.ReceiptAttachmentId == file.Id)
        {
            return;
        }

        var previous = line.ReceiptAttachmentId;
        line.ReceiptAttachmentId = file.Id;
        document.FirstAttachedAtUtc ??= now;
        audit.Record(db, nameof(OdcEntry), line.Id, "ReceiptAttached",
            before: previous is null ? null : new { ReceiptFileId = previous },
            after: new { ReceiptFileId = file.Id, file.FileName, file.SizeBytes, FromLibrary = true });
    }

    /// <summary>Every line using each file: expense lines (card charges become expense lines too) and vendor ODCs.</summary>
    public static async Task<Dictionary<Guid, List<ReceiptUseDto>>> UsesAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0)
        {
            return [];
        }

        var lines = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => o.ReceiptAttachmentId != null && fileIds.Contains(o.ReceiptAttachmentId.Value))
            .Select(o => new { FileId = o.ReceiptAttachmentId!.Value, o.Id, o.ExpenseReportId, o.IncurredOn, o.Description, o.Amount, o.Status })
            .ToListAsync(ct);
        var reportIds = lines.Where(l => l.ExpenseReportId != null).Select(l => l.ExpenseReportId!.Value).Distinct().ToList();
        var titles = await db.Set<ExpenseReport>().AsNoTracking().Where(r => reportIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Title, ct);
        return lines.GroupBy(l => l.FileId).ToDictionary(g => g.Key, g => g.OrderBy(l => l.IncurredOn)
            .Select(l => new ReceiptUseDto(l.Id, l.ExpenseReportId, l.ExpenseReportId is { } r ? titles.GetValueOrDefault(r) : null, l.IncurredOn, l.Description, l.Amount,
                (OdcApprovalState)(int)l.Status))
            .ToList());
    }

    public static ReceiptDto ToDto(StoredFile file, ReceiptDocument? document, IReadOnlyList<ReceiptUseDto>? uses) => new(
        file.Id, file.FileName, file.ContentType, file.SizeBytes, file.UploadedAtUtc, file.UploadedBy,
        document is null ? ReceiptDocumentKind.Receipt : (ReceiptDocumentKind)(int)document.Kind,
        document?.Vendor, document?.Location, document?.DocumentDate, document?.Total,
        document is null ? ReceiptReadMethod.None : (ReceiptReadMethod)(int)document.ReadSource,
        uses ?? []);

    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
