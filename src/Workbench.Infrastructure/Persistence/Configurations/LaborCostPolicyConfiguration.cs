using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class LaborCostPolicyConfiguration : IEntityTypeConfiguration<LaborCostPolicy>
{
    public void Configure(EntityTypeBuilder<LaborCostPolicy> b)
    {
        b.Property(p => p.Method).HasConversion<string>().HasMaxLength(32);
        b.Property(p => p.Reason).HasMaxLength(500);
        b.Property(p => p.CreatedBy).HasMaxLength(320);
        b.HasIndex(p => p.EffectiveFrom);
    }
}
