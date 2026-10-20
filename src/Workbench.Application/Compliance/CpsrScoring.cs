using Workbench.Contracts.Compliance;
using Workbench.Domain.Compliance;

namespace Workbench.Application.Compliance;

/// <summary>
/// CPSR thresholds and required evidence, bound from config <c>Compliance:Cpsr</c>. Defaults follow the FAR thresholds in
/// effect from October 2025 (micro-purchase $15,000, simplified acquisition $350,000, certified cost or pricing data
/// $2.5 million); change them in configuration when they're adjusted. Lists left unset use the defaults.
/// </summary>
public sealed class CpsrOptions
{
    public const string Section = "Compliance:Cpsr";

    public decimal MicroPurchaseThreshold { get; set; } = 15_000m;
    public decimal SimplifiedAcquisitionThreshold { get; set; } = 350_000m;
    public decimal CostOrPricingDataThreshold { get; set; } = 2_500_000m;

    /// <summary>Awards above this need a SAM.gov exclusions check before award (FAR 9.405-2, 52.209-6).</summary>
    public decimal DebarmentCheckThreshold { get; set; } = 35_000m;

    public int MinimumQuotes { get; set; } = 2;

    /// <summary>An exclusions check older than this many days before the award date doesn't count.</summary>
    public int DebarmentCheckMaxAgeDays { get; set; } = 90;

    /// <summary>Clauses every government-funded order flows down.</summary>
    public string[]? RequiredClauses { get; set; }

    /// <summary>More clauses for orders above the simplified acquisition threshold.</summary>
    public string[]? RequiredClausesAboveSat { get; set; }

    public ApprovalLevel[]? ApprovalLevels { get; set; }

    public sealed class ApprovalLevel
    {
        public string Name { get; set; } = "";

        /// <summary>The level signs off orders above this amount.</summary>
        public decimal AboveAmount { get; set; }
    }

    public static readonly string[] DefaultClauses = ["52.203-19", "52.204-21", "52.204-25", "52.222-50"];
    public static readonly string[] DefaultClausesAboveSat = ["52.219-8", "52.222-35", "52.222-37"];

    public CpsrSettingsDto ToSettings()
    {
        var levels = ApprovalLevels is { Length: > 0 }
            ? ApprovalLevels.Select((l, i) => new CpsrApprovalLevelDto(i + 1, l.Name, l.AboveAmount)).ToList()
            :
            [
                new CpsrApprovalLevelDto(1, "Buyer", 0),
                new CpsrApprovalLevelDto(2, "Purchasing manager", MicroPurchaseThreshold),
                new CpsrApprovalLevelDto(3, "Contracts director", SimplifiedAcquisitionThreshold),
                new CpsrApprovalLevelDto(4, "Executive", CostOrPricingDataThreshold),
            ];
        return new CpsrSettingsDto(MicroPurchaseThreshold, SimplifiedAcquisitionThreshold, CostOrPricingDataThreshold, DebarmentCheckThreshold, MinimumQuotes,
            DebarmentCheckMaxAgeDays, levels, CpsrScoring.Clauses(RequiredClauses ?? DefaultClauses), CpsrScoring.Clauses(RequiredClausesAboveSat ?? DefaultClausesAboveSat));
    }
}

/// <summary>A purchase order or subcontract as the review sees it.</summary>
public sealed record CpsrDocument(
    ProcurementDocumentKind Kind,
    Guid Id,
    string Number,
    string VendorName,
    string? VendorSize,
    DateOnly Date,
    decimal Value,
    bool GovernmentFunded,
    string? PrimeContractNumber);

/// <summary>Scores one procurement file against the thresholds: each element is met, missing or doesn't apply.</summary>
public static class CpsrScoring
{
    public static string Usd(decimal amount) => "$" + amount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public static string Label(CpsrElement e) => e switch
    {
        CpsrElement.Competition => "Competition",
        CpsrElement.PriceAnalysis => "Price or cost analysis",
        CpsrElement.Flowdowns => "Flowdown clauses",
        CpsrElement.Debarment => "Exclusions (debarment) check",
        CpsrElement.SmallBusiness => "Small business reporting",
        _ => "Approvals",
    };

