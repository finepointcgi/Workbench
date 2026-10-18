using Workbench.Domain.Cash;

namespace Workbench.Application.Cash;

/// <summary>What a match contributes to the math: the dates on each side.</summary>
internal sealed record MatchDates(Guid MatchId, IReadOnlyList<DateOnly> BankDates, IReadOnlyList<DateOnly> GlDates);

internal sealed record ReconciliationFigures(
    decimal StatementEndingBalance,
    decimal GlBalance,
    decimal DepositsInTransit,
    decimal OutstandingPayments,
    decimal UnrecordedBankItems,
    IReadOnlyList<GlCashLine> Outstanding,
    IReadOnlyList<BankTransaction> Unrecorded)
{
    public decimal AdjustedBankBalance => StatementEndingBalance + DepositsInTransit - OutstandingPayments;

    public decimal AdjustedBookBalance => GlBalance + UnrecordedBankItems;

    public decimal Difference => AdjustedBankBalance - AdjustedBookBalance;
}

/// <summary>
/// The reconciliation as of a statement end date. A match has cleared by then when all of its bank items and GL lines
/// are dated on or before it. GL lines dated by then that haven't cleared are outstanding: deposits in transit (receipts)
/// and outstanding checks and payments. Bank items dated by then that haven't cleared aren't recorded in the GL yet
/// (fees, interest). Adjusted bank = statement + deposits in transit − outstanding payments; adjusted book = GL balance +
/// unrecorded bank items; they agree when everything is accounted for.
/// </summary>
internal static class ReconciliationMath
{
    public static ReconciliationFigures Compute(
        DateOnly endDate,
        decimal statementEndingBalance,
        IReadOnlyList<GlCashLine> glLines,
        IReadOnlyDictionary<Guid, Guid> glLineMatches,
        IReadOnlyList<BankTransaction> bankItems,
        IReadOnlyList<MatchDates> matches)
    {
        var cleared = matches.Where(m => m.BankDates.All(d => d <= endDate) && m.GlDates.All(d => d <= endDate)).Select(m => m.MatchId).ToHashSet();
        var gl = glLines.Where(l => l.Date <= endDate).ToList();
        var outstanding = gl.Where(l => !glLineMatches.TryGetValue(l.LineId, out var m) || !cleared.Contains(m)).ToList();
        var unrecorded = bankItems.Where(t => t.Date <= endDate && (t.MatchId is not { } m || !cleared.Contains(m))).ToList();
        return new ReconciliationFigures(
            statementEndingBalance,
            gl.Sum(l => l.Amount),
            outstanding.Where(l => l.Amount > 0).Sum(l => l.Amount),
            -outstanding.Where(l => l.Amount < 0).Sum(l => l.Amount),
            unrecorded.Sum(t => t.Amount),
            outstanding,
            unrecorded);
    }
}
