namespace Workbench.Domain.Accounting;

/// <summary>Where a journal entry came from. Subledgers post with their own source and the source record's id.</summary>
public enum JournalSource
{
    Manual,
    Timesheet,
    Odc,
    Payroll,
    Billing,
    AP,
    Allocation,
    Reversal,

    /// <summary>Fixed assets: capitalization reclasses and disposals (source id = the asset).</summary>
    Asset,

    /// <summary>Monthly depreciation runs (source id = the run).</summary>
    Depreciation,

    Bank,

    /// <summary>Monthly revenue recognition runs (source id = the project's revenue run).</summary>
    Revenue,

    /// <summary>Materials: inventory receipts, issues to projects, adjustments, cycle counts and revaluations (source id = the inventory transaction).</summary>
    Inventory,

    /// <summary>Manufacturing: work-order issues to WIP, labor applied, scrap, completions and variances (source id = the transaction or labor report).</summary>
    Manufacturing,

    /// <summary>Recurring journals (source id = the template; the revision is the occurrence number).</summary>
    Recurring,
}

public enum JournalStatus
{
    Draft,
    Posted,
    Reversed,
}

/// <summary>
/// A journal entry: balanced debits and credits on one date in one fiscal period. Once posted it is immutable;
/// a correction is a reversing entry (<see cref="ReversalOfId"/>) plus a new entry.
/// </summary>
public sealed class JournalEntry : Entity
{
    /// <summary>Assigned when posted; 0 while a draft.</summary>
    public int Sequence { get; set; }

    /// <summary>For example <c>JE-000042</c>; drafts are <c>DRAFT</c>.</summary>
    public string Number { get; set; } = DraftNumber;

    public DateOnly Date { get; set; }
    public Guid FiscalPeriodId { get; set; }
    public JournalSource Source { get; set; }

    /// <summary>The source record (timesheet, ODC, invoice...) or, for a reversal, the reversed entry.</summary>
    public Guid? SourceId { get; set; }

    /// <summary>The source record's revision when posted (a timesheet's revision, an ODC's reopen count), for idempotent posting.</summary>
    public int SourceRevision { get; set; }

    public required string Description { get; set; }
    public JournalStatus Status { get; set; } = JournalStatus.Draft;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    /// <summary>On a reversing entry: the entry it reverses.</summary>
    public Guid? ReversalOfId { get; set; }

    /// <summary>On a reversed entry: the reversing entry.</summary>
    public Guid? ReversedById { get; set; }

    public string? ReversalReason { get; set; }

    public List<JournalLine> Lines { get; } = [];

    public const string DraftNumber = "DRAFT";

    public decimal TotalDebit => Lines.Sum(l => l.Debit);

    public decimal TotalCredit => Lines.Sum(l => l.Credit);

    public bool IsBalanced => TotalDebit == TotalCredit;

    /// <summary>
    /// Checks the entry can be posted: at least two lines, each with exactly one positive amount in whole cents, and
    /// total debits equal to total credits. Throws <see cref="ArgumentException"/> otherwise.
    /// </summary>
    public void EnsurePostable()
    {
        if (Status != JournalStatus.Draft)
        {
            throw new InvalidOperationException($"Journal entry {Number} is {Status}; posted entries can't be changed. Reverse it instead.");
        }

        if (string.IsNullOrWhiteSpace(Description))
        {
            throw new ArgumentException("A journal entry needs a description.");
        }

        if (Lines.Count < 2)
        {
            throw new ArgumentException("A journal entry needs at least two lines.");
        }

        foreach (var line in Lines)
        {
            line.EnsureValid();
        }

        if (!IsBalanced)
        {
            throw new ArgumentException($"The entry doesn't balance: debits {TotalDebit:N2}, credits {TotalCredit:N2} (difference {TotalDebit - TotalCredit:N2}).");
        }
    }

    /// <summary>Marks a validated draft as posted with its number.</summary>
    public void MarkPosted(int sequence, string actor, DateTime utcNow)
    {
        EnsurePostable();
        Sequence = sequence;
        Number = FormatNumber(sequence);
        Status = JournalStatus.Posted;
        PostedBy = actor;
        PostedAtUtc = utcNow;
    }

    /// <summary>
    /// A draft that reverses this posted entry: the same lines with debits and credits swapped, dated
    /// <paramref name="date"/> in <paramref name="periodId"/>.
    /// </summary>
    public JournalEntry CreateReversal(DateOnly date, Guid periodId, string reason, string actor, DateTime utcNow)
    {
        if (Status != JournalStatus.Posted)
        {
            throw new InvalidOperationException(Status == JournalStatus.Reversed
                ? $"Journal entry {Number} is already reversed."
                : $"Journal entry {Number} isn't posted, so there's nothing to reverse.");
        }

        if (ReversalOfId is not null)
        {
            throw new InvalidOperationException($"Journal entry {Number} is itself a reversal; post a new entry instead.");
        }

        var reversal = new JournalEntry
        {
            Date = date,
            FiscalPeriodId = periodId,
            Source = JournalSource.Reversal,
            SourceId = Id,
            Description = $"Reversal of {Number}: {Description}",
            CreatedBy = actor,
            CreatedAtUtc = utcNow,
            ReversalOfId = Id,
            ReversalReason = reason,
        };
        foreach (var line in Lines.OrderBy(l => l.LineNumber))
        {
            reversal.Lines.Add(new JournalLine
            {
                JournalEntryId = reversal.Id,
                LineNumber = line.LineNumber,
                AccountId = line.AccountId,
                OrgUnitId = line.OrgUnitId,
                ProjectId = line.ProjectId,
                WbsElementId = line.WbsElementId,
                Debit = line.Credit,
                Credit = line.Debit,
                Memo = line.Memo,
            });
        }

        return reversal;
    }

    /// <summary>Links this entry to its posted reversal.</summary>
    public void MarkReversed(JournalEntry reversal, string reason)
    {
        if (Status != JournalStatus.Posted || reversal.ReversalOfId != Id || reversal.Status != JournalStatus.Posted)
        {
            throw new InvalidOperationException($"Journal entry {Number} can't be marked reversed.");
        }

        Status = JournalStatus.Reversed;
        ReversedById = reversal.Id;
        ReversalReason = reason;
    }

    public static string FormatNumber(int sequence) => $"JE-{sequence:D6}";
}

/// <summary>
/// One debit or credit on a journal entry. Besides the account, every line carries the GovCon segments that tie cost to
/// contracts: organization unit, project and WBS element (all optional).
/// </summary>
public sealed class JournalLine : Entity
{
    public Guid JournalEntryId { get; set; }
    public int LineNumber { get; set; }
    public Guid AccountId { get; set; }
    public Guid? OrgUnitId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? WbsElementId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? Memo { get; set; }

    public void EnsureValid()
    {
        if (Debit < 0 || Credit < 0)
        {
            throw new ArgumentException($"Line {LineNumber}: amounts can't be negative.");
        }

        if ((Debit == 0) == (Credit == 0))
        {
            throw new ArgumentException($"Line {LineNumber}: enter either a debit or a credit.");
        }

        if (decimal.Round(Debit, 2) != Debit || decimal.Round(Credit, 2) != Credit)
        {
            throw new ArgumentException($"Line {LineNumber}: amounts are in whole cents.");
        }
    }
}
