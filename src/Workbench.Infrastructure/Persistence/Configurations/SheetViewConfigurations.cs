using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class SheetViewConfiguration : IEntityTypeConfiguration<SheetView>
{
    public void Configure(EntityTypeBuilder<SheetView> b)
    {
        b.Property(v => v.Name).HasMaxLength(120);
        b.Property(v => v.OwnerEmail).HasMaxLength(320);
        b.Property(v => v.DefinitionJson).HasMaxLength(50_000);
        b.HasOne<Sheet>().WithMany().HasForeignKey(v => v.SheetId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(v => new { v.SheetId, v.OwnerEmail });
    }
}

internal sealed class SheetViewPreferenceConfiguration : IEntityTypeConfiguration<SheetViewPreference>
{
    public void Configure(EntityTypeBuilder<SheetViewPreference> b)
    {
        b.Property(p => p.UserEmail).HasMaxLength(320);
        b.HasOne<Sheet>().WithMany().HasForeignKey(p => p.SheetId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<SheetView>().WithMany().HasForeignKey(p => p.ViewId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(p => new { p.UserEmail, p.SheetId }).IsUnique();
    }
}
