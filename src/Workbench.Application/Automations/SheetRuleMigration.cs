using Microsoft.EntityFrameworkCore;
using Workbench.Application.Automation;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Automations;
using Workbench.Domain.Automations;
using Legacy = Workbench.Domain.Automation;

namespace Workbench.Application.Automations;

/// <summary>
/// Data migration: moves sheet rules (the old <c>Rules</c> and <c>RuleRuns</c> tables) into sheet automations. Each rule becomes an
/// automation with the same id (one version, by its author, enabled as it was), its runs become run-log rows, and the dates its
/// date triggers already fired for become fired markers, so nothing fires twice. The rule rows are then removed. Idempotent:
/// run after every migration (<c>DatabaseMigrator</c>) and safe to run again.
/// </summary>
public static class SheetRuleMigration
{
    /// <summary>Migrates every rule left. Returns how many were migrated.</summary>
    public static async Task<int> MigrateAsync(IWorkbenchDbContext db, CancellationToken cancellationToken = default)
    {
        var rules = await db.Set<Legacy.Rule>().ToListAsync(cancellationToken);
        if (rules.Count == 0)
        {
            return 0;
        }

        var ids = rules.Select(r => r.Id).ToList();
        var already = (await db.Set<AutomationRecord>().AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => a.Id).ToListAsync(cancellationToken)).ToHashSet();
        var runs = await db.Set<Legacy.RuleRun>().AsNoTracking().Where(r => ids.Contains(r.RuleId)).ToListAsync(cancellationToken);
        foreach (var rule in rules.Where(r => !already.Contains(r.Id)))
        {
            var author = rule.CreatedBy.Trim().ToLowerInvariant();
            var definition = RuleMapping.ToDefinition(rule.SheetId, rule.Trigger.ToDto(), rule.Conditions.Select(c => c.ToDto()).ToList(), rule.Actions.Select(a => a.ToDto()).ToList());
            var version = new AutomationVersion
            {
                AutomationId = rule.Id, Version = 1, Name = rule.Name, DefinitionJson = AutomationJson.Serialize(definition), CreatedBy = author,
                CreatedAtUtc = rule.UpdatedAtUtc == default ? rule.CreatedAtUtc : rule.UpdatedAtUtc, Note = "Migrated from the sheet's rules",
            };
            db.Set<AutomationRecord>().Add(new AutomationRecord
            {
                Id = rule.Id,
                Name = rule.Name,
                IsEnabled = rule.Enabled,
                TriggerKind = definition.Trigger.Kind.ToString(),
                EntityType = AutomationSheets.EntityType,
                SheetId = rule.SheetId,
                TriggerKeys = definition.Trigger.Kind switch
                {
                    AutomationTriggerKind.RecordEvent => nameof(RecordEventKind.Created),
                    _ => definition.Trigger.FieldKey,
                },
                CurrentVersion = 1,
                CurrentVersionId = version.Id,
                CreatedBy = author,
                CreatedAtUtc = rule.CreatedAtUtc,
                UpdatedBy = author,
                UpdatedAtUtc = version.CreatedAtUtc,
            });
            db.Set<AutomationVersion>().Add(version);

            foreach (var run in runs.Where(r => r.RuleId == rule.Id))
            {
                db.Set<AutomationRun>().Add(new AutomationRun
                {
                    Id = run.Id,
                    AutomationId = rule.Id,
                    Version = 1,
                    AutomationName = rule.Name,
                    TriggerKind = definition.Trigger.Kind.ToString(),
                    TriggerDetail = run.TriggerDate is { } d ? $"Date {d:yyyy-MM-dd}" : null,
                    EntityType = AutomationSheets.EntityType,
                    RecordId = run.ItemId,
                    Principal = author,
                    Status = run.Outcome switch
                    {
                        Legacy.RuleRunOutcome.Succeeded => nameof(AutomationRunStatus.Succeeded),
                        Legacy.RuleRunOutcome.Skipped => nameof(AutomationRunStatus.Skipped),
                        _ => nameof(AutomationRunStatus.Failed),
                    },
                    Error = run.Error,
                    StepsJson = run.Detail is null ? "[]" : AutomationJson.Serialize(run.Detail.Split("; ").Select((detail, i) =>
                        new AutomationStepDto(i, definition.Actions.ElementAtOrDefault(i)?.Kind ?? AutomationActionKind.Notify, AutomationStepOutcome.Done, detail)).ToList()),
                    ConditionsJson = "[]",
                    StartedAtUtc = run.FiredAtUtc,
                    FinishedAtUtc = run.FiredAtUtc,
                });
            }

            foreach (var key in runs.Where(r => r.RuleId == rule.Id && r.ItemId is not null && r.TriggerDate is not null)
                         .Select(r => SheetAutomationRuntime.DateKey(r.ItemId!.Value, r.TriggerDate!.Value)).Distinct())
            {
                db.Set<AutomationTriggerState>().Add(new AutomationTriggerState { AutomationId = rule.Id, Key = key, FiredAtUtc = DateTime.UtcNow });
            }
        }

        // The rules' runs go with them (cascade); their history now lives in the run log.
        db.Set<Legacy.RuleRun>().RemoveRange(await db.Set<Legacy.RuleRun>().Where(r => ids.Contains(r.RuleId)).ToListAsync(cancellationToken));
        db.Set<Legacy.Rule>().RemoveRange(rules);
        await db.SaveChangesAsync(cancellationToken);
        return rules.Count - already.Count;
    }
}
