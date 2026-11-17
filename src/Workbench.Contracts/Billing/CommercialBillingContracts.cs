namespace Workbench.Contracts.Billing;

// Commercial billing: agreements without CLINs (fixed fee with milestones, retainers, recurring charges, T&M on a rate card),
// customer credits and write-offs, payment reminders, and the clean invoice layout with the organization's remittance details.
// Everything bills through the same invoice pipeline as government contracts (IInvoiceService), so the GL, revenue
// recognition and aging see these invoices like any other.

// Mirror the domain enums in Workbench.Domain.Billing by name.
public enum AgreementModel { TimeAndMaterials, FixedFeeMilestones, Retainer, Recurring }

public enum BillingCadenceKind { Monthly, Quarterly, SemiAnnual, Annual }

public enum UnusedHoursKind { Off, Expire, Rollover }

public enum InvoiceLayoutKind { Government, Commercial }

// ---- Agreement billing plans ----

public sealed record AgreementBillingPlanDto(
    Guid ContractId,
    string ContractNumber,
    string ContractName,
    string CustomerName,
    AgreementModel Model,
    BillingCadenceKind Cadence,
    DateOnly StartDate,
    DateOnly? EndDate,
    decimal Amount,
    string? Description,
    bool InvoiceOnComplete,
    decimal? IncludedHours,
    UnusedHoursKind UnusedHours,
    decimal? OverageRate,
    bool AutoGenerate,
    DateOnly? NextPeriodFrom,
    DateOnly? NextPeriodTo,
    int PeriodsBilled,
    decimal BilledToDate,
    string Summary);

/// <summary>
/// The billing model of an agreement. Retainer and recurring need a start date and an amount; fixed fee takes its amounts
/// from its milestones; T&amp;M takes hours at the contract's rate card.
/// </summary>
public sealed record SaveAgreementBillingPlanRequest(
    AgreementModel Model,
    BillingCadenceKind Cadence = BillingCadenceKind.Monthly,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    decimal Amount = 0m,
    string? Description = null,
    bool InvoiceOnComplete = true,
    decimal? IncludedHours = null,
    UnusedHoursKind UnusedHours = UnusedHoursKind.Off,
    decimal? OverageRate = null,
    bool AutoGenerate = true);

public sealed record AgreementMilestoneDto(
    Guid Id,
    Guid ContractId,
    string Description,
    decimal Amount,
    DateOnly TargetDate,
    DateOnly? CompletedDate,
    string? CompletedBy,
    Guid? InvoiceId,
    string? InvoiceNumber,
    int Order)
{
    public bool IsComplete => CompletedDate is not null;

    public bool IsBilled => InvoiceId is not null;
}

public sealed record SaveAgreementMilestoneRequest(string Description, decimal Amount, DateOnly TargetDate);

/// <summary>Completing a milestone makes it billable. A draft invoice is created when the plan says so (<see cref="CreateDraftInvoice"/> overrides it).</summary>
public sealed record CompleteAgreementMilestoneRequest(DateOnly CompletedDate, bool? CreateDraftInvoice = null);

public sealed record AgreementMilestoneCompletionDto(AgreementMilestoneDto Milestone, InvoiceDto? Draft);

/// <summary>A retainer or recurring period that has started and has no invoice yet.</summary>
public sealed record DueAgreementPeriodDto(
    Guid ContractId,
    string ContractNumber,
    string CustomerName,
    InvoiceKind Type,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    decimal Amount,
    string? Description,
    bool HasDraft);

public sealed record GenerateDueInvoicesResultDto(int Created, IReadOnlyList<string> InvoiceNumbers, IReadOnlyList<string> Skipped);

public interface IAgreementBillingService
{
    /// <summary>The contract's billing plan, or null when it bills the government way (CLINs).</summary>
    Task<AgreementBillingPlanDto?> GetPlanAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgreementBillingPlanDto>> ListPlansAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets the billing model of a contract with no CLINs. Refused when the contract has CLINs.</summary>
    Task<AgreementBillingPlanDto> SavePlanAsync(Guid contractId, SaveAgreementBillingPlanRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes the plan when nothing was billed through it.</summary>
    Task RemovePlanAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgreementMilestoneDto>> ListMilestonesAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<AgreementMilestoneDto> CreateMilestoneAsync(Guid contractId, SaveAgreementMilestoneRequest request, CancellationToken cancellationToken = default);

    Task<AgreementMilestoneDto> UpdateMilestoneAsync(Guid milestoneId, SaveAgreementMilestoneRequest request, CancellationToken cancellationToken = default);

    Task<AgreementMilestoneCompletionDto> CompleteMilestoneAsync(Guid milestoneId, CompleteAgreementMilestoneRequest request, CancellationToken cancellationToken = default);

    Task<AgreementMilestoneDto> ReopenMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default);

