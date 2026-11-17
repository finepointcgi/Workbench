namespace Workbench.Domain.Billing;

// Commercial (non-government) billing. An agreement is an ordinary contract with an AgreementBillingPlan: it has no CLINs, no
// funding and no ceiling, and bills through the same invoice pipeline (draft -> approve -> GL -> aging) as every other contract.

/// <summary>How an agreement without CLINs bills.</summary>
public enum AgreementBillingModel
{
    /// <summary>Approved hours at the contract's rate card, plus expenses at cost. No ceiling or funding is checked.</summary>
    TimeAndMaterials,

    /// <summary>A fixed fee paid in stages: each <see cref="AgreementMilestone"/> bills its amount once complete.</summary>
    FixedFeeMilestones,

    /// <summary>A fixed amount each period, optionally with included hours.</summary>
    Retainer,

    /// <summary>A recurring or subscription charge (managed services): an amount each period between a start and an end date.</summary>
    Recurring,
}

public enum BillingCadence
{
    Monthly,
    Quarterly,
    SemiAnnual,
    Annual,
}

/// <summary>What happens to retainer hours the customer didn't use in a period. <see cref="Off"/> (the default) ignores hours.</summary>
public enum UnusedHoursPolicy
{
    /// <summary>Hours aren't tracked against the retainer.</summary>
    Off,

    /// <summary>Unused hours are lost at the end of the period; hours over the included amount bill at the overage rate.</summary>
    Expire,

    /// <summary>Unused hours carry into the following periods; hours over what's included and carried bill at the overage rate.</summary>
    Rollover,
}

/// <summary>The billing model of one commercial agreement. A contract has at most one.</summary>
public sealed class AgreementBillingPlan : Entity
{
    public Guid ContractId { get; set; }
    public AgreementBillingModel Model { get; set; }

    /// <summary>Retainer and recurring: how often a period is billed.</summary>
    public BillingCadence Cadence { get; set; } = BillingCadence.Monthly;

    /// <summary>Retainer and recurring: the first day of the first period.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Retainer and recurring: the last day billed; the last period is prorated by days when it ends early. Null runs until stopped.</summary>
    public DateOnly? EndDate { get; set; }

    /// <summary>Retainer and recurring: the amount per period.</summary>
    public decimal Amount { get; set; }

    /// <summary>Printed on the invoice line ("Managed services", "Monthly advisory retainer").</summary>
    public string? Description { get; set; }

    /// <summary>Fixed fee: creating a draft invoice for a milestone as soon as it's marked complete.</summary>
    public bool InvoiceOnComplete { get; set; } = true;

    /// <summary>Retainer: hours included in each period (null: not tracked).</summary>
    public decimal? IncludedHours { get; set; }

    public UnusedHoursPolicy UnusedHours { get; set; } = UnusedHoursPolicy.Off;

    /// <summary>Retainer: the hourly rate for hours over what's included, when unused hours are tracked.</summary>
    public decimal? OverageRate { get; set; }

    /// <summary>Retainer and recurring: draft invoices for periods that have started are created by the daily run.</summary>
    public bool AutoGenerate { get; set; } = true;

    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A stage of a fixed-fee agreement. Marking it complete makes it billable; an invoice bills it once.</summary>
public sealed class AgreementMilestone : Entity
{
    public Guid ContractId { get; set; }
    public required string Description { get; set; }
    public decimal Amount { get; set; }

    /// <summary>When the stage is due, for planning and reminders.</summary>
    public DateOnly TargetDate { get; set; }

    public DateOnly? CompletedDate { get; set; }
    public string? CompletedBy { get; set; }

    /// <summary>The invoice (draft or approved) billing it; cleared when that invoice is voided or deleted.</summary>
    public Guid? InvoiceId { get; set; }

    public int Order { get; set; }
}

/// <summary>A balance written off as uncollectible (debit bad debt expense, credit AR billed). Reversible with a reason.</summary>
public sealed class InvoiceWriteOff : Entity, Companies.ICompanyOwned
{
    public Guid CompanyId { get; set; } = Companies.Company.DefaultId;
    public Guid InvoiceId { get; set; }
    public Guid CustomerId { get; set; }
    public DateOnly WrittenOffOn { get; set; }
    public decimal Amount { get; set; }
    public required string Reason { get; set; }
    public Guid? JournalEntryId { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool IsReversed { get; set; }
    public string? ReversalReason { get; set; }
    public string? ReversedBy { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
}

/// <summary>
/// What the organization prints on commercial invoices and how it reminds about them: payment terms, remittance details,
/// the default invoice layout. One row per database; absent means the defaults (and the <c>Billing</c> configuration).
/// </summary>
public sealed class BillingOrganizationSettings : Entity
{
    /// <summary>Printed as the seller when set; otherwise the <c>Billing:CompanyName</c> configuration.</summary>
    public string? LegalName { get; set; }

    public string? RemitToAddress { get; set; }

    /// <summary>How to pay: bank, ACH or wire details, a lockbox, "checks payable to..." (free text, printed on the invoice).</summary>
    public string? RemittanceInstructions { get; set; }

    /// <summary>Printed under the totals ("Net 30. Late balances accrue 1.5% a month.").</summary>
    public string? PaymentTermsText { get; set; }

    /// <summary>Payment terms for customers without their own, in days.</summary>
    public int DefaultPaymentTermsDays { get; set; } = 30;

    public string? InvoiceFooter { get; set; }

    /// <summary>Contact for billing questions, printed on the invoice.</summary>
    public string? BillingContact { get; set; }

    /// <summary>Finance is reminded this many days before an invoice is due (0 turns the reminder off).</summary>
    public int ReminderDaysBeforeDue { get; set; } = 3;

    /// <summary>The layout invoices print in unless one is chosen: government vouchers or the clean commercial invoice.</summary>
    public InvoiceLayout DefaultLayout { get; set; } = InvoiceLayout.Government;

    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum InvoiceLayout
{
    /// <summary>The existing layouts: SF 1034 / SF 1035 vouchers and the CLIN-grouped invoice.</summary>
    Government,

    /// <summary>A clean invoice with no CLINs or voucher blocks, with terms, due date and remittance details.</summary>
    Commercial,
}
