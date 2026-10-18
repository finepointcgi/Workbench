using Workbench.Contracts.Collaboration;

namespace Workbench.Application.Identity;

/// <summary>
/// The user for background work with nobody signed in (the worker's scheduled jobs). It passes every permission
/// check, so never register it in the web host.
/// </summary>
public sealed class SystemCurrentUser : ICurrentUser
{
    public const string SystemEmail = "system@workbench.local";

    public string Email => SystemEmail;

    public string DisplayName => "Workbench";

    public bool IsAuthenticated => true;
}
