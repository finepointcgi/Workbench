using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.Integrations;
using Workbench.Application.Notifications;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Identity;
using Workbench.Domain.Automations;
using Workbench.Domain.Identity;
using Workbench.Domain.Integrations;

namespace Workbench.Application.Automations;

/// <summary>
/// The "manage automations" and "run automation X" permissions, and field-level permission keys for an automation's
/// principal. A seam for the permission catalog: until it lands, everything here is for administrators (and Finance fields
/// for Finance). Replace the registration to read the catalog.
/// </summary>
public interface IAutomationPermissions
{
    Task<bool> CanManageAsync(CancellationToken cancellationToken = default);

    Task<bool> CanRunAsync(Guid automationId, CancellationToken cancellationToken = default);

    /// <summary>Whether an automation acting as <paramref name="principal"/> holds <paramref name="permissionKey"/> (a field's key).</summary>
    bool PrincipalHas(AutomationPrincipal principal, string permissionKey);
}

public sealed class AdminAutomationPermissions(AccessControl access) : IAutomationPermissions
{
    public Task<bool> CanManageAsync(CancellationToken cancellationToken = default) => access.HasAsync(Permission.AdminAutomations, cancellationToken);

    public Task<bool> CanRunAsync(Guid automationId, CancellationToken cancellationToken = default) => access.HasAsync(Permission.AdminAutomations, cancellationToken);

    public bool PrincipalHas(AutomationPrincipal principal, string permissionKey) => permissionKey switch
    {
        AutomationPermissionKeys.FinanceFields => principal.Roles.IsFinance(),
        _ => principal.Roles.IsAdmin(),
    };
}

/// <summary>One request to run an automation (from the queue, a schedule, a person, or a dry run).</summary>
public sealed record AutomationInvocation(
    Guid AutomationId,
    AutomationTriggerKind TriggerKind,
    string? TriggerDetail = null,
    string? EntityType = null,
    Guid? RecordId = null,
    string? Actor = null,
    string? ChangesJson = null,
    int Depth = 0,
    Guid? ParentRunId = null,
    bool DryRun = false,
    string? PrincipalOverride = null,
    string? PayloadJson = null,
    AutomationDefinition? DefinitionOverride = null,
    string? NameOverride = null);

