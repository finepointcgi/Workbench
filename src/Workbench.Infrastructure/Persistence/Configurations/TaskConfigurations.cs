using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain;
using Workbench.Domain.Tasks;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class ItemAssigneeConfiguration : IEntityTypeConfiguration<ItemAssignee>
{
    public void Configure(EntityTypeBuilder<ItemAssignee> b)
    {
        b.Property(a => a.Email).HasMaxLength(320);
        b.Property(a => a.AssignedBy).HasMaxLength(512);
        b.HasIndex(a => new { a.ItemId, a.Email }).IsUnique();
        b.HasIndex(a => a.Email);
        b.HasIndex(a => a.SheetId);
        b.HasOne<Item>().WithMany().HasForeignKey(a => a.ItemId).OnDelete(DeleteBehavior.Cascade);
    }
}
