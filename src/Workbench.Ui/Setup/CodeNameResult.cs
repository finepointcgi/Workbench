namespace Workbench.Ui.Setup;

/// <summary>What <see cref="CodeNameDialog"/> returns.</summary>
public sealed record CodeNameResult(string Code, string Name, bool IsActive, string? Category, Guid? ParentId);
