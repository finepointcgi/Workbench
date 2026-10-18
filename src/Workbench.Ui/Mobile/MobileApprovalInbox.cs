using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Time;

namespace Workbench.Ui.Mobile;

/// <summary>
/// Everything routed to the signed-in user for approval, read through the existing approval services (each one decides
/// what the user may see). A queue the user can't read, or a service the host doesn't have, is left empty.
/// </summary>
internal sealed record MobileApprovalInbox(
    IReadOnlyList<TimesheetDto> Timesheets,
    IReadOnlyList<ExpenseReportDetailDto> Expenses,
    IReadOnlyList<LeaveRequestDto> Leave,
    IReadOnlyList<RequisitionSummaryDto> Requisitions,
    IReadOnlyList<BillApprovalItemDto> Bills)
{
    public int Count => Timesheets.Count + Expenses.Count + Leave.Count + Requisitions.Count + Bills.Count;

    public static async Task<MobileApprovalInbox> LoadAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        // One after another: the services share the circuit's scoped state, which isn't meant for concurrent use.
        var timesheets = await Queue(services.GetService<ITimesheetApprovalService>(), (s, ct) => s.ListPendingAsync(ct), cancellationToken);
        var expenses = await Queue(services.GetService<IOdcApprovalService>(), async (s, ct) => (await s.GetMyQueueAsync(ct)).Reports, cancellationToken);
        var leave = await Queue(services.GetService<ILeaveRequestService>(), (s, ct) => s.ListPendingApprovalsAsync(ct), cancellationToken);
        var requisitions = await Queue(services.GetService<IRequisitionService>(), (s, ct) => s.ListApprovalQueueAsync(ct), cancellationToken);
        var bills = await Queue(services.GetService<IApBillService>(), (s, ct) => s.ListApprovalQueueAsync(ct), cancellationToken);
        return new(timesheets, expenses, leave, requisitions, bills);
    }

    private static async Task<IReadOnlyList<T>> Queue<TService, T>(TService? service, Func<TService, CancellationToken, Task<IReadOnlyList<T>>> load, CancellationToken cancellationToken)
        where TService : class
    {
        if (service is null)
        {
            return [];
        }

        try
        {
            return await load(service, cancellationToken);
        }
        catch (Exception ex) when (MobileFormat.IsExpected(ex))
        {
            return [];
        }
    }
}
