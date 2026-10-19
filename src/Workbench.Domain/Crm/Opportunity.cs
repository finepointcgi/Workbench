using Workbench.Domain.ProjectAccounting;

namespace Workbench.Domain.Crm;

/// <summary>The capture stages, in order. Won, Lost and NoBid are outcomes: the pursuit is over.</summary>
public enum OpportunityStage
{
    Identified,
    Qualified,
    Capture,
    Proposal,
    Submitted,
    Won,
    Lost,
    NoBid,
}

/// <summary>New work, or a recompete of a contract someone (maybe us) already holds.</summary>
public enum OpportunityKind
{
    New,
    Recompete,
}

/// <summary>Federal small-business set-asides (FAR Part 19), as SAM.gov names them.</summary>
public enum SetAside
{
    None,
    SmallBusiness,
    EightA,
    HubZone,
    ServiceDisabledVeteranOwned,
    WomenOwned,
    EconomicallyDisadvantagedWomenOwned,
    VeteranOwned,
    Other,
}

/// <summary>Where an opportunity came from.</summary>
public enum OpportunitySource
{
    Manual,
    SamGov,
    CsvImport,
}

/// <summary>Why a pursuit was won, lost or not bid (win/loss analysis).</summary>
public enum OutcomeReason
{
    Price,
    Technical,
    PastPerformance,
    CustomerRelationship,
    Incumbency,
    Staffing,
    Teaming,
    NotAligned,
    NoCapacity,
    Cancelled,
    Other,
}

/// <summary>
/// A pursuit of government work, from first sighting to award. The public shape other modules use (proposal pricing) is
/// <c>Contracts.Crm.OpportunityDto</c> through <c>IOpportunityService</c>: refer to an opportunity by <see cref="Entity.Id"/>.
/// </summary>
public sealed class Opportunity : Entity
{
    /// <summary>Workbench's own number, e.g. <c>OPP-0007</c>. Unique.</summary>
    public required string Number { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>The government's solicitation number (RFP/RFQ number), as entered.</summary>
    public string? SolicitationNumber { get; set; }

    /// <summary><see cref="SolicitationNumber"/> upper-cased without spaces or dashes: what imports de-duplicate on.</summary>
    public string? SolicitationKey { get; set; }

    /// <summary>The buying agency or office (a <see cref="CrmAccount"/> of kind Agency or Office).</summary>
    public Guid? AgencyAccountId { get; set; }

    /// <summary>The agency's name as the source gave it, when it isn't (yet) an account.</summary>
    public string? AgencyName { get; set; }

    public string? NaicsCode { get; set; }
    public SetAside SetAside { get; set; }

    /// <summary>For example GSA MAS, OASIS+, SEWP V, or "Standalone".</summary>
    public string? ContractVehicle { get; set; }

    public ContractType? ContractType { get; set; }
    public OpportunityKind Kind { get; set; }

    /// <summary>Total estimated value over the whole period of performance, options included.</summary>
    public decimal EstimatedValue { get; set; }

    public DateOnly? EstimatedAwardDate { get; set; }
    public int? PeriodOfPerformanceMonths { get; set; }

    public DateOnly? RfiDueOn { get; set; }
    public DateOnly? RfpReleaseOn { get; set; }
    public DateOnly? ProposalDueOn { get; set; }

    public string? PlaceOfPerformance { get; set; }

    /// <summary>Who holds the work now (recompetes), as a competitor account and/or a name.</summary>
    public Guid? IncumbentAccountId { get; set; }
    public string? IncumbentName { get; set; }

    /// <summary>Lower-case emails of the capture and proposal managers: they edit the opportunity.</summary>
    public string? CaptureManagerEmail { get; set; }
    public string? ProposalManagerEmail { get; set; }

    public OpportunityStage Stage { get; set; }
    public DateTime StageChangedAtUtc { get; set; }

    /// <summary>Probability of win and probability the government awards at all (go), each 0–100.</summary>
    public int Pwin { get; set; }
    public int Pgo { get; set; }

    public OpportunitySource Source { get; set; }
    public string? SourceUrl { get; set; }

    /// <summary>SAM.gov notice id, when imported from SAM.gov.</summary>
    public string? SamNoticeId { get; set; }

    // ---- Outcome ----
    public OutcomeReason? OutcomeReason { get; set; }
    public string? OutcomeNotes { get; set; }
    public DateOnly? DecidedOn { get; set; }
    public decimal? AwardedValue { get; set; }

    /// <summary>Who won it, when we lost.</summary>
    public string? AwardedTo { get; set; }

    /// <summary>The opportunity's bid-and-proposal charge code under the indirect B&amp;P element (created at Capture).</summary>
    public Guid? BpWbsElementId { get; set; }

    // ---- Won handoff ----
    public Guid? CustomerId { get; set; }
    public Guid? ContractId { get; set; }
    public Guid? ProjectId { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum TeamingRole
{
    Subcontractor,
    Prime,
    JointVenture,
    MentorProtege,
}

/// <summary>A teaming partner on an opportunity and their share of the work.</summary>
public sealed class OpportunityTeammate : Entity
{
    public Guid OpportunityId { get; set; }
    public Guid? AccountId { get; set; }
    public required string Name { get; set; }
    public TeamingRole Role { get; set; }

    /// <summary>Their percentage of the work (0–100). Ours is what's left.</summary>
    public decimal WorksharePercent { get; set; }

    public string? Notes { get; set; }
}

/// <summary>A company expected to bid against us.</summary>
public sealed class OpportunityCompetitor : Entity
{
    public Guid OpportunityId { get; set; }
    public Guid? AccountId { get; set; }
    public required string Name { get; set; }
    public bool IsIncumbent { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Append-only: every stage change, for the pipeline history and win/loss timing.</summary>
public sealed class OpportunityStageChange : Entity
{
    public Guid OpportunityId { get; set; }
    public OpportunityStage From { get; set; }
    public OpportunityStage To { get; set; }
    public required string ChangedBy { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public string? Note { get; set; }
}
