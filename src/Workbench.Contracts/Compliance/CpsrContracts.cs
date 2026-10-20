namespace Workbench.Contracts.Compliance;

// Contractor purchasing system review (CPSR, DFARS 252.244-7001 and FAR 44.3) support (Finance and administrators): a
// procurement file per purchase order or subcontract with the evidence a reviewer samples, scored against the dollar
// thresholds, a readiness dashboard over a sample, and an export of the files for the reviewer.

public enum ProcurementDocumentKind { PurchaseOrder, Subcontract }

public enum CompetitionType { NotRecorded, Competitive, SoleSource, MicroPurchase }

public enum PriceAnalysisType { None, PriceAnalysis, CostAnalysis, Both }

/// <summary>The parts of a procurement file a CPSR checks.</summary>
public enum CpsrElement { Competition, PriceAnalysis, Flowdowns, Debarment, SmallBusiness, Approvals }

public enum CpsrElementStatus { Met, Missing, NotApplicable }

public sealed record CpsrElementResultDto(CpsrElement Element, string Label, CpsrElementStatus Status, string Detail);

public sealed record CpsrApprovalLevelDto(int Level, string Name, decimal AboveAmount);

public sealed record ProcurementFileApprovalDto(int Level, string LevelName, string ApprovedBy, DateTime ApprovedAtUtc, string? Comment);

/// <summary>Competition evidence from a request for quotes (supplied by the RFQ module through <see cref="IProcurementCompetitionSource"/>).</summary>
public sealed record CompetitionEvidenceDto(string Reference, int QuotesSolicited, int QuotesReceived, string? AwardBasis = null, string? Link = null);

public sealed record ProcurementFileDto(
    Guid? FileId,
    ProcurementDocumentKind Kind,
    Guid DocumentId,
    string Number,
    string VendorName,
    string? VendorSize,
    DateOnly Date,
    decimal Value,

    /// <summary>Charged to a government contract (a project on a PO line, or a prime contract on a subcontract).</summary>
    bool GovernmentFunded,
    string? PrimeContractNumber,
    CompetitionType Competition,
    int? QuotesSolicited,
    int? QuotesReceived,
    string? SoleSourceJustification,
    PriceAnalysisType PriceAnalysis,
    string? PriceAnalysisNotes,
    IReadOnlyList<string> FlowdownClauses,
    IReadOnlyList<string> RequiredClauses,
    DateOnly? DebarmentCheckedOn,
    string? DebarmentResult,
    string? SmallBusinessReportPeriod,
    string? Notes,
    IReadOnlyList<ProcurementFileApprovalDto> Approvals,
    IReadOnlyList<CpsrApprovalLevelDto> RequiredApprovals,
    CompetitionEvidenceDto? RfqEvidence,
    IReadOnlyList<CpsrElementResultDto> Elements,

    /// <summary>Elements met ÷ elements that apply, as a percentage.</summary>
    int Score,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record SaveProcurementFileRequest(
    CompetitionType Competition,
    int? QuotesSolicited = null,
    int? QuotesReceived = null,
    string? SoleSourceJustification = null,
    PriceAnalysisType PriceAnalysis = PriceAnalysisType.None,
    string? PriceAnalysisNotes = null,
    IReadOnlyList<string>? FlowdownClauses = null,
    DateOnly? DebarmentCheckedOn = null,
    string? DebarmentResult = null,
    string? SmallBusinessReportPeriod = null,
    string? Notes = null);

/// <summary>Signs the file off at an approval level (1 = buyer; higher levels as the value grows).</summary>
public sealed record ApproveProcurementFileRequest(int Level, string? Comment = null);

/// <summary>
/// Which documents to review: issued purchase orders and active or closed subcontracts dated in the range (default the
/// last 12 months). Every document above the simplified acquisition threshold is in the sample; the rest is a repeatable
/// pseudo-random pick (change <see cref="Seed"/> for another sample).
/// </summary>
public sealed record CpsrReviewRequest(DateOnly? From = null, DateOnly? To = null, int SampleSize = 25, int Seed = 0);

public sealed record CpsrElementSummaryDto(CpsrElement Element, string Label, int Applicable, int Met, decimal PercentMet);

public sealed record CpsrMissingEvidenceDto(ProcurementDocumentKind Kind, Guid DocumentId, string Number, CpsrElement Element, string Label, string Detail);

public sealed record CpsrSampleRowDto(ProcurementDocumentKind Kind, Guid DocumentId, string Number, string VendorName, DateOnly Date, decimal Value, bool GovernmentFunded, int Score, int MissingCount);

public sealed record CpsrSettingsDto(
    decimal MicroPurchaseThreshold,
    decimal SimplifiedAcquisitionThreshold,
    decimal CostOrPricingDataThreshold,
    decimal DebarmentCheckThreshold,
    int MinimumQuotes,
    int DebarmentCheckMaxAgeDays,
    IReadOnlyList<CpsrApprovalLevelDto> ApprovalLevels,
    IReadOnlyList<string> RequiredClauses,
    IReadOnlyList<string> RequiredClausesAboveSat);

/// <summary>The readiness dashboard: the overall score (elements met ÷ elements that apply), per element, the sample and every missing piece of evidence.</summary>
public sealed record CpsrReadinessDto(
    DateOnly From,
    DateOnly To,
    int Population,
    int SampleSize,
    int Score,
    string Rating,
    IReadOnlyList<CpsrElementSummaryDto> Elements,
    IReadOnlyList<CpsrSampleRowDto> Sample,
    IReadOnlyList<CpsrMissingEvidenceDto> Missing,
    CpsrSettingsDto Settings);

public sealed record ComplianceExportFile(string FileName, string ContentType, string Content);

public interface ICpsrService
{
    Task<CpsrReadinessDto> GetReadinessAsync(CpsrReviewRequest request, CancellationToken cancellationToken = default);

    /// <summary>The document's file (an empty, unsaved one when nothing has been recorded yet) with its scoring.</summary>
    Task<ProcurementFileDto> GetFileAsync(ProcurementDocumentKind kind, Guid documentId, CancellationToken cancellationToken = default);

    Task<ProcurementFileDto> SaveFileAsync(ProcurementDocumentKind kind, Guid documentId, SaveProcurementFileRequest request, CancellationToken cancellationToken = default);

    Task<ProcurementFileDto> ApproveAsync(ProcurementDocumentKind kind, Guid documentId, ApproveProcurementFileRequest request, CancellationToken cancellationToken = default);

    Task<CpsrSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>The sample's procurement files as one printable HTML document for the reviewer.</summary>
    Task<ComplianceExportFile> ExportReviewAsync(CpsrReviewRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Competition evidence from requests for quotes, keyed by purchase order. The procurement (RFQ) module registers an
/// implementation; until then a no-op default finds nothing and the file's own quote counts are used.
/// </summary>
public interface IProcurementCompetitionSource
{
    Task<IReadOnlyDictionary<Guid, CompetitionEvidenceDto>> GetForPurchaseOrdersAsync(IReadOnlyCollection<Guid> purchaseOrderIds, CancellationToken cancellationToken = default);
}
