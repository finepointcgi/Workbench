namespace Workbench.Domain.Compliance;

/// <summary>How the source was selected (DFARS 252.244-7001 (c)(5): competition or a documented sole-source reason).</summary>
public enum CompetitionMethod
{
    NotRecorded,
    Competitive,
    SoleSource,

    /// <summary>At or under the micro-purchase threshold: competition isn't required when the price is reasonable.</summary>
    MicroPurchase,
}

/// <summary>How the price was shown to be fair and reasonable (FAR 15.404-1).</summary>
public enum PriceAnalysisKind
{
    None,
    PriceAnalysis,
    CostAnalysis,

    /// <summary>Both price and cost analysis.</summary>
    Both,
}

/// <summary>
/// The procurement file for one purchase order or subcontract: the evidence a contractor purchasing system review (CPSR)
/// samples. Exactly one of <see cref="PurchaseOrderId"/> and <see cref="SubcontractId"/> is set.
/// </summary>
public sealed class ProcurementFile : Entity
{
    public Guid? PurchaseOrderId { get; set; }
    public Guid? SubcontractId { get; set; }

    public CompetitionMethod Competition { get; set; }
    public int? QuotesSolicited { get; set; }
    public int? QuotesReceived { get; set; }
    public string? SoleSourceJustification { get; set; }

    public PriceAnalysisKind PriceAnalysis { get; set; }
    public string? PriceAnalysisNotes { get; set; }

    /// <summary>FAR/DFARS clauses flowed down, comma separated (for example "52.204-21,52.219-8").</summary>
    public string FlowdownClauses { get; set; } = "";

    /// <summary>When the vendor was checked against the SAM.gov exclusions list (FAR 9.405-2).</summary>
    public DateOnly? DebarmentCheckedOn { get; set; }

    public string? DebarmentResult { get; set; }

    /// <summary>The ISR/SSR (eSRS) report this award is counted in, for example "FY2026 ISR (Mar)".</summary>
    public string? SmallBusinessReportPeriod { get; set; }

    public string? Notes { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<ProcurementFileApproval> Approvals { get; } = [];
}

/// <summary>One sign-off on a procurement file at an approval level (levels come from the dollar thresholds).</summary>
public sealed class ProcurementFileApproval : Entity
{
    public Guid FileId { get; set; }

    /// <summary>1 = buyer, then higher levels as the value grows (see the CPSR options).</summary>
    public int Level { get; set; }

    public required string LevelName { get; set; }
    public required string ApprovedBy { get; set; }
    public DateTime ApprovedAtUtc { get; set; }
    public string? Comment { get; set; }
}
