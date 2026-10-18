using Workbench.Contracts.Approvals;
using Workbench.Contracts.MyTodo;

namespace Workbench.Application.MyTodo;

/// <summary>
/// One to-do for everything in the approval inbox (timesheets, expenses, ODCs, requisitions, bills, invoices, journal
/// entries): what's waiting and how long the oldest has waited. The page previews the items themselves.
/// </summary>
public sealed class ApprovalTodoProvider : IMyTodoProvider
{
    /// <summary>An approval waiting this many business days is overdue.</summary>
    public const int OverdueAfterBusinessDays = 2;

    public string Name => "Approvals";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        var inbox = await context.InboxAsync(cancellationToken);
        return inbox.Count == 0 ? [] : [For(inbox, context)];
    }

    public static MyTodoItem For(IReadOnlyList<ApprovalInboxItemDto> inbox, MyTodoContext context)
    {
        var oldest = inbox.Max(i => i.BusinessDaysWaiting);
        var kinds = inbox.GroupBy(i => i.DocumentLabel)
            .OrderByDescending(g => g.Count())
            .Select(g => MyTodoRules.Plural(g.Count(), g.Key.ToLowerInvariant()));
        var waited = oldest switch
        {
            0 => "submitted today",
            1 => "the oldest has waited 1 business day",
            _ => $"the oldest has waited {oldest} business days",
        };
        return new MyTodoItem(
            "approvals:inbox", TodoCategories.Approval,
            inbox.Count == 1 ? $"Review {inbox[0].Title}" : $"Review {inbox.Count} approvals waiting on you",
            $"{string.Join(", ", kinds)}; {waited}.",
            oldest >= OverdueAfterBusinessDays ? TodoUrgency.Overdue : TodoUrgency.Today, null,
            inbox.Count == 1 ? inbox[0].Link : "approvals", inbox.Count == 1 ? "Review" : "Open approvals", Priority: 12);
    }

    /// <summary>Longest waiting first.</summary>
    public static IEnumerable<ApprovalInboxItemDto> Oldest(IEnumerable<ApprovalInboxItemDto> inbox) =>
        inbox.OrderByDescending(i => i.BusinessDaysWaiting).ThenBy(i => i.WaitingSinceUtc ?? DateTime.MaxValue).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase);
}
