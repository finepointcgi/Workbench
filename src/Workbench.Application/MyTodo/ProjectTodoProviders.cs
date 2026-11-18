using System.Globalization;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.Revenue;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.MyTodo;

// Project managers' to-dos. Each provider walks the open projects the user manages (MyTodoContext.ManagedProjectsAsync)
// and reads them through the module's own permission-checked service, so a PM sees only their projects.

/// <summary>
/// Estimates at completion: a PM's project on cost-to-cost revenue (or with EACs on record) needs a fresh EAC every
/// <see cref="ReviewEveryDays"/> days, and a rejected one needs resubmitting. Finance is asked to decide submitted EACs.
/// </summary>
public sealed class EacReviewTodoProvider : IMyTodoProvider
{
    public const int ReviewEveryDays = 90;

    /// <summary>A review is listed from this many days before it's due.</summary>
    public const int LeadDays = 14;

    public string Name => "Estimates at completion";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.Service<IRevenueSetupService>() is not { } revenue)
        {
            return [];
        }

        var items = new List<MyTodoItem>();
        foreach (var project in await context.ManagedProjectsAsync(cancellationToken))
        {
            if (project.ContractId is null)
            {
                continue;
            }

            var detail = await revenue.GetProjectAsync(project.Id, cancellationToken);
            if (For(project, detail, context) is { } item)
            {
                items.Add(item);
            }
        }

        if (context.User.Can(Permission.RevenueManage))
        {
            var pending = await revenue.ListEstimatesAsync(EstimateState.Submitted, cancellationToken);
            var others = pending.Where(e => !string.Equals(e.SubmittedBy, context.User.Email, StringComparison.OrdinalIgnoreCase)).ToList();
            if (others.Count > 0)
            {
                items.Add(new MyTodoItem(
                    "eac:decide", TodoCategories.Finance,
                    others.Count == 1 ? $"Review the EAC for {others[0].ProjectCode}" : $"Review {others.Count} estimates at completion",
                    $"Submitted by project managers and waiting for Finance: {string.Join(", ", others.Select(e => e.ProjectCode).Distinct().Take(5))}.",
                    TodoUrgency.ThisWeek, null, "accounting/revenue/estimates", "Review EACs", Priority: 4));
            }
        }

        return items;
    }

    public static MyTodoItem? For(Project project, ProjectRevenueDto detail, MyTodoContext context)
    {
        var needsEac = detail.Setups.Any(s => s.Method == RevenueMethodKind.CostToCost) || detail.Estimates.Count > 0;
        if (!needsEac)
        {
            return null;
        }

        var link = $"projects/{project.Id}/revenue";
        var latest = detail.Estimates.OrderByDescending(e => e.SubmittedAtUtc).FirstOrDefault();
        if (latest is null)
        {
            return new MyTodoItem(
                $"eac:{project.Id}:first", TodoCategories.Project,
                $"Submit an estimate at completion for {project.Code}",
                $"{project.Name} recognizes revenue on cost-to-cost and has no EAC yet.",
                TodoUrgency.ThisWeek, null, link, "Submit EAC", Priority: 6);
        }

        if (latest.Status == EstimateState.Submitted)
        {
            return null; // waiting on Finance, nothing for the PM to do
        }

        if (latest.Status == EstimateState.Rejected)
        {
            return new MyTodoItem(
                $"eac:{project.Id}:rejected:{latest.Id}", TodoCategories.Project,
                $"Revise the EAC for {project.Code}",
                $"Finance sent back the estimate as of {latest.AsOf:MMM d}" + (string.IsNullOrWhiteSpace(latest.DecisionNote) ? "." : $": \"{latest.DecisionNote.Trim()}\""),
                TodoUrgency.Today, context.Today, link, "Revise EAC", Priority: 8);
        }

        var due = latest.AsOf.AddDays(ReviewEveryDays);
        if (due > context.Today.AddDays(LeadDays))
        {
            return null;
        }

        return new MyTodoItem(
            $"eac:{project.Id}:review:{due:yyyy-MM-dd}", TodoCategories.Project,
            $"Review the EAC for {project.Code}",
            $"The last estimate at completion is from {latest.AsOf:MMM d, yyyy}. Review it every {ReviewEveryDays} days so revenue stays right.",
            context.UrgencyFor(due), due, link, "Review EAC", Priority: 6);
    }
}

/// <summary>
/// A project manager's funding warning: one to-do per contract whose funding is running low, using the same CLIN levels
/// and projection Finance's Limitation of Funds notices use.
/// </summary>
public sealed class FundingTodoProvider : IMyTodoProvider
{
    public string Name => "Funding";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.Service<IFundingService>() is not { } funding)
        {
            return [];
        }

        var items = new List<MyTodoItem>();
        var seen = new HashSet<Guid>(); // two projects on one contract share one warning
        foreach (var project in await context.ManagedProjectsAsync(cancellationToken))
        {
            if (project.ContractId is not { } contractId || await funding.GetProjectFundingStatusAsync(project.Id, cancellationToken) is not { } status
                || FundingRunway.From(status) is not { IsLow: true } run || !seen.Add(contractId))
            {
                continue;
            }

            var money = ReceivingTodoProvider.Money(run.Remaining);
            var clins = run.LowClins.Count == 1 ? $"CLIN {run.LowClins[0]}" : $"CLINs {string.Join(", ", run.LowClins)}";
            var byDate = context.UrgencyFor(run.RunsOutOn, TodoUrgency.ThisWeek);
            var urgency = run.Level switch
            {
                FundingLevel.Exhausted => TodoUrgency.Overdue,
                FundingLevel.Critical => byDate == TodoUrgency.Overdue ? TodoUrgency.Overdue : TodoUrgency.Today,
                _ => byDate == TodoUrgency.Later ? TodoUrgency.ThisWeek : byDate,
            };
            items.Add(new MyTodoItem(
                $"funding:{contractId}:{run.Level}", TodoCategories.Project,
                run.Level == FundingLevel.Exhausted
                    ? $"{project.Code} has run out of funding on {run.ContractNumber}"
                    : $"{project.Code} is running low on funding" + (run.WeeksText is { } weeks ? $": about {weeks} left" : ""),
                $"{clins} on {run.ContractNumber} {(run.Level == FundingLevel.Exhausted ? "has" : "have")} {money} of funding left"
                    + (run.RunsOutOn is { } runOut && run.Level != FundingLevel.Exhausted ? $" and will run out around {runOut:MMM d} at the current pace" : "")
                    + ". Slow spending or ask Finance to request incremental funding.",
                urgency, null, $"projects/{project.Id}", "See funding", Priority: (int)run.Level)); // the run-out date is in the reason: showing it as "Due" read like a deadline
        }

        return items;
    }
}
