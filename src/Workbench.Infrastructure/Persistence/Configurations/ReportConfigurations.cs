using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Reports;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class SavedReportConfiguration : IEntityTypeConfiguration<SavedReport>
{
    public void Configure(EntityTypeBuilder<SavedReport> b)
    {
        b.ToTable("SavedReports");
        b.Property(r => r.Name).HasMaxLength(200);
        b.Property(r => r.Description).HasMaxLength(1000);
        b.Property(r => r.OwnerEmail).HasMaxLength(320);
        b.Property(r => r.SharedWith).HasMaxLength(200);
        b.Property(r => r.StandardKey).HasMaxLength(64);
        b.Property(r => r.DefinitionJson).HasMaxLength(32000);
        b.HasIndex(r => r.OwnerEmail);
    }
}

internal sealed class ReportScheduleConfiguration : IEntityTypeConfiguration<ReportSchedule>
{
    public void Configure(EntityTypeBuilder<ReportSchedule> b)
    {
        b.ToTable("ReportSchedules");
        b.Property(s => s.OwnerEmail).HasMaxLength(320);
        b.Property(s => s.Format).HasMaxLength(8);
        b.Property(s => s.AdditionalRecipients).HasMaxLength(2000);
        b.Property(s => s.LastStatus).HasMaxLength(500);
        b.HasIndex(s => new { s.Enabled, s.NextRunUtc });
        b.HasIndex(s => s.SavedReportId);
        b.HasOne<SavedReport>().WithMany().HasForeignKey(s => s.SavedReportId).OnDelete(DeleteBehavior.Restrict);
    }
}
