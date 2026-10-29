using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Calendar;
using Workbench.Domain.Notifications;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class CalendarFeedTokenConfiguration : IEntityTypeConfiguration<CalendarFeedToken>
{
    public void Configure(EntityTypeBuilder<CalendarFeedToken> b)
    {
        b.Property(t => t.UserEmail).HasMaxLength(320);
        b.Property(t => t.TokenHash).HasMaxLength(64);
        b.Property(t => t.TokenProtected).HasMaxLength(2000);
        b.HasIndex(t => t.UserEmail).IsUnique();
        b.HasIndex(t => t.TokenHash).IsUnique();
    }
}

internal sealed class ChatChannelConfiguration : IEntityTypeConfiguration<ChatChannel>
{
    public void Configure(EntityTypeBuilder<ChatChannel> b)
    {
        b.Property(c => c.Kind).HasMaxLength(32);
        b.Property(c => c.WebhookUrlProtected).HasMaxLength(4000);
        b.Property(c => c.Host).HasMaxLength(255);
        b.Property(c => c.UpdatedBy).HasMaxLength(320);
        b.Property(c => c.LastError).HasMaxLength(500);
        b.HasIndex(c => c.Kind).IsUnique();
    }
}

internal sealed class ChatNotificationChoiceConfiguration : IEntityTypeConfiguration<ChatNotificationChoice>
{
    public void Configure(EntityTypeBuilder<ChatNotificationChoice> b)
    {
        b.Property(c => c.UserEmail).HasMaxLength(320);
        b.Property(c => c.ChannelKind).HasMaxLength(32);
        b.Property(c => c.Group).HasMaxLength(32);
        b.HasIndex(c => new { c.UserEmail, c.ChannelKind, c.Group }).IsUnique();
    }
}
