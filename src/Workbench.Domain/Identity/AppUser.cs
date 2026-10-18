namespace Workbench.Domain.Identity;

/// <summary>A person who has signed in at least once (or was seeded). Created just in time on first sign-in.</summary>
public sealed class AppUser : Entity
{
    /// <summary>Lower-case, unique. Memberships, comments and Person cells refer to users by email.</summary>
    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Admins see every workspace and can do everything.</summary>
    public bool IsAdmin { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
}
