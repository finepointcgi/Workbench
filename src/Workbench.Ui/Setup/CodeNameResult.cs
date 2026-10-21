namespace Workbench.Ui.Setup;

/// <summary>What <see cref="CodeNameDialog"/> returns. <see cref="CustomValues"/>: custom field values, when the dialog edits a customizable record.</summary>
public sealed record CodeNameResult(string Code, string Name, bool IsActive, string? Category, Guid? ParentId, IReadOnlyDictionary<string, object?>? CustomValues = null);
