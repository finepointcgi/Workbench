namespace Workbench.Contracts.Calendar;

/// <summary>
/// The signed-in person's calendar feed. <see cref="Url"/> is the address to paste into Outlook, Google Calendar or Apple Calendar,
/// or null when no feed is switched on. The address is private: anyone who has it can read the feed.
/// </summary>
public sealed record CalendarFeedDto(bool Enabled, string? Url);

/// <summary>Switch the person's read-only calendar feed on, get a fresh address, or switch it off.</summary>
public interface ICalendarFeedService
{
    Task<CalendarFeedDto> GetMineAsync(CancellationToken cancellationToken = default);

    /// <summary>Turns the feed on (keeping the address when it's already on).</summary>
    Task<CalendarFeedDto> EnableAsync(CancellationToken cancellationToken = default);

    /// <summary>A new address; the old one stops working at once (use this if the link was shared by mistake).</summary>
    Task<CalendarFeedDto> RotateAsync(CancellationToken cancellationToken = default);

    /// <summary>Switches the feed off; the address stops working.</summary>
    Task RevokeAsync(CancellationToken cancellationToken = default);
}
