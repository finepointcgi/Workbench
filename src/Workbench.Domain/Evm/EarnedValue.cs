namespace Workbench.Domain.Evm;

/// <summary>How a work package earns its budget (BCWP).</summary>
public enum EarnedValueTechnique
{
    /// <summary>Nothing until the work is complete, then the whole budget.</summary>
    ZeroHundred,

    /// <summary>Half when the work starts, the rest when it's complete.</summary>
    FiftyFifty,

    /// <summary>The budget × the % complete (a progress entry, else the plan sheet's "% complete" or status).</summary>
    PercentComplete,

    /// <summary>The sum of the weights of completed milestones.</summary>
    Milestones,

    /// <summary>Earned as planned: BCWP = BCWS, so it never has a schedule variance.</summary>
    LevelOfEffort,

    /// <summary>The same % complete as the discrete work package it's apportioned to.</summary>
    Apportioned,
}

/// <summary>
/// A project's performance measurement baseline (PMB): the budget of a locked budget version, distributed to work packages
/// and time-phased by a schedule baseline. Control accounts are the WBS elements at <see cref="ControlAccountLevel"/>.
/// Management reserve and undistributed budget are held outside the work packages. One per project; it changes only
/// through approved baseline change requests once a status period is closed.
/// </summary>
public sealed class EvmBaseline : Entity
{
    public Guid ProjectId { get; set; }

    /// <summary>The locked budget version the PMB was built from.</summary>
    public Guid BudgetVersionId { get; set; }

    /// <summary>The plan sheet's schedule baseline used to time-phase the work packages, if any.</summary>
    public Guid? ScheduleBaselineId { get; set; }

    /// <summary>The depth of control accounts in the WBS (the project's top elements are level 1).</summary>
    public int ControlAccountLevel { get; set; }

    public decimal ManagementReserve { get; set; }
    public decimal UndistributedBudget { get; set; }

    /// <summary>Starts at 0 and goes up by one with each approved change request.</summary>
    public int Revision { get; set; }

    /// <summary>A control account needs a variance analysis when |SV| or |CV| exceeds this share of BCWS/BCWP (percent) …</summary>
    public decimal ThresholdPercent { get; set; } = 10m;

    /// <summary>… and this many dollars.</summary>
    public decimal ThresholdAmount { get; set; } = 5_000m;

