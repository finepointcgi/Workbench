using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Crm;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample capture plans: the biggest open pursuits in Capture and Proposal get win themes, discriminators, hot
/// buttons, a SWOT, risks, a Pwin assessment and (with a due date) the color-team schedule. Runs once, after the CRM seed.
/// </summary>
internal static class CapturePlanSeed
{
    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<OpportunityPlanItem>().AnyAsync(cancellationToken))
        {
            return;
        }

        var pursuits = (await db.Set<Opportunity>().Where(o => o.Stage == OpportunityStage.Capture || o.Stage == OpportunityStage.Proposal).ToListAsync(cancellationToken))
            .OrderByDescending(o => o.EstimatedValue).Take(2).ToList();
        if (pursuits.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        foreach (var o in pursuits)
        {
            var by = o.CaptureManagerEmail ?? CrmSeed.BdEmail;
            var order = 0;
            void Item(CapturePlanItemKind kind, string text) =>
                db.Add(new OpportunityPlanItem { OpportunityId = o.Id, Kind = kind, Text = text, Order = order++, CreatedBy = by, CreatedAtUtc = now });

            Item(CapturePlanItemKind.HotButton, "No disruption to mission users during the transition");
            Item(CapturePlanItemKind.HotButton, "Cybersecurity posture: ATO renewals are behind schedule");
            Item(CapturePlanItemKind.HotButton, "Predictable cost: the last contract overran twice");
            Item(CapturePlanItemKind.WinTheme, "A 60-day transition with zero unplanned downtime, proven on our last two takeovers");
            Item(CapturePlanItemKind.WinTheme, "ATO-ready from day one: our cleared team already holds the agency's RMF packages");
            Item(CapturePlanItemKind.Discriminator, "Only bidder with an in-house FedRAMP High environment");
            Item(CapturePlanItemKind.Discriminator, "Incumbent capture of 80% of key personnel through letters of commitment");
            Item(CapturePlanItemKind.Strength, "CPARS Exceptional on two similar-size task orders");
            Item(CapturePlanItemKind.Strength, "Relationships with the program office and the CO");
            Item(CapturePlanItemKind.Weakness, "No prime past performance at this agency");
            Item(CapturePlanItemKind.Opportunity, "Agency moving to a single enterprise vehicle next fiscal year");
            Item(CapturePlanItemKind.Threat, "Incumbent may bid aggressively low to keep the work");
            Item(CapturePlanItemKind.Risk, "Price: incumbent's rates are 8% below ours. Mitigation: blended labor mix and an offshore-free price-to-win review");

            db.Add(new OpportunityPwinAssessment
            {
                OpportunityId = o.Id, CustomerRelationship = 4, SolutionFit = 4, PastPerformance = 3, PriceToWin = 2, CompetitivePosition = 3, Teaming = 4,
                Notes = "Strong with the PM; price is the risk.", AssessedBy = by, AssessedAtUtc = now,
            });

            if (o.ProposalDueOn is { } due && due > today)
            {
                foreach (var (kind, daysBefore) in Application.Crm.CapturePlanService.StandardSchedule)
                {
                    var on = Application.Crm.CapturePlanService.Workday(due.AddDays(-daysBefore));
                    db.Add(new OpportunityMilestone
                    {
                        OpportunityId = o.Id, Kind = kind, Name = Contracts.Crm.CapturePlanLabels.Label((Contracts.Crm.MilestoneKind)(int)kind),
                        DueOn = kind == MilestoneKind.Submission ? due : on, OwnerEmail = o.ProposalManagerEmail ?? by, IsDone = on < today,
                        CompletedAtUtc = on < today ? now : null, CreatedBy = by, CreatedAtUtc = now,
                    });
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
