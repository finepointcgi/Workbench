namespace Workbench.Contracts;

/// <summary>Thrown when a requested sheet, item or column doesn't exist. The API maps it to 404.</summary>
public sealed class WorkbenchNotFoundException(string message) : Exception(message)
{
    public static WorkbenchNotFoundException For(string kind, Guid id) => new($"{kind} {id} was not found.");
}
