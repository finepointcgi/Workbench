using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Application.Accounting;

/// <summary>
/// Closing a period from Setup › Fiscal calendar goes through the month-end close checklist (IPeriodCloseService), so the
/// older setup screen can't close a period with unapproved time, undecided ODCs, unposted items or an unbalanced trial
/// balance. Reopening stays a plain status change.
/// </summary>
public sealed class ChecklistFiscalCalendarService(IFiscalCalendarService inner, IPeriodCloseService periodClose) : IFiscalCalendarService
{
    public Task<IReadOnlyList<FiscalPeriodDto>> ListPeriodsAsync(int? fiscalYear = null, CancellationToken cancellationToken = default) =>
        inner.ListPeriodsAsync(fiscalYear, cancellationToken);

    public Task<FiscalPeriodDto?> GetPeriodForDateAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        inner.GetPeriodForDateAsync(date, cancellationToken);

    public Task<IReadOnlyList<FiscalPeriodDto>> CreateFiscalYearAsync(int fiscalYear, DateOnly start, CancellationToken cancellationToken = default) =>
        inner.CreateFiscalYearAsync(fiscalYear, start, cancellationToken);

    public async Task<FiscalPeriodDto> SetPeriodStatusAsync(Guid periodId, FiscalPeriodState status, string reason, CancellationToken cancellationToken = default)
    {
        if (status != FiscalPeriodState.Closed)
        {
            return await inner.SetPeriodStatusAsync(periodId, status, reason, cancellationToken);
        }

        try
        {
            await periodClose.CloseAsync(periodId, new ClosePeriodRequest(reason), cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // The setup endpoint maps argument errors to 400; keep the checklist's message.
            throw new ArgumentException(ex.Message, ex);
        }

        var periods = await inner.ListPeriodsAsync(cancellationToken: cancellationToken);
        return periods.FirstOrDefault(p => p.Id == periodId) ?? throw WorkbenchNotFoundException.For("Fiscal period", periodId);
    }
}
