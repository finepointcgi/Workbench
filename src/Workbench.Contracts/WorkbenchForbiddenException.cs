namespace Workbench.Contracts;

/// <summary>
/// Thrown when the signed-in user can see something but isn't allowed to do what they asked (for example a Viewer
/// editing an item). The API maps it to 403 and the API client maps 403 back to it. It derives from
/// <see cref="UnauthorizedAccessException"/> so existing handlers for that keep working.
/// </summary>
public class WorkbenchForbiddenException(string message) : UnauthorizedAccessException(message)
{
    public WorkbenchForbiddenException()
        : this("You don't have permission to do that.")
    {
    }
}
