namespace Workbench.Domain.Planning;

public enum RecurrenceFrequency
{
    Monthly,
    Quarterly,
    Annually,
}

/// <summary>Which day of the occurrence's month the entry is dated: the 1st (rent paid in advance) or the last day (accruals).</summary>
public enum RecurringPostingDay
{
    FirstDay,
    LastDay,
}

/// <summary>How a template's lines carry their amounts.</summary>
public enum RecurringAmountMode
{
    /// <summary>Each line has its own amount.</summary>
    Fixed,

    /// <summary>Each line is a percentage of the template's <see cref="RecurringJournalTemplate.BaseAmount"/>; each side adds up to 100%.</summary>
    Percentage,
}

public enum EntrySide
{
    Debit,
    Credit,
}

/// <summary>
/// A recurring journal: lines, a schedule (monthly, quarterly or annually from a start date until an end date or a
/// number of occurrences) and what to do with each generated entry (leave it as a draft or post it, and optionally reverse
/// it on the first day of the next period). Generated entries link back through <see cref="RecurringJournalRun"/>.
/// </summary>
public sealed class RecurringJournalTemplate : Entity
{
    public required string Name { get; set; }

    /// <summary>The generated entries' description.</summary>
    public required string Description { get; set; }

    public RecurrenceFrequency Frequency { get; set; }
    public RecurringPostingDay PostingDay { get; set; } = RecurringPostingDay.LastDay;

    /// <summary>The first occurrence is in this date's month.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>No occurrence is dated after this (inclusive). Null with <see cref="Occurrences"/> null means no end.</summary>
    public DateOnly? EndDate { get; set; }

    /// <summary>Stop after this many occurrences.</summary>
    public int? Occurrences { get; set; }

    public RecurringAmountMode AmountMode { get; set; }

    /// <summary>For <see cref="RecurringAmountMode.Percentage"/>: the amount each side's percentages split.</summary>
    public decimal BaseAmount { get; set; }

    /// <summary>Post generated entries (true) or leave them as drafts for review (false).</summary>
    public bool PostAutomatically { get; set; }

    /// <summary>Reverse each posted entry on the first day of the next fiscal period (an accrual).</summary>
    public bool AutoReverse { get; set; }

    public bool IsActive { get; set; } = true;

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    public List<RecurringJournalTemplateLine> Lines { get; } = [];

    /// <summary>
    /// The scheduled occurrences (1-based number and entry date) dated on or before <paramref name="through"/>, honouring
    /// the end date and the occurrence limit.
    /// </summary>
    public IEnumerable<(int Number, DateOnly Date)> Schedule(DateOnly through)
    {
        var step = Frequency switch
        {
            RecurrenceFrequency.Quarterly => 3,
            RecurrenceFrequency.Annually => 12,
            _ => 1,
        };
        // A start after the 1st means that month's first-day occurrence is already past: start with the next month.
        var month = new DateOnly(StartDate.Year, StartDate.Month, 1);
        if (PostingDay == RecurringPostingDay.FirstDay && StartDate.Day > 1)
        {
            month = month.AddMonths(1);
        }

        for (var n = 1; Occurrences is null || n <= Occurrences; n++)
        {
            var date = PostingDay == RecurringPostingDay.FirstDay ? month : month.AddMonths(1).AddDays(-1);
            if (date > through || (EndDate is { } end && date > end))
            {
                yield break;
            }

            yield return (n, date);
            month = month.AddMonths(step);
        }
    }

