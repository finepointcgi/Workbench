using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain;
using Workbench.Domain.Automation;
using Workbench.Domain.Notifications;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class RuleConfiguration : IEntityTypeConfiguration<Rule>
{
    public void Configure(EntityTypeBuilder<Rule> b)
    {
        b.Property(r => r.Name).HasMaxLength(200);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.Property(r => r.Trigger).HasJsonConversion();
        b.Property(r => r.Conditions).HasJsonConversion();
        b.Property(r => r.Actions).HasJsonConversion();
        b.HasOne<Sheet>().WithMany().HasForeignKey(r => r.SheetId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(r => new { r.SheetId, r.Enabled });
    }
}

// No foreign key to Item: the run log outlives the item it describes, like activity.
internal sealed class RuleRunConfiguration : IEntityTypeConfiguration<RuleRun>
{
    public void Configure(EntityTypeBuilder<RuleRun> b)
    {
        b.Property(r => r.Outcome).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Detail).HasMaxLength(2_000);
        b.Property(r => r.Error).HasMaxLength(2_000);
        b.HasOne<Rule>().WithMany().HasForeignKey(r => r.RuleId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(r => new { r.SheetId, r.FiredAtUtc });
        b.HasIndex(r => new { r.RuleId, r.ItemId, r.TriggerDate });
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.Property(n => n.RecipientEmail).HasMaxLength(320);
        b.Property(n => n.Kind).HasMaxLength(32);
        b.Property(n => n.Title).HasMaxLength(500);
        b.Property(n => n.Body).HasMaxLength(4_000);
        b.Property(n => n.Link).HasMaxLength(500);
        b.HasIndex(n => new { n.RecipientEmail, n.CreatedAtUtc });
        b.HasIndex(n => new { n.RecipientEmail, n.ReadAtUtc });
    }
}

internal sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> b)
    {
        b.Property(p => p.UserEmail).HasMaxLength(320);
        b.Property(p => p.Delivery).HasConversion<string>().HasMaxLength(32);
        b.HasIndex(p => p.UserEmail).IsUnique();
    }
}

internal static class JsonPropertyExtensions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Stores the property as JSON text, compared by its serialized form.</summary>
    public static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> property)
        where T : class, new()
    {
        property.HasConversion(
            v => JsonSerializer.Serialize(v, Options),
            v => JsonSerializer.Deserialize<T>(v, Options) ?? new T(),
            new ValueComparer<T>(
                (a, c) => JsonSerializer.Serialize(a, Options) == JsonSerializer.Serialize(c, Options),
                v => JsonSerializer.Serialize(v, Options).GetHashCode(),
                v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Options), Options)!));
        return property;
    }
}
