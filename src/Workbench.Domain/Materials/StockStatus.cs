namespace Workbench.Domain.Materials;

/// <summary>Whether stock can be used. Only available stock is picked, issued, kitted or netted by MRP.</summary>
public enum InventoryStockStatus
{
    Available,

    /// <summary>Received or built, waiting for inspection.</summary>
    InspectionHold,

    /// <summary>Rejected and waiting for the material review board's disposition (an open NCR).</summary>
    Nonconforming,
}