    /// <summary>
    /// The lines' debits and credits for one occurrence, in whole cents. Percentage lines split <see cref="BaseAmount"/>
    /// by the largest remainder, so each side adds up to exactly the base amount.
    /// </summary>
    public IReadOnlyList<(RecurringJournalTemplateLine Line, decimal Debit, decimal Credit)> Amounts()
    {
        var lines = Lines.OrderBy(l => l.LineNumber).ToList();
        if (AmountMode == RecurringAmountMode.Fixed)
        {
            return lines.Select(l => (l, l.Side == EntrySide.Debit ? l.Amount : 0m, l.Side == EntrySide.Credit ? l.Amount : 0m)).ToList();
        }

        var amounts = new Dictionary<RecurringJournalTemplateLine, decimal>();
        foreach (var side in new[] { EntrySide.Debit, EntrySide.Credit })
        {
            var sideLines = lines.Where(l => l.Side == side).ToList();
            var split = Allocation.Split(BaseAmount, sideLines.Select(l => l.Percent).ToList());
            for (var i = 0; i < sideLines.Count; i++)
            {
                amounts[sideLines[i]] = split[i];
            }
        }

        return lines.Select(l => (l, l.Side == EntrySide.Debit ? amounts[l] : 0m, l.Side == EntrySide.Credit ? amounts[l] : 0m)).ToList();
    }
}

public sealed class RecurringJournalTemplateLine : Entity
{
    public Guid TemplateId { get; set; }
    public int LineNumber { get; set; }
    public Guid AccountId { get; set; }
    public Guid? OrgUnitId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? WbsElementId { get; set; }
    public EntrySide Side { get; set; }

    /// <summary>For fixed templates.</summary>
    public decimal Amount { get; set; }

    /// <summary>For percentage templates: this line's share of its side, in percent (25 = 25%).</summary>
    public decimal Percent { get; set; }

    public string? Memo { get; set; }
}

public enum RecurringRunStatus
{
    /// <summary>The entry was created as a draft.</summary>
    Draft,

    /// <summary>The entry was posted.</summary>
    Posted,

    /// <summary>Nothing was created (the period is closed or the entry was invalid); <see cref="RecurringJournalRun.Message"/> says why. Generating again retries it.</summary>
    Skipped,
}

/// <summary>One occurrence of a template: the entry it generated (and its reversal), or why it was skipped.</summary>
public sealed class RecurringJournalRun : Entity
{
    public Guid TemplateId { get; set; }

    /// <summary>1-based, unique per template.</summary>
    public int Occurrence { get; set; }

    public DateOnly ScheduledDate { get; set; }
    public Guid? FiscalPeriodId { get; set; }
    public RecurringRunStatus Status { get; set; }
    public Guid? JournalEntryId { get; set; }
    public Guid? ReversalEntryId { get; set; }
    public string? Message { get; set; }
    public decimal Amount { get; set; }
    public required string GeneratedBy { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
}

/// <summary>The organization's recurring journal setting (one row): whether the background job generates due entries on its own.</summary>
public sealed class RecurringJournalSettings : Entity
{
    public bool AutoGenerate { get; set; }
    public DateTime? LastAutoRunUtc { get; set; }
    public string? LastAutoRunSummary { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>Splitting money in whole cents.</summary>
public static class Allocation
{
    /// <summary>
    /// Splits <paramref name="total"/> by <paramref name="weights"/> (any positive scale) into whole cents that add up to
    /// exactly the total: each share is rounded down, then the leftover cents go to the largest remainders (earliest first on ties).
    /// All-zero weights split evenly.
    /// </summary>
    public static IReadOnlyList<decimal> Split(decimal total, IReadOnlyList<decimal> weights)
    {
        if (weights.Count == 0)
        {
            return [];
        }

        var w = weights.Sum() == 0 ? weights.Select(_ => 1m).ToList() : weights.ToList();
        var sum = w.Sum();
        var cents = decimal.Round(total * 100m, 0, MidpointRounding.AwayFromZero);
        var sign = cents < 0 ? -1m : 1m;
        cents = Math.Abs(cents);
        var raw = w.Select(x => cents * x / sum).ToList();
        var floors = raw.Select(decimal.Floor).ToList();
        var left = cents - floors.Sum();
        foreach (var i in raw.Select((r, i) => (Remainder: r - floors[i], i)).OrderByDescending(x => x.Remainder).ThenBy(x => x.i).Take((int)left).Select(x => x.i).ToList())
        {
            floors[i] += 1;
        }

        return floors.Select(f => sign * f / 100m).ToList();
    }
}
