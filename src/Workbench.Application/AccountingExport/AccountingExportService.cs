using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Collaboration;
using Workbench.Application.Companies;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.AccountingExport;
using Workbench.Contracts.Security;
using Workbench.Domain.AccountingExport;

namespace Workbench.Application.AccountingExport;

/// <summary>
/// Builds accounting files and keeps the books on them. The rules that make an export safe to repeat:
/// <list type="bullet">
/// <item>A record exported to a target (QuickBooks, Xero, Generic) is remembered in <see cref="AccountingExportedRecord"/>, unique per
/// target × kind × record. A later export of any period that overlaps leaves those records out, so overlapping or repeated periods never
/// duplicate anything in the destination, and an entry posted late into an exported period is picked up by the next export.</item>
/// <item>Exporting what was exported before is an explicit "again" with a note; the run says so.</item>
/// <item>The run, the exported-record marks and the audit row are one transaction; the unique index is what stops two exports running at the
/// same moment from both taking a record (the loser gets a conflict and nothing is saved).</item>
/// <item>Records a format can't carry (credit memos in a QuickBooks CSV) are warned about and not marked, so they are offered again.</item>
/// <item>The file is kept in the blob store with its SHA-256, so history can give back exactly what was downloaded.</item>
/// </list>
/// </summary>
public sealed class AccountingExportService(
    IWorkbenchDbContextFactory dbFactory,
    IEnumerable<IAccountingExporter> exporters,
    IBlobStore blobs,
    AuditRecorder audit,
    SecurityEventLog securityLog,
    TimeProvider clock,
    CompanyScope? companies = null,
    Billing.IInvoiceTaxHook? salesTaxHook = null) : IAccountingExportService
{
    public const string RunEntity = nameof(AccountingExportRun);
    public const string SecurityEventExport = SecurityEventTypes.ExportAccounting;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public Task<IReadOnlyList<AccountingFormatDto>> ListFormatsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AccountingFormatDto>>(exporters.Select(e => e.Format).OrderBy(f => f.Target).ThenBy(f => f.Name, StringComparer.Ordinal).ToList());

    private IAccountingExporter FindExporter(string formatId) =>
        exporters.FirstOrDefault(e => string.Equals(e.Format.Id, formatId, StringComparison.Ordinal)) ?? throw new ArgumentException($"'{formatId}' isn't an export format.");

    // What a run would do: the file (when there is something to put in it), the counts and why it may not go ahead.
    private sealed record Plan(
        IAccountingExporter Exporter, AccountingFileRequest Request, ExportFile? File, int Candidates, int AlreadyExported, List<ExportCandidate> Marked,
        IReadOnlyList<AccountingUnmappedDto> Unmapped, IReadOnlyList<AccountingPriorRunDto> Prior, List<string> Warnings, string? Blocked);

    private async Task<Plan> PlanAsync(IWorkbenchDbContext db, AccountingFileRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var exporter = FindExporter(request.FormatId);
        if (!exporter.Format.Kinds.Contains(request.Kind))
        {
            throw new ArgumentException($"{exporter.Format.Name} can't export {KindLabel(request.Kind)}.");
        }

        if (request.Kind != AccountingExportKind.ChartOfAccounts && request.To < request.From)
        {
            throw new ArgumentException("The period ends before it starts.");
        }

        var note = request.Note?.Trim();
        if (request.Again && (note is null || note.Length < 3))
        {
            throw new ArgumentException("Exporting records again needs a note saying why.");
        }

        if (note is { Length: > 1000 })
        {
            throw new ArgumentException("The note is too long (1,000 characters at most).");
        }

        var target = exporter.Format.Target.ToString();
        var kind = request.Kind.ToString();
        var visible = await (companies?.VisibleCompanyIdsAsync(ct) ?? Task.FromResult<IReadOnlySet<Guid>?>(null));
        var candidates = await ExportDataLoader.CandidatesAsync(db, request, visible, ct);
        var candidateIds = candidates.Select(c => c.Id).ToList();
        var exportedIds = (await db.Set<AccountingExportedRecord>().AsNoTracking()
            .Where(r => r.Target == target && r.Kind == kind && candidateIds.Contains(r.RecordId)).Select(r => r.RecordId).ToListAsync(ct)).ToHashSet();
        var marked = request.Again ? candidates : candidates.Where(c => !exportedIds.Contains(c.Id)).ToList();

        var runs = db.Set<AccountingExportRun>().AsNoTracking().Where(r => r.Target == target && r.Kind == kind);
        var prior = (await runs.Where(r => request.Kind == AccountingExportKind.ChartOfAccounts || (r.PeriodFrom <= request.To && r.PeriodTo >= request.From))
                .OrderByDescending(r => r.CreatedAtUtc).Take(5).ToListAsync(ct))
            .Select(r => new AccountingPriorRunDto(r.Id, r.CreatedAtUtc, r.CreatedBy, r.PeriodFrom, r.PeriodTo, r.RecordCount, r.IsRepeat)).ToList();

        var warnings = new List<string>();
        var lastRun = await runs.OrderByDescending(r => r.CreatedAtUtc).Select(r => (DateTime?)r.CreatedAtUtc).FirstOrDefaultAsync(ct);
        var everExported = lastRun is null ? [] : await db.Set<AccountingExportedRecord>().AsNoTracking().Where(r => r.Target == target && r.Kind == kind).Select(r => r.RecordId).ToListAsync(ct);
        warnings.AddRange(await ExportDataLoader.VoidedSinceAsync(db, request.Kind, everExported, lastRun, ct));
        if (!request.Again && exportedIds.Count > 0)
        {
            warnings.Add($"{exportedIds.Count} record(s) in this period were exported to {exporter.Format.Target} before and are left out.");
        }

        if (request.Kind == AccountingExportKind.JournalEntries && !request.IncludeSubledgerJournals)
        {
            warnings.Add("Entries made by invoices, payments and bills are left out; export those documents instead so nothing is counted twice.");
        }

        if (marked.Count == 0)
        {
            var blocked = candidates.Count == 0
                ? "There is nothing to export for this period."
                : $"All {candidates.Count} record(s) in this period were already exported to {exporter.Format.Target}. Choose \"Export again\" and give a reason to repeat them.";
            return new Plan(exporter, request, null, candidates.Count, exportedIds.Count, marked, [], prior, warnings, blocked);
        }

        var mappings = await db.Set<AccountingMapping>().AsNoTracking().Where(m => m.Target == target).ToListAsync(ct);
        var resolver = new MappingResolver(mappings, exporter.Format.Target);
        var dataset = await ExportDataLoader.BuildAsync(db, request.Kind, marked.Select(c => c.Id).ToList(), resolver, ct, salesTaxHook);
        var file = exporter.Export(new ExportInput(request.Kind, request.From, request.To, dataset, UtcNow));
        warnings.AddRange(file.Warnings);
        var unmapped = resolver.Unmapped();
        var skipped = file.Skipped.ToHashSet();
        var keep = marked.Where(c => !skipped.Contains(c.Id)).ToList();
        string? reason = null;
        if (keep.Count == 0)
        {
            reason = "Nothing in this period can go in " + exporter.Format.Name + " (see the warnings).";
        }
        else if (!request.AllowUnmapped && resolver.Unmapped(blockingOnly: true).Count > 0)
        {
            var n = resolver.Unmapped(blockingOnly: true).Count;
            reason = $"{n} account, customer, vendor or item name{(n == 1 ? " has" : "s have")} no mapping for {exporter.Format.Target}. Map them, or choose to export with the Workbench names.";
        }

        if (unmapped.Count > 0 && request.AllowUnmapped)
        {
            warnings.Add($"{unmapped.Count} name(s) were exported unmapped, with Workbench's own names; the destination may not recognise them.");
        }

        return new Plan(exporter, request, file, candidates.Count, exportedIds.Count, keep, unmapped, prior, warnings.Distinct().ToList(), reason);
    }

    public async Task<AccountingExportPreviewDto> PreviewAsync(AccountingFileRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var plan = await PlanAsync(db, request, cancellationToken);
        return new AccountingExportPreviewDto(request.FormatId, request.Kind, plan.File?.RecordCount ?? 0, plan.AlreadyExported, plan.File?.RowCount ?? 0, plan.File?.ControlTotal ?? 0,
            plan.Unmapped, plan.Prior, plan.Warnings, plan.Blocked is null, plan.Blocked);
    }

    public async Task<AccountingExportResultDto> ExportAsync(AccountingFileRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var plan = await PlanAsync(db, request, cancellationToken);
        if (plan.Blocked is not null || plan.File is null)
        {
            throw new InvalidOperationException(plan.Blocked ?? "Nothing to export.");
        }

        var file = plan.File;
        var target = plan.Exporter.Format.Target.ToString();
        var kind = request.Kind.ToString();
        var runId = Guid.CreateVersion7();
        var blobKey = $"accounting-export/{runId:N}";
        var checksum = Convert.ToHexStringLower(SHA256.HashData(file.Content));
        var now = UtcNow;
        var note = request.Note?.Trim();
        var repeat = request.Again && plan.AlreadyExported > 0;

        await blobs.SaveAsync(blobKey, new MemoryStream(file.Content), cancellationToken);
        try
        {
            // Re-read the marks inside this unit of work: another export may have taken some records since the plan was made.
            var ids = plan.Marked.Select(c => c.Id).ToList();
            var existing = await db.Set<AccountingExportedRecord>().Where(r => r.Target == target && r.Kind == kind && ids.Contains(r.RecordId)).ToDictionaryAsync(r => r.RecordId, cancellationToken);
            if (!request.Again && existing.Count > 0)
            {
                throw new InvalidOperationException("Another export just took some of these records. Preview again.");
            }

            var run = new AccountingExportRun
            {
                Id = runId,
                Target = target,
                FormatId = plan.Exporter.Format.Id,
                Kind = kind,
                PeriodFrom = request.From,
                PeriodTo = request.To,
                CompanyId = request.CompanyId,
                FileName = file.FileName,
                ContentType = file.ContentType,
                RowCount = file.RowCount,
                RecordCount = file.RecordCount,
                ControlTotal = file.ControlTotal,
                Checksum = checksum,
                WarningsJson = JsonSerializer.Serialize(plan.Warnings, Json),
                WarningCount = plan.Warnings.Count,
                UnmappedAllowed = request.AllowUnmapped && plan.Unmapped.Count > 0,
                UnmappedCount = plan.Unmapped.Count,
                IncludedSubledgerJournals = request.Kind == AccountingExportKind.JournalEntries && request.IncludeSubledgerJournals,
                IsRepeat = repeat,
                Note = note,
                BlobKey = blobKey,
                CreatedBy = audit.Actor,
                CreatedAtUtc = now,
            };
            db.Set<AccountingExportRun>().Add(run);

            foreach (var c in plan.Marked)
            {
                if (existing.TryGetValue(c.Id, out var mark))
                {
                    mark.LastRunId = runId;
                    mark.ExportCount++;
                    mark.LastExportedAtUtc = now;
                }
                else
                {
                    db.Set<AccountingExportedRecord>().Add(new AccountingExportedRecord
                    {
                        Target = target, Kind = kind, RecordId = c.Id, RecordNumber = Clip(c.Number, 60), FirstRunId = runId, LastRunId = runId, ExportCount = 1,
                        FirstExportedAtUtc = now, LastExportedAtUtc = now,
                    });
                }
            }

            audit.Record(db, RunEntity, runId, "Exported", after: new
            {
                request.FormatId, Kind = kind, request.From, request.To, file.FileName, file.RowCount, file.RecordCount, file.ControlTotal, Checksum = checksum, repeat, UnmappedAllowed = run.UnmappedAllowed,
            }, reason: note);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await blobs.DeleteAsync(blobKey, CancellationToken.None);
            if (ex is DbUpdateException)
            {
                throw new InvalidOperationException("Another export just took some of these records. Preview again.", ex);
            }

            throw;
        }

        await securityLog.RecordAsync(SecurityEventExport, $"{kind} {request.From:yyyy-MM-dd}..{request.To:yyyy-MM-dd}", SecurityOutcomes.Success,
            $"{plan.Exporter.Format.Id}: {file.RecordCount} records, {file.RowCount} rows, sha256 {checksum[..12]}{(repeat ? ", again" : "")}", cancellationToken: cancellationToken);
        return new AccountingExportResultDto(runId, file.FileName, file.ContentType, file.Content, file.RowCount, file.RecordCount, file.ControlTotal, checksum, plan.Warnings);
    }

    public async Task<IReadOnlyList<AccountingExportRunDto>> ListRunsAsync(AccountingExportKind? kind = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<AccountingExportRun>().AsNoTracking().AsQueryable();
        if (kind is { } k)
        {
            var name = k.ToString();
            q = q.Where(r => r.Kind == name);
        }

        var rows = await q.OrderByDescending(r => r.CreatedAtUtc).Take(Math.Clamp(limit, 1, 500)).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<AccountingExportResultDto> DownloadRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await db.Set<AccountingExportRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Export run", runId);
        if (run.BlobKey is null)
        {
            throw new InvalidOperationException("The file of this run wasn't kept.");
        }

        byte[] bytes;
        try
        {
            await using var stream = await blobs.OpenReadAsync(run.BlobKey, cancellationToken);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cancellationToken);
            bytes = ms.ToArray();
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException("The file of this run is no longer in storage.");
        }

        var checksum = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(checksum, run.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The stored file no longer matches its checksum, so it was not given out.");
        }

        await securityLog.RecordAsync(SecurityEventExport, $"run {run.Id:N}", SecurityOutcomes.Success, $"download of {run.FileName}", cancellationToken: cancellationToken);
        return new AccountingExportResultDto(run.Id, run.FileName, run.ContentType, bytes, run.RowCount, run.RecordCount, run.ControlTotal, run.Checksum, Warnings(run));
    }

    private AccountingExportRunDto ToDto(AccountingExportRun r)
    {
        var format = exporters.FirstOrDefault(e => e.Format.Id == r.FormatId)?.Format;
        return new AccountingExportRunDto(r.Id, Enum.Parse<AccountingTarget>(r.Target), r.FormatId, format?.Name ?? r.FormatId, Enum.Parse<AccountingExportKind>(r.Kind), r.PeriodFrom, r.PeriodTo,
            r.FileName, r.RowCount, r.RecordCount, r.ControlTotal, r.Checksum, Warnings(r), r.UnmappedAllowed, r.UnmappedCount, r.IsRepeat, r.Note, r.CreatedBy, r.CreatedAtUtc, r.BlobKey is not null);
    }

    private static IReadOnlyList<string> Warnings(AccountingExportRun r) =>
        string.IsNullOrEmpty(r.WarningsJson) ? [] : JsonSerializer.Deserialize<List<string>>(r.WarningsJson, Json) ?? [];

    private static string Clip(string text, int max) => text.Length > max ? text[..max] : text;

    public static string KindLabel(AccountingExportKind kind) => kind switch
    {
        AccountingExportKind.ChartOfAccounts => "the chart of accounts",
        AccountingExportKind.JournalEntries => "journal entries",
        AccountingExportKind.Invoices => "invoices",
        AccountingExportKind.Payments => "customer payments",
        _ => "vendor bills",
    };
}