    Task DeleteMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default);

    /// <summary>Retainer and recurring periods that have started by <paramref name="asOf"/> (today) and aren't invoiced yet.</summary>
    Task<IReadOnlyList<DueAgreementPeriodDto>> ListDuePeriodsAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Creates a draft invoice for each due period (of one contract, or all). Drafts are never approved automatically.</summary>
    Task<GenerateDueInvoicesResultDto> GenerateDueDraftsAsync(DateOnly? asOf = null, Guid? contractId = null, CancellationToken cancellationToken = default);
}

// ---- Receivables: credits, write-offs, reminders ----

public sealed record CustomerCreditDto(
    Guid PaymentId,
    Guid CustomerId,
    string CustomerName,
    DateOnly ReceivedOn,
    string? Reference,
    decimal Amount,
    decimal Unapplied);

public sealed record CustomerCreditSummaryDto(Guid CustomerId, string CustomerName, decimal Credit);

/// <summary>The A/R aging with customer credits beside it: net receivable is what's owed less cash held unapplied.</summary>
public sealed record ReceivablesAgingDto(ArAgingDto Aging, IReadOnlyList<CustomerCreditSummaryDto> Credits, decimal TotalCredits, decimal NetReceivable);

public sealed record ApplyCreditRequest(IReadOnlyList<PaymentApplicationInput> Applications);

public sealed record WriteOffInvoiceRequest(decimal Amount, string Reason, DateOnly? Date = null);

public sealed record ReverseWriteOffRequest(string Reason);

public sealed record WriteOffDto(
    Guid Id,
    Guid InvoiceId,
    string InvoiceNumber,
    Guid CustomerId,
    string CustomerName,
    DateOnly WrittenOffOn,
    decimal Amount,
    string Reason,
    string? JournalEntryNumber,
    bool IsReversed,
    string? ReversalReason,
    string CreatedBy,
    DateTime CreatedAtUtc);

public interface IReceivablesService
{
    /// <summary>The A/R aging (current, 1–30, 31–60, 61–90, 90+ days past due) by customer, with unapplied customer credits.</summary>
    Task<ReceivablesAgingDto> GetAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Receipts with cash not yet applied to an invoice (overpayments).</summary>
    Task<IReadOnlyList<CustomerCreditDto>> ListCreditsAsync(Guid? customerId = null, CancellationToken cancellationToken = default);

    /// <summary>Applies a receipt's unapplied cash to open invoices. No journal entry: the receipt already credited AR.</summary>
    Task<PaymentDto> ApplyCreditAsync(Guid paymentId, ApplyCreditRequest request, CancellationToken cancellationToken = default);

    /// <summary>Writes off all or part of an open invoice (debit bad debt expense, credit AR billed). A reason is required.</summary>
    Task<WriteOffDto> WriteOffAsync(Guid invoiceId, WriteOffInvoiceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reverses a write-off (its journal entry and the invoice's balance), with a reason.</summary>
    Task<WriteOffDto> ReverseWriteOffAsync(Guid writeOffId, ReverseWriteOffRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WriteOffDto>> ListWriteOffsAsync(Guid? invoiceId = null, Guid? customerId = null, CancellationToken cancellationToken = default);

    /// <summary>Tells Finance about invoices coming due and past due (once per invoice and reminder). Returns how many were sent.</summary>
    Task<int> SendPaymentRemindersAsync(CancellationToken cancellationToken = default);
}

// ---- Organization billing settings and the commercial invoice ----

public sealed record BillingSettingsDto(
    string? LegalName,
    string? RemitToAddress,
    string? RemittanceInstructions,
    string? PaymentTermsText,
    int DefaultPaymentTermsDays,
    string? InvoiceFooter,
    string? BillingContact,
    int ReminderDaysBeforeDue,
    InvoiceLayoutKind DefaultLayout);

public sealed record SaveBillingSettingsRequest(
    string? LegalName,
    string? RemitToAddress,
    string? RemittanceInstructions,
    string? PaymentTermsText,
    int DefaultPaymentTermsDays,
    string? InvoiceFooter,
    string? BillingContact,
    int ReminderDaysBeforeDue,
    InvoiceLayoutKind DefaultLayout);

/// <summary>What the commercial invoice layout prints beside the invoice, and which layout opens by default.</summary>
public sealed record InvoiceRemittanceDto(
    string SellerName,
    string? RemitToAddress,
    string? RemittanceInstructions,
    string? PaymentTermsText,
    int TermsDays,
    DateOnly DueDate,
    string? Footer,
    string? BillingContact,
    InvoiceLayoutKind Layout);

public interface IBillingSettingsService
{
    Task<BillingSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    Task<BillingSettingsDto> SaveAsync(SaveBillingSettingsRequest request, CancellationToken cancellationToken = default);
}
