using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Domain.Travel;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class TravelAuthorizationConfiguration : IEntityTypeConfiguration<TravelAuthorization>
{
    public void Configure(EntityTypeBuilder<TravelAuthorization> b)
    {
        b.Property(x => x.Number).HasMaxLength(20);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Purpose).HasMaxLength(2000);
        b.Property(x => x.OtherDescription).HasMaxLength(500);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.EstimatedAirfare).Money();
        b.Property(x => x.EstimatedLodging).Money();
        b.Property(x => x.EstimatedMie).Money();
        b.Property(x => x.EstimatedRentalCar).Money();
        b.Property(x => x.EstimatedOther).Money();
        b.Property(x => x.AdvanceRequested).Money();
        b.Property(x => x.AdvanceIssuedAmount).HasPrecision(18, 2);
        b.Property(x => x.AdvanceIssuedBy).HasMaxLength(320);
        b.Property(x => x.AdvanceReference).HasMaxLength(100);
        b.Property(x => x.CreatedBy).HasMaxLength(320);
        b.Property(x => x.SubmittedBy).HasMaxLength(320);
        b.Property(x => x.ApprovedBy).HasMaxLength(320);
        b.Property(x => x.RejectedBy).HasMaxLength(320);
        b.Property(x => x.RejectionReason).HasMaxLength(1000);
        b.Property(x => x.CancelledBy).HasMaxLength(320);
        b.Property(x => x.CancellationReason).HasMaxLength(1000);
        b.Ignore(x => x.EstimatedTotal);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.EmployeeId, x.Status });
        b.HasIndex(x => x.ExpenseReportId).IsUnique();
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(x => x.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ExpenseReport>().WithMany().HasForeignKey(x => x.ExpenseReportId).OnDelete(DeleteBehavior.SetNull);
        b.HasMany(x => x.Destinations).WithOne().HasForeignKey(x => x.TravelAuthorizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TravelAuthorizationDestinationConfiguration : IEntityTypeConfiguration<TravelAuthorizationDestination>
{
    public void Configure(EntityTypeBuilder<TravelAuthorizationDestination> b)
    {
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Country).HasMaxLength(100);
        b.Property(x => x.State).HasMaxLength(100);
        b.Property(x => x.Destination).HasMaxLength(200);
        b.HasIndex(x => new { x.TravelAuthorizationId, x.Order });
    }
}

internal sealed class TravelLineCategoryConfiguration : IEntityTypeConfiguration<TravelLineCategory>
{
    public void Configure(EntityTypeBuilder<TravelLineCategory> b)
    {
        b.Property(x => x.Category).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(x => x.OdcEntryId).IsUnique();
        b.HasOne<OdcEntry>().WithMany().HasForeignKey(x => x.OdcEntryId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CardProgramConfiguration : IEntityTypeConfiguration<CardProgram>
{
    public void Configure(EntityTypeBuilder<CardProgram> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Issuer).HasMaxLength(200);
        b.Property(x => x.PaymentResponsibility).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.CreatedBy).HasMaxLength(320);
        b.HasIndex(x => x.Name).IsUnique();
        b.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.LiabilityAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CorporateCardConfiguration : IEntityTypeConfiguration<CorporateCard>
{
    public void Configure(EntityTypeBuilder<CorporateCard> b)
    {
        b.Property(x => x.Last4).HasMaxLength(4);
        b.Property(x => x.NameOnCard).HasMaxLength(200);
        b.HasIndex(x => new { x.ProgramId, x.Last4 });
        b.HasIndex(x => x.EmployeeId);
        b.HasOne<CardProgram>().WithMany().HasForeignKey(x => x.ProgramId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CardStatementImportConfiguration : IEntityTypeConfiguration<CardStatementImport>
{
    public void Configure(EntityTypeBuilder<CardStatementImport> b)
    {
        b.Property(x => x.FileName).HasMaxLength(255);
        b.Property(x => x.Format).HasMaxLength(16);
        b.Property(x => x.ImportedTotal).Money();
        b.Property(x => x.ImportedBy).HasMaxLength(320);
        b.HasIndex(x => new { x.ProgramId, x.ImportedAtUtc });
        b.HasOne<CardProgram>().WithMany().HasForeignKey(x => x.ProgramId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CardTransactionConfiguration : IEntityTypeConfiguration<CardTransaction>
{
    public void Configure(EntityTypeBuilder<CardTransaction> b)
    {
        b.Property(x => x.CardLast4).HasMaxLength(4);
        b.Property(x => x.DedupeKey).HasMaxLength(200);
        b.Property(x => x.ExternalId).HasMaxLength(200);
        b.Property(x => x.Amount).Money();
        b.Property(x => x.Merchant).HasMaxLength(300);
        b.Property(x => x.MerchantCategory).HasMaxLength(100);
        b.Property(x => x.AttachedBy).HasMaxLength(320);
        b.Property(x => x.ExcludedReason).HasMaxLength(500);
        b.Property(x => x.ExcludedBy).HasMaxLength(320);
        b.HasIndex(x => new { x.ProgramId, x.DedupeKey }).IsUnique();
        b.HasIndex(x => new { x.EmployeeId, x.TransactionDate });
        b.HasIndex(x => x.OdcEntryId);
        b.HasOne<CardProgram>().WithMany().HasForeignKey(x => x.ProgramId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CardStatementImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CorporateCard>().WithMany().HasForeignKey(x => x.CardId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OdcEntry>().WithMany().HasForeignKey(x => x.OdcEntryId).OnDelete(DeleteBehavior.SetNull);
    }
}
