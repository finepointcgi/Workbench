using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Compliance;
using Workbench.Domain.Compliance;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Compliance;

/// <summary>
/// Procurement files for purchase orders and subcontracts, their CPSR scoring, the readiness dashboard over a sample and
/// the reviewer export. Competition evidence from requests for quotes comes through <see cref="IProcurementCompetitionSource"/>.
/// </summary>
public sealed class CpsrService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    IProcurementCompetitionSource competition,
    IOptions<CpsrOptions> options,
    TimeProvider clock) : ICpsrService
{
    public const string FileEntity = nameof(ProcurementFile);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(UtcNow);
    private CpsrSettingsDto Settings => options.Value.ToSettings();

    public Task<CpsrSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);

    public async Task<CpsrReadinessDto> GetReadinessAsync(CpsrReviewRequest request, CancellationToken cancellationToken = default)
    {
        var (from, to, sample) = await SampleAsync(request, cancellationToken);
        return Summarize(from, to, sample.Population, sample.Files);
    }

    public async Task<ProcurementFileDto> GetFileAsync(ProcurementDocumentKind kind, Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await FileDtoAsync(db, kind, documentId, cancellationToken);
    }

    public async Task<ProcurementFileDto> SaveFileAsync(ProcurementDocumentKind kind, Guid documentId, SaveProcurementFileRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Competition) || !Enum.IsDefined(request.PriceAnalysis))
        {
            throw new ArgumentException("Unknown competition or analysis type.");
        }

        if (request.QuotesSolicited is < 0 || request.QuotesReceived is < 0)
        {
            throw new ArgumentException("Quote counts can't be negative.");
        }

        if (request.QuotesSolicited is { } solicited && request.QuotesReceived > solicited)
        {
            throw new ArgumentException("More quotes received than were asked for.");
        }

        if (request.DebarmentCheckedOn > Today)
        {
            throw new ArgumentException("The exclusions check date is in the future.");
        }

        var clauses = CpsrScoring.Clauses(request.FlowdownClauses);
        if (clauses.Any(c => c.Length > 40))
        {
            throw new ArgumentException("A clause number is at most 40 characters (for example 52.204-21).");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await DocumentAsync(db, kind, documentId, cancellationToken);
        var file = await FindFileAsync(db, kind, documentId, tracked: true, cancellationToken);
        var before = file is null ? null : Snapshot(file);
        file ??= new ProcurementFile
        {
            PurchaseOrderId = kind == ProcurementDocumentKind.PurchaseOrder ? documentId : null,
            SubcontractId = kind == ProcurementDocumentKind.Subcontract ? documentId : null,
            UpdatedBy = audit.Actor,
        };
        file.Competition = (CompetitionMethod)(int)request.Competition;
        file.QuotesSolicited = request.QuotesSolicited;
        file.QuotesReceived = request.QuotesReceived;
        file.SoleSourceJustification = PurchasingSupport.Optional(request.SoleSourceJustification, "Sole-source justification", 4000);
        file.PriceAnalysis = (PriceAnalysisKind)(int)request.PriceAnalysis;
        file.PriceAnalysisNotes = PurchasingSupport.Optional(request.PriceAnalysisNotes, "Price analysis notes", 4000);
        file.FlowdownClauses = string.Join(",", clauses);
        if (file.FlowdownClauses.Length > 2000)
        {
            throw new ArgumentException("The flowdown clause list is at most 2,000 characters.");
        }

        file.DebarmentCheckedOn = request.DebarmentCheckedOn;
        file.DebarmentResult = PurchasingSupport.Optional(request.DebarmentResult, "Exclusions check result", 200);
        file.SmallBusinessReportPeriod = PurchasingSupport.Optional(request.SmallBusinessReportPeriod, "Report period", 64);
        file.Notes = PurchasingSupport.Optional(request.Notes, "Notes", 4000);
        file.UpdatedBy = audit.Actor;
        file.UpdatedAtUtc = UtcNow;
        if (before is null)
        {
            db.Set<ProcurementFile>().Add(file);
        }

        audit.Record(db, FileEntity, file.Id, before is null ? "Created" : "Updated", before, Snapshot(file));
        await db.SaveChangesAsync(cancellationToken);
        return await FileDtoAsync(db, kind, documentId, cancellationToken);
    }

    public async Task<ProcurementFileDto> ApproveAsync(ProcurementDocumentKind kind, Guid documentId, ApproveProcurementFileRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var level = Settings.ApprovalLevels.FirstOrDefault(l => l.Level == request.Level)
            ?? throw new ArgumentException($"Approval levels are 1 to {Settings.ApprovalLevels.Count}.");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await DocumentAsync(db, kind, documentId, cancellationToken);
        var file = await FindFileAsync(db, kind, documentId, tracked: true, cancellationToken);
        if (file is null)
        {
            file = new ProcurementFile
            {
                PurchaseOrderId = kind == ProcurementDocumentKind.PurchaseOrder ? documentId : null,
                SubcontractId = kind == ProcurementDocumentKind.Subcontract ? documentId : null,
                UpdatedBy = audit.Actor,
                UpdatedAtUtc = UtcNow,
            };
            db.Set<ProcurementFile>().Add(file);
        }

        if (file.Approvals.Any(a => a.Level == level.Level))
        {
            throw new InvalidOperationException($"The file already has its {level.Name} approval.");
        }

        var approval = new ProcurementFileApproval
        {
            FileId = file.Id,
            Level = level.Level,
            LevelName = level.Name,
            ApprovedBy = audit.Actor,
            ApprovedAtUtc = UtcNow,
            Comment = PurchasingSupport.Optional(request.Comment, "Comment", 1000),
        };
        file.Approvals.Add(approval);
        db.Set<ProcurementFileApproval>().Add(approval);
        audit.Record(db, FileEntity, file.Id, "Approved", after: new { approval.Level, approval.LevelName, approval.Comment });
        await db.SaveChangesAsync(cancellationToken);
        return await FileDtoAsync(db, kind, documentId, cancellationToken);
    }

    public async Task<ComplianceExportFile> ExportReviewAsync(CpsrReviewRequest request, CancellationToken cancellationToken = default)
    {
        var (from, to, sample) = await SampleAsync(request, cancellationToken);
        var summary = Summarize(from, to, sample.Population, sample.Files);
        var html = new StringBuilder();
        ComplianceHtml.Begin(html, $"Purchasing system review file, {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
        html.Append("<p>").Append(Enc($"Sample of {summary.SampleSize} of {summary.Population} purchase orders and subcontracts. Score {summary.Score}% ({summary.Rating}). Prepared {UtcNow:yyyy-MM-dd HH:mm} UTC by {audit.Actor}.")).Append("</p>");
        html.Append("<h2>Results by element</h2><table><thead><tr><th>Element</th><th>Applies to</th><th>Met</th><th>Percent met</th></tr></thead><tbody>");
        foreach (var e in summary.Elements)
        {
            html.Append($"<tr><td>{Enc(e.Label)}</td><td>{e.Applicable}</td><td>{e.Met}</td><td>{e.PercentMet:0.#}%</td></tr>");
        }

        html.Append("</tbody></table>");
        foreach (var f in sample.Files)
        {
            html.Append($"<h2>{Enc(f.Kind == ProcurementDocumentKind.PurchaseOrder ? "Purchase order" : "Subcontract")} {Enc(f.Number)}: {Enc(f.VendorName)}</h2>");
            html.Append("<dl>");
            Dl(html, "Date", f.Date.ToString("yyyy-MM-dd"));
            Dl(html, "Value", f.Value.ToString("N2", System.Globalization.CultureInfo.InvariantCulture));
            Dl(html, "Government contract", f.GovernmentFunded ? f.PrimeContractNumber ?? "Yes" : "No");
            Dl(html, "Vendor business size", f.VendorSize ?? "Not recorded");
            Dl(html, "Competition", f.Competition switch
            {
                CompetitionType.Competitive => $"Competitive: {f.QuotesReceived?.ToString() ?? "?"} of {f.QuotesSolicited?.ToString() ?? "?"} quotes received",
                CompetitionType.SoleSource => "Sole source",
                CompetitionType.MicroPurchase => "Micro-purchase",
                _ => f.RfqEvidence is { } r ? $"From {r.Reference}: {r.QuotesReceived} of {r.QuotesSolicited} quotes received" : "Not recorded",
            });
            if (f.SoleSourceJustification is { } j)
            {
                Dl(html, "Sole-source justification", j);
            }

            Dl(html, "Price or cost analysis", $"{f.PriceAnalysis}{(f.PriceAnalysisNotes is { } n ? $": {n}" : "")}");
            Dl(html, "Flowdown clauses", f.FlowdownClauses.Count == 0 ? "None recorded" : string.Join(", ", f.FlowdownClauses));
            Dl(html, "Exclusions check", f.DebarmentCheckedOn is { } d ? $"{d:yyyy-MM-dd} {f.DebarmentResult}" : "Not recorded");
            Dl(html, "Small business report", f.SmallBusinessReportPeriod ?? "Not recorded");
            Dl(html, "Approvals", f.Approvals.Count == 0 ? "None" : string.Join("; ", f.Approvals.Select(a => $"{a.LevelName}: {a.ApprovedBy} on {a.ApprovedAtUtc:yyyy-MM-dd}")));
            html.Append("</dl><table><thead><tr><th>Element</th><th>Result</th><th>Detail</th></tr></thead><tbody>");
            foreach (var e in f.Elements)
            {
                html.Append($"<tr><td>{Enc(e.Label)}</td><td>{Enc(e.Status == CpsrElementStatus.NotApplicable ? "Doesn't apply" : e.Status.ToString())}</td><td>{Enc(e.Detail)}</td></tr>");
            }

            html.Append($"</tbody></table><p>File score: {f.Score}%</p>");
        }

        ComplianceHtml.End(html);
        return new ComplianceExportFile($"cpsr-review-{from:yyyyMMdd}-{to:yyyyMMdd}.html", "text/html", html.ToString());

        static void Dl(StringBuilder html, string term, string value) => html.Append($"<dt>{Enc(term)}</dt><dd>{Enc(value)}</dd>");
    }

    // ---- Helpers ----

    private sealed record SampleResult(int Population, IReadOnlyList<ProcurementFileDto> Files);

    private async Task<(DateOnly From, DateOnly To, SampleResult Sample)> SampleAsync(CpsrReviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var to = request.To ?? Today;
        var from = request.From ?? to.AddYears(-1).AddDays(1);
        if (from > to)
        {
            throw new ArgumentException("The review range starts after it ends.");
        }

        var size = Math.Clamp(request.SampleSize, 1, 500);
        await using var db = await dbFactory.CreateAsync(ct);
        var documents = await DocumentsAsync(db, from, to, ct);
        var sat = options.Value.SimplifiedAcquisitionThreshold;
        var high = documents.Where(d => d.Value > sat).OrderByDescending(d => d.Value).ToList();
        var rest = documents.Where(d => d.Value <= sat).OrderBy(d => Pick(d.Id, request.Seed)).ThenBy(d => d.Number, StringComparer.Ordinal).Take(Math.Max(0, size - high.Count));
        var sample = high.Concat(rest).OrderBy(d => d.Date).ThenBy(d => d.Number, StringComparer.Ordinal).ToList();
        var files = await FilesAsync(db, sample, ct);
        return (from, to, new SampleResult(documents.Count, files));
    }

    /// <summary>A repeatable pseudo-random sort key (FNV-1a over the id and the seed).</summary>
    private static uint Pick(Guid id, int seed)
    {
        var hash = 2166136261u ^ (uint)seed;
        foreach (var b in id.ToByteArray())
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }

    private CpsrReadinessDto Summarize(DateOnly from, DateOnly to, int population, IReadOnlyList<ProcurementFileDto> files)
    {
        var all = files.SelectMany(f => f.Elements).ToList();
        var elements = Enum.GetValues<CpsrElement>().Select(e =>
        {
            var applicable = all.Count(x => x.Element == e && x.Status != CpsrElementStatus.NotApplicable);
            var met = all.Count(x => x.Element == e && x.Status == CpsrElementStatus.Met);
            return new CpsrElementSummaryDto(e, CpsrScoring.Label(e), applicable, met, applicable == 0 ? 100 : decimal.Round(100m * met / applicable, 1));
        }).ToList();
        var score = CpsrScoring.Score(all);
        var missing = files.SelectMany(f => f.Elements.Where(e => e.Status == CpsrElementStatus.Missing)
            .Select(e => new CpsrMissingEvidenceDto(f.Kind, f.DocumentId, f.Number, e.Element, e.Label, e.Detail))).ToList();
        var sample = files.Select(f => new CpsrSampleRowDto(f.Kind, f.DocumentId, f.Number, f.VendorName, f.Date, f.Value, f.GovernmentFunded, f.Score,
            f.Elements.Count(e => e.Status == CpsrElementStatus.Missing))).ToList();
        return new CpsrReadinessDto(from, to, population, files.Count, score, files.Count == 0 ? "Nothing to review" : CpsrScoring.Rating(score), elements, sample, missing, Settings);
    }

    /// <summary>Issued (or later) purchase orders and active or closed subcontracts dated in the range.</summary>
    private static async Task<List<CpsrDocument>> DocumentsAsync(IWorkbenchDbContext db, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var orders = await db.Set<PurchaseOrder>().AsNoTracking().Include(o => o.Lines)
            .Where(o => o.OrderDate >= from && o.OrderDate <= to && o.Status != PurchaseOrderStatus.Draft && o.Status != PurchaseOrderStatus.Cancelled)
            .ToListAsync(ct);
        var subs = await db.Set<Subcontract>().AsNoTracking()
            .Where(s => s.PeriodStart >= from && s.PeriodStart <= to && s.Status != SubcontractStatus.Draft).ToListAsync(ct);
        return await ToDocumentsAsync(db, orders, subs, ct);
    }

    private static async Task<List<CpsrDocument>> ToDocumentsAsync(IWorkbenchDbContext db, IReadOnlyList<PurchaseOrder> orders, IReadOnlyList<Subcontract> subs, CancellationToken ct)
    {
        var vendorIds = orders.Select(o => o.VendorId).Concat(subs.Select(s => s.VendorId)).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var projectIds = orders.SelectMany(o => o.Lines).Where(l => l.ProjectId != null).Select(l => l.ProjectId!.Value).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ContractId, ct);
        var contractIds = projects.Values.Where(c => c != null).Select(c => c!.Value).Concat(subs.Where(s => s.PrimeContractId != null).Select(s => s.PrimeContractId!.Value)).Distinct().ToList();
        var contracts = await db.Set<Contract>().AsNoTracking().Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.ContractNumber, ct);
        var result = new List<CpsrDocument>();
        foreach (var o in orders)
        {
            var vendor = vendors.GetValueOrDefault(o.VendorId);
            var contract = o.Lines.Where(l => l.ProjectId != null).Select(l => projects.GetValueOrDefault(l.ProjectId!.Value)).FirstOrDefault(c => c != null);
            result.Add(new CpsrDocument(ProcurementDocumentKind.PurchaseOrder, o.Id, o.Number, vendor?.Name ?? "", vendor?.BusinessSize, o.OrderDate, o.Total,
                o.Lines.Any(l => l.ProjectId != null), contract is { } c ? contracts.GetValueOrDefault(c) : null));
        }

        foreach (var s in subs)
        {
            var vendor = vendors.GetValueOrDefault(s.VendorId);
            result.Add(new CpsrDocument(ProcurementDocumentKind.Subcontract, s.Id, s.Number, vendor?.Name ?? "", vendor?.BusinessSize, s.PeriodStart, s.CeilingValue,
                s.PrimeContractId != null, s.PrimeContractId is { } c ? contracts.GetValueOrDefault(c) : null));
        }

        return result;
    }

    private static async Task<CpsrDocument> DocumentAsync(IWorkbenchDbContext db, ProcurementDocumentKind kind, Guid id, CancellationToken ct)
    {
        if (kind == ProcurementDocumentKind.PurchaseOrder)
        {
            var order = await db.Set<PurchaseOrder>().AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Purchase order", id);
            return (await ToDocumentsAsync(db, [order], [], ct))[0];
        }

        if (kind == ProcurementDocumentKind.Subcontract)
        {
            var sub = await db.Set<Subcontract>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Subcontract", id);
            return (await ToDocumentsAsync(db, [], [sub], ct))[0];
        }

        throw new ArgumentException("Unknown document kind.");
    }

    private static async Task<ProcurementFile?> FindFileAsync(IWorkbenchDbContext db, ProcurementDocumentKind kind, Guid id, bool tracked, CancellationToken ct)
    {
        var q = db.Set<ProcurementFile>().Include(f => f.Approvals).AsQueryable();
        if (!tracked)
        {
            q = q.AsNoTracking();
        }

        return kind == ProcurementDocumentKind.PurchaseOrder
            ? await q.FirstOrDefaultAsync(f => f.PurchaseOrderId == id, ct)
            : await q.FirstOrDefaultAsync(f => f.SubcontractId == id, ct);
    }

    private async Task<ProcurementFileDto> FileDtoAsync(IWorkbenchDbContext db, ProcurementDocumentKind kind, Guid id, CancellationToken ct) =>
        (await FilesAsync(db, [await DocumentAsync(db, kind, id, ct)], ct))[0];

    private async Task<IReadOnlyList<ProcurementFileDto>> FilesAsync(IWorkbenchDbContext db, IReadOnlyList<CpsrDocument> documents, CancellationToken ct)
    {
        var orderIds = documents.Where(d => d.Kind == ProcurementDocumentKind.PurchaseOrder).Select(d => d.Id).ToList();
        var subIds = documents.Where(d => d.Kind == ProcurementDocumentKind.Subcontract).Select(d => d.Id).ToList();
        var files = await db.Set<ProcurementFile>().AsNoTracking().Include(f => f.Approvals)
            .Where(f => (f.PurchaseOrderId != null && orderIds.Contains(f.PurchaseOrderId.Value)) || (f.SubcontractId != null && subIds.Contains(f.SubcontractId.Value)))
            .ToListAsync(ct);
        var byDocument = files.ToDictionary(f => f.PurchaseOrderId ?? f.SubcontractId!.Value);
        var rfqs = orderIds.Count == 0 ? new Dictionary<Guid, CompetitionEvidenceDto>() : await competition.GetForPurchaseOrdersAsync(orderIds, ct);
        var settings = Settings;
        return documents.Select(d =>
        {
            var file = byDocument.GetValueOrDefault(d.Id);
            var rfq = d.Kind == ProcurementDocumentKind.PurchaseOrder ? rfqs.GetValueOrDefault(d.Id) : null;
            var elements = CpsrScoring.Evaluate(d, file, rfq, settings);
            return new ProcurementFileDto(file?.Id, d.Kind, d.Id, d.Number, d.VendorName, d.VendorSize, d.Date, d.Value, d.GovernmentFunded, d.PrimeContractNumber,
                (CompetitionType)(int)(file?.Competition ?? CompetitionMethod.NotRecorded), file?.QuotesSolicited, file?.QuotesReceived, file?.SoleSourceJustification,
                (PriceAnalysisType)(int)(file?.PriceAnalysis ?? PriceAnalysisKind.None), file?.PriceAnalysisNotes, CpsrScoring.Clauses((file?.FlowdownClauses ?? "").Split(',')),
                CpsrScoring.RequiredClauses(d, settings), file?.DebarmentCheckedOn, file?.DebarmentResult, file?.SmallBusinessReportPeriod, file?.Notes,
                (file?.Approvals ?? []).OrderBy(a => a.Level).Select(a => new ProcurementFileApprovalDto(a.Level, a.LevelName, a.ApprovedBy, a.ApprovedAtUtc, a.Comment)).ToList(),
                CpsrScoring.RequiredApprovals(d.Value, settings), rfq, elements, CpsrScoring.Score(elements), file?.UpdatedBy, file?.UpdatedAtUtc);
        }).ToList();
    }

    private static string Enc(string text) => WebUtility.HtmlEncode(text);

    private static object Snapshot(ProcurementFile f) => new
    {
        Competition = f.Competition.ToString(),
        f.QuotesSolicited,
        f.QuotesReceived,
        f.SoleSourceJustification,
        PriceAnalysis = f.PriceAnalysis.ToString(),
        f.PriceAnalysisNotes,
        f.FlowdownClauses,
        f.DebarmentCheckedOn,
        f.DebarmentResult,
        f.SmallBusinessReportPeriod,
        f.Notes,
    };
}

