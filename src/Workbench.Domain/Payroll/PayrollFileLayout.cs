namespace Workbench.Domain.Payroll;

/// <summary>
/// A provider's file layouts as configured for this company (Payroll › Providers): the hours export columns and the
/// payroll register columns, date formats and number handling (JSON). No row means the built-in default layout.
/// </summary>
public sealed class PayrollFileLayout : Entity
{
    public required string ProviderKey { get; set; }
    public string? HoursLayoutJson { get; set; }
    public string? RegisterLayoutJson { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
