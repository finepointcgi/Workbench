using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Collaboration;
using Workbench.Application.Files;
using Workbench.Application.Identity;
using Workbench.Application.PerDiem;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Receipts;
using Workbench.Domain.Files;

namespace Workbench.Application.Receipts;

/// <summary>
/// "My receipts" (<see cref="IReceiptLibraryService"/>). Lists, changes and deletes only the signed-in person's own files;
/// opening and reading a file someone else uploaded is decided by <c>PermissionCheckingReceiptLibraryService</c> (approvers
/// and Finance, for files on lines they can see).
/// </summary>
public sealed class ReceiptLibraryService(
    IWorkbenchDbContextFactory dbFactory,
    FileStorage files,
    ReceiptReader reader,
    AuditRecorder audit,
    ICurrentUser currentUser,
    IOptions<PerDiemOptions> perDiem,
    TimeProvider clock) : IReceiptLibraryService
{
    private string Me => AccessControl.Normalize(currentUser.Email);

    public async Task<IReadOnlyList<ReceiptDto>> ListMineAsync(ReceiptQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new ReceiptQuery();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if ((await ReceiptLibrary.BackfillAsync(db, Me, cancellationToken)).Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var rows = await db.Set<ReceiptDocument>().AsNoTracking()
            .Where(d => d.OwnerEmail == Me && d.RemovedAtUtc == null)
            .Join(db.Set<StoredFile>().AsNoTracking(), d => d.FileId, f => f.Id, (d, f) => new { Document = d, File = f })
            .OrderByDescending(x => x.Document.CreatedAtUtc)
            .Take(2000)
            .ToListAsync(cancellationToken);

        // Filtered in memory: a person's library is small, and this keeps search case-insensitive on every provider.
        var search = query.Search?.Trim();
        var filtered = rows.Where(x =>
            (string.IsNullOrEmpty(search)
                || x.File.FileName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (x.Document.Vendor?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (x.Document.Location?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (x.Document.Total is { } t && t.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Contains(search, StringComparison.Ordinal)))
            && (query.From is not { } from || DateOf(x.Document, x.File) >= from)
            && (query.To is not { } to || DateOf(x.Document, x.File) <= to)
            && (query.MinAmount is not { } min || x.Document.Total >= min)
            && (query.MaxAmount is not { } max || x.Document.Total <= max))
            .ToList();
        var uses = await ReceiptLibrary.UsesAsync(db, filtered.Select(x => x.File.Id).ToList(), cancellationToken);
        return filtered
            .Select(x => ReceiptLibrary.ToDto(x.File, x.Document, uses.GetValueOrDefault(x.File.Id)))
            .Where(r => !query.UnattachedOnly || !r.IsInUse)
            .OrderBy(r => r.IsInUse)
            .ThenByDescending(r => r.UploadedAtUtc)
            .ThenByDescending(r => r.Id)
            .Take(Math.Clamp(query.Take, 1, 1000))
            .ToList();
    }

    public async Task<ReceiptDto> GetAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (file, document) = await FindAsync(db, fileId, cancellationToken);
        var uses = await ReceiptLibrary.UsesAsync(db, [fileId], cancellationToken);
        return ReceiptLibrary.ToDto(file, document, uses.GetValueOrDefault(fileId));
    }

    public async Task<ReceiptReadingDto> UploadAsync(string fileName, Stream content, Guid? expenseReportId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = await ReceiptLibrary.ReadAllAsync(content, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var documentId = Guid.CreateVersion7();
        var file = await files.SaveAsync(db, ReceiptLibrary.LibraryOwnerType, documentId, fileName, new MemoryStream(bytes), cancellationToken);
        try
        {
            var reading = await reader.ReadAsync(file.FileName, file.ContentType, bytes, cancellationToken);
            var now = clock.GetUtcNow().UtcDateTime;
            var document = ReceiptLibrary.NewDocument(file, Me, reading, now);
            document.Id = documentId;
            db.Set<ReceiptDocument>().Add(document);
            audit.Record(db, ReceiptLibrary.AuditEntityType, document.Id, "Uploaded", after: new { FileId = file.Id, file.FileName, file.SizeBytes, ReadBy = reading.Method.ToString() });
            await db.SaveChangesAsync(cancellationToken);
            return new ReceiptReadingDto(ReceiptLibrary.ToDto(file, document, []), reading, await CheckAsync(db, reading, expenseReportId, cancellationToken));
        }
        catch
        {
            await files.DiscardAsync(file);
            throw;
        }
    }

    public async Task<ReceiptReadingDto> ReadAsync(Guid fileId, Guid? expenseReportId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (file, document) = await FindAsync(db, fileId, cancellationToken);
        var reading = document is null ? null : ReceiptLibrary.StoredReading(document);
        if (reading is null)
        {
            // Read once (text layer, or the assistant when it's on) and keep it, so choosing the file again is instant.
            var (_, stream) = await files.OpenAsync(db, fileId, cancellationToken);
            byte[] bytes;
            await using (stream)
            {
                bytes = await ReceiptLibrary.ReadAllAsync(stream, cancellationToken);
            }

            reading = await reader.ReadAsync(file.FileName, file.ContentType, bytes, cancellationToken);
            if (document is not null && document.OwnerEmail == Me)
            {
                var tracked = await db.Set<ReceiptDocument>().FirstAsync(d => d.Id == document.Id, cancellationToken);
                ReceiptLibrary.ApplyReading(tracked, file.FileName, reading, clock.GetUtcNow().UtcDateTime);
                await db.SaveChangesAsync(cancellationToken);
                document = tracked;
            }
        }

        var uses = await ReceiptLibrary.UsesAsync(db, [fileId], cancellationToken);
        return new ReceiptReadingDto(ReceiptLibrary.ToDto(file, document, uses.GetValueOrDefault(fileId)), reading, await CheckAsync(db, reading, expenseReportId, cancellationToken));
    }

    public async Task<LodgingRateCheckDto> CheckLodgingAsync(LodgingRateCheckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Nights is < 1 or > 90)
        {
            throw new ArgumentException("A stay is 1 to 90 nights.");
        }

        if (request.NightlyRate <= 0)
        {
            throw new ArgumentException("The nightly rate must be greater than 0.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var nights = Enumerable.Range(0, request.Nights).Select(i => (request.CheckIn.AddDays(i), request.NightlyRate)).ToList();
        return (await LodgingRateChecker.CheckAsync(db, nights, Blank(request.City), Blank(request.State)?.ToUpperInvariant(), request.ExpenseReportId,
            perDiem.Value.LodgingOveragePolicy, cancellationToken))!;
    }

    public async Task<ReceiptDto> UpdateAsync(Guid fileId, UpdateReceiptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Total is < 0)
        {
            throw new ArgumentException("The total can't be negative.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (file, document) = await ReceiptLibrary.FindMineAsync(db, fileId, Me, cancellationToken);
        document.Kind = (ReceiptKind)(int)request.Kind;
        document.Vendor = Clip(Blank(request.Vendor), 200);
        document.Location = Clip(Blank(request.Location), 200);
        document.DocumentDate = request.DocumentDate;
        document.Total = request.Total is { } total ? decimal.Round(total, 2) : null;
        await db.SaveChangesAsync(cancellationToken);
        var uses = await ReceiptLibrary.UsesAsync(db, [fileId], cancellationToken);
        return ReceiptLibrary.ToDto(file, document, uses.GetValueOrDefault(fileId));
    }

    public async Task DeleteAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (file, document) = await ReceiptLibrary.FindMineAsync(db, fileId, Me, cancellationToken);
        var uses = (await ReceiptLibrary.UsesAsync(db, [fileId], cancellationToken)).GetValueOrDefault(fileId) ?? [];
        if (uses.Count > 0)
        {
            var where = string.Join("; ", uses.Take(3).Select(u => $"\"{u.Description}\"{(u.ExpenseReportTitle is { } t ? $" on {t}" : "")}"));
            throw new InvalidOperationException(
                $"{file.FileName} is attached to {uses.Count} line{(uses.Count == 1 ? "" : "s")} ({where}{(uses.Count > 3 ? "; …" : "")}). Remove it from {(uses.Count == 1 ? "that line" : "those lines")} first.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (document.FirstAttachedAtUtc is not null || file.OwnerType != ReceiptLibrary.LibraryOwnerType)
        {
            // It backed a line once: the audit trail points at it, so it's kept and only leaves the library.
            document.RemovedAtUtc = now;
            document.RemovedBy = Me;
            audit.Record(db, ReceiptLibrary.AuditEntityType, document.Id, "Removed", before: new { FileId = file.Id, file.FileName },
                reason: "Kept because it was attached to an expense line before");
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        audit.Record(db, ReceiptLibrary.AuditEntityType, document.Id, "Deleted", before: new { FileId = file.Id, file.FileName, file.SizeBytes });
        db.Set<ReceiptDocument>().Remove(document);
        db.Set<StoredFile>().Remove(file);
        await db.SaveChangesAsync(cancellationToken);
        await files.DiscardAsync(file);
    }

    public async Task<ReceiptContent> OpenAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await FindAsync(db, fileId, cancellationToken);
        var (file, stream) = await files.OpenAsync(db, fileId, cancellationToken);
        return new ReceiptContent(file.FileName, file.ContentType, stream);
    }

    public string GetContentUrl(Guid fileId) => $"api/v1/receipts/{fileId}/content";

    /// <summary>
    /// A library file (or a receipt on a line, which has no library entry yet). Whether the signed-in person may see it is
    /// the permission decorator's call; removed files are found only through the lines that still use them.
    /// </summary>
    private static async Task<(StoredFile File, ReceiptDocument? Document)> FindAsync(IWorkbenchDbContext db, Guid fileId, CancellationToken ct)
    {
        var document = await db.Set<ReceiptDocument>().AsNoTracking().FirstOrDefaultAsync(d => d.FileId == fileId, ct);
        var file = await db.Set<StoredFile>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null || (document is null && file.OwnerType != ReceiptLibrary.LineOwnerType))
        {
            throw WorkbenchNotFoundException.For("Receipt", fileId);
        }

        return (file, document);
    }

    private async Task<LodgingRateCheckDto?> CheckAsync(IWorkbenchDbContext db, ReceiptExtractionDto reading, Guid? reportId, CancellationToken ct) =>
        reading.IsLodging
            ? await LodgingRateChecker.CheckAsync(db, LodgingRateChecker.NightsOf(reading), reading.City, reading.State, reportId, perDiem.Value.LodgingOveragePolicy, ct)
            : null;

    private static DateOnly DateOf(ReceiptDocument document, StoredFile file) => document.DocumentDate ?? DateOnly.FromDateTime(file.UploadedAtUtc);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
