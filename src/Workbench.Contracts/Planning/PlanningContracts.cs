using Workbench.Contracts.Accounting;

namespace Workbench.Contracts.Planning;

// Recurring journals, corporate GL budgets, budget vs actual and the corporate planning cycle. Recurring journals,
// budget versions and the reports are for Finance and administrators; org unit owners fill in and submit their own
// unit's lines in a planning cycle, and whoever the "Org unit budget" approval workflow routes a submission to decides it.

public enum RecurrenceKind { Monthly, Quarterly, Annually }

public enum RecurringDay { FirstDay, LastDay }

public enum RecurringAmounts { Fixed, Percentage }

public enum LineSide { Debit, Credit }

public enum RecurringRunState { Draft, Posted, Skipped }

// ---- Recurring journals ----

public sealed record RecurringLineDto(
    int LineNumber,
    Guid AccountId,
    string AccountLabel,
    Guid? OrgUnitId,
    string? OrgUnitCode,
    Guid? ProjectId,
    string? ProjectCode,
    Guid? WbsElementId,
    string? WbsCode,
    LineSide Side,
    decimal Amount,
    decimal Percent,
    string? Memo);

public sealed record RecurringTemplateDto(
    Guid Id,
    string Name,
    string Description,
    RecurrenceKind Frequency,
    RecurringDay PostingDay,
    DateOnly StartDate,
    DateOnly? EndDate,
    int? Occurrences,
    RecurringAmounts AmountMode,
    decimal BaseAmount,
    bool PostAutomatically,
    bool AutoReverse,
    bool IsActive,
    decimal AmountPerOccurrence,
    int Generated,
    DateOnly? NextDate,
    IReadOnlyList<RecurringLineDto> Lines,
    string CreatedBy,
    DateTime CreatedAtUtc);

public sealed record RecurringLineInput(
    Guid AccountId,
    LineSide Side,
    decimal Amount = 0,
    decimal Percent = 0,
    string? Memo = null,
    Guid? OrgUnitId = null,
    Guid? ProjectId = null,
    Guid? WbsElementId = null);

/// <summary>
/// A template. Give an <see cref="EndDate"/> or a number of <see cref="Occurrences"/> (or neither, for no end). With
/// <see cref="RecurringAmounts.Percentage"/>, each line's percent splits <see cref="BaseAmount"/> and each side adds up to 100.
/// </summary>
public sealed record SaveRecurringTemplateRequest(
    string Name,
    string Description,
    RecurrenceKind Frequency,
    DateOnly StartDate,
    IReadOnlyList<RecurringLineInput> Lines,
    RecurringDay PostingDay = RecurringDay.LastDay,
    DateOnly? EndDate = null,
    int? Occurrences = null,
    RecurringAmounts AmountMode = RecurringAmounts.Fixed,
    decimal BaseAmount = 0,
    bool PostAutomatically = false,
    bool AutoReverse = false);

/// <summary>One scheduled occurrence and what happened to it (nothing yet, a draft, posted, or skipped and why).</summary>
public sealed record RecurringRunDto(
    Guid? Id,
    Guid TemplateId,
    string TemplateName,
    int Occurrence,
    DateOnly ScheduledDate,
    string? PeriodName,
    RecurringRunState? Status,
    Guid? JournalEntryId,
    string? EntryNumber,
    Guid? ReversalEntryId,
    string? ReversalNumber,
    decimal Amount,
    string? Message,
    string? GeneratedBy,
    DateTime? GeneratedAtUtc);

/// <summary>
/// Generates the entries that are due: with <see cref="FiscalPeriodId"/>, the occurrences dated in that period;
/// without, every occurrence dated up to today. <see cref="TemplateId"/> limits it to one template.
/// </summary>
public sealed record GenerateRecurringRequest(Guid? FiscalPeriodId = null, Guid? TemplateId = null);

public sealed record RecurringGenerationResultDto(int Drafted, int Posted, int Reversed, int Skipped, int AlreadyDone, IReadOnlyList<RecurringRunDto> Runs, IReadOnlyList<string> Messages);

public sealed record RecurringSettingsDto(bool AutoGenerate, DateTime? LastAutoRunUtc, string? LastAutoRunSummary);

