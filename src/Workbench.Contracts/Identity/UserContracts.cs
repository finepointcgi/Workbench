namespace Workbench.Contracts.Identity;

public sealed record UserDto(string Email, string DisplayName, bool IsAdmin, DateTime CreatedAtUtc, DateTime? LastSeenAtUtc);

/// <summary>People who have signed in (or were added). Used for Person columns, mentions, sharing and admin.</summary>
public interface IUserDirectory
{
    /// <summary>The signed-in user, provisioning them if needed.</summary>
    Task<UserDto> GetMeAsync(CancellationToken cancellationToken = default);

    /// <summary>Users whose email or name contains <paramref name="query"/> (all when blank), ordered by name.</summary>
    Task<IReadOnlyList<UserDto>> SearchAsync(string? query, int take = 20, CancellationToken cancellationToken = default);

    /// <summary>Admins only. Throws <see cref="ArgumentException"/> when it would remove the last admin.</summary>
    Task<UserDto> SetAdminAsync(string email, bool isAdmin, CancellationToken cancellationToken = default);
}

/// <summary>
/// How the UI signs the user out. The web host sends the browser to the sign-out endpoint; the desktop app forgets
/// its tokens.
/// </summary>
public interface ISignOutService
{
    Task SignOutAsync();
}
