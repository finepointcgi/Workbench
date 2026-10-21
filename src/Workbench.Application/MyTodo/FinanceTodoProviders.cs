using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Identity;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.MyTodo;

// Finance's to-dos (Finance and Admin only): what's left to close the books and run payroll.

/// <summary>
/// The close checklist of every fiscal period that has ended but is still open: each failing check (bank
/// reconciliation, the revenue run, timesheets, postings, ...) is a to-do, due <see cref="CloseDueDays"/> days after period end.
/// </summary>
public sealed class PeriodCloseTodoProvider : IMyTodoProvider
{
    public const int CloseDueDays = 10;

    /// <summary>At most this many ended-but-open periods are checked (oldest first).</summary>
    public const int MaxPeriods = 2;

    public string Name => "Period close";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (!context.User.Can(Permission.GlClose) || context.Service<IPeriodCloseService>() is not { } close)
        {
            return [];
        }

        List<Guid> periods;
        await using (var db = await context.DbFactory.CreateAsync(cancellationToken))
        {
            var today = context.Today;
            periods = await db.Set<FiscalPeriod>().AsNoTracking()
                .Where(p => p.Status == PeriodStatus.Open && p.End < today)
                .OrderBy(p => p.Start)
                .Select(p => p.Id)
                .Take(MaxPeriods)
                .ToListAsync(cancellationToken);
        }

        var items = new List<MyTodoItem>();
        foreach (var id in periods)
        {
            var checklist = await close.GetChecklistAsync(id, cancellationToken);
            items.AddRange(For(checklist, context));
        }

        return items;
    }

    public static IEnumerable<MyTodoItem> For(PeriodCloseChecklistDto checklist, MyTodoContext context)
    {
        if (checklist.IsClosed)
        {
            yield break;
        }

        var due = checklist.End.AddDays(CloseDueDays);
        var period = checklist.FiscalPeriodName;
        var failing = checklist.Checks.Where(c => !c.Passed).ToList();
        foreach (var check in failing)
        {
            var (title, link, label) = check.Kind switch
            {
                CloseCheckKind.BankReconciled => ($"Reconcile the bank accounts for {period}", "cash/reconciliations", "Reconcile"),
                CloseCheckKind.RevenueRecognized => ($"Post the revenue run for {period}", "accounting/revenue", "Post revenue"),
                CloseCheckKind.TimesheetsApproved => ($"Get {period} timesheets submitted and approved", "time/team", "See timesheets"),
                CloseCheckKind.OdcsDecided => ($"Decide the open ODCs for {period}", "odcs/approvals", "Review ODCs"),
                CloseCheckKind.NothingUnposted => ($"Post what's left for {period}", "accounting/close", "Open close"),
                CloseCheckKind.TrialBalanceBalanced => ($"Fix the trial balance for {period}", "accounting/reports", "Trial balance"),
                CloseCheckKind.AllocationPosted => ($"Post the indirect cost allocation for {period}", "accounting/indirect-rates", "Allocate"),
                CloseCheckKind.UnbilledAccrual => ($"Post the unbilled accrual for {period}", "billing/accrual", "Accrue"),
                CloseCheckKind.DepreciationPosted => ($"Run depreciation for {period}", "assets/depreciation", "Run depreciation"),
                _ => ($"{check.Title} ({period})", "accounting/close", "Open close"),
            };
            yield return new MyTodoItem(
                $"close:{checklist.FiscalPeriodId}:{check.Kind}", TodoCategories.Finance,
                title, $"{check.Summary.TrimEnd('.')}. {MyTodoRules.Plural(failing.Count, "close step")} still open for {period}.",
                context.UrgencyFor(due), due, link, label, Priority: 3);
        }

        if (failing.Count == 0)
        {
            yield return new MyTodoItem(
                $"close:{checklist.FiscalPeriodId}:ready", TodoCategories.Finance,
                $"Close {period}", "Every close check passes. Close the period so nothing more posts to it.",
                context.UrgencyFor(due), due, "accounting/close", "Close period", Priority: 3);
        }
    }
}

/// <summary>Payroll runs waiting on Finance: hours to export, the register to import, the journal to post.</summary>
public sealed class PayrollTodoProvider : IMyTodoProvider
{
    public string Name => "Payroll";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (!context.User.Can(Permission.PayrollRun) || context.Service<IPayrollRunService>() is not { } runs)
        {
            return [];
        }

        return (await runs.ListAsync(cancellationToken: cancellationToken)).Select(r => For(r, context)).OfType<MyTodoItem>().ToList();
    }

    public static MyTodoItem? For(PayrollRunDto run, MyTodoContext context)
    {
        var range = MyTodoRules.Range(run.PeriodStart, run.PeriodEnd);
        var (title, reason, due) = run.Status switch
        {
            PayrollRunState.Draft => ($"Export hours for payroll ({range})", $"{run.PayGroupCode}: check the hours and send them to {run.ProviderKey}. Pay date {run.CheckDate:MMM d}.", run.CheckDate.AddDays(-2)),
            PayrollRunState.Exported => ($"Import the payroll register ({range})", $"{run.PayGroupCode}: hours went to {run.ProviderKey}; import the register once payroll is processed.", run.CheckDate),
            PayrollRunState.RegisterImported => ($"Post payroll for {range}", $"{run.PayGroupCode}: the register is imported ({ReceivingTodoProvider.Money(run.RegisterGross ?? 0)} gross). Post it to the general ledger.", run.CheckDate),
            _ => ((string?)null, (string?)null, default(DateOnly)),
        };
        return title is null
            ? null
            : new MyTodoItem($"payroll:{run.Id}:{run.Status}", TodoCategories.Finance, title, reason!, context.UrgencyFor(due), due, $"payroll/runs/{run.Id}", "Open payroll run", Priority: 4);
    }
}
