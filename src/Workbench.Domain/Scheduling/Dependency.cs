namespace Workbench.Domain.Scheduling;

public enum DependencyType
{
    FinishToStart,
    StartToStart,
    FinishToFinish,
    StartToFinish,
}

/// <summary>A link between two items on the same sheet: the successor is constrained by the predecessor.</summary>
public sealed class Dependency : Entity
{
    public Guid SheetId { get; set; }
    public Guid PredecessorId { get; set; }
    public Guid SuccessorId { get; set; }
    public DependencyType Type { get; set; }

    /// <summary>Working days between the linked dates. Negative values are lead time.</summary>
    public int LagDays { get; set; }
}
