namespace Workbench.Application.Support;

/// <summary>Configuration section <c>Support</c>: where in-app problem reports and feedback are emailed.</summary>
public sealed class SupportOptions
{
    public const string Section = "Support";

    /// <summary>The support team's address. Blank: requests are only stored (administrators see them in the app).</summary>
    public string? Email { get; set; }
}
