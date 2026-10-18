namespace Workbench.Domain.Dashboards;

public sealed class Dashboard : Entity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string OwnerEmail { get; set; }
    public Guid? WorkspaceId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<Widget> Widgets { get; } = [];
}

public enum WidgetType
{
    Metric,
    Chart,
    ItemList,
    ScheduleHealth,
    ProjectFinancials,
    RichText,
    ContractFunding,
}

public enum WidgetHeight
{
    Small,
    Medium,
    Large,
}

/// <summary>A tile on a dashboard. <see cref="ConfigJson"/> holds the type-specific settings.</summary>
public sealed class Widget : Entity
{
    public Guid DashboardId { get; set; }
    public WidgetType Type { get; set; }
    public required string Title { get; set; }
    public int Order { get; set; }

    /// <summary>Grid columns on wide screens, 1–12.</summary>
    public int Width { get; set; } = 6;

    public WidgetHeight Height { get; set; } = WidgetHeight.Medium;
    public string ConfigJson { get; set; } = "{}";
}
