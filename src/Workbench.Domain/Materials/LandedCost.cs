namespace Workbench.Domain.Materials;

public enum LandedChargeKind
{
    Freight,
    Duty,
    Handling,
    Other,
}

/// <summary>How a landed charge is spread over the receipt lines it covers.</summary>
public enum LandedCostMethod
{
    /// <summary>By each line's received value (quantity × PO price).</summary>
    Value,

    Quantity,

    /// <summary>By the weight entered for each line (from the bill of lading).</summary>
    Weight,
}

public enum LandedChargeStatus
{
    Draft,
    Posted,
    Cancelled,
}

/// <summary>
/// Freight, duty or handling paid to bring purchased stock in, from an AP bill line (charged to the landed cost clearing
/// account) or entered by hand against a chosen account. Posting allocates it over the receipt lines it covers: the share
/// of units still on hand raises inventory value; the share already issued goes where those units went (the project, the
/// work order's WIP, or cost of goods sold); standard-cost parts take it as purchase price variance.
/// </summary>
public sealed class LandedCharge : Entity
{
    /// <summary>For example "LC-000002".</summary>
    public required string Number { get; set; }

    public LandedChargeKind Kind { get; set; }
    public required string Description { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public LandedCostMethod Method { get; set; }

    /// <summary>The AP bill line the charge came from (each line is allocated once).</summary>
    public Guid? ApBillLineId { get; set; }

    public Guid? VendorId { get; set; }

    /// <summary>The account the allocation credits: the bill line's account, or the one chosen for a manual charge.</summary>
    public Guid CreditAccountId { get; set; }

    /// <summary>The company of the AP bill (journal entries post to it); null for manual charges (the default company).</summary>
    public Guid? CompanyId { get; set; }

    public LandedChargeStatus Status { get; set; } = LandedChargeStatus.Draft;
    public Guid? JournalEntryId { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    public List<LandedChargeLine> Lines { get; } = [];
}

/// <summary>One receipt line (a purchase receipt inventory transaction) a landed charge covers, and what it got.</summary>
public sealed class LandedChargeLine : Entity
{
    public Guid LandedChargeId { get; set; }

    /// <summary>The <see cref="InventoryTransactionType.PurchaseReceipt"/> transaction.</summary>
    public Guid ReceiptTransactionId { get; set; }

    public Guid PurchaseReceiptId { get; set; }
    public Guid PartId { get; set; }

    /// <summary>Weight-based charges: the line's weight (any unit, used as a ratio).</summary>
    public decimal? Weight { get; set; }

    /// <summary>The line's share of the charge (whole cents; the lines add up to the charge).</summary>
    public decimal Amount { get; set; }

    /// <summary>Raised the value of stock still on hand.</summary>
    public decimal ToInventory { get; set; }

    /// <summary>Followed units already issued (projects, work orders, cost of goods sold, adjustments).</summary>
    public decimal ToIssued { get; set; }

    /// <summary>Standard-cost parts: purchase price variance.</summary>
    public decimal ToVariance { get; set; }
}