/// <summary>
/// Runs one automation against one record: pause and disable switches, loop protection (chain depth, a chain revisiting
/// the same automation and record, a per-record hourly limit), the principal (the actor or the service user, with only
/// their own roles), the trigger's from/to values, the conditions, then each action with its permission check. Every run,
/// including skips and dry runs, is logged with its inputs, condition results, each action's outcome and errors.
/// </summary>
public sealed class AutomationExecutor(
    IWorkbenchDbContextFactory dbFactory,
    IServiceProvider services,
    IAutomationPermissions permissions,
    INotificationPublisher publisher,
    TimeProvider clock,
    ILogger<AutomationExecutor> logger)
{
    public const string NotificationKind = "automation";

    private static readonly HashSet<string> NotPeople = new(StringComparer.OrdinalIgnoreCase) { "system", "workbench system", "" };

    public async Task<AutomationRunDto> RunAsync(AutomationInvocation inv, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var automation = await db.Set<AutomationRecord>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == inv.AutomationId, ct);
        if (automation is null && inv.DefinitionOverride is null)
        {
            throw WorkbenchNotFoundException.For("Automation", inv.AutomationId);
        }

        var definition = inv.DefinitionOverride ?? AutomationJson.Definition(
            (await db.Set<AutomationVersion>().AsNoTracking().FirstAsync(v => v.Id == automation!.CurrentVersionId, ct)).DefinitionJson);
        var now = clock.GetUtcNow().UtcDateTime;
        var run = new AutomationRun
        {
            AutomationId = inv.AutomationId,
            Version = automation?.CurrentVersion ?? 0, // A dry run of an edited draft is logged against the version it was opened from.
            AutomationName = Clip(inv.NameOverride ?? automation?.Name ?? "Unsaved automation", 200)!,
            TriggerKind = inv.TriggerKind.ToString(),
            TriggerDetail = Clip(inv.TriggerDetail, 200),
            EntityType = inv.EntityType,
            RecordId = inv.RecordId,
            Actor = Person(inv.Actor),
            Principal = "",
            Depth = inv.Depth,
            ParentRunId = inv.ParentRunId,
            IsDryRun = inv.DryRun,
            Status = nameof(AutomationRunStatus.Succeeded),
            StartedAtUtc = now,
        };
        var steps = new List<AutomationStepDto>();
        var conditions = new List<AutomationConditionResultDto>();
        var inputs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (await SkipReasonAsync(db, automation, inv, ct) is { } skip)
            {
                if (!inv.DryRun)
                {
                    return await FinishAsync(db, run, AutomationRunStatus.Skipped, skip, steps, conditions, inputs, ct);
                }

                run.Error = $"Note: {skip}";
            }

            var actor = Person(inv.Actor);
            var principalEmail = inv.PrincipalOverride
                ?? (definition.RunAs == AutomationRunAs.ServicePrincipal ? definition.ServicePrincipalEmail : actor ?? automation?.UpdatedBy)
                ?? throw new ArgumentException("This automation has no user to act as.");
            var principal = await AutomationPrincipal.LoadAsync(db, principalEmail, ct);
            run.Principal = Clip(principal.Email, 320)!;
            if (!principal.IsActive)
            {
                return await FinishAsync(db, run, AutomationRunStatus.Failed, $"{principal.Email} isn't an active user, so the automation can't act as them.", steps, conditions, inputs, ct);
            }

            var entity = AutomationEntities.Find(inv.EntityType);
            object? record = null;
            if (entity is not null && inv.RecordId is { } recordId)
            {
                record = await AutomationEntities.LoadAsync(db, entity, recordId, ct);
            }

            var change = RecordChange.FromQueue(inv.ChangesJson);
            if (entity is not null && inv.RecordId is not null && record is null && change?.Events.Contains(RecordEventKind.Deleted) != true)
            {
                return await FinishAsync(db, run, AutomationRunStatus.Failed, $"The {entity.Label.ToLowerInvariant()} no longer exists.", steps, conditions, inputs, ct);
            }

            var scope = new AutomationScope
            {
                Db = db,
                Services = services,
                Principal = principal,
                Permissions = permissions,
                Entity = entity,
                RecordId = inv.RecordId,
                Record = record,
                Actor = actor,
                Change = change,
                Payload = Payload(inv.PayloadJson),
                AutomationName = run.AutomationName,
                Ct = ct,
            };
            run.RecordLabel = Clip(scope.RecordLabel, 300);
            inputs["trigger"] = $"{inv.TriggerKind}{(inv.TriggerDetail is null ? "" : $": {inv.TriggerDetail}")}";
            if (change?.Json is { } changeJson)
            {
                inputs["change"] = Clip(changeJson, 2000);
            }

            if (!(inv.DryRun && change is null) && !TriggerValuesMatch(definition.Trigger, change, conditions))
            {
                inputs.AddRange(scope.Inputs);
                return await FinishAsync(db, run, inv.DryRun ? AutomationRunStatus.DryRun : AutomationRunStatus.ConditionsNotMet, run.Error, steps, conditions, inputs, ct);
            }

            var matched = await AutomationConditions.EvaluateAsync(definition.Conditions, scope, conditions);
            if (!matched)
            {
                inputs.AddRange(scope.Inputs);
                return await FinishAsync(db, run, inv.DryRun ? AutomationRunStatus.DryRun : AutomationRunStatus.ConditionsNotMet, run.Error, steps, conditions, inputs, ct);
            }

            using (AutomationChain.Enter(run.Id, inv.Depth))
            using (ActAs(principal))
            {
                for (var index = 0; index < definition.Actions.Count; index++)
                {
                    steps.Add(await ActAsync(db, scope, automation, run, inv, definition.Actions[index], index, ct));
                }
            }

            inputs.AddRange(scope.Inputs);
            var status = inv.DryRun ? AutomationRunStatus.DryRun
                : steps.Count == 0 || steps.All(s => s.Outcome is AutomationStepOutcome.Done or AutomationStepOutcome.Skipped) ? AutomationRunStatus.Succeeded
                : steps.Any(s => s.Outcome == AutomationStepOutcome.Done) ? AutomationRunStatus.PartiallyFailed
                : AutomationRunStatus.Failed;
            return await FinishAsync(db, run, status, run.Error, steps, conditions, inputs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Automation {AutomationId} failed", inv.AutomationId);
            (db as DbContext)?.ChangeTracker.Clear();
            return await FinishAsync(db, run, AutomationRunStatus.Failed, ex.Message, steps, conditions, inputs, ct);
        }
    }

    /// <summary>Why the run shouldn't happen, or null: paused, disabled, too deep, a loop, or too many runs on this record.</summary>
    private async Task<string?> SkipReasonAsync(IWorkbenchDbContext db, AutomationRecord? automation, AutomationInvocation inv, CancellationToken ct)
    {
        var settings = await db.Set<AutomationSettings>().AsNoTracking().FirstOrDefaultAsync(ct) ?? new AutomationSettings();
        if (settings.PausedAll)
        {
            return "Every automation is paused (Admin › Automation › Settings).";
        }

        if (automation is null)
        {
            return null;
        }

        if (automation.DeletedAtUtc is not null || !automation.IsEnabled)
        {
            return "The automation is turned off.";
        }

        if (inv.Depth > settings.MaxChainDepth)
        {
            return $"Loop protection: a chain of automations went {inv.Depth} levels deep (the limit is {settings.MaxChainDepth}).";
        }

        var parent = inv.ParentRunId;
        for (var hops = 0; parent is { } parentId && hops <= settings.MaxChainDepth; hops++)
        {
            var link = await db.Set<AutomationRun>().AsNoTracking().Where(r => r.Id == parentId)
                .Select(r => new { r.AutomationId, r.RecordId, r.ParentRunId }).FirstOrDefaultAsync(ct);
            if (link is null)
            {
                break;
            }

            if (link.AutomationId == inv.AutomationId && link.RecordId == inv.RecordId && inv.TriggerKind != AutomationTriggerKind.Manual)
            {
                return "Loop protection: this automation already ran on this record earlier in the same chain.";
            }

            parent = link.ParentRunId;
        }

        if (inv.RecordId is { } recordId && !inv.DryRun)
        {
            var since = clock.GetUtcNow().UtcDateTime.AddHours(-1);
            var skipped = nameof(AutomationRunStatus.Skipped);
            var recent = await db.Set<AutomationRun>().AsNoTracking()
                .CountAsync(r => r.AutomationId == inv.AutomationId && r.RecordId == recordId && !r.IsDryRun && r.Status != skipped && r.StartedAtUtc >= since, ct);
            if (recent >= settings.MaxRunsPerRecordPerHour)
            {
                return $"Rate limit: it already ran {recent} times on this record in the last hour (the limit is {settings.MaxRunsPerRecordPerHour}).";
            }
        }

        return null;
    }

    internal static bool TriggerValuesMatch(AutomationTrigger trigger, RecordChange? change, List<AutomationConditionResultDto> results)
    {
        if (trigger.Kind != AutomationTriggerKind.FieldChanged || trigger.FieldKey is not { } field || (trigger.FromValue is null && trigger.ToValue is null))
        {
            return true;
        }

        var before = change?.BeforeValue(field);
        var after = change?.AfterValue(field);
        var ok = (trigger.FromValue is null || AutomationConditions.Compare(ConditionOperator.Equals, before, trigger.FromValue))
            && (trigger.ToValue is null || AutomationConditions.Compare(ConditionOperator.Equals, after, trigger.ToValue));
        var wanted = $"{field} changed{(trigger.FromValue is null ? "" : $" from {trigger.FromValue}")}{(trigger.ToValue is null ? "" : $" to {trigger.ToValue}")}";
        results.Add(new AutomationConditionResultDto(wanted, ok, $"{before ?? "(empty)"} → {after ?? "(empty)"}"));
        return ok;
    }

    private async Task<AutomationStepDto> ActAsync(IWorkbenchDbContext db, AutomationScope scope, AutomationRecord? automation, AutomationRun run,
        AutomationInvocation inv, AutomationAction action, int index, CancellationToken ct)
    {
        AutomationStepDto Step(AutomationStepOutcome outcome, string detail) => new(index, action.Kind, outcome, Clip(detail, 1000)!);
        var dry = inv.DryRun;
        try
        {
            switch (action.Kind)
            {
                case AutomationActionKind.Block:
                    var refusal = await scope.RenderAsync(action.Message);
                    return dry
                        ? Step(AutomationStepOutcome.WouldDo, $"Would refuse the change: \"{refusal}\"")
                        : Step(AutomationStepOutcome.Skipped, "Refusing a change only applies before it's saved (record events); nothing to do here.");

                case AutomationActionKind.Notify or AutomationActionKind.CreateTask:
                {
                    if (await DeniedReadAsync(scope) is { } denied)
                    {
                        return Step(AutomationStepOutcome.Denied, denied);
                    }

                    var to = (await scope.RecipientsAsync(action.Recipients ?? [])).ToList();
                    if (inv.TriggerKind is AutomationTriggerKind.RecordEvent or AutomationTriggerKind.FieldChanged && scope.Actor is { } self)
                    {
                        to.Remove(self); // People aren't told about their own changes.
                    }

                    var title = await scope.RenderAsync(action.Title ?? "{automation}: {record}");
                    var body = await scope.RenderAsync(action.Message);
                    if (to.Count == 0)
                    {
                        return Step(AutomationStepOutcome.Skipped, "Nobody to send it to.");
                    }

                    if (action.Kind == AutomationActionKind.CreateTask)
                    {
                        var due = action.DueInDays is { } days ? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(days) : (DateOnly?)null;
                        if (dry)
                        {
                            return Step(AutomationStepOutcome.WouldDo, $"Would give {string.Join(", ", to)} the to-do \"{title}\"{(due is null ? "" : $" due {due:MMM d}")}.");
                        }

                        foreach (var email in to)
                        {
                            db.Set<AutomationTask>().Add(new AutomationTask
                            {
                                AutomationId = inv.AutomationId, RunId = run.Id, AssigneeEmail = email, Title = Clip(title, 300)!, Notes = Clip(body, 2000), DueOn = due,
                                EntityType = scope.Entity?.Key, RecordId = scope.RecordId, Link = Clip(scope.Link, 300), CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
                            });
                        }

                        await db.SaveChangesAsync(ct);
                        await publisher.PublishAsync(to, NotificationKind, $"New to-do: {title}", body, scope.Link ?? "");
                        return Step(AutomationStepOutcome.Done, $"Gave {string.Join(", ", to)} the to-do \"{title}\".");
                    }

                    if (dry)
                    {
                        return Step(AutomationStepOutcome.WouldDo, $"Would notify {string.Join(", ", to)}: \"{title}\"{(body.Length == 0 ? "" : $" — {body}")}");
                    }

                    var sent = await publisher.PublishAsync(to, NotificationKind, title, body.Length == 0 ? null : body, string.IsNullOrEmpty(scope.Link) ? null : scope.Link);
                    return Step(AutomationStepOutcome.Done, $"Notified {string.Join(", ", sent)}: \"{title}\"");
                }

                case AutomationActionKind.SetField:
                {
                    var entity = scope.Entity ?? throw new ArgumentException("Setting a field needs a record.");
                    var field = action.FieldKey ?? throw new ArgumentException("Choose the field to set.");
                    if (!entity.CanWrite(scope.Principal.Roles))
                    {
                        return Step(AutomationStepOutcome.Denied, $"{scope.Principal.Email} can't change {entity.Label.ToLowerInvariant()} records, so the automation can't either.");
                    }

                    if (!await scope.PrincipalCanReadFieldAsync(field))
                    {
                        return Step(AutomationStepOutcome.Denied, $"{scope.Principal.Email} doesn't have access to {field}.");
                    }

                    var value = await scope.RenderAsync(action.Value);
                    if (dry)
                    {
                        return Step(AutomationStepOutcome.WouldDo, $"Would set {field} to \"{value}\".");
                    }

                    // Built-in fields follow the automation entity's rules (checked above for the principal); custom fields go
                    // through the custom-fields catalog, which validates, audits and checks the field's own permission.
                    var catalog = field.StartsWith(CustomFieldValues.CatalogPrefix, StringComparison.Ordinal)
                        ? services.GetRequiredService<IEntityFieldCatalog>()
                        : ActivatorUtilities.CreateInstance<BuiltInEntityFieldCatalog>(services);
                    await catalog.SetValueAsync(entity.Key, scope.RecordId!.Value, field, value, ct);
                    return Step(AutomationStepOutcome.Done, $"Set {field} to \"{value}\".");
                }

                case AutomationActionKind.RequireApproval:
                {
                    var entity = scope.Entity ?? throw new ArgumentException("Requiring an approval needs a record.");
                    var key = action.WorkflowKey ?? throw new ArgumentException("Choose the approval workflow.");
                    if (!ApprovalDocumentTypes.IsCustom(key) || !await db.Set<Domain.Approvals.ApprovalWorkflow>().AnyAsync(w => w.DocumentType == key, ct))
                    {
                        throw new ArgumentException($"\"{key}\" isn't a record approval workflow (Setup › Approval workflows).");
                    }

                    if (await DeniedReadAsync(scope) is { } denied)
                    {
                        return Step(AutomationStepOutcome.Denied, denied);
                    }

                    var engine = services.GetService<ApprovalEngine>() ?? new ApprovalEngine(clock);
                    var submitter = scope.Actor ?? scope.Principal.Email;
                    var subject = new ApprovalSubject(key, scope.RecordId!.Value, Clip($"{entity.Label}: {scope.RecordLabel}", 300)!, submitter, scope.Link ?? "",
                        Summary: $"Sent for approval by the automation \"{run.AutomationName}\"", SubmitterEmployeeId: (await scope.EmployeeAsync())?.Id,
                        Charges: await scope.ProjectAsync() is { } projectId ? [new ApprovalCharge(projectId, 0m, await scope.OrgUnitAsync())] : null,
                        CompanyId: await scope.CompanyAsync());
                    if (dry)
                    {
                        var preview = await engine.StartAsync(db, subject, ct, persist: false);
                        return Step(AutomationStepOutcome.WouldDo, $"Would send it for approval ({key}): {string.Join(", ", preview.Tasks.Select(t => t.StepName).Distinct())}.");
                    }

                    var route = await engine.StartAsync(db, subject, ct);
                    db.Set<AutomationApprovalRequest>().Add(new AutomationApprovalRequest
                    {
                        ApprovalInstanceId = route.Instance.Id, DocumentType = key, EntityType = entity.Key, RecordId = scope.RecordId!.Value,
                        AutomationId = inv.AutomationId, RunId = run.Id, CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
                    });
                    await db.SaveChangesAsync(ct);
                    await publisher.PublishAsync(route.NotifyEmails, "approval.requested", $"Approval needed: {subject.Title}", subject.Summary, "approvals");
                    return Step(AutomationStepOutcome.Done, $"Sent it for approval; waiting on {(route.NotifyEmails.Count == 0 ? "an administrator" : string.Join(", ", route.NotifyEmails))}.");
                }

                case AutomationActionKind.PostToChat or AutomationActionKind.CallWebhook:
                {
                    if (await DeniedReadAsync(scope) is { } denied)
                    {
                        return Step(AutomationStepOutcome.Denied, denied);
                    }

                    var webhook = action.Kind == AutomationActionKind.CallWebhook;
                    Guid targetId;
                    string targetName;
                    if (webhook)
                    {
                        var endpoint = await db.Set<WebhookEndpoint>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == action.WebhookEndpointId, ct)
                            ?? throw new ArgumentException("The webhook endpoint no longer exists.");
                        if (!endpoint.IsActive)
                        {
                            return Step(AutomationStepOutcome.Skipped, $"The endpoint \"{endpoint.Name}\" is turned off.");
                        }

                        (targetId, targetName) = (endpoint.Id, endpoint.Name);
                    }
                    else
                    {
                        var connector = await db.Set<IntegrationConnector>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == action.ConnectorId, ct)
                            ?? throw new ArgumentException("The chat connector no longer exists.");
                        if (connector.Kind is not ("teams" or "slack"))
                        {
                            throw new ArgumentException($"\"{connector.Name}\" isn't a Teams or Slack connector.");
                        }

                        if (!connector.IsActive)
                        {
                            return Step(AutomationStepOutcome.Skipped, $"The connector \"{connector.Name}\" is turned off.");
                        }

                        (targetId, targetName) = (connector.Id, connector.Name);
                    }

                    var title = await scope.RenderAsync(action.Title ?? "{automation}: {record}");
                    var text = await scope.RenderAsync(action.Message);
                    var data = new Dictionary<string, object?>
                    {
                        ["automationId"] = inv.AutomationId,
                        ["automation"] = run.AutomationName,
                        ["title"] = title,
                        ["text"] = text,
                        ["entityType"] = scope.Entity?.Key,
                        ["recordId"] = scope.RecordId,
                        ["record"] = scope.RecordLabel,
                        ["link"] = scope.Link,
                    };
                    if (webhook && scope.Entity is not null && scope.Record is not null)
                    {
                        data["fields"] = await ReadableFieldsAsync(scope);
                    }

                    if (dry)
                    {
                        return Step(AutomationStepOutcome.WouldDo, webhook ? $"Would POST \"{title}\" with the record's fields to {targetName}." : $"Would post \"{title}\" to {targetName}.");
                    }

                    var now = clock.GetUtcNow().UtcDateTime;
                    var row = IntegrationOutbox.Build(webhook ? WebhookEventType : ChatEventType, scope.Entity?.ClrType.Name ?? "Automation",
                        scope.RecordId ?? inv.AutomationId, data, scope.Principal.Email, now);
                    row.DispatchedAtUtc = now;
                    row.DeliveryCount = 1;
                    db.Set<IntegrationOutboxEvent>().Add(row);
                    db.Set<IntegrationDelivery>().Add(IntegrationDispatcher.NewDelivery(row, webhook ? DeliveryTargetKind.Webhook : DeliveryTargetKind.Connector, targetId, now,
                        requestedBy: $"automation:{inv.AutomationId:N}"));
                    await db.SaveChangesAsync(ct);
                    return Step(AutomationStepOutcome.Done, webhook ? $"Queued a POST to {targetName} (see Admin › Integrations › Deliveries)." : $"Queued a post to {targetName}.");
                }

                case AutomationActionKind.StartAutomation:
                {
                    var targetId = action.TargetAutomationId ?? throw new ArgumentException("Choose the automation to start.");
                    var target = await db.Set<AutomationRecord>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == targetId && a.DeletedAtUtc == null, ct)
                        ?? throw new ArgumentException("The automation to start no longer exists.");
                    if (dry)
                    {
                        return Step(AutomationStepOutcome.WouldDo, $"Would start \"{target.Name}\" on this record (as {scope.Principal.Email}).");
                    }

                    db.Set<AutomationQueueItem>().Add(new AutomationQueueItem
                    {
                        AutomationId = target.Id,
                        TriggerKind = nameof(AutomationTriggerKind.Manual),
                        TriggerDetail = $"Started by \"{Clip(run.AutomationName, 150)}\"",
                        EntityType = scope.Entity?.Key,
                        RecordId = scope.RecordId,
                        Actor = scope.Principal.Email, // The child acts as this run's principal, never as someone with more access.
                        Depth = inv.Depth + 1,
                        ParentRunId = run.Id,
                        DedupKey = $"start:{run.Id:N}:{index}",
                        CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
                        PrincipalOverride = scope.Principal.Email,
                    });
                    await db.SaveChangesAsync(ct);
                    services.GetService<AutomationSignal>()?.Pulse();
                    return Step(AutomationStepOutcome.Done, $"Started \"{target.Name}\".");
                }

                default:
                    return Step(AutomationStepOutcome.Skipped, "Unknown action.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or WorkbenchNotFoundException or DbUpdateException)
        {
            (db as DbContext)?.ChangeTracker.Clear();
            return Step(ex is UnauthorizedAccessException ? AutomationStepOutcome.Denied : AutomationStepOutcome.Failed, ex.Message);
        }
    }

    public const string WebhookEventType = "automation.webhook";
    public const string ChatEventType = "automation.message";

    private static async Task<string?> DeniedReadAsync(AutomationScope scope) =>
        await scope.PrincipalCanReadRecordAsync() ? null
            : $"{scope.Principal.Email} can't see this {scope.Entity?.Label.ToLowerInvariant() ?? "record"}, so the automation can't share it.";

    private static async Task<Dictionary<string, string?>> ReadableFieldsAsync(AutomationScope scope)
    {
        var fields = new Dictionary<string, string?>();
        foreach (var field in AutomationEntities.Fields(scope.Entity!).Where(f => scope.Entity!.Property(f.Key) is not null))
        {
            var value = await scope.FieldAsync(field.Key);
            if (value is not AutomationScope.NoAccess)
            {
                fields[field.Key] = AutomationScope.Format(value);
            }
        }

        return fields;
    }

    /// <summary>Outside a request (background runs), the run's audit rows and service calls are attributed to the principal.</summary>
    private IDisposable? ActAs(AutomationPrincipal principal)
    {
        if (services.GetService<IHttpContextAccessor>()?.HttpContext is not null)
        {
            return null;
        }

        return Reports.ReportRunAs.Enter(services, principal.Email, $"Workbench Automation ({principal.Email})");
    }

    private async Task<AutomationRunDto> FinishAsync(IWorkbenchDbContext db, AutomationRun run, AutomationRunStatus status, string? error,
        List<AutomationStepDto> steps, List<AutomationConditionResultDto> conditions, Dictionary<string, string?> inputs, CancellationToken ct)
    {
        run.Status = status.ToString();
        run.Error = Clip(error, 2000);
        run.Principal = run.Principal.Length == 0 ? "(none)" : run.Principal;
        run.StepsJson = AutomationJson.Serialize(steps);
        run.ConditionsJson = AutomationJson.Serialize(conditions);
        run.InputsJson = AutomationJson.Serialize(inputs);
        run.FinishedAtUtc = clock.GetUtcNow().UtcDateTime;
        if (run.AutomationId != Guid.Empty)
        {
            db.Set<AutomationRun>().Add(run);
            await db.SaveChangesAsync(ct);
        }

        return AutomationMapping.ToDto(run);
    }

    private static JsonElement? Payload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("data", out var data) ? data.Clone() : doc.RootElement.Clone();
    }

    private static string? Person(string? email) =>
        email is null || NotPeople.Contains(email.Trim()) || !email.Contains('@') ? null : ApprovalEngine.Normalize(email);

    internal static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}

internal static class DictionaryExtensions
{
    public static void AddRange(this Dictionary<string, string?> target, IReadOnlyDictionary<string, string?> source)
    {
        foreach (var (key, value) in source)
        {
            target[key] = value;
        }
    }
}

public static class AutomationMapping
{
    public static AutomationRunDto ToDto(AutomationRun r) => new(
        r.Id, r.AutomationId, r.AutomationName, r.Version, Enum.TryParse<AutomationTriggerKind>(r.TriggerKind, out var k) ? k : AutomationTriggerKind.Manual,
        r.TriggerDetail, r.EntityType, r.RecordId, r.RecordLabel, r.Actor, r.Principal, r.Depth, r.ParentRunId,
        Enum.TryParse<AutomationRunStatus>(r.Status, out var s) ? s : AutomationRunStatus.Failed, r.Error, r.StartedAtUtc, r.FinishedAtUtc,
        Read<List<AutomationStepDto>>(r.StepsJson) ?? [], Read<List<AutomationConditionResultDto>>(r.ConditionsJson) ?? [],
        Read<Dictionary<string, string?>>(r.InputsJson) ?? []);

    private static T? Read<T>(string? json) => string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, AutomationJson.Options);
}
