using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Workbench.Application;
using Workbench.Domain;

namespace Workbench.Infrastructure.Persistence;

public sealed class WorkbenchDbContext(DbContextOptions<WorkbenchDbContext> options)
    : DbContext(options), IWorkbenchDbContext
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Sheet> Sheets => Set<Sheet>();
    public DbSet<Column> Columns => Set<Column>();
    public DbSet<Item> Items => Set<Item>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Workspace>(b =>
        {
            b.Property(w => w.Name).HasMaxLength(200);
            b.HasMany(w => w.Sheets).WithOne(s => s.Workspace).HasForeignKey(s => s.WorkspaceId);
        });

        modelBuilder.Entity<Sheet>(b =>
        {
            b.Property(s => s.Name).HasMaxLength(200);
            b.Property(s => s.CostpointProjectId).HasMaxLength(64);
            b.HasMany(s => s.Columns).WithOne().HasForeignKey(c => c.SheetId);
            b.HasMany(s => s.Items).WithOne().HasForeignKey(i => i.SheetId);
            b.HasIndex(s => s.UpdatedAtUtc);
        });

        modelBuilder.Entity<Column>(b =>
        {
            b.Property(c => c.Name).HasMaxLength(200);
            b.Property(c => c.Type).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<Item>(b =>
        {
            b.Property(i => i.Title).HasMaxLength(500);
            b.Property(i => i.CostpointWbsId).HasMaxLength(64);
            b.HasIndex(i => new { i.SheetId, i.ParentId, i.Order });
            b.Property(i => i.Values)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                    v => JsonSerializer.Deserialize<Dictionary<string, string?>>(v, JsonSerializerOptions.Default) ?? new(),
                    new ValueComparer<Dictionary<string, string?>>(
                        (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                        v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                        v => new Dictionary<string, string?>(v)));
        });
    }
}
