using Microsoft.EntityFrameworkCore;
using Workbench.Application.Automations;
using Workbench.Domain.Automations;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample automations (Admin › Automation), published by dev@ from the built-in templates: notify the PM when a
/// contract's funding drops under 25%, require a receipt on expenses over $75 (a validation rule on expense report submit),
/// and escalate timesheet approvals after 2 business days. Runs once.
/// </summary>
internal static class AutomationSeed
{
    private const string Admin = "dev@workbench.local";

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<AutomationRecord>().AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var key in new[] { AutomationTemplates.FundingLow, AutomationTemplates.ReceiptRequired, AutomationTemplates.TimesheetEscalation })
        {
            var request = AutomationTemplates.Find(key)!.Request;
            var definition = request.Definition;
            var automation = new AutomationRecord
            {
                Name = request.Name,
                Description = request.Description,
                IsEnabled = true,
                TriggerKind = definition.Trigger.Kind.ToString(),
                EntityType = definition.Trigger.EntityType,
                TriggerKeys = definition.Trigger.Kind switch
                {
                    Contracts.Automations.AutomationTriggerKind.RecordEvent => string.Join(",", definition.Trigger.Events ?? []),
                    Contracts.Automations.AutomationTriggerKind.Threshold => definition.Trigger.Threshold?.ToString(),
                    _ => null,
                },
                HasBlockingAction = definition.Actions.Any(a => a.Kind == Contracts.Automations.AutomationActionKind.Block),
                CurrentVersion = 1,
                TemplateKey = key,
                CreatedBy = Admin,
                CreatedAtUtc = now,
                UpdatedBy = Admin,
                UpdatedAtUtc = now,
            };
            var version = new AutomationVersion
            {
                AutomationId = automation.Id,
                Version = 1,
                Name = request.Name,
                DefinitionJson = AutomationJson.Serialize(definition),
                CreatedBy = Admin,
                CreatedAtUtc = now,
                Note = "Sample automation",
            };
            automation.CurrentVersionId = version.Id;
            db.Set<AutomationRecord>().Add(automation);
            db.Set<AutomationVersion>().Add(version);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
