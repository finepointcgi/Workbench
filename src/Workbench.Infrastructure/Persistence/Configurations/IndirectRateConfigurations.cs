using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Indirect rates: pool setups and their rates. Rates are append-only (superseded, never deleted).

internal sealed class IndirectPoolSetupConfiguration : IEntityTypeConfiguration<IndirectPoolSetup>
{
    public void Configure(EntityTypeBuilder<IndirectPoolSetup> b)
    {
        b.ToTable("IndirectPoolSetups");
        b.Property(p => p.Pool).HasConversion<string>().HasMaxLength(32);
        b.Property(p => p.Base).HasConversion<string>().HasMaxLength(32);
        b.Property(p => p.Name).HasMaxLength(100);
        b.Property(p => p.CostClasses).HasMaxLength(200);
        b.Property(p => p.UpdatedBy).HasMaxLength(320);
        b.Ignore(p => p.CostClassList);
        b.HasIndex(p => p.Pool).IsUnique();
        b.HasOne<Account>().WithMany().HasForeignKey(p => p.AppliedAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(p => p.AbsorbedAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(p => p.Accounts).WithOne().HasForeignKey(a => a.PoolSetupId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class IndirectPoolAccountConfiguration : IEntityTypeConfiguration<IndirectPoolAccount>
{
    public void Configure(EntityTypeBuilder<IndirectPoolAccount> b)
    {
        b.ToTable("IndirectPoolAccounts");
        b.HasIndex(a => new { a.PoolSetupId, a.AccountId }).IsUnique();
        b.HasOne<Account>().WithMany().HasForeignKey(a => a.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class IndirectRateConfiguration : IEntityTypeConfiguration<IndirectRate>
{
    public void Configure(EntityTypeBuilder<IndirectRate> b)
    {
        b.ToTable("IndirectRates");
        b.Property(r => r.Pool).HasConversion<string>().HasMaxLength(32);
        b.Property(r => r.Basis).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Rate).HasPrecision(18, 6);
        b.Property(r => r.Note).HasMaxLength(1000);
        b.Property(r => r.ApprovedBy).HasMaxLength(320);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.Ignore(r => r.IsCurrent);
        b.HasIndex(r => new { r.FiscalYear, r.Basis, r.Pool });
    }
}
