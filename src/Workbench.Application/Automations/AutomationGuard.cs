using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Contracts.Automations;
using Workbench.Domain.Automations;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Automations;

/// <summary>
/// Validation automations (a Block action): checked while the module's change is being made, against the record as it is
/// about to be saved, in the module's own unit of work. When the conditions match, the change is refused with the
/// automation's message (<see cref="AutomationBlockedException"/>, a 400) and the refusal is logged as a Blocked run.
/// Conditions read every field (a refusal shares nothing); the message only shows fields the person may read.
/// </summary>
public sealed class AutomationGuard(IWorkbenchDbContextFactory dbFactory, IServiceProvider services, IAutomationPermissions permissions, TimeProvider clock,
    ILogger<AutomationGuard> logger)
{
    public async Task CheckAsync(IWorkbenchDbContext db, Guid automationId, AutomationEntity entity, AuditEvent row, RecordChange change, CancellationToken ct)
    {
        var automation = await db.Set<AutomationRecord>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == automationId, ct);
        if (automation is null || !automation.IsEnabled || automation.DeletedAtUtc is not null)
        {
            return;
        }

        var version = await db.Set<AutomationVersion>().AsNoTracking().FirstAsync(v => v.Id == automation.CurrentVersionId, ct);
        var definition = AutomationJson.Definition(version.DefinitionJson);
        if (definition.Actions.FirstOrDefault(a => a.Kind == AutomationActionKind.Block) is not { } block)
        {
            return;
        }

        var record = await AutomationEntities.LoadAsync(db, entity, row.EntityId, ct);
        var actorEmail = row.Actor is { } a && a.Contains('@') ? a : null;
        var actor = actorEmail is null ? new AutomationPrincipal("", Contracts.Identity.OrgRoles.Employee, null, false) : await AutomationPrincipal.LoadAsync(db, actorEmail, ct);
        AutomationScope Scope(IAutomationPermissions p) => new()
        {
            Db = db, Services = services, Principal = actor, Permissions = p, Entity = entity, RecordId = row.EntityId, Record = record,
            Actor = actorEmail, Change = change, AutomationName = automation.Name, Ct = ct,
        };

        var full = Scope(AllFields.Instance);
        var results = new List<AutomationConditionResultDto>();
        if (!AutomationExecutor.TriggerValuesMatch(definition.Trigger, change, results)
            || !await AutomationConditions.EvaluateAsync(definition.Conditions, full, results))
        {
            return;
        }

        var message = await Scope(permissions).RenderAsync(block.Message);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = $"The automation \"{automation.Name}\" doesn't allow this change.";
        }

        await LogAsync(db, automation, entity, row, full, results, message, ct);
        throw new AutomationBlockedException(message, automation.Id);
    }

    private async Task LogAsync(IWorkbenchDbContext moduleDb, AutomationRecord automation, AutomationEntity entity, AuditEvent row, AutomationScope scope,
        List<AutomationConditionResultDto> results, string message, CancellationToken ct)
    {
        // The module's unit of work is about to be abandoned, so the refusal is written on its own. Not while the module holds
        // an open transaction (SQLite would wait on its own lock); the refusal still stands, it just isn't logged.
        if (moduleDb is DbContext { Database.CurrentTransaction: not null })
        {
            return;
        }

        try
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;
            db.Set<AutomationRun>().Add(new AutomationRun
            {
                AutomationId = automation.Id,
                Version = automation.CurrentVersion,
                AutomationName = automation.Name,
                TriggerKind = automation.TriggerKind,
                TriggerDetail = row.Action,
                EntityType = entity.Key,
                RecordId = row.EntityId,
                RecordLabel = AutomationExecutor.Clip(scope.RecordLabel, 300),
                Actor = AutomationExecutor.Clip(scope.Actor, 320),
                Principal = AutomationExecutor.Clip(scope.Actor, 320) ?? "(none)",
                Status = nameof(AutomationRunStatus.Blocked),
                Error = AutomationExecutor.Clip(message, 2000),
                ConditionsJson = AutomationJson.Serialize(results),
                InputsJson = AutomationJson.Serialize(scope.Inputs),
                StepsJson = AutomationJson.Serialize(new[] { new AutomationStepDto(0, AutomationActionKind.Block, AutomationStepOutcome.Done, $"Refused the change: \"{message}\"") }),
                StartedAtUtc = now,
                FinishedAtUtc = now,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't log the refusal by automation {AutomationId}", automation.Id);
        }
    }

    private sealed class AllFields : IAutomationPermissions
    {
        public static readonly AllFields Instance = new();

        public Task<bool> CanManageAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> CanRunAsync(Guid automationId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public bool PrincipalHas(AutomationPrincipal principal, string permissionKey) => true;
    }
}
