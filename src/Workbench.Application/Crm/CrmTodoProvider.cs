using Microsoft.EntityFrameworkCore;
using Workbench.Application.MyTodo;
using Workbench.Contracts.Crm;
using Workbench.Contracts.MyTodo;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.Crm;

/// <summary>
/// Business development to-dos: proposal (and RFI) due dates on the opportunities the user captures or manages the proposal
/// for, gate decisions waiting on them, and CRM tasks assigned to them.
/// </summary>
public sealed class CrmTodoProvider : IMyTodoProvider
{
    /// <summary>How far ahead a due date is listed.</summary>
    public const int LookAheadDays = 21;

    public string Name => "Business development";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        var me = Identity.AccessControl.Normalize(context.User.Email);
        if (me.Length == 0)
        {
            return [];
        }

        var items = new List<MyTodoItem>();
        var horizon = context.Today.AddDays(LookAheadDays);
        await using (var db = await context.DbFactory.CreateAsync(cancellationToken))
        {
            var mine = await db.Set<D.Opportunity>().AsNoTracking()
                .Where(o => (o.CaptureManagerEmail == me || o.ProposalManagerEmail == me) && CrmMapping.OpenStages.Contains(o.Stage) && o.Stage != D.OpportunityStage.Submitted)
                .ToListAsync(cancellationToken);
            foreach (var o in mine)
            {
                if (o.ProposalDueOn is { } due && due <= horizon)
                {
                    items.Add(new MyTodoItem($"bd:proposal:{o.Id}", TodoCategories.Project, $"Proposal due: {o.Number} {o.Name}",
                        (due < context.Today ? $"Was due {due:ddd MMM d}" : $"Due {due:ddd MMM d}") + $" · {OpportunityStages.Label(o.Stage.ToContract())}",
                        context.UrgencyFor(due), due, $"bd/opportunities/{o.Id}", "Open opportunity", Priority: 2));
                }

                if (o.RfiDueOn is { } rfi && rfi >= context.Today.AddDays(-7) && rfi <= horizon && o.Stage <= D.OpportunityStage.Capture)
                {
                    items.Add(new MyTodoItem($"bd:rfi:{o.Id}", TodoCategories.Project, $"RFI response due: {o.Number} {o.Name}", $"Due {rfi:ddd MMM d}",
                        context.UrgencyFor(rfi), rfi, $"bd/opportunities/{o.Id}", "Open opportunity", Priority: 1));
                }
            }

            var tasks = await db.Set<D.CrmActivity>().AsNoTracking()
                .Where(a => a.Kind == D.CrmActivityKind.Task && !a.IsDone && a.AssignedTo == me && a.DueOn != null && a.DueOn <= horizon)
                .ToListAsync(cancellationToken);
            items.AddRange(tasks.Select(t => new MyTodoItem($"bd:task:{t.Id}", TodoCategories.Project, t.Subject,
                t.DueOn < context.Today ? $"Business development task · was due {t.DueOn:ddd MMM d}" : $"Business development task · due {t.DueOn:ddd MMM d}",
                context.UrgencyFor(t.DueOn), t.DueOn, CrmAccountService.ActivityLink(t), "Open")));
        }

        if (context.Service<IOpportunityGateService>() is { } gates)
        {
            items.AddRange((await gates.ListWaitingOnMeAsync(cancellationToken)).Select(r => new MyTodoItem($"bd:gate:{r.Id}", TodoCategories.Approval,
                $"Gate decision: {r.OpportunityName}",
                $"{r.GateName}: {(r.Recommendation == GateRecommendation.Go ? "Go" : "No-go")}, {r.ScorePercent:0.#}% · submitted {DateOnly.FromDateTime(r.SubmittedAtUtc):MMM d}",
                DateOnly.FromDateTime(r.SubmittedAtUtc).AddDays(2) < context.Today ? TodoUrgency.Overdue : TodoUrgency.Today, null,
                $"bd/opportunities/{r.OpportunityId}?tab=gates", "Decide", Priority: 3)));
        }

        return items;
    }
}
