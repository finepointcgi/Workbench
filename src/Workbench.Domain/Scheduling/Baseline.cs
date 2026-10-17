namespace Workbench.Domain.Scheduling;

/// <summary>A named snapshot of every item's planned dates, used to measure schedule variance.</summary>
public sealed class Baseline : Entity
{
    public Guid SheetId { get; set; }
    public required string Name { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public List<BaselineEntry> Entries { get; } = [];
}

public sealed class BaselineEntry : Entity
{
    public Guid BaselineId { get; set; }
    public Guid ItemId { get; set; }
    public DateOnly? Start { get; set; }
    public DateOnly? Finish { get; set; }
}
