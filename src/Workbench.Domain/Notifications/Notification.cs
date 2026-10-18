namespace Workbench.Domain.Notifications;

/// <summary>Something a person should know about: a rule notified them, they were @mentioned or assigned.</summary>
public sealed class Notification : Entity
{
    public required string RecipientEmail { get; set; }
    public required string Kind { get; set; }
    public required string Title { get; set; }
    public string Body { get; set; } = "";

    /// <summary>App-relative link, e.g. <c>sheets/{sheetId}?item={itemId}</c>.</summary>
    public string? Link { get; set; }

    public Guid? SheetId { get; set; }
    public Guid? ItemId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }

    /// <summary>When it went out by email (immediately or in a digest); null while it hasn't.</summary>
    public DateTime? EmailedAtUtc { get; set; }
}

public enum NotificationDelivery
{
    ImmediateEmail,
    DailyDigest,
    InAppOnly,
}

/// <summary>How a person wants to hear about notifications. People without a row get <see cref="NotificationDelivery.DailyDigest"/>.</summary>
public sealed class NotificationPreference : Entity
{
    public required string UserEmail { get; set; }
    public NotificationDelivery Delivery { get; set; } = NotificationDelivery.DailyDigest;
    public DateTime? LastDigestSentUtc { get; set; }
}
