using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> b)
    {
        b.Property(w => w.Name).HasMaxLength(200);
        b.HasMany(w => w.Sheets).WithOne(s => s.Workspace).HasForeignKey(s => s.WorkspaceId);
    }
}

internal sealed class SheetConfiguration : IEntityTypeConfiguration<Sheet>
{
    public void Configure(EntityTypeBuilder<Sheet> b)
    {
        b.Property(s => s.Name).HasMaxLength(200);
        b.Property(s => s.CostpointProjectId).HasMaxLength(64);
        b.HasMany(s => s.Columns).WithOne().HasForeignKey(c => c.SheetId);
        b.HasMany(s => s.Items).WithOne().HasForeignKey(i => i.SheetId);
        b.HasIndex(s => s.UpdatedAtUtc);
    }
}

internal sealed class ColumnConfiguration : IEntityTypeConfiguration<Column>
{
    public void Configure(EntityTypeBuilder<Column> b)
    {
        b.Property(c => c.Name).HasMaxLength(200);
        b.Property(c => c.Type).HasConversion<string>().HasMaxLength(32);
    }
}

internal sealed class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> b)
    {
        b.Property(i => i.Title).HasMaxLength(500);
        b.Property(i => i.CostpointWbsId).HasMaxLength(64);
        b.Property(i => i.CreatedBy).HasMaxLength(320);
        b.Property(i => i.ModifiedBy).HasMaxLength(320);
        b.HasIndex(i => new { i.SheetId, i.ParentId, i.Order });
        b.Property(i => i.Values)
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<Dictionary<string, string?>>(v, JsonSerializerOptions.Default) ?? new(),
                new ValueComparer<Dictionary<string, string?>>(
                    (a, c) => a!.Count == c!.Count && !a.Except(c).Any(),
                    v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                    v => new Dictionary<string, string?>(v)));
    }
}
