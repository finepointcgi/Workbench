using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class SheetFormatRuleConfiguration : IEntityTypeConfiguration<SheetFormatRule>
{
    public void Configure(EntityTypeBuilder<SheetFormatRule> b)
    {
        b.Property(r => r.UpdatedBy).HasMaxLength(320);
        b.Property(r => r.DefinitionJson).HasMaxLength(20_000);
        b.HasIndex(r => new { r.SheetId, r.Order });
        b.HasOne<Sheet>().WithMany().HasForeignKey(r => r.SheetId).OnDelete(DeleteBehavior.Cascade);
    }
}
