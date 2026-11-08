using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Identity;
using Workbench.Contracts.MyTodo;
using Workbench.Domain.ProjectAccounting;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.MyTodo;

/// <summary>
/// Finance's to-dos from a win: a won opportunity with no contract yet (create the contract and project), and a new
/// project still waiting on its funding or labor categories (so the project manager can plan it). Shown to whoever may
/// set up contracts, and gone as soon as the step is done.
/// </summary>
public sealed class WonHandoffTodoProvider : IMyTodoProvider
{
    /// <summary>At most this many of each kind are listed.</summary>
    public const int MaxItems = 20;

    public string Name => "Won contracts";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (!context.User.Can(Permission.SetupContracts))
        {
            return [];
        }

        var items = new List<MyTodoItem>();
        await using var db = await context.DbFactory.CreateAsync(cancellationToken);

        var won = await db.Set<D.Opportunity>().AsNoTracking()
            .Where(o => o.Stage == D.OpportunityStage.Won && o.ContractId == null)
            .OrderByDescending(o => o.DecidedOn).Take(MaxItems)
            .Select(o => new { o.Id, o.Number, o.Name, o.DecidedOn }).ToListAsync(cancellationToken);
        items.AddRange(won.Select(o => new MyTodoItem($"won:{o.Id}", TodoCategories.Finance, $"{o.Number} {o.Name} was won: create its contract and project",
            "Business development is waiting on you to set it up and add the funding.", TodoUrgency.ThisWeek, null,
            $"bd/opportunities/{o.Id}?tab=outcome", "Create the contract", Priority: 3)));

        var planning = await db.Set<Project>().AsNoTracking()
            .Where(p => p.Status == ProjectStatus.Planning && p.ContractId != null)
            .OrderBy(p => p.Code).Take(MaxItems).ToListAsync(cancellationToken);
        foreach (var project in planning)
        {
            if (await ProjectPlanService.MissingSetupAsync(db, project, cancellationToken) is { Count: > 0 } missing)
            {
                var funding = missing.Contains("funding");
                items.Add(new MyTodoItem($"setup-project:{project.Id}", TodoCategories.Finance, $"{project.Code} {project.Name} needs {string.Join(" and ", missing)}",
                    "The project manager can't make the plan live until it's added.", TodoUrgency.ThisWeek, null,
                    funding ? $"setup/contracts/{project.ContractId}" : $"projects/{project.Id}/plan", funding ? "Add funding" : "Pick labor categories", Priority: 3));
            }
        }

        return items;
    }
}
