using System.Globalization;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.MyTodo;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.MyTodo;

// Project managers' to-dos. Each provider walks the open projects the user manages (MyTodoContext.ManagedProjectsAsync)
// and reads them through the module's own permission-checked service, so a PM sees only their projects.

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
