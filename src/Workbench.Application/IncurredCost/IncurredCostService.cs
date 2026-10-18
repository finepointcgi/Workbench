using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.IncurredCost;
using Workbench.Domain.IncurredCost;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.IncurredCost;

/// <summary>
/// Incurred cost submissions: generation from the books, typed-in values, the draft → reviewed → certified → submitted
/// workflow (every step audited) and the .xlsx export. Permission checks are a decorator (Finance and administrators).
/// </summary>
public sealed class IncurredCostService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : IIncurredCostService
{
    private const string EntityType = "IncurredCostSubmission";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<IncurredCostSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var submissions = await db.Set<IncurredCostSubmission>().AsNoTracking().ToListAsync(cancellationToken);
        var ids = submissions.Select(s => s.Id).ToList();
        var inputs = (await db.Set<IncurredCostInput>().AsNoTracking().Where(i => ids.Contains(i.SubmissionId)).ToListAsync(cancellationToken)).ToLookup(i => i.SubmissionId);
        return submissions.OrderByDescending(s => s.FiscalYear).Select(s =>
            {
                var build = Build(s, inputs[s.Id]);
                return new IncurredCostSummaryDto(s.Id, s.FiscalYear, (IncurredCostState)(int)s.Status, s.Revision, s.GeneratedBy, s.GeneratedAtUtc,
                    build.Totals.FirstOrDefault(t => t.Schedule == "A" && t.Key == "claimedCost")?.Value ?? 0,
                    build.Checks.Count(c => c.Severity == IceCheckSeverity.Error), build.Checks.Count(c => c.Severity == IceCheckSeverity.Warning),
                    s.CertifiedBy, s.CertifiedAtUtc, s.SubmittedBy, s.SubmittedAtUtc);
            })
            .ToList();
    }

    public async Task<IncurredCostPackageDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var submission = await FindAsync(db, id, tracked: false, cancellationToken);
        return await ToDtoAsync(db, submission, cancellationToken);
    }

    public async Task<IncurredCostPackageDto> CreateAsync(CreateIncurredCostRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var submission = await IncurredCostGenerator.CreateAsync(db, request.FiscalYear, audit, clock, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, submission, cancellationToken);
    }

    public async Task<IncurredCostPackageDto> RegenerateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var submission = await FindAsync(db, id, tracked: true, cancellationToken);
        RequireDraft(submission, "regenerated");
        var inputs = await InputsAsync(db, id, cancellationToken);
        var before = Build(submission, inputs).Totals;
        var source = await IceSourceLoader.LoadAsync(db, submission.FiscalYear, cancellationToken);
        var after = IceBuilder.Build(source, IceInputs.From(inputs)).Totals;
        var changes = Diff(before, after);

        var now = clock.GetUtcNow().UtcDateTime;
        submission.SchedulesJson = source.Serialize();
        submission.LastDiffJson = JsonSerializer.Serialize(changes, Json);
        submission.Revision++;
        submission.GeneratedBy = audit.Actor;
        submission.GeneratedAtUtc = now;
        submission.UpdatedAtUtc = now;
        audit.Record(db, EntityType, submission.Id, "Regenerated", after: new { submission.FiscalYear, submission.Revision, Changed = changes.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, submission, cancellationToken);
    }

    public async Task<IncurredCostPackageDto> SaveInputsAsync(Guid id, SaveIceInputsRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var submission = await FindAsync(db, id, tracked: true, cancellationToken);
        RequireDraft(submission, "edited");
        var existing = await db.Set<IncurredCostInput>().Where(i => i.SubmissionId == id).ToListAsync(cancellationToken);
        var editable = Build(submission, existing).Inputs;
        var now = clock.GetUtcNow().UtcDateTime;
        var changed = new List<object>();
        foreach (var input in request.Inputs ?? [])
        {
            var cell = (input.Schedule ?? "", input.RowKey ?? "", input.Column ?? "");
            if (!editable.Editable.Contains(cell))
            {
                throw new ArgumentException($"Schedule {input.Schedule} has no input {input.Column} on row {input.RowKey}.");
            }

            var value = string.IsNullOrWhiteSpace(input.Value) ? null : input.Value.Trim();
            if (value is not null && editable.Numeric.Contains(cell) && !IceInputs.TryParse(value, out _))
            {
                throw new ArgumentException($"\"{value}\" isn't an amount (Schedule {input.Schedule}).");
            }

            if (value is { Length: > 4000 })
            {
                throw new ArgumentException("An input can be at most 4,000 characters.");
            }

            var row = existing.FirstOrDefault(i => i.Schedule == cell.Item1 && i.RowKey == cell.Item2 && i.Column == cell.Item3);
            if (row?.Value == value)
            {
                continue;
            }

            changed.Add(new { input.Schedule, input.RowKey, input.Column, Before = row?.Value, After = value });
            if (row is null)
            {
                row = new IncurredCostInput { SubmissionId = id, Schedule = cell.Item1, RowKey = cell.Item2, Column = cell.Item3, UpdatedBy = audit.Actor };
                db.Set<IncurredCostInput>().Add(row);
                existing.Add(row);
            }

            row.Value = value;
            row.UpdatedBy = audit.Actor;
            row.UpdatedAtUtc = now;
        }

        if (changed.Count > 0)
        {
            submission.UpdatedAtUtc = now;
            audit.Record(db, EntityType, submission.Id, "InputsSaved", after: changed);
            await db.SaveChangesAsync(cancellationToken);
        }

        return await ToDtoAsync(db, submission, cancellationToken);
    }

    public async Task<IncurredCostPackageDto> MarkReviewedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var submission = await FindAsync(db, id, tracked: true, cancellationToken);
        RequireDraft(submission, "marked reviewed");
        var errors = Build(submission, await InputsAsync(db, id, cancellationToken)).Checks.Where(c => c.Severity == IceCheckSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Fix these first: {string.Join("; ", errors.Select(e => e.Title))}.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        submission.Status = IncurredCostStatus.Reviewed;
        submission.ReviewedBy = audit.Actor;
        submission.ReviewedAtUtc = now;
        submission.UpdatedAtUtc = now;
        audit.Record(db, EntityType, submission.Id, "Reviewed", after: new { submission.FiscalYear, submission.Revision });
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, submission, cancellationToken);
    }

    public async Task<IncurredCostPackageDto> ReopenAsync(Guid id, ReopenIncurredCostRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ArgumentException("Say why the package goes back to draft.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var submission = await FindAsync(db, id, tracked: true, cancellationToken);
        if (submission.Status is not (IncurredCostStatus.Reviewed or IncurredCostStatus.Certified))
        {
            throw new InvalidOperationException(submission.IsLocked
                ? $"The FY{submission.FiscalYear} package was submitted and is locked."
                : $"The FY{submission.FiscalYear} package is already a draft.");
        }

        var before = new { Status = submission.Status.ToString(), submission.ReviewedBy, submission.CertifiedBy, submission.CertifierName };
        submission.Status = IncurredCostStatus.Draft;
        submission.ReviewedBy = null;
        submission.ReviewedAtUtc = null;
        submission.CertifiedBy = null;
        submission.CertifiedAtUtc = null;
        submission.CertifierName = null;
        submission.CertifierTitle = null;
        submission.CertificateText = null;
        submission.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, EntityType, submission.Id, "Reopened", before, new { Status = "Draft" }, request.Reason.Trim());
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, submission, cancellationToken);
    }

    public async Task<IncurredCostPackageDto> CertifyAsync(Guid id, CertifyIncurredCostRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.CertifierName) || string.IsNullOrWhiteSpace(request.CertifierTitle))
        {
            throw new ArgumentException("Give the certifying official's name and title.");
        }

        if (!request.Acknowledged)
        {
            throw new ArgumentException("The certifying official has to confirm the certificate.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var submission = await FindAsync(db, id, tracked: true, cancellationToken);
        if (submission.Status != IncurredCostStatus.Reviewed)
        {
            throw new InvalidOperationException(submission.Status == IncurredCostStatus.Draft
                ? "Mark the package reviewed before it's certified."
                : $"The FY{submission.FiscalYear} package is already {submission.Status.ToString().ToLowerInvariant()}.");
        }

        var package = Build(submission, await InputsAsync(db, id, cancellationToken));
        var firm = package.Schedule("Cover").Row("contractor")?.TextOf("value");
        if (string.IsNullOrWhiteSpace(firm))
        {
            throw new ArgumentException("Type the contractor's name under Contractor information first; it goes on the certificate.");
        }

        var source = IceSource.Deserialize(submission.SchedulesJson);
        var now = clock.GetUtcNow().UtcDateTime;
        submission.Status = IncurredCostStatus.Certified;
        submission.CertifiedBy = audit.Actor;
        submission.CertifiedAtUtc = now;
        submission.CertifierName = request.CertifierName.Trim();
        submission.CertifierTitle = request.CertifierTitle.Trim();
        submission.CertificateText = IceBuilder.CertificateText(firm.Trim(), source.FiscalYear, source.Start, source.End);
        submission.UpdatedAtUtc = now;
        audit.Record(db, EntityType, submission.Id, "Certified",
            after: new { submission.FiscalYear, submission.Revision, submission.CertifierName, submission.CertifierTitle, submission.CertificateText });
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, submission, cancellationToken);
    }

    public async Task<IncurredCostPackageDto> SubmitAsync(Guid id, SubmitIncurredCostRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var submission = await FindAsync(db, id, tracked: true, cancellationToken);
        if (submission.Status != IncurredCostStatus.Certified)
        {
            throw new InvalidOperationException(submission.IsLocked
                ? $"The FY{submission.FiscalYear} package was already submitted."
                : "Only a certified package can be submitted.");
        }

        var reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        if (reference is { Length: > 200 })
        {
            throw new ArgumentException("The submission reference can be at most 200 characters.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        submission.Status = IncurredCostStatus.Submitted;
        submission.SubmittedBy = audit.Actor;
        submission.SubmittedAtUtc = now;
        submission.SubmissionReference = reference;
        submission.UpdatedAtUtc = now;
        audit.Record(db, EntityType, submission.Id, "Submitted", after: new { submission.FiscalYear, submission.Revision, Reference = reference });
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, submission, cancellationToken);
    }

    public async Task<IncurredCostExportFile> ExportWorkbookAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var package = await GetAsync(id, cancellationToken);
        return new IncurredCostExportFile($"incurred-cost-FY{package.FiscalYear}.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", IceWorkbook.Create(package));
    }

    // ---- Helpers ----

    private static async Task<IncurredCostSubmission> FindAsync(IWorkbenchDbContext db, Guid id, bool tracked, CancellationToken ct)
    {
        var query = db.Set<IncurredCostSubmission>().AsQueryable();
        return await (tracked ? query : query.AsNoTracking()).FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw WorkbenchNotFoundException.For("Incurred cost submission", id);
    }

    private static Task<List<IncurredCostInput>> InputsAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        db.Set<IncurredCostInput>().AsNoTracking().Where(i => i.SubmissionId == id).ToListAsync(ct);

    private static void RequireDraft(IncurredCostSubmission submission, string action)
    {
        if (submission.Status != IncurredCostStatus.Draft)
        {
            throw new InvalidOperationException(submission.IsLocked
                ? $"The FY{submission.FiscalYear} package was submitted and is locked; it can't be {action}."
                : $"The FY{submission.FiscalYear} package is {submission.Status.ToString().ToLowerInvariant()}; take it back to draft before it's {action}.");
        }
    }

    private static IceCertification? Certification(IncurredCostSubmission s) =>
        s.CertifiedAtUtc is { } at && s.CertificateText is { } text
            ? new IceCertification(s.CertifierName ?? "", s.CertifierTitle ?? "", s.CertifiedBy ?? "", at, text)
            : null;

    private static IceBuild Build(IncurredCostSubmission s, IEnumerable<IncurredCostInput> inputs) =>
        IceBuilder.Build(IceSource.Deserialize(s.SchedulesJson), IceInputs.From(inputs), Certification(s));

    internal static List<IceTotalChangeDto> Diff(IReadOnlyList<IceTotalDto> before, IReadOnlyList<IceTotalDto> after)
    {
        var old = before.ToDictionary(t => (t.Schedule, t.Key));
        var now = after.ToDictionary(t => (t.Schedule, t.Key));
        return old.Keys.Union(now.Keys)
            .Select(k => (Key: k, Before: old.GetValueOrDefault(k), After: now.GetValueOrDefault(k)))
            .Where(x => x.Before?.Value != x.After?.Value)
            .OrderBy(x => Array.IndexOf(IceBuilder.Order, x.Key.Schedule)).ThenBy(x => x.Key.Key, StringComparer.Ordinal)
            .Select(x => new IceTotalChangeDto(x.Key.Schedule, x.Key.Key, (x.After ?? x.Before)!.Label, x.Before?.Value, x.After?.Value))
            .ToList();
    }

    private static async Task<IncurredCostPackageDto> ToDtoAsync(IWorkbenchDbContext db, IncurredCostSubmission s, CancellationToken ct)
    {
        var build = Build(s, await InputsAsync(db, s.Id, ct));
        var source = IceSource.Deserialize(s.SchedulesJson);
        var changes = s.LastDiffJson is null ? [] : JsonSerializer.Deserialize<List<IceTotalChangeDto>>(s.LastDiffJson, Json) ?? [];
        return new IncurredCostPackageDto(s.Id, s.FiscalYear, source.Start, source.End, (IncurredCostState)(int)s.Status, s.Revision, s.GeneratedBy, s.GeneratedAtUtc,
            s.ReviewedBy, s.ReviewedAtUtc, s.CertifiedBy, s.CertifiedAtUtc, s.CertifierName, s.CertifierTitle, s.CertificateText,
            s.SubmittedBy, s.SubmittedAtUtc, s.SubmissionReference, build.Schedules, build.Checks, build.Totals, changes, build.Notes);
    }
}

