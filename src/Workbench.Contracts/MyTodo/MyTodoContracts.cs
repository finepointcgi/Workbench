using Workbench.Contracts.Approvals;
using Workbench.Contracts.Time;

namespace Workbench.Contracts.MyTodo;

/// <summary>How soon a to-do needs doing. The list is sorted in this order.</summary>
public enum TodoUrgency
{
    Overdue,
    Today,
    ThisWeek,
    Later,
}

/// <summary>What a to-do is about; the UI picks an icon from it.</summary>
public static class TodoCategories
{
    public const string Timesheet = "timesheet";
    public const string Expense = "expense";
    public const string Leave = "leave";
    public const string Approval = "approval";
    public const string Receiving = "receiving";
    public const string Sheet = "sheet";
    public const string Project = "project";
    public const string Finance = "finance";
}

public enum TodoInlineActionKind
{
    /// <summary><see cref="IApprovalInboxService.DecideAsync"/> with Approve, for <see cref="TodoInlineAction.DocumentType"/>.</summary>
    ApproveDocument,

    /// <summary>Approves the leave request <see cref="TodoInlineAction.TargetId"/>.</summary>
    ApproveLeave,
}

/// <summary>A one-click action the page can run without leaving it.</summary>
public sealed record TodoInlineAction(TodoInlineActionKind Kind, string Label, Guid TargetId, string? DocumentType = null);

/// <summary>
/// One thing the signed-in person should do. <paramref name="Key"/> is stable across loads (the page uses it to
/// remove an item once it's done). <paramref name="Link"/> is app-relative and opens where the work is done.
/// </summary>
public sealed record MyTodoItem(
    string Key,
    string Category,
    string Title,
    string Reason,
    TodoUrgency Urgency,
    DateOnly? Due,
    string Link,
    string LinkLabel,
    TodoInlineAction? Action = null,
    int Priority = 0);

public sealed record LeaveBalanceChipDto(string PolicyName, decimal Available);

/// <summary>
/// The "This week" strip: the current timesheet period's hours (null when the user has no employee record), leave
/// balances and this week's allocated hours.
/// </summary>
public sealed record MyWeekDto(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal HoursEntered,
    decimal StandardHours,
    TimesheetState? Status,
    IReadOnlyList<LeaveBalanceChipDto> Leave,
    decimal? AllocatedHours,
    int AllocatedProjects);

/// <summary>The approvals waiting on the user: the count and the oldest few.</summary>
public sealed record ApprovalsPreviewDto(int Count, IReadOnlyList<ApprovalInboxItemDto> Top)
{
    /// <summary>Document types simple enough to approve from the home page without opening them.</summary>
    public static readonly IReadOnlySet<string> QuickApprove = new HashSet<string> { ApprovalDocumentTypes.Timesheet };

    public static bool CanQuickApprove(ApprovalInboxItemDto item) => QuickApprove.Contains(item.DocumentType);
}

/// <summary>
/// The home page. <paramref name="Items"/> is sorted by urgency, then due date, then priority (higher first).
/// <paramref name="Unavailable"/> names the sources that failed this time (the rest still load).
/// </summary>
public sealed record MyTodoDto(
    string DisplayName,
    DateOnly Today,
    IReadOnlyList<MyTodoItem> Items,
    MyWeekDto? Week,
    ApprovalsPreviewDto? Approvals,
    IReadOnlyList<string> Unavailable)
{
    public string FirstName => DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? DisplayName;
}

/// <summary>What the signed-in person should do next: only their own items and the ones waiting on them.</summary>
public interface IMyTodoService
{
    Task<MyTodoDto> GetAsync(CancellationToken cancellationToken = default);
}