    /// <summary>Clause numbers trimmed, upper-cased, without duplicates, in order.</summary>
    public static IReadOnlyList<string> Clauses(IEnumerable<string>? clauses) =>
        (clauses ?? []).SelectMany(c => c.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(c => c.ToUpperInvariant()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    public static IReadOnlyList<string> RequiredClauses(CpsrDocument doc, CpsrSettingsDto s) =>
        !doc.GovernmentFunded ? [] : doc.Value > s.SimplifiedAcquisitionThreshold ? [.. s.RequiredClauses, .. s.RequiredClausesAboveSat.Except(s.RequiredClauses)] : s.RequiredClauses;

    /// <summary>The approval levels the value needs (always the first; each further level above its amount).</summary>
    public static IReadOnlyList<CpsrApprovalLevelDto> RequiredApprovals(decimal value, CpsrSettingsDto s) =>
        s.ApprovalLevels.Where((l, i) => i == 0 || value > l.AboveAmount).ToList();

    public static int Score(IReadOnlyList<CpsrElementResultDto> elements)
    {
        var applicable = elements.Count(e => e.Status != CpsrElementStatus.NotApplicable);
        return applicable == 0 ? 100 : (int)Math.Round(100m * elements.Count(e => e.Status == CpsrElementStatus.Met) / applicable, MidpointRounding.AwayFromZero);
    }

    public static string Rating(int score) => score switch
    {
        >= 90 => "Ready for review",
        >= 75 => "Needs attention",
        _ => "Not ready",
    };

    public static IReadOnlyList<CpsrElementResultDto> Evaluate(CpsrDocument doc, ProcurementFile? file, CompetitionEvidenceDto? rfq, CpsrSettingsDto s)
    {
        var micro = doc.Value <= s.MicroPurchaseThreshold;
        return
        [
            Competition(doc, file, rfq, s, micro),
            PriceAnalysis(doc, file, s, micro),
            Flowdowns(doc, file, s),
            Debarment(doc, file, s),
            SmallBusiness(doc, file, s, micro),
            Approvals(doc, file, s),
        ];
    }

    private static CpsrElementResultDto Result(CpsrElement e, CpsrElementStatus status, string detail) => new(e, Label(e), status, detail);

    private static CpsrElementResultDto Competition(CpsrDocument doc, ProcurementFile? file, CompetitionEvidenceDto? rfq, CpsrSettingsDto s, bool micro)
    {
        const CpsrElement e = CpsrElement.Competition;
        if (micro)
        {
            return Result(e, CpsrElementStatus.NotApplicable, $"At or under the {Usd(s.MicroPurchaseThreshold)} micro-purchase threshold.");
        }

        var competition = file?.Competition ?? CompetitionMethod.NotRecorded;
        if (competition == CompetitionMethod.SoleSource)
        {
            return string.IsNullOrWhiteSpace(file!.SoleSourceJustification)
                ? Result(e, CpsrElementStatus.Missing, "Sole source, but there's no written justification.")
                : Result(e, CpsrElementStatus.Met, "Sole source with a written justification.");
        }

        if (competition == CompetitionMethod.MicroPurchase)
        {
            return Result(e, CpsrElementStatus.Missing, $"Marked as a micro-purchase, but the value is above {Usd(s.MicroPurchaseThreshold)}: record the quotes or a sole-source justification.");
        }

        var quotes = file?.QuotesReceived ?? rfq?.QuotesReceived;
        if (competition == CompetitionMethod.NotRecorded && rfq is null)
        {
            return Result(e, CpsrElementStatus.Missing, "No competition recorded: add the quotes received or a sole-source justification.");
        }

        var source = file?.QuotesReceived is null && rfq is not null ? $" (from {rfq.Reference})" : "";
        return quotes >= s.MinimumQuotes
            ? Result(e, CpsrElementStatus.Met, $"{quotes} quotes received{source}.")
            : Result(e, CpsrElementStatus.Missing, $"{quotes ?? 0} quote{(quotes == 1 ? "" : "s")} received{source}; {s.MinimumQuotes} are needed, or a sole-source justification.");
    }

    private static CpsrElementResultDto PriceAnalysis(CpsrDocument doc, ProcurementFile? file, CpsrSettingsDto s, bool micro)
    {
        const CpsrElement e = CpsrElement.PriceAnalysis;
        if (micro)
        {
            return Result(e, CpsrElementStatus.NotApplicable, $"At or under the {Usd(s.MicroPurchaseThreshold)} micro-purchase threshold.");
        }

        var kind = file?.PriceAnalysis ?? PriceAnalysisKind.None;
        if (kind == PriceAnalysisKind.None)
        {
            return Result(e, CpsrElementStatus.Missing, "No price or cost analysis recorded.");
        }

        if (doc.Value > s.CostOrPricingDataThreshold && kind == PriceAnalysisKind.PriceAnalysis)
        {
            return Result(e, CpsrElementStatus.Missing, $"Above {Usd(s.CostOrPricingDataThreshold)}, a cost analysis is needed as well as a price analysis.");
        }

        return string.IsNullOrWhiteSpace(file!.PriceAnalysisNotes)
            ? Result(e, CpsrElementStatus.Missing, "Describe the analysis: what the price was compared with and why it's fair and reasonable.")
            : Result(e, CpsrElementStatus.Met, kind switch { PriceAnalysisKind.CostAnalysis => "Cost analysis documented.", PriceAnalysisKind.Both => "Price and cost analysis documented.", _ => "Price analysis documented." });
    }

    private static CpsrElementResultDto Flowdowns(CpsrDocument doc, ProcurementFile? file, CpsrSettingsDto s)
    {
        const CpsrElement e = CpsrElement.Flowdowns;
        if (!doc.GovernmentFunded)
        {
            return Result(e, CpsrElementStatus.NotApplicable, "Not charged to a government contract.");
        }

        var present = Clauses((file?.FlowdownClauses ?? "").Split(',')).ToHashSet(StringComparer.Ordinal);
        var missing = RequiredClauses(doc, s).Where(c => !present.Contains(c)).ToList();
        return missing.Count == 0
            ? Result(e, CpsrElementStatus.Met, $"All {RequiredClauses(doc, s).Count} required clauses flowed down.")
            : Result(e, CpsrElementStatus.Missing, $"Missing {string.Join(", ", missing)}.");
    }

    private static CpsrElementResultDto Debarment(CpsrDocument doc, ProcurementFile? file, CpsrSettingsDto s)
    {
        const CpsrElement e = CpsrElement.Debarment;
        if (doc.Value <= s.DebarmentCheckThreshold)
        {
            return Result(e, CpsrElementStatus.NotApplicable, $"At or under {Usd(s.DebarmentCheckThreshold)}.");
        }

        if (file?.DebarmentCheckedOn is not { } checkedOn)
        {
            return Result(e, CpsrElementStatus.Missing, "No SAM.gov exclusions check recorded.");
        }

        if (checkedOn > doc.Date)
        {
            return Result(e, CpsrElementStatus.Missing, $"Checked on {checkedOn:yyyy-MM-dd}, after the award on {doc.Date:yyyy-MM-dd}; check before awarding.");
        }

        var age = doc.Date.DayNumber - checkedOn.DayNumber;
        return age > s.DebarmentCheckMaxAgeDays
            ? Result(e, CpsrElementStatus.Missing, $"Checked {age} days before the award; more than {s.DebarmentCheckMaxAgeDays} days doesn't count.")
            : Result(e, CpsrElementStatus.Met, $"Checked {checkedOn:yyyy-MM-dd}{(string.IsNullOrWhiteSpace(file.DebarmentResult) ? "" : $": {file.DebarmentResult}")}.");
    }

    private static CpsrElementResultDto SmallBusiness(CpsrDocument doc, ProcurementFile? file, CpsrSettingsDto s, bool micro)
    {
        const CpsrElement e = CpsrElement.SmallBusiness;
        if (!doc.GovernmentFunded || micro)
        {
            return Result(e, CpsrElementStatus.NotApplicable, doc.GovernmentFunded ? $"At or under the {Usd(s.MicroPurchaseThreshold)} micro-purchase threshold." : "Not charged to a government contract.");
        }

        if (string.IsNullOrWhiteSpace(doc.VendorSize))
        {
            return Result(e, CpsrElementStatus.Missing, "The vendor's business size isn't recorded (set it on the vendor).");
        }

        return string.IsNullOrWhiteSpace(file?.SmallBusinessReportPeriod)
            ? Result(e, CpsrElementStatus.Missing, $"Vendor is {doc.VendorSize}; record the ISR/SSR report this award is counted in.")
            : Result(e, CpsrElementStatus.Met, $"Vendor is {doc.VendorSize}; counted in {file!.SmallBusinessReportPeriod}.");
    }

    private static CpsrElementResultDto Approvals(CpsrDocument doc, ProcurementFile? file, CpsrSettingsDto s)
    {
        const CpsrElement e = CpsrElement.Approvals;
        var needed = RequiredApprovals(doc.Value, s);
        var top = needed.Max(l => l.Level);
        var approvals = file?.Approvals ?? [];
        if (approvals.Any(a => a.Level >= top))
        {
            var a = approvals.Where(x => x.Level >= top).OrderByDescending(x => x.Level).First();
            return Result(e, CpsrElementStatus.Met, $"Approved by {a.ApprovedBy} ({a.LevelName}).");
        }

        var level = needed.First(l => l.Level == top);
        var has = approvals.Count == 0 ? "no approvals yet" : $"has {string.Join(", ", approvals.OrderBy(x => x.Level).Select(x => x.LevelName))}";
        return Result(e, CpsrElementStatus.Missing, top == 1
            ? $"Needs {level.Name} approval; {has}."
            : $"Over {Usd(level.AboveAmount)}, needs {level.Name} approval; {has}.");
    }
}
