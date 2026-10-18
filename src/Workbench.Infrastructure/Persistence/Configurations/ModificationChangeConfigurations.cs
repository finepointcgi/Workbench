using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class ContractModificationChangeConfiguration : IEntityTypeConfiguration<ContractModificationChange>
{
    public void Configure(EntityTypeBuilder<ContractModificationChange> b)
    {
        b.Property(c => c.Kind).HasConversion<string>().HasMaxLength(32);
        b.Property(c => c.ClinNumber).HasMaxLength(16);
        b.Property(c => c.Amount).HasPrecision(18, 2);
        b.Property(c => c.ClinDescription).HasMaxLength(500);
        b.Property(c => c.Pricing).HasConversion<string>().HasMaxLength(32);
        b.Property(c => c.FeeRate).HasPrecision(9, 6);
        b.Property(c => c.Note).HasMaxLength(500);
        b.HasIndex(c => new { c.ModificationId, c.Sequence });
        b.HasOne<ContractModification>().WithMany().HasForeignKey(c => c.ModificationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ClinFundingNoticeConfiguration : IEntityTypeConfiguration<ClinFundingNotice>
{
    public void Configure(EntityTypeBuilder<ClinFundingNotice> b)
    {
        b.Property(n => n.FundedValue).Money();
        b.Property(n => n.IncurredAndCommitted).Money();
        b.Property(n => n.Title).HasMaxLength(200);
        b.Property(n => n.Recipients).HasMaxLength(2000);
        b.HasIndex(n => new { n.ClinId, n.ThresholdPercent, n.FundedValue }).IsUnique();
        b.HasOne<Contract>().WithMany().HasForeignKey(n => n.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Clin>().WithMany().HasForeignKey(n => n.ClinId).OnDelete(DeleteBehavior.Restrict);
    }
}
