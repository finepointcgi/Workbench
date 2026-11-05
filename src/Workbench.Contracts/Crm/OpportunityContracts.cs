using Workbench.Contracts.Contracts;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Contracts.Crm;

/// <summary>The capture stages, in order. Won, Lost and NoBid are outcomes.</summary>
public enum OpportunityStage { Identified, Qualified, Capture, Proposal, Submitted, Won, Lost, NoBid }

public enum OpportunityKind { New, Recompete }

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

public enum OpportunitySource { Manual, SamGov, CsvImport, CaptureExec }

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

public enum TeamingRole { Subcontractor, Prime, JointVenture, MentorProtege }

public static class OpportunityStages
{
    /// <summary>The pursuit stages, in order (not the outcomes).</summary>
    public static readonly IReadOnlyList<OpportunityStage> Open =
        [OpportunityStage.Identified, OpportunityStage.Qualified, OpportunityStage.Capture, OpportunityStage.Proposal, OpportunityStage.Submitted];

    public static readonly IReadOnlyList<OpportunityStage> Outcomes = [OpportunityStage.Won, OpportunityStage.Lost, OpportunityStage.NoBid];

    public static bool IsOpen(this OpportunityStage stage) => stage < OpportunityStage.Won;

    public static string Label(OpportunityStage stage) => stage switch
    {
        OpportunityStage.NoBid => "No-bid",
        _ => stage.ToString(),
    };

    public static string Label(SetAside setAside) => setAside switch
    {
        SetAside.None => "Full and open",
        SetAside.SmallBusiness => "Small business",
        SetAside.EightA => "8(a)",
        SetAside.HubZone => "HUBZone",
        SetAside.ServiceDisabledVeteranOwned => "SDVOSB",
        SetAside.WomenOwned => "WOSB",
        SetAside.EconomicallyDisadvantagedWomenOwned => "EDWOSB",
        SetAside.VeteranOwned => "VOSB",
        _ => "Other",
    };

    public static string Label(OutcomeReason reason) => reason switch
    {
        OutcomeReason.PastPerformance => "Past performance",
        OutcomeReason.CustomerRelationship => "Customer relationship",
        OutcomeReason.NotAligned => "Not aligned with our work",
        OutcomeReason.NoCapacity => "No capacity to bid",
        _ => reason.ToString(),
    };
}

public sealed record TeammateDto(Guid Id, Guid? AccountId, string Name, TeamingRole Role, decimal WorksharePercent, string? Notes);

public sealed record CompetitorDto(Guid Id, Guid? AccountId, string Name, bool IsIncumbent, string? Notes);

/// <summary>
/// An opportunity: the stable public shape other modules (proposal pricing) read through <see cref="IOpportunityService"/>.
/// Refer to it by <see cref="Id"/>. Money is business-sensitive: only people who may see the pipeline get one.
/// <see cref="WeightedValue"/> = <see cref="EstimatedValue"/> × Pwin × Pgo.
/// </summary>
public sealed record OpportunityDto(
    Guid Id,
    string Number,
    string Name,
    string? Description,
    string? SolicitationNumber,
    Guid? AgencyAccountId,
    string? AgencyName,
    string? NaicsCode,
    SetAside SetAside,
    string? ContractVehicle,
    ContractKind? ContractType,
    OpportunityKind Kind,
    decimal EstimatedValue,
    DateOnly? EstimatedAwardDate,
    int? PeriodOfPerformanceMonths,
    DateOnly? RfiDueOn,
    DateOnly? RfpReleaseOn,
    DateOnly? ProposalDueOn,
    string? PlaceOfPerformance,
    Guid? IncumbentAccountId,
    string? IncumbentName,
    string? CaptureManagerEmail,
    string? CaptureManagerName,
    string? ProposalManagerEmail,
    string? ProposalManagerName,
    OpportunityStage Stage,
    DateTime StageChangedAtUtc,
    int Pwin,
    int Pgo,
    decimal WeightedValue,
    OpportunitySource Source,
    string? SourceUrl,
    OutcomeReason? OutcomeReason,
    string? OutcomeNotes,
    DateOnly? DecidedOn,
    decimal? AwardedValue,
    string? AwardedTo,
    Guid? BpWbsElementId,
    string? BpChargeCode,
    Guid? CustomerId,
    Guid? ContractId,
    Guid? ProjectId,
    IReadOnlyList<TeammateDto> Teammates,
    IReadOnlyList<CompetitorDto> Competitors,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    bool CanEdit = false)
{
    /// <summary>Our share of the work: what teammates don't take.</summary>
    public decimal OurWorksharePercent => Math.Max(0m, 100m - Teammates.Sum(t => t.WorksharePercent));
}

/// <summary>Filters for <see cref="IOpportunityService.ListAsync"/>; every field is optional (null = any).</summary>
public sealed record OpportunityFilter(
    IReadOnlyList<OpportunityStage>? Stages = null,
    Guid? AgencyAccountId = null,
    string? ManagerEmail = null,
    OpportunityKind? Kind = null,
    string? Search = null,
    bool OpenOnly = false,
    bool MineOnly = false);

