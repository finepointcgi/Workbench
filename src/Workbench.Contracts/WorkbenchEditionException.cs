namespace Workbench.Contracts;

/// <summary>
/// Thrown when something needs the Business edition (or more seats than the license has). The API maps it to 402 and the
/// API client maps 402 back to it. It derives from <see cref="WorkbenchForbiddenException"/> so existing handlers still catch it.
/// </summary>
public sealed class WorkbenchEditionException(string message) : WorkbenchForbiddenException(message);
