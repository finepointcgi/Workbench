namespace Workbench.Domain.Calendar;

/// <summary>
/// A person's private calendar feed address. The token is the only credential (calendar apps can't sign in), so it is long and
/// random; the database finds the feed by <see cref="TokenHash"/> and keeps <see cref="TokenProtected"/> (encrypted) so the owner can
/// copy the link again. Rotating replaces the token; revoking deletes the row. One feed per person.
/// </summary>
public sealed class CalendarFeedToken : Entity
{
    /// <summary>Lower-case.</summary>
    public required string UserEmail { get; set; }

    /// <summary>Lower-case hex SHA-256 of the token.</summary>
    public required string TokenHash { get; set; }

    public required string TokenProtected { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? LastFetchedAtUtc { get; set; }
}
