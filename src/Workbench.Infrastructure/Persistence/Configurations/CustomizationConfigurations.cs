using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Customization;
using Workbench.Domain.Dashboards;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class FormLayoutConfiguration : IEntityTypeConfiguration<FormLayout>
{
    public void Configure(EntityTypeBuilder<FormLayout> b)
    {
        b.Property(l => l.EntityType).HasMaxLength(64);
        b.Property(l => l.RoleKey).HasMaxLength(100);
        b.Property(l => l.ScopeKey).HasMaxLength(140);
        b.Property(l => l.SectionsJson).HasMaxLength(100_000);
        b.Property(l => l.UpdatedBy).HasMaxLength(320);
        b.HasIndex(l => new { l.EntityType, l.ScopeKey }).IsUnique();
    }
}

internal sealed class SavedListViewConfiguration : IEntityTypeConfiguration<SavedListView>
{
    public void Configure(EntityTypeBuilder<SavedListView> b)
    {
        b.Property(v => v.ListKey).HasMaxLength(64);
        b.Property(v => v.Name).HasMaxLength(120);
        b.Property(v => v.OwnerEmail).HasMaxLength(320);
        b.Property(v => v.Visibility).HasConversion<string>().HasMaxLength(16);
        b.Property(v => v.RoleKey).HasMaxLength(100);
        b.Property(v => v.DefinitionJson).HasMaxLength(50_000);
        b.HasIndex(v => v.ListKey);
        b.HasIndex(v => v.OwnerEmail);
    }
}

internal sealed class ListViewPreferenceConfiguration : IEntityTypeConfiguration<ListViewPreference>
{
    public void Configure(EntityTypeBuilder<ListViewPreference> b)
    {
        b.Property(p => p.UserEmail).HasMaxLength(320);
        b.Property(p => p.ListKey).HasMaxLength(64);
        b.HasIndex(p => new { p.UserEmail, p.ListKey }).IsUnique();
    }
}

internal sealed class RoleHomePageConfiguration : IEntityTypeConfiguration<RoleHomePage>
{
    public void Configure(EntityTypeBuilder<RoleHomePage> b)
    {
        b.Property(h => h.RoleKey).HasMaxLength(100);
        b.HasIndex(h => h.RoleKey).IsUnique();
        b.HasIndex(h => h.DashboardId).IsUnique();
        b.HasOne<Dashboard>().WithMany().HasForeignKey(h => h.DashboardId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class HomePagePreferenceConfiguration : IEntityTypeConfiguration<HomePagePreference>
{
    public void Configure(EntityTypeBuilder<HomePagePreference> b)
    {
        b.Property(p => p.UserEmail).HasMaxLength(320);
        b.Property(p => p.OrderJson).HasMaxLength(20_000);
        b.Property(p => p.HiddenJson).HasMaxLength(20_000);
        b.HasIndex(p => new { p.UserEmail, p.DashboardId }).IsUnique();
    }
}
