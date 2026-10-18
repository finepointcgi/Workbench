using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.Odcs;
using Workbench.Domain.PerDiem;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.MyTodo;

/// <summary>The user's draft and rejected expense reports, and receipts their lines still need.</summary>
public sealed class ExpenseTodoProvider : IMyTodoProvider
{
    public string Name => "Expense reports";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.User.EmployeeId is null || context.Service<IExpenseReportService>() is not { } reports)
        {
            return [];
        }

        var open = (await reports.ListMyReportsAsync(cancellationToken)).Where(r => r.IsEditable).ToList();
        if (open.Count == 0)
        {
            return [];
        }

        var threshold = context.Service<IOdcService>() is { } odcs ? (await odcs.GetPolicyAsync(cancellationToken)).ReceiptThreshold : 75m;
        var missing = await MissingReceiptsAsync(context, open.Select(r => r.Id).ToList(), threshold, cancellationToken);
        return open.Select(r => For(r, missing.GetValueOrDefault(r.Id), context)).ToList();
    }

    public static MyTodoItem For(ExpenseReportDto report, int missingReceipts, MyTodoContext context)
    {
        var trip = report.TripStart is { } s ? $"{MyTodoRules.Range(s, report.TripEnd ?? s)} · " : "";
        var amount = report.Total.ToString("C", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var receipts = missingReceipts > 0 ? $" {MyTodoRules.Plural(missingReceipts, "receipt")} missing." : "";
        var link = $"expenses/{report.Id}";
        if (report.Status == OdcApprovalState.Rejected)
        {
            return new MyTodoItem(
                $"expense:rejected:{report.Id}", TodoCategories.Expense,
                $"Fix and resubmit \"{report.Title}\"",
                $"Sent back{(string.IsNullOrWhiteSpace(report.RejectionReason) ? "." : $": \"{report.RejectionReason.Trim()}\"")}{receipts}",
                TodoUrgency.Today, context.Today, link, "Fix report", Priority: 15);
        }

        // A finished trip waiting to be submitted is this week's job; one still being planned can wait.
        var tripOver = report.TripEnd is { } end && end < context.Today;
        var reason = report.LineCount == 0
            ? $"{trip}Draft with no expenses yet."
            : $"{trip}{amount} in a draft that hasn't been submitted.{receipts}";
        return new MyTodoItem(
            $"expense:draft:{report.Id}", TodoCategories.Expense,
            missingReceipts > 0 ? $"Add receipts and submit \"{report.Title}\"" : $"Submit your expense report \"{report.Title}\"",
            tripOver ? reason + " Submit it so you're reimbursed." : reason,
            tripOver ? TodoUrgency.ThisWeek : TodoUrgency.Later, null, link, missingReceipts > 0 ? "Add receipts" : "Open report", Priority: 5);
    }

    /// <summary>Lines at or over the receipt threshold with no receipt (per diem lines other than lodging need none), per report.</summary>
    private static async Task<Dictionary<Guid, int>> MissingReceiptsAsync(MyTodoContext context, List<Guid> reportIds, decimal threshold, CancellationToken cancellationToken)
    {
        await using var db = await context.DbFactory.CreateAsync(cancellationToken);
        var me = context.User.EmployeeId;
        var exempt = db.Set<PerDiemLine>().Where(l => l.Kind != PerDiemLineKind.Lodging).Select(l => l.OdcEntryId);
        var lines = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => o.ExpenseReportId != null && reportIds.Contains(o.ExpenseReportId.Value) && o.EmployeeId == me
                && o.ReceiptAttachmentId == null && o.Amount >= threshold && !exempt.Contains(o.Id))
            .Select(o => o.ExpenseReportId!.Value)
            .ToListAsync(cancellationToken);
        return lines.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
    }
}
