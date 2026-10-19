namespace Workbench.Domain.Planning;

/// <summary>The kind of corporate budget version: the original plan, a revision during the year, or a forecast.</summary>
public enum GlBudgetKind
{
    Original,
    Revised,
    Forecast,
}

public enum GlBudgetStatus
{
    /// <summary>Being prepared: lines can change.</summary>
    Draft,

    /// <summary>Adopted and locked: no line can change. Changes go into a new (revised) version.</summary>
    Adopted,
}

/// <summary>
/// A corporate GL budget for one fiscal year: an amount per account × org unit × fiscal period. Amounts are in the
/// account's natural sign (revenue and expenses both positive). Distinct from the project budgets in
/// <c>ProjectAccounting.BudgetVersion</c>.
/// </summary>
public sealed class GlBudgetVersion : Entity
{
    public Guid FiscalYearId { get; set; }
    public GlBudgetKind Kind { get; set; }

    /// <summary>Unique per fiscal year, for example <c>FY2028 Original</c>.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }
    public GlBudgetStatus Status { get; set; } = GlBudgetStatus.Draft;

    /// <summary>The version this one was copied from.</summary>
    public Guid? SourceVersionId { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? AdoptedBy { get; set; }
    public DateTime? AdoptedAtUtc { get; set; }

    public bool IsLocked => Status == GlBudgetStatus.Adopted;

    /// <summary>Throws when the version is adopted (locked).</summary>
    public void EnsureEditable()
    {
        if (IsLocked)
        {
            throw new InvalidOperationException($"{Name} was adopted on {AdoptedAtUtc:yyyy-MM-dd} and is locked. Copy it to a revised version to make changes.");
        }
    }
}

/// <summary>One budget amount: account × org unit × fiscal period. Unique within the version.</summary>
public sealed class GlBudgetLine : Entity
{
    public Guid VersionId { get; set; }
    public Guid AccountId { get; set; }
    public Guid OrgUnitId { get; set; }
    public Guid FiscalPeriodId { get; set; }
    public decimal Amount { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum PlanningCycleStatus
{
    /// <summary>Org unit owners are filling in and submitting their budgets.</summary>
    Open,

    /// <summary>The budget was adopted; the cycle is finished.</summary>
    Adopted,
}

/// <summary>
/// A corporate planning cycle for a fiscal year: Finance opens it and assigns an owner per org unit; each owner fills
/// in their unit's budget and submits it through the approval workflow; approved submissions roll up into
/// <see cref="VersionId"/>, which is locked when the cycle is adopted.
/// </summary>
public sealed class PlanningCycle : Entity
{
    public Guid FiscalYearId { get; set; }
    public required string Name { get; set; }
    public string? Instructions { get; set; }
    public DateOnly? DueDate { get; set; }

    /// <summary>The corporate budget version the approved submissions roll up into.</summary>
    public Guid VersionId { get; set; }

    public PlanningCycleStatus Status { get; set; } = PlanningCycleStatus.Open;
    public required string OpenedBy { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public string? AdoptedBy { get; set; }
    public DateTime? AdoptedAtUtc { get; set; }
}

public enum BudgetSubmissionStatus
{
    /// <summary>Assigned; nothing entered yet.</summary>
    NotStarted,

    Drafting,
    Submitted,
    Approved,

    /// <summary>Sent back by an approver; the owner changes and resubmits it.</summary>
    Returned,
}

/// <summary>One org unit's part of a planning cycle: its owner, its lines and where it is in approval.</summary>
public sealed class BudgetSubmission : Entity
{
    public Guid CycleId { get; set; }
    public Guid OrgUnitId { get; set; }
    public Guid OwnerEmployeeId { get; set; }
    public BudgetSubmissionStatus Status { get; set; } = BudgetSubmissionStatus.NotStarted;

    /// <summary>Counts submissions, so each approval trip is distinct.</summary>
    public int Revision { get; set; }

    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionComment { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<BudgetSubmissionLine> Lines { get; } = [];

    public bool IsEditable => Status is BudgetSubmissionStatus.NotStarted or BudgetSubmissionStatus.Drafting or BudgetSubmissionStatus.Returned;
}

/// <summary>An amount the org unit owner proposes: account × fiscal period (the org unit is the submission's).</summary>
public sealed class BudgetSubmissionLine : Entity
{
    public Guid SubmissionId { get; set; }
    public Guid AccountId { get; set; }
    public Guid FiscalPeriodId { get; set; }
    public decimal Amount { get; set; }
}
