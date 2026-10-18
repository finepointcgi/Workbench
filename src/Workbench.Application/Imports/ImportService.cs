using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Imports.Smartsheet;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Imports;
using Workbench.Domain.Imports;
using ContractKind = Workbench.Contracts.Imports.ImportKind;
using ContractRecordAction = Workbench.Contracts.Imports.ImportRecordAction;
using ContractSource = Workbench.Contracts.Imports.ImportSource;
using ContractStatus = Workbench.Contracts.Imports.ImportJobStatus;
using ImportJobStatus = Workbench.Domain.Imports.ImportJobStatus;
using ImportKind = Workbench.Domain.Imports.ImportKind;
using ImportSource = Workbench.Domain.Imports.ImportSource;

namespace Workbench.Application.Imports;

/// <summary>
/// The import wizard's back end. Jobs move Uploaded → Mapped → Validated → DryRunDone → Imported (or Failed) →
/// RolledBack. Validation, the dry run and the import all run the same importer, in different <see cref="ImportMode"/>s.
/// </summary>
public sealed class ImportService(
    IWorkbenchDbContextFactory dbFactory,
    IEnumerable<IImporter> importers,
    IServiceProvider services,
    ICurrentUser currentUser,
    AuditRecorder audit,
    ImportRollback rollback,
    ISmartsheetClient smartsheet,
    IDataProtectionProvider protection,
    TimeProvider clock,
    ILogger<ImportService> logger) : IImportService
{
    /// <summary>How long a Smartsheet token stays usable in a job before it has to be entered again.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(8);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<Guid, byte> Running = new();
    private readonly ITimeLimitedDataProtector _tokens = protection.CreateProtector("Workbench.Imports.SmartsheetToken.v1").ToTimeLimitedDataProtector();

    public Task<IReadOnlyList<ImportKindDto>> ListKindsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ImportKindDto>>(importers.Select(ToDto).ToList());

    public async Task<IReadOnlyList<ImportJobDto>> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var jobs = await db.Set<ImportJob>().AsNoTracking().Include(j => j.Files).OrderByDescending(j => j.CreatedAtUtc).Take(500).ToListAsync(cancellationToken);
        return jobs.Select(ToDto).ToList();
    }

    public async Task<ImportJobDetailDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var job = await db.Set<ImportJob>().AsNoTracking().Include(j => j.Files).FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        return job is null ? null : await DetailAsync(job, cancellationToken);
    }

    public async Task<ImportJobDetailDto> UploadAsync(UploadImportRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Source == ContractSource.SmartsheetApi)
        {
            throw new ArgumentException("Smartsheet API imports start by connecting with a token, not by uploading files.");
        }

        var importer = Find((ImportSource)request.Source, (ImportKind)request.Kind);
        if (request.Files is not { Count: > 0 })
        {
            throw new ArgumentException("Choose a file to upload.");
        }

        var job = NewJob(importer, request.Options);
        var order = 0;
        foreach (var file in request.Files)
        {
            var name = Path.GetFileName(file.FileName ?? "").Trim();
            if (name.Length == 0 || !(name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || TabularFile.IsExcel(name) || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"'{name}' isn't a .csv or .xlsx file.");
            }

            if (file.Content is not { Length: > 0 })
            {
                throw new ArgumentException($"{name} is empty.");
            }

            if (file.Content.LongLength > TabularFile.MaxBytes)
            {
                throw new ArgumentException($"{name} is larger than {TabularFile.MaxBytes / 1024 / 1024} MB.");
            }

            // Fail fast on a file that can't be read at all.
            _ = TabularFile.Read(name, file.Content);
            job.Files.Add(new ImportJobFile
            {
                ImportJobId = job.Id,
                FileName = name,
                ContentType = TabularFile.IsExcel(name) ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv",
                Length = file.Content.LongLength,
                Content = file.Content,
                Order = order++,
            });
        }

        var preview = await importer.PreviewAsync(job, job.Files, cancellationToken);
        job.MappingJson = JsonSerializer.Serialize(preview.SuggestedMapping, Json);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        db.Set<ImportJob>().Add(job);
        audit.Record(db, nameof(ImportJob), job.Id, "Uploaded", after: new { job.Source, job.Kind, Files = job.Files.Select(f => new { f.FileName, f.Length }) });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(job, cancellationToken);
    }

    public async Task<ImportJobDetailDto> ConnectSmartsheetAsync(ConnectSmartsheetRequest request, CancellationToken cancellationToken = default)
    {
        var token = request.ApiToken?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            throw new ArgumentException("Enter a Smartsheet API token.");
        }

        var sheets = await smartsheet.ListSheetsAsync(token, cancellationToken);
        var importer = Find(ImportSource.SmartsheetApi, ImportKind.Sheets);
        var job = NewJob(importer, request.Options);
        job.EncryptedToken = _tokens.Protect(token, TokenLifetime);
        job.AvailableJson = JsonSerializer.Serialize(sheets.Select(s => new SmartsheetSheetInfoDto(s.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Name, s.ModifiedAt)).ToList(), Json);
        job.MappingJson = JsonSerializer.Serialize(new ImportMappingDto(new Dictionary<string, string?>(), []), Json);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        db.Set<ImportJob>().Add(job);
        audit.Record(db, nameof(ImportJob), job.Id, "Connected", after: new { job.Source, SheetCount = sheets.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(job, cancellationToken);
    }

    public async Task<ImportJobDetailDto> SelectSmartsheetSheetsAsync(Guid jobId, SelectSmartsheetSheetsRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SheetIds is not { Count: > 0 })
        {
            throw new ArgumentException("Choose at least one sheet.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var job = await LoadAsync(db, jobId, cancellationToken);
        RequireStatus(job, "choose sheets", ImportJobStatus.Uploaded, ImportJobStatus.Mapped, ImportJobStatus.Validated, ImportJobStatus.DryRunDone);
        if (job.Source != ImportSource.SmartsheetApi)
        {
            throw new ArgumentException("Only Smartsheet API jobs pick sheets.");
        }

        var token = Token(job);
        var available = Available(job).Select(s => s.Id).ToHashSet();
        db.Set<ImportJobFile>().RemoveRange(job.Files);
        job.Files.Clear();
        var order = 0;
        foreach (var id in request.SheetIds.Distinct())
        {
            if (!available.Contains(id))
            {
                throw new ArgumentException($"Sheet {id} isn't one this token can see.");
            }

            var sheet = await smartsheet.GetSheetAsync(token, id, cancellationToken);
            var content = JsonSerializer.SerializeToUtf8Bytes(sheet, Json);
            var file = new ImportJobFile { ImportJobId = job.Id, FileName = $"{sheet.Name}.json", ContentType = "application/json", Length = content.LongLength, Content = content, Order = order++ };
            job.Files.Add(file);
            db.Set<ImportJobFile>().Add(file);
        }

        var importer = Find(job.Source, job.Kind);
        var preview = await importer.PreviewAsync(job, job.Files, cancellationToken);
        job.MappingJson = JsonSerializer.Serialize(preview.SuggestedMapping, Json);
        job.OptionsJson = JsonSerializer.Serialize(Options(job) with { SheetIds = request.SheetIds.Distinct().ToList() }, Json);
        Reset(job, ImportJobStatus.Uploaded);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(job, cancellationToken);
    }

    public async Task<ImportJobDetailDto> SaveMappingAsync(Guid jobId, SaveImportMappingRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var job = await LoadAsync(db, jobId, cancellationToken);
        RequireStatus(job, "change the mapping", ImportJobStatus.Uploaded, ImportJobStatus.Mapped, ImportJobStatus.Validated, ImportJobStatus.DryRunDone);
        if (job.Files.Count == 0)
        {
            throw new ArgumentException(job.Source == ImportSource.SmartsheetApi ? "Choose the sheets to import first." : "Upload a file first.");
        }

        var importer = Find(job.Source, job.Kind);
        var known = importer.Fields.Select(f => f.Name).ToHashSet();
        var fields = request.Mapping.Fields.Where(kv => known.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => string.IsNullOrWhiteSpace(kv.Value) ? null : kv.Value.Trim());
        var options = request.Options with { SheetIds = Options(job).SheetIds };
        job.MappingJson = JsonSerializer.Serialize(new ImportMappingDto(fields, request.Mapping.Columns ?? []), Json);
        job.OptionsJson = JsonSerializer.Serialize(options, Json);
        Reset(job, ImportJobStatus.Mapped);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(job, cancellationToken);
    }

    public Task<ImportJobDetailDto> ValidateAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        RunAsync(jobId, ImportMode.Validate, cancellationToken);

    public Task<ImportJobDetailDto> DryRunAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        RunAsync(jobId, ImportMode.DryRun, cancellationToken);

    public Task<ImportJobDetailDto> ImportAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        RunAsync(jobId, ImportMode.Apply, cancellationToken);

    public async Task<IReadOnlyList<ImportRecordDto>> ListRecordsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<ImportJob>().AnyAsync(j => j.Id == jobId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Import job", jobId);
        }

        return (await db.Set<ImportJobRecord>().AsNoTracking().Where(r => r.ImportJobId == jobId).OrderBy(r => r.Sequence).ToListAsync(cancellationToken))
            .Select(r => new ImportRecordDto(r.Sequence, r.EntityType[(r.EntityType.LastIndexOf('.') + 1)..], r.EntityId, (ContractRecordAction)(int)r.Action, r.Label))
            .ToList();
    }

    public async Task<RollbackCheckDto> CheckRollbackAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var job = await LoadAsync(db, jobId, cancellationToken);
        var count = await db.Set<ImportJobRecord>().CountAsync(r => r.ImportJobId == jobId, cancellationToken);
        if (job.Status is not (ImportJobStatus.Imported or ImportJobStatus.Failed))
        {
            return new RollbackCheckDto(false, [job.Status == ImportJobStatus.RolledBack ? "This import was already rolled back." : "Only an imported job can be rolled back."], count);
        }

        var blockers = await rollback.FindBlockersAsync(jobId, cancellationToken);
        return new RollbackCheckDto(blockers.Count == 0, blockers, count);
    }

    public async Task<ImportJobDetailDto> RollBackAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (!Running.TryAdd(jobId, 0))
        {
            throw new ArgumentException("This job is already running.");
        }

        try
        {
            var check = await CheckRollbackAsync(jobId, cancellationToken);
            if (!check.CanRollBack)
            {
                throw new ArgumentException("This import can't be rolled back: " + string.Join(" ", check.Blockers));
            }

            await rollback.RollBackAsync(jobId, cancellationToken);
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var job = await LoadAsync(db, jobId, cancellationToken);
            job.Status = ImportJobStatus.RolledBack;
            job.RolledBackAtUtc = Now();
            job.RolledBackBy = currentUser.Email;
            job.UpdatedAtUtc = Now();
            job.EncryptedToken = null;
            audit.Record(db, nameof(ImportJob), job.Id, "RolledBack", after: new { check.RecordCount });
            await db.SaveChangesAsync(cancellationToken);
            return await DetailAsync(job, cancellationToken);
        }
        finally
        {
            Running.TryRemove(jobId, out _);
        }
    }

    public async Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var job = await LoadAsync(db, jobId, cancellationToken);
        RequireStatus(job, "discard it", ImportJobStatus.Uploaded, ImportJobStatus.Mapped, ImportJobStatus.Validated, ImportJobStatus.DryRunDone);
        db.Set<ImportJob>().Remove(job);
        audit.Record(db, nameof(ImportJob), job.Id, "Discarded");
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ImportJobDetailDto> RunAsync(Guid jobId, ImportMode mode, CancellationToken cancellationToken)
    {
        if (!Running.TryAdd(jobId, 0))
        {
            throw new ArgumentException("This job is already running.");
        }

        try
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var job = await LoadAsync(db, jobId, cancellationToken);
            switch (mode)
            {
                case ImportMode.Validate:
                    RequireStatus(job, "validate", ImportJobStatus.Mapped, ImportJobStatus.Validated, ImportJobStatus.DryRunDone);
                    break;
                case ImportMode.DryRun:
                    RequireStatus(job, "do a dry run (validate first)", ImportJobStatus.Validated, ImportJobStatus.DryRunDone);
                    break;
                default:
                    RequireStatus(job, "import (do a dry run first)", ImportJobStatus.DryRunDone);
                    break;
            }

            var importer = Find(job.Source, job.Kind);
            var token = job.Source == ImportSource.SmartsheetApi && mode == ImportMode.Apply && (Options(job).IncludeComments || Options(job).IncludeAttachments)
                ? Token(job)
                : null;
            var run = new ImportRun(mode, job.Id, dbFactory);
            var context = new ImportContext(job, job.Files.OrderBy(f => f.Order).ToList(), Mapping(job), Options(job), run, services, token);
            var failed = false;
            try
            {
                await importer.RunAsync(context, cancellationToken);
                await run.FinalizeAsync(cancellationToken);
            }
            catch (Exception ex) when (mode == ImportMode.Apply && ex is not OperationCanceledException and not UnauthorizedAccessException)
            {
                // Partly imported: keep what was recorded so it can be rolled back.
                logger.LogError(ex, "Import job {JobId} failed.", job.Id);
                await run.FinalizeAsync(CancellationToken.None);
                failed = true;
                job.Message = $"The import stopped: {ex.Message} Rows before the failure were imported; roll the job back to undo them.";
            }
            catch (Exception ex) when (ImportRun.IsRowError(ex))
            {
                run.Error("", 0, null, ex.Message);
            }

            job.TotalRows = run.TotalRows;
            job.CreateCount = run.Creates;
            job.UpdateCount = run.Updates;
            job.SkipCount = run.Skips;
            job.ErrorCount = run.ErrorRows;
            job.ErrorsJson = JsonSerializer.Serialize(run.Errors, Json);
            job.PlanJson = JsonSerializer.Serialize(run.Plan, Json);
            job.UpdatedAtUtc = Now();
            job.Status = mode switch
            {
                ImportMode.Validate => ImportJobStatus.Validated,
                ImportMode.DryRun => ImportJobStatus.DryRunDone,
                _ => failed ? ImportJobStatus.Failed : ImportJobStatus.Imported,
            };
            if (mode == ImportMode.Apply)
            {
                // The token is only needed while the job runs.
                job.EncryptedToken = null;
                job.ImportedAtUtc = Now();
                job.ImportedBy = currentUser.Email;
                if (!failed)
                {
                    job.Message = null;
                }

                audit.Record(db, nameof(ImportJob), job.Id, failed ? "ImportFailed" : "Imported",
                    after: new { job.TotalRows, job.CreateCount, job.UpdateCount, job.SkipCount, job.ErrorCount }, reason: job.Message);
            }

            await db.SaveChangesAsync(CancellationToken.None);
            return await DetailAsync(job, cancellationToken);
        }
        finally
        {
            Running.TryRemove(jobId, out _);
        }
    }

    private ImportJob NewJob(IImporter importer, ImportOptionsDto? options)
    {
        var now = Now();
        return new ImportJob
        {
            Source = importer.Source,
            Kind = importer.Kind,
            Status = ImportJobStatus.Uploaded,
            OptionsJson = JsonSerializer.Serialize(options ?? new ImportOptionsDto(), Json),
            CreatedBy = currentUser.Email,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    private void Reset(ImportJob job, ImportJobStatus status)
    {
        job.Status = status;
        job.TotalRows = job.CreateCount = job.UpdateCount = job.SkipCount = job.ErrorCount = 0;
        job.ErrorsJson = null;
        job.PlanJson = null;
        job.UpdatedAtUtc = Now();
    }

    private string Token(ImportJob job)
    {
        if (job.EncryptedToken is null)
        {
            throw new ArgumentException("The Smartsheet token for this job was discarded; connect again.");
        }

        try
        {
            return _tokens.Unprotect(job.EncryptedToken);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new ArgumentException($"The Smartsheet token for this job has expired (tokens are kept for {TokenLifetime.TotalHours:0} hours); connect again.");
        }
    }

    private static async Task<ImportJob> LoadAsync(IWorkbenchDbContext db, Guid jobId, CancellationToken cancellationToken) =>
        await db.Set<ImportJob>().Include(j => j.Files).FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
        ?? throw WorkbenchNotFoundException.For("Import job", jobId);

    private static void RequireStatus(ImportJob job, string what, params ImportJobStatus[] allowed)
    {
        if (!allowed.Contains(job.Status))
        {
            throw new ArgumentException($"This job is {job.Status}; you can't {what} now.");
        }
    }

    private IImporter Find(ImportSource source, ImportKind kind) =>
        importers.FirstOrDefault(i => i.Source == source && i.Kind == kind)
        ?? throw new ArgumentException($"There's no {source} importer for {kind}.");

    private async Task<ImportJobDetailDto> DetailAsync(ImportJob job, CancellationToken cancellationToken)
    {
        var importer = Find(job.Source, job.Kind);
        var files = job.Files.OrderBy(f => f.Order).ToList();
        ImportPreview preview;
        try
        {
            preview = files.Count == 0 ? new ImportPreview([], [], Mapping(job)) : await importer.PreviewAsync(job, files, cancellationToken);
        }
        catch (Exception ex) when (ImportRun.IsRowError(ex))
        {
            preview = new ImportPreview([], [], Mapping(job));
        }

        return new ImportJobDetailDto(
            ToDto(job),
            ToDto(importer),
            preview.Headers,
            preview.SampleRows,
            Mapping(job),
            Options(job),
            Deserialize<List<ImportErrorDto>>(job.ErrorsJson) ?? [],
            Deserialize<List<ImportPlanRowDto>>(job.PlanJson) ?? [],
            Available(job));
    }

    private static ImportMappingDto Mapping(ImportJob job) =>
        Deserialize<ImportMappingDto>(job.MappingJson) is { } m ? m with { Columns = m.Columns ?? [], Fields = m.Fields ?? new Dictionary<string, string?>() }
            : new ImportMappingDto(new Dictionary<string, string?>(), []);

    private static ImportOptionsDto Options(ImportJob job) => Deserialize<ImportOptionsDto>(job.OptionsJson) ?? new ImportOptionsDto();

    private static IReadOnlyList<SmartsheetSheetInfoDto> Available(ImportJob job) => Deserialize<List<SmartsheetSheetInfoDto>>(job.AvailableJson) ?? [];

    private static T? Deserialize<T>(string? json) where T : class => json is null ? null : JsonSerializer.Deserialize<T>(json, Json);

    private static ImportKindDto ToDto(IImporter importer) =>
        new((ContractSource)(int)importer.Source, (ContractKind)(int)importer.Kind, importer.Title, importer.Description,
            importer.Fields.Select(f => f.ToDto()).ToList(), importer.SampleFile);

    private static ImportJobDto ToDto(ImportJob job) => new(
        job.Id, (ContractSource)(int)job.Source, (ContractKind)(int)job.Kind, (ContractStatus)(int)job.Status,
        job.Files.OrderBy(f => f.Order).Select(f => new ImportFileDto(f.FileName, f.Length)).ToList(),
        new ImportCountsDto(job.TotalRows, job.CreateCount, job.UpdateCount, job.SkipCount, job.ErrorCount),
        job.Message, job.CreatedBy, job.CreatedAtUtc, job.UpdatedAtUtc, job.ImportedAtUtc, job.RolledBackAtUtc, job.RolledBackBy,
        job.EncryptedToken is not null);

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;
}
