using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Assets;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class AssetCategoryConfiguration : IEntityTypeConfiguration<AssetCategory>
{
    public void Configure(EntityTypeBuilder<AssetCategory> b)
    {
        b.Property(c => c.Code).HasMaxLength(32);
        b.Property(c => c.Name).HasMaxLength(200);
        b.Property(c => c.DefaultMethod).HasConversion<string>().HasMaxLength(24);
        b.HasIndex(c => c.Code).IsUnique();
        b.HasOne<Account>().WithMany().HasForeignKey(c => c.AssetAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(c => c.AccumulatedDepreciationAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(c => c.ExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FixedAssetConfiguration : IEntityTypeConfiguration<FixedAsset>
{
    public void Configure(EntityTypeBuilder<FixedAsset> b)
    {
        b.Property(a => a.TagNumber).HasMaxLength(32);
        b.Property(a => a.Description).HasMaxLength(500);
        b.Property(a => a.Cost).Money();
        b.Property(a => a.SalvageValue).Money();
        b.Property(a => a.AccumulatedDepreciation).Money();
        b.Property(a => a.DisposalProceeds).HasPrecision(18, 2);
        b.Property(a => a.Method).HasConversion<string>().HasMaxLength(24);
        b.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(a => a.Location).HasMaxLength(200);
        b.Property(a => a.SerialNumber).HasMaxLength(100);
        b.Property(a => a.GovernmentPropertyNumber).HasMaxLength(64);
        b.Property(a => a.DisposalReason).HasMaxLength(1000);
        b.Property(a => a.CreatedBy).HasMaxLength(320);
        b.Ignore(a => a.IsGovernmentProperty);
        b.Ignore(a => a.NetBookValue);
        b.HasIndex(a => a.TagNumber).IsUnique();
        b.HasIndex(a => a.SourceBillLineId);
        b.HasIndex(a => a.Status);
        b.HasOne<AssetCategory>().WithMany().HasForeignKey(a => a.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(a => a.AssetAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(a => a.AccumulatedDepreciationAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(a => a.ExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(a => a.CustodianEmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(a => a.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(a => a.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Contract>().WithMany().HasForeignKey(a => a.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(a => a.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApBillLine>().WithMany().HasForeignKey(a => a.SourceBillLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(a => a.CapitalizationEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(a => a.DisposalEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AssetDepreciationRunConfiguration : IEntityTypeConfiguration<AssetDepreciationRun>
{
    public void Configure(EntityTypeBuilder<AssetDepreciationRun> b)
    {
        b.Property(r => r.Total).Money();
        b.Property(r => r.RunBy).HasMaxLength(320);
        b.HasIndex(r => r.FiscalPeriodId);
        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(r => r.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AssetDepreciationConfiguration : IEntityTypeConfiguration<AssetDepreciation>
{
    public void Configure(EntityTypeBuilder<AssetDepreciation> b)
    {
        b.Property(d => d.Amount).Money();
        b.HasIndex(d => new { d.AssetId, d.FiscalPeriodId }).IsUnique();
        b.HasIndex(d => d.FiscalPeriodId);
        b.HasOne<FixedAsset>().WithMany().HasForeignKey(d => d.AssetId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(d => d.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AssetDepreciationRun>().WithMany().HasForeignKey(d => d.RunId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AssetTransferConfiguration : IEntityTypeConfiguration<AssetTransfer>
{
    public void Configure(EntityTypeBuilder<AssetTransfer> b)
    {
        b.Property(t => t.FromLocation).HasMaxLength(200);
        b.Property(t => t.ToLocation).HasMaxLength(200);
        b.Property(t => t.Reason).HasMaxLength(1000);
        b.Property(t => t.TransferredBy).HasMaxLength(320);
        b.HasIndex(t => t.AssetId);
        b.HasOne<FixedAsset>().WithMany().HasForeignKey(t => t.AssetId).OnDelete(DeleteBehavior.Restrict);
    }
}
