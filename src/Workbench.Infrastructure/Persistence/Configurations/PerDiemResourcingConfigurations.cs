using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.PerDiem;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class PerDiemRateBatchConfiguration : IEntityTypeConfiguration<PerDiemRateBatch>
{
    public void Configure(EntityTypeBuilder<PerDiemRateBatch> b)
    {
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Source).HasMaxLength(40);
        b.Property(x => x.FileName).HasMaxLength(255);
        b.Property(x => x.ImportedBy).HasMaxLength(320);
        b.HasIndex(x => new { x.FiscalYear, x.Kind, x.ImportedAtUtc });
    }
}

internal sealed class PerDiemRateConfiguration : IEntityTypeConfiguration<PerDiemRate>
{
    public void Configure(EntityTypeBuilder<PerDiemRate> b)
    {
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Country).HasMaxLength(100);
        b.Property(x => x.State).HasMaxLength(100);
        b.Property(x => x.Destination).HasMaxLength(200);
        b.Property(x => x.County).HasMaxLength(500);
        b.Property(x => x.LodgingMax).Money();
        b.Property(x => x.MieRate).Money();
        b.HasIndex(x => new { x.BatchId, x.State, x.Destination });
        b.HasOne<PerDiemRateBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PerDiemMieTierConfiguration : IEntityTypeConfiguration<PerDiemMieTier>
{
    public void Configure(EntityTypeBuilder<PerDiemMieTier> b)
    {
        b.Property(x => x.Total).Money();
        b.Property(x => x.Breakfast).Money();
        b.Property(x => x.Lunch).Money();
        b.Property(x => x.Dinner).Money();
        b.Property(x => x.Incidentals).Money();
        b.Property(x => x.FirstLastDay).Money();
        b.HasIndex(x => new { x.BatchId, x.Total });
        b.HasOne<PerDiemRateBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TravelPerDiemConfiguration : IEntityTypeConfiguration<TravelPerDiem>
{
    public void Configure(EntityTypeBuilder<TravelPerDiem> b)
    {
        b.Property(x => x.UpdatedBy).HasMaxLength(320);
        b.HasIndex(x => x.ExpenseReportId).IsUnique();
        b.HasOne<ExpenseReport>().WithMany().HasForeignKey(x => x.ExpenseReportId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(x => x.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Destinations).WithOne().HasForeignKey(x => x.TravelPerDiemId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Days).WithOne().HasForeignKey(x => x.TravelPerDiemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TravelDestinationConfiguration : IEntityTypeConfiguration<TravelDestination>
{
    public void Configure(EntityTypeBuilder<TravelDestination> b)
    {
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Country).HasMaxLength(100);
        b.Property(x => x.State).HasMaxLength(100);
        b.Property(x => x.Destination).HasMaxLength(200);
    }
}

internal sealed class TravelDayConfiguration : IEntityTypeConfiguration<TravelDay>
{
    public void Configure(EntityTypeBuilder<TravelDay> b)
    {
        b.Property(x => x.LodgingClaimed).Money();
        b.Property(x => x.LodgingTaxClaimed).Money();
        b.Property(x => x.MieClaimed).HasPrecision(18, 2);
        b.Property(x => x.Justification).HasMaxLength(1000);
        b.HasIndex(x => new { x.TravelPerDiemId, x.Date }).IsUnique();
    }
}

internal sealed class PerDiemLineConfiguration : IEntityTypeConfiguration<PerDiemLine>
{
    public void Configure(EntityTypeBuilder<PerDiemLine> b)
    {
        // No foreign key to OdcEntry: the expense report service deletes its lines without knowing about per diem.
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.Amount).Money();
        b.HasIndex(x => x.OdcEntryId);
        b.HasOne<TravelPerDiem>().WithMany().HasForeignKey(x => x.TravelPerDiemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ResourceAllocationConfiguration : IEntityTypeConfiguration<ResourceAllocation>
{
    public void Configure(EntityTypeBuilder<ResourceAllocation> b)
    {
        b.Property(x => x.Hours).Hours();
        b.Property(x => x.Percent).HasPrecision(9, 2);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.CreatedBy).HasMaxLength(320);
        b.Property(x => x.UpdatedBy).HasMaxLength(320);
        b.HasIndex(x => new { x.EmployeeId, x.WeekStart });
        b.HasIndex(x => new { x.ProjectId, x.WeekStart });
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(x => x.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(x => x.LaborCategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.AssignmentId);
        b.HasOne<StaffingAssignment>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StaffingAssignmentConfiguration : IEntityTypeConfiguration<StaffingAssignment>
{
    public void Configure(EntityTypeBuilder<StaffingAssignment> b)
    {
        b.Property(x => x.TotalHours).Hours();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.CreatedBy).HasMaxLength(320);
        b.Property(x => x.UpdatedBy).HasMaxLength(320);
        b.HasIndex(x => new { x.ProjectId, x.EmployeeId });
        b.HasIndex(x => new { x.EmployeeId, x.To });
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(x => x.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(x => x.LaborCategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}
