using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Compliance;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class ProcurementFileConfiguration : IEntityTypeConfiguration<ProcurementFile>
{
    public void Configure(EntityTypeBuilder<ProcurementFile> b)
    {
        b.ToTable("ProcurementFiles");
        b.Property(f => f.Competition).HasConversion<string>().HasMaxLength(16);
        b.Property(f => f.SoleSourceJustification).HasMaxLength(4000);
        b.Property(f => f.PriceAnalysis).HasConversion<string>().HasMaxLength(16);
        b.Property(f => f.PriceAnalysisNotes).HasMaxLength(4000);
        b.Property(f => f.FlowdownClauses).HasMaxLength(2000);
        b.Property(f => f.DebarmentResult).HasMaxLength(200);
        b.Property(f => f.SmallBusinessReportPeriod).HasMaxLength(64);
        b.Property(f => f.Notes).HasMaxLength(4000);
        b.Property(f => f.UpdatedBy).HasMaxLength(320);
        b.HasIndex(f => f.PurchaseOrderId).IsUnique();
        b.HasIndex(f => f.SubcontractId).IsUnique();
        b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(f => f.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Subcontract>().WithMany().HasForeignKey(f => f.SubcontractId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(f => f.Approvals).WithOne().HasForeignKey(a => a.FileId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProcurementFileApprovalConfiguration : IEntityTypeConfiguration<ProcurementFileApproval>
{
    public void Configure(EntityTypeBuilder<ProcurementFileApproval> b)
    {
        b.ToTable("ProcurementFileApprovals");
        b.Property(a => a.LevelName).HasMaxLength(64);
        b.Property(a => a.ApprovedBy).HasMaxLength(320);
        b.Property(a => a.Comment).HasMaxLength(1000);
        b.HasIndex(a => a.FileId);
    }
}

internal sealed class DisclosureStatementConfiguration : IEntityTypeConfiguration<DisclosureStatement>
{
    public void Configure(EntityTypeBuilder<DisclosureStatement> b)
    {
        b.ToTable("DisclosureStatements");
        b.Property(s => s.Form).HasConversion<string>().HasMaxLength(8);
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.Title).HasMaxLength(200);
        b.Property(s => s.ChangeSummary).HasMaxLength(4000);
        b.Property(s => s.CreatedBy).HasMaxLength(320);
        b.Property(s => s.MadeEffectiveBy).HasMaxLength(320);
        b.HasIndex(s => new { s.Form, s.Version }).IsUnique();
        b.HasOne<DisclosureStatement>().WithMany().HasForeignKey(s => s.PreviousVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DisclosureAnswerConfiguration : IEntityTypeConfiguration<DisclosureAnswer>
{
    public void Configure(EntityTypeBuilder<DisclosureAnswer> b)
    {
        b.ToTable("DisclosureAnswers");
        b.Property(a => a.ItemKey).HasMaxLength(32);
        b.Property(a => a.Choice).HasMaxLength(64);
        b.Property(a => a.Text).HasMaxLength(8000);
        b.Property(a => a.UpdatedBy).HasMaxLength(320);
        b.HasIndex(a => new { a.StatementId, a.ItemKey }).IsUnique();
        b.HasOne<DisclosureStatement>().WithMany().HasForeignKey(a => a.StatementId).OnDelete(DeleteBehavior.Restrict);
    }
}
