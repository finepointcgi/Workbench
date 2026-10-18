namespace Workbench.Contracts.Collaboration;

/// <summary>
/// The signed-in user. The web host reads it from the Blazor circuit's authentication state or the request's
/// <c>HttpContext.User</c> (cookie or bearer token); background jobs use a system user.
/// <see cref="Email"/> is lower-case, and empty when nobody is signed in.
/// </summary>
public interface ICurrentUser
{
    string Email { get; }

    /// <summary>The name to show for the user. Defaults to the email.</summary>
    string DisplayName => Email;

    bool IsAuthenticated => !string.IsNullOrEmpty(Email);
}
