using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain;
using Workbench.Domain.Accounting;
using Workbench.Domain.Assets;
using Workbench.Domain.Billing;
using Workbench.Domain.Cash;
using Workbench.Domain.Companies;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Multicompany. The default company is model seed data, and every CompanyId column defaults to its id, so the migration
// that adds a column puts every existing row in the default company. Foreign keys restrict deletes: companies are
// deactivated, never deleted. The company columns are configured here, beside the owning entities' own configurations.

internal sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> b)
    {
        b.ToTable("Companies");
        b.Property(c => c.Code).HasMaxLength(16);
        b.Property(c => c.LegalName).HasMaxLength(200);
        b.Property(c => c.Ein).HasMaxLength(10);
        b.Property(c => c.HomeCurrencyCode).HasMaxLength(3);
        b.Property(c => c.UpdatedBy).HasMaxLength(320);
        b.Ignore(c => c.IsDefault);
        b.HasIndex(c => c.Code).IsUnique();
        b.HasData(new
        {
            Id = Company.DefaultId,
            Code = "MAIN",
            LegalName = "Main company",
            FiscalYearStartMonth = 10,
            HomeCurrencyCode = "USD",
            IsActive = true,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
    }
}

internal sealed class IntercompanyAccountConfiguration : IEntityTypeConfiguration<IntercompanyAccount>
{
    public void Configure(EntityTypeBuilder<IntercompanyAccount> b)
    {
        b.ToTable("IntercompanyAccounts");
        b.Property(a => a.UpdatedBy).HasMaxLength(320);
        b.HasIndex(a => new { a.CompanyId, a.CounterpartyCompanyId }).IsUnique();
        b.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Company>().WithMany().HasForeignKey(a => a.CounterpartyCompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(a => a.DueFromAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(a => a.DueToAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CompanyPeriodCloseConfiguration : IEntityTypeConfiguration<CompanyPeriodClose>
{
    public void Configure(EntityTypeBuilder<CompanyPeriodClose> b)
    {
        b.ToTable("CompanyPeriodCloses");
        b.Property(c => c.ChangedBy).HasMaxLength(320);
        b.Property(c => c.Reason).HasMaxLength(1000);
        b.HasIndex(c => new { c.CompanyId, c.FiscalPeriodId }).IsUnique();
        b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(c => c.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UserCompanyAccessConfiguration : IEntityTypeConfiguration<UserCompanyAccess>
{
    public void Configure(EntityTypeBuilder<UserCompanyAccess> b)
    {
        b.ToTable("UserCompanyAccess");
        b.HasIndex(a => new { a.UserId, a.CompanyId }).IsUnique();
        b.HasOne<AppUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UserCompanySelectionConfiguration : IEntityTypeConfiguration<UserCompanySelection>
{
    public void Configure(EntityTypeBuilder<UserCompanySelection> b)
    {
        b.ToTable("UserCompanySelections");
        b.HasIndex(s => s.UserId).IsUnique();
        b.HasOne<AppUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Company>().WithMany().HasForeignKey(s => s.CompanyId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>The company column of a company-owned record: defaults to the default company, indexed, restricted.</summary>
internal abstract class CompanyColumnConfiguration<T> : IEntityTypeConfiguration<T>
    where T : Entity, ICompanyOwned
{
    public virtual void Configure(EntityTypeBuilder<T> b)
    {
        b.Property(x => x.CompanyId).HasDefaultValue(Company.DefaultId);
        b.HasIndex(x => x.CompanyId);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContractCompanyConfiguration : CompanyColumnConfiguration<Contract>;

internal sealed class ProjectCompanyConfiguration : CompanyColumnConfiguration<Project>;

internal sealed class EmployeeCompanyConfiguration : CompanyColumnConfiguration<Employee>;

internal sealed class ApBillCompanyConfiguration : CompanyColumnConfiguration<ApBill>;

internal sealed class ApPaymentRunCompanyConfiguration : CompanyColumnConfiguration<ApPaymentRun>;

internal sealed class InvoiceCompanyConfiguration : CompanyColumnConfiguration<Invoice>;

internal sealed class PaymentCompanyConfiguration : CompanyColumnConfiguration<Payment>;

internal sealed class BankAccountCompanyConfiguration : CompanyColumnConfiguration<BankAccount>;

internal sealed class FixedAssetCompanyConfiguration : CompanyColumnConfiguration<FixedAsset>;

internal sealed class IndirectPoolSetupCompanyConfiguration : CompanyColumnConfiguration<IndirectPoolSetup>;

internal sealed class JournalEntryCompanyConfiguration : CompanyColumnConfiguration<JournalEntry>;

internal sealed class JournalLineCompanyConfiguration : IEntityTypeConfiguration<JournalLine>
{
    public void Configure(EntityTypeBuilder<JournalLine> b)
    {
        b.Property(l => l.CompanyId).HasDefaultValue(Company.DefaultId);
        b.HasIndex(l => new { l.CompanyId, l.AccountId });
        b.HasOne<Company>().WithMany().HasForeignKey(l => l.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Company>().WithMany().HasForeignKey(l => l.IntercompanyCompanyId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AccountCompanyConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b) =>
        b.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);
}
