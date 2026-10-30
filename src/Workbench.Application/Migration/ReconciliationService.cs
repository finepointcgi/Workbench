using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Imports;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Migration;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Security;
using Workbench.Domain.Migration;

namespace Workbench.Application.Migration;

/// <summary>
/// Parallel-run reconciliation: compares uploaded Costpoint reports with Workbench's figures through saved mappings and
/// tolerances, keeps every run (file, mapping and rows) and its sign-off as go-live evidence. Permissions: see
/// <c>PermissionCheckingReconciliationService</c> (Finance or Admin).
/// </summary>
public sealed class ReconciliationService(
    IWorkbenchDbContextFactory dbFactory,
    IFiscalCalendarService calendar,
    ReconciliationSources sources,
    UnanetReconcileCauses unanetCauses,
    AuditRecorder audit,
    SecurityEventLog securityLog,
    TimeProvider clock) : IReconciliationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record StoredResult(IReadOnlyList<string> KeyLabels, IReadOnlyList<ReconRowDto> Rows, IReadOnlyList<ReconProblemDto> Problems, int OmittedMatched = 0);

    public Task<IReadOnlyList<ReconReportDto>> ListReportsAsync(CancellationToken cancellationToken = default) => Task.FromResult(ReconciliationReports.All);

    // ---- Mappings ----

    public async Task<IReadOnlyList<ReconMappingDto>> ListMappingsAsync(ReconReportKind? kind = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<ReconciliationMapping>().AsNoTracking();
        if (kind is { } k)
        {
            var report = (ReconciliationReport)(int)k;
            query = query.Where(m => m.Report == report);
        }

        return (await query.ToListAsync(cancellationToken)).OrderBy(m => m.Report).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(ToDto).ToList();
    }

    public async Task<ReconMappingDto> SaveMappingAsync(ReconMappingDto mapping, CancellationToken cancellationToken = default)
    {
        ReconciliationEngine.Validate(ReconciliationReports.Get(mapping.Kind), mapping);
        var name = mapping.Name.Trim();
        if (name.Length > 100)
        {
            throw new ArgumentException("The mapping name can be at most 100 characters.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = (ReconciliationReport)(int)mapping.Kind;
        var entity = await db.Set<ReconciliationMapping>().FirstOrDefaultAsync(m => m.Report == report && m.Name == name, cancellationToken);
        var before = entity?.MappingJson;
        var json = JsonSerializer.Serialize(mapping with { Id = null, Name = name, UpdatedBy = null, UpdatedAtUtc = null }, Json);
        if (entity is null)
        {
            entity = new ReconciliationMapping { Report = report, Name = name, MappingJson = json, UpdatedBy = audit.Actor };
            db.Set<ReconciliationMapping>().Add(entity);
        }

        entity.MappingJson = json;
        entity.UpdatedBy = audit.Actor;
        entity.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, nameof(ReconciliationMapping), entity.Id, before is null ? "Created" : "Updated", before: before, after: json);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task DeleteMappingAsync(Guid mappingId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entity = await db.Set<ReconciliationMapping>().FirstOrDefaultAsync(m => m.Id == mappingId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Reconciliation mapping", mappingId);
        db.Set<ReconciliationMapping>().Remove(entity);
        audit.Record(db, nameof(ReconciliationMapping), entity.Id, "Deleted", before: entity.MappingJson);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ReconMappingDto ToDto(ReconciliationMapping entity) =>
        (JsonSerializer.Deserialize<ReconMappingDto>(entity.MappingJson, Json) ?? throw new InvalidOperationException("Unreadable mapping."))
        with { Id = entity.Id, Kind = (ReconReportKind)(int)entity.Report, Name = entity.Name, UpdatedBy = entity.UpdatedBy, UpdatedAtUtc = entity.UpdatedAtUtc };

    // ---- Preview and run ----

    public async Task<ReconFilePreviewDto> PreviewFileAsync(PreviewReconFileRequest request, CancellationToken cancellationToken = default)
    {
        var report = ReconciliationReports.Get(request.Kind);
        var file = Read(report, request.FileName, request.Content);

        // The most recently saved mapping whose columns this file has; else a suggestion from the headers.
        var saved = (await ListMappingsAsync(request.Kind, cancellationToken)).OrderByDescending(m => m.UpdatedAtUtc).FirstOrDefault(m => Fits(report, m, file.Headers));
        var mapping = saved ?? ReconciliationEngine.Suggest(report, file, $"Costpoint {report.Title.ToLowerInvariant()}");
        return new ReconFilePreviewDto(request.Kind, request.FileName, file.Headers, file.Rows.Take(8).Select(r => r.Cells).ToList(), mapping);
    }

    private static bool Fits(ReconReportDto report, ReconMappingDto mapping, IReadOnlyList<string> headers)
    {
        try
        {
            ReconciliationEngine.Validate(report, mapping, headers);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static TabularFile Read(ReconReportDto report, string fileName, byte[] content)
    {
        if (content.Length == 0)
        {
            throw new ArgumentException("The file is empty.");
        }

        var file = TabularFile.Read(fileName, content, report.Fields.SelectMany(f => f.Aliases));
        if (file.Headers.Count == 0)
        {
            throw new ArgumentException($"{fileName} has no header row.");
        }

        return file;
    }

    public async Task<ReconRunDetailDto> RunAsync(RunReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        var report = ReconciliationReports.Get(request.Kind);
        if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 255)
        {
            throw new ArgumentException("Name the uploaded file (at most 255 characters).");
        }

        ReconMappingDto mapping;
        if (request.MappingId is { } mappingId)
        {
            mapping = (await ListMappingsAsync(request.Kind, cancellationToken)).FirstOrDefault(m => m.Id == mappingId)
                ?? throw WorkbenchNotFoundException.For("Reconciliation mapping", mappingId);
        }
        else
        {
            mapping = request.Mapping ?? throw new ArgumentException("Choose a saved mapping or map the columns.");
            if (mapping.Kind != request.Kind)
            {
                throw new ArgumentException("The mapping is for another report.");
            }
        }

        if (request.SaveMappingAs is { Length: > 0 } saveAs)
        {
            mapping = await SaveMappingAsync(mapping with { Name = saveAs }, cancellationToken);
        }

        var file = Read(report, request.FileName, request.Content);
        var scope = await ScopeAsync(report, mapping.Options, request.FiscalPeriodId, request.AsOf, cancellationToken);
        var figures = await sources.LoadAsync(request.Kind, scope, cancellationToken);
        var comparison = ReconciliationEngine.Compare(report, mapping, file, figures);
        if (report.Source == "Unanet")
        {
            comparison = comparison with { Rows = await unanetCauses.ExplainAsync(request.Kind, scope, comparison.Rows, cancellationToken) };
        }

        var keyLabels = report.Fields.Where(f => f.Role == ReconFieldRole.Key).Select(f => f.Label).ToList();
        var json = JsonSerializer.Serialize(new StoredResult(keyLabels, comparison.Rows, comparison.Problems), Json);
        if (json.Length > ReconciliationLimits.MaxResultJson)
        {
            // Keep every difference; drop matched rows (they're still counted) so the evidence fits.
            var kept = comparison.Rows.Where(r => r.Status != ReconRowStatus.Matched).ToList();
            json = JsonSerializer.Serialize(new StoredResult(keyLabels, kept, comparison.Problems.Take(5000).ToList(), comparison.Rows.Count - kept.Count), Json);
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = new ReconciliationRun
        {
            Report = (ReconciliationReport)(int)request.Kind,
            FiscalPeriodId = scope.Period.Id,
            PeriodName = scope.Period.Name,
            FromDate = report.UsesAsOf ? scope.Period.Start : scope.From,
            ToDate = report.UsesAsOf ? scope.AsOf : scope.To,
            MappingId = mapping.Id,
            MappingName = mapping.Name,
            MappingJson = JsonSerializer.Serialize(mapping, Json),
            FileName = request.FileName,
            FileContent = request.Content,
            Outcome = comparison.AllMatched ? ReconciliationOutcome.Matched : ReconciliationOutcome.Differences,
            KeyCount = comparison.Rows.Count,
            MatchedCount = comparison.Count(ReconRowStatus.Matched),
            DifferentCount = comparison.Count(ReconRowStatus.Different),
            OnlyCostpointCount = comparison.Count(ReconRowStatus.OnlyInCostpoint),
            OnlyWorkbenchCount = comparison.Count(ReconRowStatus.OnlyInWorkbench),
            ProblemCount = comparison.Problems.Count,
            PrimaryField = comparison.PrimaryField,
            CostpointTotal = comparison.CostpointTotal,
            WorkbenchTotal = comparison.WorkbenchTotal,
            ResultJson = json,
            RunBy = audit.Actor,
            RunAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.Set<ReconciliationRun>().Add(run);
        audit.Record(db, nameof(ReconciliationRun), run.Id, "Run", after: new
        {
            Report = report.Title, run.PeriodName, run.FileName, run.MappingName, run.Outcome, run.KeyCount, run.MatchedCount, run.DifferentCount,
            run.OnlyCostpointCount, run.OnlyWorkbenchCount, run.ProblemCount,
        });
        await db.SaveChangesAsync(cancellationToken);
        return Detail(run);
    }

    private async Task<ReconScope> ScopeAsync(ReconReportDto report, ReconOptionsDto options, Guid periodId, DateOnly? asOf, CancellationToken ct)
    {
        var periods = await calendar.ListPeriodsAsync(null, ct);
        var period = periods.FirstOrDefault(p => p.Id == periodId) ?? throw WorkbenchNotFoundException.For("Fiscal period", periodId);
        var first = periods.Where(p => p.FiscalYear == period.FiscalYear).MinBy(p => p.Start) ?? period;
        var range = report.UsesRange ? options.Range ?? report.DefaultRange : ReconRange.Period;
        var from = range switch
        {
            ReconRange.YearToDate => first.Start,
            ReconRange.InceptionToDate when report.Kind != ReconReportKind.IndirectRates => new DateOnly(2000, 1, 1),
            ReconRange.InceptionToDate => first.Start,
            _ => period.Start,
        };
        return new ReconScope(period, first, from, period.End, asOf ?? period.End, options.ProjectLevel);
    }

    // ---- Runs ----

    public async Task<IReadOnlyList<ReconRunSummaryDto>> ListRunsAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<ReconciliationRun>().AsNoTracking();
        if (fiscalPeriodId is { } id)
        {
            query = query.Where(r => r.FiscalPeriodId == id);
        }

        return (await query.Select(SummaryColumns).ToListAsync(cancellationToken)).OrderByDescending(r => r.RunAtUtc).Select(Summary).ToList();
    }

    public async Task<ReconRunDetailDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await db.Set<ReconciliationRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Reconciliation run", runId);
        return Detail(run);
    }

    public async Task<ReconDrillDownDto> DrillDownAsync(Guid runId, string key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await db.Set<ReconciliationRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Reconciliation run", runId);
        var detail = Detail(run);
        var row = detail.Rows.FirstOrDefault(r => r.Key == key) ?? throw new ArgumentException($"The run has no row '{key}'.");
        var report = ReconciliationReports.Get(detail.Run.Kind);
        var mapping = JsonSerializer.Deserialize<ReconMappingDto>(run.MappingJson, Json)!;
        var keyFields = report.Fields.Where(f => f.Role == ReconFieldRole.Key).Select(f => f.Name).ToList();
        var rewrites = new Dictionary<string, string>();
        var target = keyFields.Select((f, i) => ReconciliationEngine.NormalizeKey(row.KeyParts[i], f, mapping.Options, rewrites).Normal).ToList();

        // A Workbench detail line belongs to the key when its parts normalize to it; at WBS level a parent key also takes
        // its children's lines.
        bool Matches(IReadOnlyList<string> parts)
        {
            for (var i = 0; i < keyFields.Count; i++)
            {
                var part = i < parts.Count ? parts[i] : "";
                var normal = ReconciliationEngine.NormalizeKey(part, keyFields[i], mapping.Options, rewrites).Normal;
                var child = keyFields[i] == "Project" && !mapping.Options.ProjectLevel && normal.StartsWith(target[i] + ".", StringComparison.Ordinal);
                if (normal != target[i] && !child)
                {
                    return false;
                }
            }

            return true;
        }

        var scope = await ScopeAsync(report, mapping.Options, run.FiscalPeriodId, report.UsesAsOf ? run.ToDate : null, cancellationToken);
        if (!report.UsesAsOf)
        {
            scope = scope with { From = run.FromDate, To = run.ToDate };
        }

        var workbench = await sources.DetailAsync(detail.Run.Kind, scope, Matches, cancellationToken);
        var file = TabularFile.Read(run.FileName, run.FileContent, report.Fields.SelectMany(f => f.Aliases));
        var sourceRows = row.SourceRows.ToHashSet();
        return new ReconDrillDownDto(row.Key, workbench.Columns, workbench.Rows,
            ["Row", .. file.Headers], file.Rows.Where(r => sourceRows.Contains(r.Number)).Select(r => (IReadOnlyList<string>)[r.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), .. r.Cells]).ToList(),
            workbench.Note);
    }

    public async Task<ReconRunSummaryDto> SignOffAsync(Guid runId, SignOffReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await db.Set<ReconciliationRun>().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Reconciliation run", runId);
        if (run.SignedOffAtUtc is not null)
        {
            throw new ArgumentException($"This run was signed off by {run.SignedOffBy} on {run.SignedOffAtUtc:yyyy-MM-dd HH:mm} UTC; run the comparison again to sign off a new one.");
        }

        var note = request.Note?.Trim();
        if (run.Outcome == ReconciliationOutcome.Differences && string.IsNullOrEmpty(note))
        {
            throw new ArgumentException("This run has differences: explain them in a note to sign it off (for example timing items cleared next month).");
        }

        if (note is { Length: > 2000 })
        {
            throw new ArgumentException("The note can be at most 2000 characters.");
        }

        run.SignedOffBy = audit.Actor;
        run.SignedOffAtUtc = clock.GetUtcNow().UtcDateTime;
        run.SignOffNote = string.IsNullOrEmpty(note) ? null : note;
        audit.Record(db, nameof(ReconciliationRun), run.Id, "SignedOff", after: new { run.PeriodName, run.Report, run.Outcome, run.SignOffNote });
        await db.SaveChangesAsync(cancellationToken);
        return Summary(run);
    }

    public async Task<IReadOnlyList<ReconStatusDto>> GetStatusAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        var runs = await ListRunsAsync(fiscalPeriodId, cancellationToken);
        // The Costpoint evidence board: Unanet reconciliations have their own page and don't count towards the Costpoint go-live.
        return ReconciliationReports.All.Where(r => r.Source == "Costpoint").Select(r => new ReconStatusDto(r.Kind, r.Title,
            runs.FirstOrDefault(x => x.Kind == r.Kind), runs.FirstOrDefault(x => x.Kind == r.Kind && x.IsSignedOff))).ToList();
    }

    public async Task<ReconExportFile> ExportAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var detail = await GetRunAsync(runId, cancellationToken);
        var content = ReconciliationWorkbook.Build(detail, ReconciliationReports.Get(detail.Run.Kind));
        await securityLog.RecordAsync(SecurityEventTypes.ExportReport, $"reconciliation {detail.Run.Kind}", SecurityOutcomes.Success,
            $"{detail.Run.PeriodName}, run {runId.ToString()[..8]}", cancellationToken: cancellationToken);
        return new ReconExportFile($"reconciliation-{detail.Run.Kind.ToString().ToLowerInvariant()}-{detail.Run.PeriodName}-{detail.Run.RunAtUtc:yyyyMMddHHmm}.xlsx".Replace(' ', '-'),
            Reports.ReportFiles.XlsxContentType, content);
    }

    // ---- Mapping to DTOs ----

    private static readonly System.Linq.Expressions.Expression<Func<ReconciliationRun, ReconciliationRun>> SummaryColumns = r => new ReconciliationRun
    {
        Id = r.Id, Report = r.Report, FiscalPeriodId = r.FiscalPeriodId, PeriodName = r.PeriodName, FromDate = r.FromDate, ToDate = r.ToDate, MappingName = r.MappingName,
        MappingJson = "", FileName = r.FileName, Outcome = r.Outcome, KeyCount = r.KeyCount, MatchedCount = r.MatchedCount, DifferentCount = r.DifferentCount,
        OnlyCostpointCount = r.OnlyCostpointCount, OnlyWorkbenchCount = r.OnlyWorkbenchCount, ProblemCount = r.ProblemCount, PrimaryField = r.PrimaryField,
        CostpointTotal = r.CostpointTotal, WorkbenchTotal = r.WorkbenchTotal, ResultJson = "", RunBy = r.RunBy, RunAtUtc = r.RunAtUtc, SignedOffBy = r.SignedOffBy,
        SignedOffAtUtc = r.SignedOffAtUtc, SignOffNote = r.SignOffNote,
    };

    private static ReconRunSummaryDto Summary(ReconciliationRun r) => new(r.Id, (ReconReportKind)(int)r.Report, r.FiscalPeriodId, r.PeriodName, r.FromDate, r.ToDate, r.FileName,
        r.MappingName, (ReconRunOutcome)(int)r.Outcome, r.KeyCount, r.MatchedCount, r.DifferentCount, r.OnlyCostpointCount, r.OnlyWorkbenchCount, r.ProblemCount, r.PrimaryField,
        r.CostpointTotal, r.WorkbenchTotal, r.RunBy, r.RunAtUtc, r.SignedOffBy, r.SignedOffAtUtc, r.SignOffNote);

    private static ReconRunDetailDto Detail(ReconciliationRun run)
    {
        var report = ReconciliationReports.Get((ReconReportKind)(int)run.Report);
        var result = JsonSerializer.Deserialize<StoredResult>(run.ResultJson, Json)!;
        var mapping = JsonSerializer.Deserialize<ReconMappingDto>(run.MappingJson, Json)!;
        var measures = report.Fields.Where(f => f.Role == ReconFieldRole.Measure && mapping.Columns.Any(c => c.Field == f.Name && c.Column is not null)).ToList();
        return new ReconRunDetailDto(Summary(run), result.KeyLabels, measures, mapping.Tolerances, result.Rows, result.Problems);
    }
}

/// <summary>The differences report: summary, differences, every row and unreadable file rows.</summary>
public static class ReconciliationWorkbook
{
    public static byte[] Build(ReconRunDetailDto detail, ReconReportDto report)
    {
        using var workbook = new XLWorkbook();
        var run = detail.Run;
        var summary = workbook.Worksheets.Add("Summary");
        var r = 1;
        summary.Cell(r, 1).Value = $"Reconciliation: {report.Title}";
        summary.Cell(r, 1).Style.Font.Bold = true;
        summary.Cell(r++, 1).Style.Font.FontSize = 14;
        r++;
        foreach (var (label, value) in new (string, string)[]
        {
            ("Costpoint report", report.CostpointReport),
            ("Period", run.PeriodName),
            ("Amounts", report.UsesAsOf ? $"As of {run.To:yyyy-MM-dd}" : $"{run.From:yyyy-MM-dd} to {run.To:yyyy-MM-dd}"),
            ("File", run.FileName),
            ("Mapping", run.MappingName),
            ("Tolerances", string.Join("; ", detail.Tolerances.Select(t => $"{t.Field} ±{t.Absolute}{(t.Percent > 0 ? $" or {t.Percent}%" : "")}"))),
            ("Run by", $"{run.RunBy} at {run.RunAtUtc:yyyy-MM-dd HH:mm} UTC"),
            ("Outcome", run.Outcome == ReconRunOutcome.Matched ? "Matched" : "Differences"),
            ("Keys compared", run.Keys.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Matched", run.Matched.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Different", run.Different.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Only in Costpoint", run.OnlyInCostpoint.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Only in Workbench", run.OnlyInWorkbench.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Unreadable file rows", run.Problems.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ($"Costpoint total ({run.PrimaryField})", run.CostpointTotal.ToString("0.00##", System.Globalization.CultureInfo.InvariantCulture)),
            ($"Workbench total ({run.PrimaryField})", run.WorkbenchTotal.ToString("0.00##", System.Globalization.CultureInfo.InvariantCulture)),
            ("Signed off", run.IsSignedOff ? $"{run.SignedOffBy} at {run.SignedOffAtUtc:yyyy-MM-dd HH:mm} UTC" : "Not signed off"),
            ("Sign-off note", run.SignOffNote ?? ""),
        })
        {
            summary.Cell(r, 1).Value = label;
            summary.Cell(r, 1).Style.Font.Bold = true;
            summary.Cell(r++, 2).Value = value;
        }

        summary.Columns().AdjustToContents(1, r, 10, 80);
        Rows(workbook.Worksheets.Add("Differences"), detail, detail.Rows.Where(x => x.Status != ReconRowStatus.Matched));
        Rows(workbook.Worksheets.Add("All rows"), detail, detail.Rows);
        var problems = workbook.Worksheets.Add("Unreadable rows");
        problems.Cell(1, 1).Value = "File row";
        problems.Cell(1, 2).Value = "Problem";
        problems.Row(1).Style.Font.Bold = true;
        for (var i = 0; i < detail.ProblemRows.Count; i++)
        {
            problems.Cell(i + 2, 1).Value = detail.ProblemRows[i].Row;
            problems.Cell(i + 2, 2).Value = detail.ProblemRows[i].Message;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void Rows(IXLWorksheet sheet, ReconRunDetailDto detail, IEnumerable<ReconRowDto> rows)
    {
        var c = 1;
        foreach (var label in detail.KeyLabels)
        {
            sheet.Cell(1, c++).Value = label;
        }

        sheet.Cell(1, c++).Value = "Name";
        sheet.Cell(1, c++).Value = "Status";
        foreach (var measure in detail.Measures)
        {
            sheet.Cell(1, c++).Value = $"{measure.Label} (Costpoint)";
            sheet.Cell(1, c++).Value = $"{measure.Label} (Workbench)";
            sheet.Cell(1, c++).Value = $"{measure.Label} (difference)";
        }

        sheet.Cell(1, c).Value = "Costpoint file rows";
        sheet.Row(1).Style.Font.Bold = true;
        var r = 2;
        foreach (var row in rows)
        {
            c = 1;
            foreach (var part in row.KeyParts)
            {
                // Text, never a formula: keys come from the uploaded file.
                sheet.Cell(r, c++).SetValue(part);
            }

            sheet.Cell(r, c++).SetValue(row.Label ?? "");
            sheet.Cell(r, c++).SetValue(row.Status switch
            {
                ReconRowStatus.Matched => "Matched",
                ReconRowStatus.Different => "Different",
                ReconRowStatus.OnlyInCostpoint => "Only in Costpoint",
                _ => "Only in Workbench",
            });
            foreach (var measure in detail.Measures)
            {
                var value = row.Values.FirstOrDefault(v => v.Field == measure.Name);
                var format = measure.Format == ReconValueFormat.Rate ? "0.0000" : "#,##0.00";
                foreach (var amount in new[] { value?.Costpoint ?? 0, value?.Workbench ?? 0, value?.Difference ?? 0 })
                {
                    sheet.Cell(r, c).Value = amount;
                    sheet.Cell(r, c++).Style.NumberFormat.Format = format;
                }

                if (value is { WithinTolerance: false })
                {
                    sheet.Cell(r, c - 1).Style.Font.FontColor = XLColor.Red;
                }
            }

            sheet.Cell(r, c).SetValue(string.Join(", ", row.SourceRows));
            r++;
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, Math.Min(r, 500), 8, 60);
    }
}
