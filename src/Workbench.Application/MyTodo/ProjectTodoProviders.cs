using System.Globalization;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Evm;
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

        if (context.User.IsFinance)
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

/// <summary>EVM variance analysis reports (VARs) a PM still has to write for the reporting period.</summary>
public sealed class VarianceTodoProvider : IMyTodoProvider
{
    /// <summary>Write-ups are due this many days after the reporting period ends.</summary>
    public const int DueDaysAfterPeriod = 10;

    public string Name => "Variance analysis";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.Service<IEvmService>() is not { } evm)
        {
            return [];
        }

        var items = new List<MyTodoItem>();
        foreach (var project in await context.ManagedProjectsAsync(cancellationToken))
        {
            if (await evm.GetBaselineAsync(project.Id, cancellationToken) is null)
            {
                continue;
            }

            var open = (await evm.ListVariancesAsync(project.Id, cancellationToken: cancellationToken))
                .Where(v => v.NeedsVar && !v.IsComplete && !v.IsFrozen)
                .ToList();
            if (open.Count == 0)
            {
                continue;
            }

            var periods = await evm.ListPeriodsAsync(project.Id, cancellationToken);
            var period = periods.FirstOrDefault(p => p.FiscalPeriodId == open[0].FiscalPeriodId);
            DateOnly? due = period is null ? null : period.End.AddDays(DueDaysAfterPeriod);
            items.Add(new MyTodoItem(
                $"evm:{project.Id}:{open[0].FiscalPeriodId}", TodoCategories.Project,
                open.Count == 1 ? $"Explain the variance on {open[0].Code}" : $"Write up {open.Count} variances on {project.Code}",
                $"{open[0].PeriodName}: {string.Join(", ", open.Take(3).Select(v => v.Code))}{(open.Count > 3 ? "…" : "")} crossed a threshold and need a cause, impact and corrective action.",
                context.UrgencyFor(due, TodoUrgency.ThisWeek), due, $"projects/{project.Id}/evm", "Write variances", Priority: 6));
        }

        return items;
    }
}

/// <summary>Ceilings on a PM's projects that reached a warning threshold.</summary>
public sealed class CeilingTodoProvider : IMyTodoProvider
{
    public string Name => "Ceilings";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.Service<ICeilingService>() is not { } ceilings)
        {
            return [];
        }

        var items = new List<MyTodoItem>();
        foreach (var project in await context.ManagedProjectsAsync(cancellationToken))
        {
            var status = await ceilings.GetProjectStatusAsync(project.Id, cancellationToken);
            foreach (var ceiling in status.Ceilings.Where(c => c.Level >= CeilingAlertLevel.Warning && c.Ceiling.IsActive))
            {
                var measure = ceiling.Measures.OrderByDescending(m => m.PercentConsumed).First();
                var metric = measure.Metric.ToString().ToLowerInvariant();
                var percent = measure.PercentConsumed.ToString("0", CultureInfo.InvariantCulture);
                items.Add(new MyTodoItem(
                    $"ceiling:{ceiling.Ceiling.Id}:{ceiling.Level}", TodoCategories.Project,
                    ceiling.Level == CeilingAlertLevel.Over
                        ? $"{ceiling.Ceiling.Label} is over its {metric} ceiling"
                        : $"{ceiling.Ceiling.Label} is at {percent}% of its {metric} ceiling",
                    ceiling.Ceiling.Mode == CeilingMode.Block
                        ? "Charging stops at the limit. Ask for more room or move work to another charge code."
                        : "Plan the remaining work or ask Finance to raise the ceiling.",
                    ceiling.Level switch { CeilingAlertLevel.Over => TodoUrgency.Overdue, CeilingAlertLevel.Critical => TodoUrgency.Today, _ => TodoUrgency.ThisWeek },
                    null, $"projects/{project.Id}/financials", "View ceilings", Priority: (int)ceiling.Level));
            }
        }

        return items;
    }
}

/// <summary>CLINs on a PM's projects whose funding is running low (the Limitation of Funds thresholds).</summary>
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
        foreach (var project in await context.ManagedProjectsAsync(cancellationToken))
        {
            if (project.ContractId is null || await funding.GetProjectFundingStatusAsync(project.Id, cancellationToken) is not { } status)
            {
                continue;
            }

            foreach (var clin in status.Clins.Where(c => c.Level is FundingLevel.Warning or FundingLevel.Critical or FundingLevel.Exhausted))
            {
                var used = (clin.PercentIncurred ?? 0m).ToString("P0", CultureInfo.GetCultureInfo("en-US"));
                // A warning is this week's job (send the notice), sooner if the money runs out sooner.
                var byDate = context.UrgencyFor(clin.ProjectedExhaustion, TodoUrgency.ThisWeek);
                var urgency = clin.Level switch
                {
                    FundingLevel.Exhausted => TodoUrgency.Overdue,
                    FundingLevel.Critical => byDate == TodoUrgency.Overdue ? TodoUrgency.Overdue : TodoUrgency.Today,
                    _ => byDate == TodoUrgency.Later ? TodoUrgency.ThisWeek : byDate,
                };
                items.Add(new MyTodoItem(
                    $"funding:{clin.ClinId}:{clin.Level}", TodoCategories.Project,
                    clin.Level == FundingLevel.Exhausted
                        ? $"CLIN {clin.Number} on {status.ContractNumber} is out of funding"
                        : $"CLIN {clin.Number} on {status.ContractNumber} has used {used} of its funding",
                    $"{ReceivingTodoProvider.Money(clin.Remaining)} left"
                        + (clin.ProjectedExhaustion is { } runOut ? $", projected to run out {runOut:MMM d}" : "")
                        + ". Ask the contracting officer for incremental funding (Limitation of Funds notice).",
                    urgency, clin.ProjectedExhaustion, $"projects/{project.Id}/financials", "View funding", Priority: (int)clin.Level));
            }
        }

        return items;
    }
}