    public DateTime CreatedAtUtc { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A work package: budget, technique and planned dates for one WBS element under a control account.</summary>
public sealed class EvmWorkPackage : Entity
{
    public Guid BaselineId { get; set; }
    public Guid WbsElementId { get; set; }

    /// <summary>The control account (a WBS element at the baseline's level, or this element itself).</summary>
    public Guid ControlAccountWbsId { get; set; }

    public EarnedValueTechnique Technique { get; set; }

    /// <summary>Budget at completion.</summary>
    public decimal Budget { get; set; }

    public DateOnly PlannedStart { get; set; }
    public DateOnly PlannedFinish { get; set; }

    /// <summary>For <see cref="EarnedValueTechnique.Apportioned"/>: the discrete work package it follows.</summary>
    public Guid? ApportionedToId { get; set; }

    /// <summary>A % complete (0–100) entered for the current status period; overrides the plan sheet.</summary>
    public decimal? PercentCompleteOverride { get; set; }

    public DateTime? ProgressUpdatedAtUtc { get; set; }
    public string? ProgressUpdatedBy { get; set; }

    public List<EvmMilestone> Milestones { get; } = [];
}

/// <summary>A weighted milestone of a <see cref="EarnedValueTechnique.Milestones"/> work package.</summary>
public sealed class EvmMilestone : Entity
{
    public Guid WorkPackageId { get; set; }
    public required string Name { get; set; }

    /// <summary>Share of the work package's budget, in percent; a work package's weights add up to 100.</summary>
    public decimal Weight { get; set; }

    public int Order { get; set; }

    /// <summary>A plan-sheet task that completes the milestone when its status is Done.</summary>
    public Guid? ItemId { get; set; }

    public DateOnly? PlannedDate { get; set; }

    /// <summary>When the milestone was met (entered, or the date it was first seen done).</summary>
    public DateOnly? CompletedOn { get; set; }
}

/// <summary>
/// A closed (frozen) monthly status of a project: one per fiscal period, with a <see cref="EvmPeriodSnapshot"/> per work
/// package. Reopening keeps the row (with <see cref="ReopenedAtUtc"/> set) so the history stays; closing again adds a new one.
/// </summary>
public sealed class EvmStatusPeriod : Entity
{
    public Guid ProjectId { get; set; }
    public Guid FiscalPeriodId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public int BaselineRevision { get; set; }
    public decimal ManagementReserve { get; set; }
    public decimal UndistributedBudget { get; set; }
    public DateTime ClosedAtUtc { get; set; }
    public required string ClosedBy { get; set; }
    public DateTime? ReopenedAtUtc { get; set; }
    public string? ReopenedBy { get; set; }
    public string? ReopenReason { get; set; }
    public List<EvmPeriodSnapshot> Snapshots { get; } = [];
}

/// <summary>Cumulative-to-date figures for one work package (or cost outside any work package) at a period close.</summary>
public sealed class EvmPeriodSnapshot : Entity
{
    public Guid StatusPeriodId { get; set; }

    /// <summary>Null for actual cost charged to a WBS element outside every work package.</summary>
    public Guid? WorkPackageId { get; set; }

    public Guid WbsElementId { get; set; }
    public Guid? ControlAccountWbsId { get; set; }
    public EarnedValueTechnique Technique { get; set; }
    public decimal Budget { get; set; }
    public decimal Bcws { get; set; }
    public decimal Bcwp { get; set; }
    public decimal Acwp { get; set; }

    /// <summary>0–100.</summary>
    public decimal PercentComplete { get; set; }
}

/// <summary>A control account's variance analysis report (VAR) for a status period, and the manager's estimate at completion.</summary>
public sealed class EvmVarianceAnalysis : Entity
{
    public Guid ProjectId { get; set; }
    public Guid FiscalPeriodId { get; set; }
    public Guid ControlAccountWbsId { get; set; }
    public string? Cause { get; set; }
    public string? Impact { get; set; }
    public string? CorrectiveAction { get; set; }

    /// <summary>The control account manager's estimate at completion (latest revised estimate), if given.</summary>
    public decimal? ManagerEac { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
    public required string UpdatedBy { get; set; }
}

public enum EvmChangeRequestStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>Where a baseline change's budget comes from.</summary>
public enum EvmBudgetSource
{
    /// <summary>Management reserve (the contract budget base doesn't change).</summary>
    ManagementReserve,

    /// <summary>Undistributed budget, e.g. distributing newly authorized work.</summary>
    UndistributedBudget,

    /// <summary>A contract change: the contract budget base changes by the amount.</summary>
    ContractChange,
}

/// <summary>A baseline change request (BCR). Approval applies it to the PMB and keeps a before/after snapshot.</summary>
public sealed class EvmChangeRequest : Entity
{
    public Guid ProjectId { get; set; }
    public Guid BaselineId { get; set; }

    /// <summary>Numbered per project from 1 (shown as BCR-001).</summary>
    public int Number { get; set; }

    public required string Title { get; set; }
    public required string Justification { get; set; }
    public EvmChangeRequestStatus Status { get; set; }
    public required string RequestedBy { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionNote { get; set; }

    /// <summary>The baseline revision the approval produced.</summary>
    public int? AppliedRevision { get; set; }

    /// <summary>The affected work packages and reserves before and after approval (JSON).</summary>
    public string? BeforeJson { get; set; }

    public string? AfterJson { get; set; }
    public List<EvmChangeRequestLine> Lines { get; } = [];
}

/// <summary>
/// One change: move <see cref="BudgetChange"/> from <see cref="Source"/> to a work package (<see cref="WbsElementId"/>),
/// or into undistributed budget (no WBS element), and optionally re-plan its dates or technique.
/// </summary>
public sealed class EvmChangeRequestLine : Entity
{
    public Guid ChangeRequestId { get; set; }
    public int Order { get; set; }
    public Guid? WbsElementId { get; set; }
    public EvmBudgetSource Source { get; set; }
    public decimal BudgetChange { get; set; }
    public EarnedValueTechnique? NewTechnique { get; set; }
    public DateOnly? NewStart { get; set; }
    public DateOnly? NewFinish { get; set; }
}
