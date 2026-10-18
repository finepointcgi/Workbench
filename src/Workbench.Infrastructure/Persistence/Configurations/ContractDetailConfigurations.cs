using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class ClinConfiguration : IEntityTypeConfiguration<Clin>
{
    public void Configure(EntityTypeBuilder<Clin> b)
    {
        b.Property(c => c.Number).HasMaxLength(16);
        b.Property(c => c.Description).HasMaxLength(500);
        b.Property(c => c.Pricing).HasConversion<string>().HasMaxLength(32);
        b.Property(c => c.CeilingValue).Money();
        b.Property(c => c.FundedValue).Money();
        b.Property(c => c.FeeRate).HasPrecision(9, 6);
        b.HasIndex(c => new { c.ContractId, c.Number }).IsUnique();
        b.HasOne<Contract>().WithMany().HasForeignKey(c => c.ContractId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContractModificationConfiguration : IEntityTypeConfiguration<ContractModification>
{
    public void Configure(EntityTypeBuilder<ContractModification> b)
    {
        b.Property(m => m.Number).HasMaxLength(16);
        b.Property(m => m.Description).HasMaxLength(1000);
        b.Property(m => m.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(m => m.ExecutedBy).HasMaxLength(320);
        b.HasIndex(m => new { m.ContractId, m.Number }).IsUnique();
        b.HasOne<Contract>().WithMany().HasForeignKey(m => m.ContractId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FundingActionConfiguration : IEntityTypeConfiguration<FundingAction>
{
    public void Configure(EntityTypeBuilder<FundingAction> b)
    {
        b.Property(f => f.Amount).Money();
        b.Property(f => f.Description).HasMaxLength(500);
        b.Property(f => f.CreatedBy).HasMaxLength(320);
        b.HasIndex(f => new { f.ClinId, f.EffectiveDate });
        b.HasOne<Contract>().WithMany().HasForeignKey(f => f.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Clin>().WithMany().HasForeignKey(f => f.ClinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ContractModification>().WithMany().HasForeignKey(f => f.ModificationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class VendorConfiguration : IEntityTypeConfiguration<Vendor>
{
    public void Configure(EntityTypeBuilder<Vendor> b)
    {
        b.Property(v => v.Code).HasMaxLength(32);
        b.Property(v => v.Name).HasMaxLength(200);
        b.Property(v => v.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(v => v.TaxIdLast4).HasMaxLength(4);
        b.Property(v => v.Uei).HasMaxLength(12);
        b.Property(v => v.BusinessSize).HasMaxLength(32);
        b.Property(v => v.RemitToAddress).HasMaxLength(1000);
        b.Property(v => v.ContactEmail).HasMaxLength(320);
        b.HasIndex(v => v.Code).IsUnique();
    }
}

internal sealed class SubcontractConfiguration : IEntityTypeConfiguration<Subcontract>
{
    public void Configure(EntityTypeBuilder<Subcontract> b)
    {
        b.Property(s => s.Number).HasMaxLength(64);
        b.Property(s => s.Description).HasMaxLength(500);
        b.Property(s => s.Pricing).HasConversion<string>().HasMaxLength(32);
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.CeilingValue).Money();
        b.Property(s => s.FundedValue).Money();
        b.HasIndex(s => s.Number).IsUnique();
        b.HasOne<Vendor>().WithMany().HasForeignKey(s => s.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Contract>().WithMany().HasForeignKey(s => s.PrimeContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Restrict);
    }
}
