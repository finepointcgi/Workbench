using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Collaboration;

namespace Workbench.Infrastructure.Persistence.Configurations;

// No foreign keys to Item: comments and attachments are cleaned up explicitly when an item is deleted
// (so attachment blobs can be removed too), and activity outlives the item it describes.

internal sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> b)
    {
        b.Property(c => c.AuthorEmail).HasMaxLength(320);
        b.Property(c => c.Body).HasMaxLength(10_000);
        b.HasIndex(c => new { c.SheetId, c.ItemId, c.CreatedAtUtc });
        b.Property(c => c.Mentions)
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<List<string>>(v, JsonSerializerOptions.Default) ?? new(),
                new ValueComparer<List<string>>(
                    (a, c) => a!.SequenceEqual(c!),
                    v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s)),
                    v => v.ToList()));
    }
}

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> b)
    {
        b.Property(a => a.FileName).HasMaxLength(255);
        b.Property(a => a.ContentType).HasMaxLength(127);
        b.Property(a => a.StorageKey).HasMaxLength(200);
        b.Property(a => a.UploadedBy).HasMaxLength(320);
        b.HasIndex(a => new { a.SheetId, a.ItemId });
    }
}

internal sealed class ActivityEventConfiguration : IEntityTypeConfiguration<ActivityEvent>
{
    public void Configure(EntityTypeBuilder<ActivityEvent> b)
    {
        b.Property(a => a.Actor).HasMaxLength(320);
        b.Property(a => a.Kind).HasMaxLength(64);
        b.Property(a => a.Summary).HasMaxLength(2_000);
        b.HasIndex(a => new { a.SheetId, a.OccurredAtUtc });
        b.HasIndex(a => new { a.SheetId, a.ItemId, a.OccurredAtUtc });
    }
}