/// <summary>Creates a fiscal year's draft package (used by the service and the development seed). Doesn't save.</summary>
public static class IncurredCostGenerator
{
    public static async Task<IncurredCostSubmission> CreateAsync(IWorkbenchDbContext db, int fiscalYear, AuditRecorder audit, TimeProvider clock, CancellationToken ct)
    {
        if (!await db.Set<FiscalYear>().AsNoTracking().AnyAsync(y => y.Year == fiscalYear, ct))
        {
            throw new ArgumentException($"FY{fiscalYear} isn't in the fiscal calendar.");
        }

        if (await db.Set<IncurredCostSubmission>().AsNoTracking().AnyAsync(s => s.FiscalYear == fiscalYear, ct))
        {
            throw new InvalidOperationException($"There's already an incurred cost package for FY{fiscalYear}; open it and regenerate the draft instead.");
        }

        var source = await IceSourceLoader.LoadAsync(db, fiscalYear, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var submission = new IncurredCostSubmission
        {
            FiscalYear = fiscalYear,
            Revision = 1,
            SchedulesJson = source.Serialize(),
            GeneratedBy = audit.Actor,
            GeneratedAtUtc = now,
            CreatedBy = audit.Actor,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Set<IncurredCostSubmission>().Add(submission);
        audit.Record(db, "IncurredCostSubmission", submission.Id, "Created", after: new { FiscalYear = fiscalYear, submission.Revision });
        return submission;
    }
}