/// <summary>Finds no RFQ evidence: the default until the procurement (RFQ) module registers its own source.</summary>
public sealed class NoCompetitionSource : IProcurementCompetitionSource
{
    public Task<IReadOnlyDictionary<Guid, CompetitionEvidenceDto>> GetForPurchaseOrdersAsync(IReadOnlyCollection<Guid> purchaseOrderIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, CompetitionEvidenceDto>>(new Dictionary<Guid, CompetitionEvidenceDto>());
}

/// <summary>A plain, printable HTML document for reviewer exports.</summary>
internal static class ComplianceHtml
{
    public static void Begin(StringBuilder html, string title)
    {
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(title)).Append("</title><style>")
            .Append("body{font-family:system-ui,sans-serif;max-width:960px;margin:2rem auto;padding:0 1rem;color:#1b1b1b;line-height:1.45}")
            .Append("table{border-collapse:collapse;width:100%;margin:.5rem 0 1rem}th,td{border:1px solid #bbb;padding:.3rem .5rem;text-align:left;vertical-align:top}")
            .Append("th{background:#f0f0f0}dl{display:grid;grid-template-columns:max-content 1fr;gap:.2rem 1rem}dt{font-weight:600}dd{margin:0}")
            .Append("h2{margin-top:2rem;border-bottom:1px solid #ccc}.muted{color:#555}@media print{h2{break-after:avoid}}")
            .Append("</style></head><body><h1>").Append(WebUtility.HtmlEncode(title)).Append("</h1>");
    }

    public static void End(StringBuilder html) => html.Append("</body></html>");
}
