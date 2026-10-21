using Microsoft.EntityFrameworkCore;
using Workbench.Application.Time;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Leave;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.MyWork;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Resourcing;
using Workbench.Contracts.Time;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.MyTodo;

/// <summary>Open purchase orders that name the user as a receiver (Finance can receive anything, but only named receivers are asked).</summary>
public sealed class ReceivingTodoProvider : IMyTodoProvider
{
    public string Name => "Receiving";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.Service<IPurchaseOrderService>() is not { } orders)
        {
            return [];
        }

        var items = new List<MyTodoItem>();
        foreach (var summary in await orders.ListReceivableAsync(cancellationToken))
        {
            if (summary.Status is not (PurchaseOrderState.Issued or PurchaseOrderState.PartiallyReceived))
            {
                continue;
            }

            var order = await orders.GetAsync(summary.Id, cancellationToken);
            if (!order.Receivers.Contains(context.User.Email, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var partly = summary.Status == PurchaseOrderState.PartiallyReceived;
            // PO dollars are Finance's; a receiver only needs to know what's still to come (as on the Receiving page).
            var finance = context.User.Can(Permission.PurchaseOrdersManage);
            items.Add(new MyTodoItem(
                $"receiving:{summary.Id}", TodoCategories.Receiving,
                $"Receive {summary.Number} from {summary.VendorName}",
                (partly, finance) switch
                {
                    (true, true) => $"Partly received ({Money(summary.Received)} of {Money(summary.Total)}). Record the rest when it arrives.",
                    (true, false) => "Partly received. Record the rest when it arrives.",
                    (false, true) => $"Ordered {summary.OrderDate:MMM d} · {Money(summary.Total)}. You're a named receiver: record it when it arrives.",
                    (false, false) => $"Ordered {summary.OrderDate:MMM d}. You're a named receiver: record it when it arrives.",
                },
                TodoUrgency.ThisWeek, null, $"purchasing/orders/{summary.Id}", "Record receipt", Priority: 2));
        }

        return items;
    }

    internal static string Money(decimal amount) => amount.ToString("C0", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}

/// <summary>Sheet items assigned to the user (My Work) that are overdue or due within a week.</summary>
public sealed class SheetTodoProvider : IMyTodoProvider
{
    public const int DueSoonDays = 7;

    public string Name => "Sheet items";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.Service<IMyWorkService>() is not { } myWork)
        {
            return [];
        }

        var work = await myWork.GetAsync(cancellationToken);
        var soon = context.Today.AddDays(DueSoonDays);

        // Tasks assigned through the Assignees field are listed by the Tasks provider ("Assigned to you: ...").
        var assigned = context.Service<Contracts.Tasks.ITaskAssignmentService>() is { } tasks
            ? (await tasks.ListMineAsync(includeDone: true, cancellationToken)).Select(t => t.ItemId).ToHashSet()
            : [];
        return work.Items
            .Where(i => i.Bucket != MyWorkBucket.Done && i.Due is { } due && due <= soon && !assigned.Contains(i.Item.Id))
            .Select(i => new MyTodoItem(
                $"sheet:{i.SheetId}:{i.Item.Id}", TodoCategories.Sheet,
                i.Item.Title,
                $"{i.SheetName} · " + (i.Due < context.Today ? $"was due {i.Due:ddd MMM d}" : $"due {i.Due:ddd MMM d}") + (i.Status is { } s ? $" · {s}" : ""),
                context.UrgencyFor(i.Due), i.Due, $"sheets/{i.SheetId}?item={i.Item.Id}", "Open item"))
            .ToList();
    }
}

/// <summary>The "This week" strip: the current timesheet period's hours, leave balances and this week's allocations.</summary>
internal static class MyWeek
{
    public static async Task<MyWeekDto?> BuildAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.User.EmployeeId is not { } me || context.Service<TimekeepingClock>() is not { } clock)
        {
            return null;
        }

        await using var db = await context.DbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == me, cancellationToken);
        if (employee is null)
        {
            return null;
        }

        var today = context.Today;
        var sheet = await db.Set<Timesheet>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.EmployeeId == me && t.PeriodStart <= today && t.PeriodEnd >= today, cancellationToken);
        var (start, end) = sheet is null ? await clock.PeriodForAsync(db, today, cancellationToken) : (sheet.PeriodStart, sheet.PeriodEnd);
        var hours = sheet is null ? 0m : await db.Set<TimeEntry>().Where(e => e.TimesheetId == sheet.Id).SumAsync(e => e.Hours, cancellationToken);

        IReadOnlyList<LeaveBalanceChipDto> leave = [];
        if (context.Service<ILeaveBalanceService>() is { } balances)
        {
            leave = (await balances.GetMyBalancesAsync(cancellationToken))
                .Where(b => b.Type != LeaveTypeKind.Holiday)
                .Select(b => new LeaveBalanceChipDto(b.PolicyName, b.Available))
                .ToList();
        }

        decimal? allocated = null;
        var projects = 0;
        if (context.Service<IResourceAllocationService>() is { } allocations)
        {
            var monday = MyWork.MyWorkCalculator.WeekStart(today);
            var rows = await allocations.ListMineAsync(monday, monday.AddDays(6), cancellationToken);
            if (rows.Count > 0)
            {
                allocated = rows.Sum(r => r.Hours);
                projects = rows.Select(r => r.ProjectId).Distinct().Count();
            }
        }

        return new MyWeekDto(start, end, hours, TimekeepingClock.StandardHours(employee, start, end),
            sheet is null ? null : (TimesheetState)(int)sheet.Status, leave, allocated, projects);
    }
}