public sealed record SaveTeammate(Guid? AccountId, string Name, TeamingRole Role, decimal WorksharePercent, string? Notes = null);

public sealed record SaveCompetitor(Guid? AccountId, string Name, bool IsIncumbent = false, string? Notes = null);

/// <summary>Creates or updates an opportunity's details (stage and outcome change through their own calls).</summary>
public sealed record SaveOpportunityRequest(
    string Name,
    string? Description = null,
    string? SolicitationNumber = null,
    Guid? AgencyAccountId = null,
    string? AgencyName = null,
    string? NaicsCode = null,
    SetAside SetAside = SetAside.None,
    string? ContractVehicle = null,
    ContractKind? ContractType = null,
    OpportunityKind Kind = OpportunityKind.New,
    decimal EstimatedValue = 0m,
    DateOnly? EstimatedAwardDate = null,
    int? PeriodOfPerformanceMonths = null,
    DateOnly? RfiDueOn = null,
    DateOnly? RfpReleaseOn = null,
    DateOnly? ProposalDueOn = null,
    string? PlaceOfPerformance = null,
    Guid? IncumbentAccountId = null,
    string? IncumbentName = null,
    string? CaptureManagerEmail = null,
    string? ProposalManagerEmail = null,
    int Pwin = 10,
    int Pgo = 50,
    IReadOnlyList<SaveTeammate>? Teammates = null,
    IReadOnlyList<SaveCompetitor>? Competitors = null,
    string? SourceUrl = null);

public sealed record ChangeStageRequest(OpportunityStage Stage, string? Note = null);

/// <summary>Records the outcome: Won or Lost (from Submitted) or NoBid (from any open stage).</summary>
public sealed record RecordOutcomeRequest(
    OpportunityStage Outcome,
    OutcomeReason Reason,
    string? Notes = null,
    DateOnly? DecidedOn = null,
    decimal? AwardedValue = null,
    string? AwardedTo = null);

public sealed record StageChangeDto(OpportunityStage From, OpportunityStage To, string ChangedBy, DateTime ChangedAtUtc, string? Note);

public sealed record HandoffClin(string Number, string Description, ClinPricingKind Pricing, decimal CeilingValue, DateOnly PeriodStart, DateOnly PeriodEnd, bool IsOption);

/// <summary>
/// Turns a won opportunity into Customer, Contract (Pending) and Project (Planning) shells with CLIN placeholders. Use an
/// existing customer (<see cref="CustomerId"/>, or the agency account's linked customer) or create one with the code and name.
/// </summary>
public sealed record HandoffRequest(
    string ContractNumber,
    string ProjectCode,
    Guid? CustomerId = null,
    string? CustomerCode = null,
    string? CustomerName = null,
    string? ContractName = null,
    string? ProjectName = null,
    Guid? ProjectManagerId = null,
    IReadOnlyList<HandoffClin>? Clins = null);

public sealed record HandoffResultDto(Guid OpportunityId, Guid CustomerId, Guid ContractId, Guid ProjectId, IReadOnlyList<Guid> ClinIds, bool CreatedCustomer);

/// <summary>
/// Opportunities. Business development, Finance and administrators see all of them; a capture or proposal manager sees and
/// edits their own (administrators edit all); everyone else sees nothing (403). <see cref="GetAsync"/> and
/// <see cref="ListAsync"/> are the stable contract other modules use.
/// </summary>
public interface IOpportunityService
{
    Task<OpportunityDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpportunityDto>> ListAsync(OpportunityFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>Business development (or administrators). The creator is the capture manager unless one is named.</summary>
    Task<OpportunityDto> CreateAsync(SaveOpportunityRequest request, CancellationToken cancellationToken = default);

    Task<OpportunityDto> UpdateAsync(Guid id, SaveOpportunityRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves to another open stage. Moving forward into a stage with an active gate is refused: pass the gate instead
    /// (<see cref="IOpportunityGateService.SubmitAsync"/>). Reaching Capture creates the opportunity's B&amp;P charge code.
    /// </summary>
    Task<OpportunityDto> ChangeStageAsync(Guid id, ChangeStageRequest request, CancellationToken cancellationToken = default);

    Task<OpportunityDto> RecordOutcomeAsync(Guid id, RecordOutcomeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Back to an open stage from Lost or No-bid (not Won once handed off).</summary>
    Task<OpportunityDto> ReopenAsync(Guid id, ChangeStageRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StageChangeDto>> GetHistoryAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Finance or administrators: creates the customer/contract/project shells for a won opportunity, linked back to it.</summary>
    Task<HandoffResultDto> HandoffAsync(Guid id, HandoffRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates the B&amp;P charge code now if the opportunity is at Capture or later and has none (e.g. after setting the B&amp;P element).</summary>
    Task<OpportunityDto> EnsureBpChargeCodeAsync(Guid id, CancellationToken cancellationToken = default);
}
