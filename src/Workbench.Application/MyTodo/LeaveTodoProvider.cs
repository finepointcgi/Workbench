using Workbench.Contracts.Leave;
using Workbench.Contracts.MyTodo;

namespace Workbench.Application.MyTodo;

/// <summary>Leave requests waiting on the user as approver (approve before the leave starts), and their own pending requests.</summary>
public sealed class LeaveTodoProvider : IMyTodoProvider
{
    public string Name => "Leave";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.Service<ILeaveRequestService>() is not { } leave)
        {
            return [];
        }

        var items = new List<MyTodoItem>();
        if (await leave.IsApproverAsync(cancellationToken))
        {
            foreach (var request in await leave.ListPendingApprovalsAsync(cancellationToken))
            {
                // The service lists only requests this user may decide; never their own (DCAA: no self-approval).
                if (request.EmployeeId == context.User.EmployeeId)
                {
                    continue;
                }

                var due = request.StartDate > context.Today ? request.StartDate.AddDays(-1) : context.Today;
                items.Add(new MyTodoItem(
                    $"leave:approve:{request.Id}", TodoCategories.Leave,
                    $"Approve {request.EmployeeName}'s leave",
                    $"{request.PolicyName} · {MyTodoRules.Range(request.StartDate, request.EndDate)} · {MyTodoRules.Hours(request.Hours)} h"
                        + (string.IsNullOrWhiteSpace(request.Comment) ? "" : $" · \"{request.Comment.Trim()}\""),
                    context.UrgencyFor(due), due, "leave", "Review",
                    new TodoInlineAction(TodoInlineActionKind.ApproveLeave, "Approve", request.Id), Priority: 8));
            }
        }

        if (context.User.EmployeeId is not null)
        {
            foreach (var mine in (await leave.ListMineAsync(cancellationToken)).Where(r => r.Status == LeaveRequestState.Pending && r.EndDate >= context.Today))
            {
                items.Add(new MyTodoItem(
                    $"leave:pending:{mine.Id}", TodoCategories.Leave,
                    $"Your leave request for {MyTodoRules.Range(mine.StartDate, mine.EndDate)} is waiting for approval",
                    $"{mine.PolicyName} · {MyTodoRules.Hours(mine.Hours)} h. Nothing to do unless your plans change.",
                    TodoUrgency.Later, null, "leave", "View request", Priority: -10));
            }
        }

        return items;
    }
}
