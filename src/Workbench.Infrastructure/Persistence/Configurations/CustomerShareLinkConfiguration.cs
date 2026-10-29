using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class CustomerShareLinkConfiguration : IEntityTypeConfiguration<CustomerShareLink>
{
    public void Configure(EntityTypeBuilder<CustomerShareLink> b)
    {
        b.Property(l => l.TokenHash).HasMaxLength(64);
        b.Property(l => l.Label).HasMaxLength(200);
        b.Property(l => l.CreatedBy).HasMaxLength(320);
        b.Property(l => l.RevokedBy).HasMaxLength(320);
        b.HasIndex(l => l.TokenHash).IsUnique();
        b.HasIndex(l => l.ContractId);
        b.HasOne<Contract>().WithMany().HasForeignKey(l => l.ContractId).OnDelete(DeleteBehavior.Cascade);
    }
}