public sealed record SaveRecurringSettingsRequest(bool AutoGenerate);

public interface IRecurringJournalService
{
    Task<IReadOnlyList<RecurringTemplateDto>> ListTemplatesAsync(bool includeInactive = true, CancellationToken cancellationToken = default);

    Task<RecurringTemplateDto> GetTemplateAsync(Guid templateId, CancellationToken cancellationToken = default);

    Task<RecurringTemplateDto> CreateTemplateAsync(SaveRecurringTemplateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes a template; entries already generated stay as they are.</summary>
    Task<RecurringTemplateDto> UpdateTemplateAsync(Guid templateId, SaveRecurringTemplateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Pauses (inactive templates generate nothing) or resumes a template.</summary>
    Task<RecurringTemplateDto> SetActiveAsync(Guid templateId, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>The schedule with what was generated: past occurrences and the next <paramref name="upcoming"/> ones.</summary>
    Task<IReadOnlyList<RecurringRunDto>> GetScheduleAsync(Guid templateId, int upcoming = 6, CancellationToken cancellationToken = default);

    /// <summary>Generated, posted and skipped occurrences, newest first (optionally for one template).</summary>
    Task<IReadOnlyList<RecurringRunDto>> ListHistoryAsync(Guid? templateId = null, int take = 200, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the due entries as drafts or posts them (per template), linked to their template; occurrences in a closed
    /// period are skipped with a message and retried next time; posted entries of auto-reversing templates are reversed on
    /// the first day of the next period once it's open. Idempotent.
    /// </summary>
    Task<RecurringGenerationResultDto> GenerateDueAsync(GenerateRecurringRequest request, CancellationToken cancellationToken = default);

    Task<RecurringSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Turns the background job (every few hours: everything due up to today) on or off.</summary>
    Task<RecurringSettingsDto> SaveSettingsAsync(SaveRecurringSettingsRequest request, CancellationToken cancellationToken = default);
}

// ---- Budget versions ----

public enum BudgetVersionKind { Original, Revised, Forecast }

public enum BudgetVersionState { Draft, Adopted }

public enum SpreadMethod
{
    /// <summary>The same amount every period (any cents left over go to the first periods).</summary>
    Evenly,

    /// <summary>In proportion to the prior fiscal year's actuals on the account and org unit, period by period.</summary>
    PriorYearPattern,

    /// <summary>By twelve percentages (one per period) that add up to 100.</summary>
    Profile,
}

public sealed record BudgetVersionDto(
    Guid Id,
    int FiscalYear,
    BudgetVersionKind Kind,
    string Name,
    string? Description,
    BudgetVersionState Status,
    Guid? SourceVersionId,
    string? SourceVersionName,
    decimal Revenue,
    decimal Expense,
    int LineCount,
    string CreatedBy,
    DateTime CreatedAtUtc,
    string? AdoptedBy,
    DateTime? AdoptedAtUtc,
    Guid? PlanningCycleId)
{
    public bool IsLocked => Status == BudgetVersionState.Adopted;

    public decimal Net => Revenue - Expense;
}

/// <summary>Creates a version for a fiscal year, optionally copying another version's lines (from any year: periods map by number).</summary>
public sealed record CreateBudgetVersionRequest(int FiscalYear, BudgetVersionKind Kind, string Name, string? Description = null, Guid? CopyFromVersionId = null);

public sealed record BudgetPeriodDto(Guid Id, int Number, string Name, DateOnly Start, DateOnly End);

/// <summary>One grid row: an account × org unit with an amount per period (in <see cref="BudgetGridDto.Periods"/> order).</summary>
public sealed record BudgetGridRowDto(
    Guid AccountId,
    string AccountNumber,
    string AccountName,
    AccountKind AccountType,
    Guid OrgUnitId,
    string OrgUnitCode,
    IReadOnlyList<decimal> Amounts)
{
    public decimal Total => Amounts.Sum();
}

public sealed record BudgetGridDto(BudgetVersionDto Version, IReadOnlyList<BudgetPeriodDto> Periods, IReadOnlyList<BudgetGridRowDto> Rows);

public sealed record BudgetCellInput(Guid AccountId, Guid OrgUnitId, Guid FiscalPeriodId, decimal Amount);

/// <summary>Sets cells (0 removes one). Amounts are in whole cents, in the account's natural sign.</summary>
public sealed record SaveBudgetCellsRequest(IReadOnlyList<BudgetCellInput> Cells);

/// <summary>Spreads an annual amount over the year's periods; replaces that account × org unit's row.</summary>
public sealed record SpreadBudgetRequest(Guid AccountId, Guid OrgUnitId, decimal AnnualAmount, SpreadMethod Method, IReadOnlyList<decimal>? Profile = null);

/// <summary>
/// CSV with a header row: <c>account,org,period,amount</c> (one amount per row; period is its name, e.g. 2027-10, or its
/// number 1–12) or <c>account,org,annual</c> (spread evenly). With <see cref="Replace"/>, the version's other lines are removed.
/// </summary>
public sealed record ImportBudgetCsvRequest(string Csv, bool Replace = false);

public sealed record BudgetImportResultDto(int CellsSet, int RowsRead, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

public interface IGlBudgetService
{
    Task<IReadOnlyList<BudgetVersionDto>> ListVersionsAsync(int? fiscalYear = null, CancellationToken cancellationToken = default);

    Task<BudgetVersionDto> CreateVersionAsync(CreateBudgetVersionRequest request, CancellationToken cancellationToken = default);

    /// <summary>The version's lines as a grid, optionally for one org unit.</summary>
    Task<BudgetGridDto> GetGridAsync(Guid versionId, Guid? orgUnitId = null, CancellationToken cancellationToken = default);

    /// <summary>Sets cells. Adopted versions are locked (<see cref="InvalidOperationException"/>). Audited.</summary>
    Task<BudgetGridDto> SaveCellsAsync(Guid versionId, SaveBudgetCellsRequest request, CancellationToken cancellationToken = default);

    Task<BudgetGridDto> SpreadAsync(Guid versionId, SpreadBudgetRequest request, CancellationToken cancellationToken = default);

    /// <summary>Imports amounts from CSV. Nothing is saved when any row is invalid; the errors say which rows and why.</summary>
    Task<BudgetImportResultDto> ImportCsvAsync(Guid versionId, ImportBudgetCsvRequest request, CancellationToken cancellationToken = default);

    /// <summary>Adopts the version: it's locked against any further change. Audited.</summary>
    Task<BudgetVersionDto> AdoptAsync(Guid versionId, CancellationToken cancellationToken = default);
}

// ---- Budget vs actual ----

/// <summary>Budget against actual for one line, for the period and the fiscal year to date. Variance is favourable-positive.</summary>
public sealed record BudgetVsActualFigures(decimal PeriodBudget, decimal PeriodActual, decimal YtdBudget, decimal YtdActual, decimal AnnualBudget, bool IsRevenue)
{
    /// <summary>Favourable is positive: revenue over budget, or cost under budget.</summary>
    public decimal PeriodVariance => IsRevenue ? PeriodActual - PeriodBudget : PeriodBudget - PeriodActual;

    public decimal YtdVariance => IsRevenue ? YtdActual - YtdBudget : YtdBudget - YtdActual;

    /// <summary>Variance as a percentage of budget; null without a budget.</summary>
    public decimal? PeriodVariancePercent => PeriodBudget == 0 ? null : Math.Round(PeriodVariance / Math.Abs(PeriodBudget) * 100m, 1);

    public decimal? YtdVariancePercent => YtdBudget == 0 ? null : Math.Round(YtdVariance / Math.Abs(YtdBudget) * 100m, 1);
}

public sealed record BudgetVsActualLineDto(string Section, string? Code, string Label, BudgetVsActualFigures Figures, bool IsTotal = false, Guid? Id = null);

public sealed record BudgetVsActualDto(
    string Title,
    Guid VersionId,
    string VersionName,
    Guid FiscalPeriodId,
    string FiscalPeriodName,
    DateOnly YearStart,
    DateOnly PeriodEnd,
    Guid? OrgUnitId,
    string? OrgUnitCode,
    IReadOnlyList<BudgetVsActualLineDto> Lines,
    IReadOnlyList<string> Notes);

public sealed record BudgetVsActualQuery(Guid VersionId, Guid FiscalPeriodId, Guid? OrgUnitId = null);

/// <summary>One pool's rates for a fiscal year: provisional (set), budgeted (from a budget version) and actual (GL year to date).</summary>
public sealed record BudgetedRateDto(
    CostPool Pool,
    string Name,
    decimal BudgetedPoolCost,
    decimal BudgetedBase,
    decimal? BudgetedRate,
    decimal? ProvisionalRate,
    decimal? TargetRate,
    decimal ActualPoolCost,
    decimal ActualBase,
    decimal? ActualRate)
{
    public decimal? BudgetedVsProvisional => BudgetedRate is { } b && ProvisionalRate is { } p ? b - p : null;

    public decimal? ActualVsBudgeted => ActualRate is { } a && BudgetedRate is { } b ? a - b : null;
}

public sealed record BudgetedRatesDto(int FiscalYear, Guid? VersionId, string? VersionName, string ActualThrough, IReadOnlyList<BudgetedRateDto> Pools, IReadOnlyList<string> Notes);

public enum BudgetReport { IncomeStatement, ByOrgUnit, ByPool }

public interface IBudgetReportService
{
    /// <summary>Income statement accounts: budget vs actual for the period and the year to date (optionally one org unit).</summary>
    Task<BudgetVsActualDto> GetIncomeStatementAsync(BudgetVsActualQuery query, CancellationToken cancellationToken = default);

    /// <summary>Revenue, expense and net per org unit.</summary>
    Task<BudgetVsActualDto> GetByOrgUnitAsync(BudgetVsActualQuery query, CancellationToken cancellationToken = default);

    /// <summary>Indirect pool cost (fringe, overhead, G&amp;A) and the direct bases, budget vs actual.</summary>
    Task<BudgetVsActualDto> GetByPoolAsync(BudgetVsActualQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Budgeted indirect rates from a version's pool and base budgets (default: the year's adopted version, else its latest),
    /// next to the provisional and actual rates.
    /// </summary>
    Task<BudgetedRatesDto> GetBudgetedRatesAsync(int fiscalYear, Guid? versionId = null, CancellationToken cancellationToken = default);

    Task<AccountingExportFile> ExportCsvAsync(BudgetReport report, BudgetVsActualQuery query, CancellationToken cancellationToken = default);
}

// ---- Planning cycles ----

public enum PlanningCycleState { Open, Adopted }

public enum SubmissionState { NotStarted, Drafting, Submitted, Approved, Returned }

public sealed record BudgetSubmissionSummaryDto(
    Guid Id,
    Guid CycleId,
    string CycleName,
    int FiscalYear,
    Guid OrgUnitId,
    string OrgUnitCode,
    string OrgUnitName,
    Guid OwnerEmployeeId,
    string OwnerName,
    string OwnerEmail,
    SubmissionState Status,
    decimal Total,
    DateTime? SubmittedAtUtc,
    string? DecidedBy,
    DateTime? DecidedAtUtc,
    string? DecisionComment,
    DateOnly? DueDate);

public sealed record PlanningCycleDto(
    Guid Id,
    int FiscalYear,
    string Name,
    string? Instructions,
    DateOnly? DueDate,
    Guid VersionId,
    string VersionName,
    PlanningCycleState Status,
    string OpenedBy,
    DateTime OpenedAtUtc,
    string? AdoptedBy,
    DateTime? AdoptedAtUtc,
    IReadOnlyList<BudgetSubmissionSummaryDto> Submissions)
{
    public int Count(SubmissionState state) => Submissions.Count(s => s.Status == state);

    public bool CanAdopt => Status == PlanningCycleState.Open && Submissions.Count > 0 && Submissions.All(s => s.Status == SubmissionState.Approved);
}

public sealed record CycleAssignmentInput(Guid OrgUnitId, Guid OwnerEmployeeId);

/// <summary>
/// Opens a cycle for a fiscal year: creates its corporate version (<see cref="VersionName"/>, Original; or copies
/// <see cref="CopyFromVersionId"/> as each unit's starting lines) and assigns an owner per org unit.
/// </summary>
public sealed record OpenPlanningCycleRequest(
    int FiscalYear,
    string Name,
    IReadOnlyList<CycleAssignmentInput> Assignments,
    string? Instructions = null,
    DateOnly? DueDate = null,
    string? VersionName = null,
    Guid? CopyFromVersionId = null);

public sealed record AccountOptionDto(Guid Id, string Number, string Name, AccountKind Type);

public sealed record SubmissionRowDto(Guid AccountId, string AccountNumber, string AccountName, AccountKind AccountType, IReadOnlyList<decimal> Amounts)
{
    public decimal Total => Amounts.Sum();
}

/// <summary>
/// An org unit's submission: its lines as a grid (rows per account, columns per period), the accounts it can budget,
/// the prior year's actuals per account, and what the signed-in user may do (the owner may recall a waiting budget).
/// </summary>
public sealed record BudgetSubmissionDto(
    BudgetSubmissionSummaryDto Summary,
    string? Instructions,
    IReadOnlyList<BudgetPeriodDto> Periods,
    IReadOnlyList<SubmissionRowDto> Rows,
    IReadOnlyList<AccountOptionDto> Accounts,
    IReadOnlyDictionary<Guid, decimal> PriorYearActuals,
    bool CanEdit,
    bool CanDecide,
    bool CanRecall = false);

public sealed record SubmissionCellInput(Guid AccountId, Guid FiscalPeriodId, decimal Amount);

public sealed record SaveSubmissionCellsRequest(IReadOnlyList<SubmissionCellInput> Cells);

public sealed record SpreadSubmissionRequest(Guid AccountId, decimal AnnualAmount, SpreadMethod Method, IReadOnlyList<decimal>? Profile = null);

public sealed record DecideSubmissionRequest(string? Comment = null);

public interface IPlanningCycleService
{
    Task<IReadOnlyList<PlanningCycleDto>> ListCyclesAsync(CancellationToken cancellationToken = default);

    /// <summary>The status dashboard: every org unit's submission and where it stands.</summary>
    Task<PlanningCycleDto> GetCycleAsync(Guid cycleId, CancellationToken cancellationToken = default);

    Task<PlanningCycleDto> OpenCycleAsync(OpenPlanningCycleRequest request, CancellationToken cancellationToken = default);

    /// <summary>Assigns (or reassigns) an org unit's owner; adds the unit to the cycle when it isn't in it yet.</summary>
    Task<PlanningCycleDto> AssignOwnerAsync(Guid cycleId, CycleAssignmentInput assignment, CancellationToken cancellationToken = default);

    /// <summary>Adopts the cycle once every submission is approved: locks its version. Audited.</summary>
    Task<PlanningCycleDto> AdoptAsync(Guid cycleId, CancellationToken cancellationToken = default);

    /// <summary>The signed-in user's submissions (the org units they own in any cycle).</summary>
    Task<IReadOnlyList<BudgetSubmissionSummaryDto>> ListMineAsync(CancellationToken cancellationToken = default);

    Task<BudgetSubmissionDto> GetSubmissionAsync(Guid submissionId, CancellationToken cancellationToken = default);

    /// <summary>Sets the owner's cells (0 removes one); only while drafting or returned.</summary>
    Task<BudgetSubmissionDto> SaveSubmissionCellsAsync(Guid submissionId, SaveSubmissionCellsRequest request, CancellationToken cancellationToken = default);

    Task<BudgetSubmissionDto> SpreadSubmissionAsync(Guid submissionId, SpreadSubmissionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Submits for approval through the "Org unit budget" workflow (default: Finance).</summary>
    Task<BudgetSubmissionDto> SubmitAsync(Guid submissionId, CancellationToken cancellationToken = default);

    /// <summary>Withdraws a submission that is waiting for approval, back to drafting.</summary>
    Task<BudgetSubmissionDto> RecallAsync(Guid submissionId, CancellationToken cancellationToken = default);

    /// <summary>Approves the step waiting on the user; the final approval rolls the lines up into the cycle's version.</summary>
    Task<BudgetSubmissionDto> ApproveAsync(Guid submissionId, DecideSubmissionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns the submission to its owner for changes (a comment is required).</summary>
    Task<BudgetSubmissionDto> ReturnAsync(Guid submissionId, DecideSubmissionRequest request, CancellationToken cancellationToken = default);
}
