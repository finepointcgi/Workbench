namespace Workbench.Domain.Automations;

/// <summary>
/// An admin-built automation (Admin › Automation). The definition lives in immutable <see cref="AutomationVersion"/>s;
/// this row is the header plus a copy of the current trigger's shape, so the capture hook can tell cheaply whether a
/// change interests any automation. Deleting hides it; versions and runs are kept.
/// </summary>
public sealed class AutomationRecord : Entity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }

    /// <summary>The current version's trigger kind (<c>AutomationTriggerKind</c> name).</summary>
    public required string TriggerKind { get; set; }

    public string? EntityType { get; set; }

    /// <summary>Comma-separated record events or the integration event key, for the capture index.</summary>
    public string? TriggerKeys { get; set; }

    /// <summary>The current version has a Block action (checked synchronously, before the change is saved).</summary>
    public bool HasBlockingAction { get; set; }

    public Guid? CurrentVersionId { get; set; }
    public int CurrentVersion { get; set; }
    public string? TemplateKey { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
}

/// <summary>One published definition (JSON <c>Contracts.Automations.AutomationDefinition</c>). Never changed.</summary>
public sealed class AutomationVersion : Entity
{
    public Guid AutomationId { get; set; }
    public int Version { get; set; }
    public required string Name { get; set; }
    public required string DefinitionJson { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? Note { get; set; }
}

/// <summary>A change or event waiting to be run by an automation, added in the same unit of work as the change.</summary>
public sealed class AutomationQueueItem : Entity
{
    public Guid AutomationId { get; set; }
    public required string TriggerKind { get; set; }

    /// <summary>The record event, integration event key, field or threshold that fired it.</summary>
    public string? TriggerDetail { get; set; }

    public string? EntityType { get; set; }
    public Guid? RecordId { get; set; }
    public string? Actor { get; set; }

    /// <summary>The change as <c>{"before":{...},"after":{...}}</c>, when known.</summary>
    public string? ChangesJson { get; set; }

    public int Depth { get; set; }
    public Guid? ParentRunId { get; set; }

    /// <summary>The integration outbox event that fired it (its payload is the run's data).</summary>
    public Guid? SourceEventId { get; set; }

    /// <summary>Who the run acts as when another automation started it (that run's principal), instead of its own setting.</summary>
    public string? PrincipalOverride { get; set; }

    /// <summary>Unique: the same event never queues the same automation twice.</summary>
    public required string DedupKey { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public int Attempts { get; set; }
}

/// <summary>The run log: one row per trigger (dry runs included), with inputs, condition results, each action's outcome and errors.</summary>
public sealed class AutomationRun : Entity
{
    public Guid AutomationId { get; set; }
    public int Version { get; set; }
    public required string AutomationName { get; set; }
    public required string TriggerKind { get; set; }
    public string? TriggerDetail { get; set; }
    public string? EntityType { get; set; }
    public Guid? RecordId { get; set; }
    public string? RecordLabel { get; set; }
    public string? Actor { get; set; }
    public required string Principal { get; set; }
    public int Depth { get; set; }
    public Guid? ParentRunId { get; set; }
    public required string Status { get; set; }
    public bool IsDryRun { get; set; }
    public string? InputsJson { get; set; }
    public string? ConditionsJson { get; set; }
    public string? StepsJson { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
}

/// <summary>A to-do an automation gave someone; shown on their home page until completed.</summary>
public sealed class AutomationTask : Entity
{
    public Guid AutomationId { get; set; }
    public Guid? RunId { get; set; }
    public required string AssigneeEmail { get; set; }
    public required string Title { get; set; }
    public string? Notes { get; set; }
    public DateOnly? DueOn { get; set; }
    public string? EntityType { get; set; }
    public Guid? RecordId { get; set; }
    public string? Link { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? CompletedBy { get; set; }
}

/// <summary>Which record a "require approval" action sent through a record approval workflow (document id = record id).</summary>
public sealed class AutomationApprovalRequest : Entity
{
    public Guid ApprovalInstanceId { get; set; }
    public required string DocumentType { get; set; }
    public required string EntityType { get; set; }
    public Guid RecordId { get; set; }
    public Guid AutomationId { get; set; }
    public Guid? RunId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? Outcome { get; set; }
}

/// <summary>Fired-once markers for schedules, date fields and thresholds (unique per automation and key), so runs aren't repeated.</summary>
public sealed class AutomationTriggerState : Entity
{
    public Guid AutomationId { get; set; }
    public required string Key { get; set; }
    public DateTime FiredAtUtc { get; set; }
}

/// <summary>The tenant's automation switches (one row): pause everything, and the loop protection limits.</summary>
public sealed class AutomationSettings : Entity
{
    public bool PausedAll { get; set; }
    public string? PausedBy { get; set; }
    public DateTime? PausedAtUtc { get; set; }
    public int MaxChainDepth { get; set; } = 5;
    public int MaxRunsPerRecordPerHour { get; set; } = 20;

    /// <summary>How far the integration event outbox has been read for automations.</summary>
    public DateTime? EventCursorUtc { get; set; }
}
