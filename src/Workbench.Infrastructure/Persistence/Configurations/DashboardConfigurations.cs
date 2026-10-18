using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Dashboards;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class DashboardConfiguration : IEntityTypeConfiguration<Dashboard>
{
    public void Configure(EntityTypeBuilder<Dashboard> b)
    {
        b.Property(d => d.Name).HasMaxLength(200);
        b.Property(d => d.Description).HasMaxLength(2_000);
        b.Property(d => d.OwnerEmail).HasMaxLength(320);
        b.HasIndex(d => d.OwnerEmail);
        b.HasMany(d => d.Widgets).WithOne().HasForeignKey(w => w.DashboardId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WidgetConfiguration : IEntityTypeConfiguration<Widget>
{
    public void Configure(EntityTypeBuilder<Widget> b)
    {
        b.Property(w => w.Title).HasMaxLength(200);
        b.Property(w => w.Type).HasConversion<string>().HasMaxLength(32);
        b.Property(w => w.Height).HasConversion<string>().HasMaxLength(16);
        b.Property(w => w.ConfigJson).HasMaxLength(20_000);
        b.HasIndex(w => new { w.DashboardId, w.Order });
    }
}
