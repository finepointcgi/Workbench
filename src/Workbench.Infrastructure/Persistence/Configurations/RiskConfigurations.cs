using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Risks;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class ProjectRiskConfiguration : IEntityTypeConfiguration<ProjectRisk>
{
    public void Configure(EntityTypeBuilder<ProjectRisk> b)
    {
        b.Property(r => r.Title).HasMaxLength(200);
        b.Property(r => r.Description).HasMaxLength(2000);
        b.Property(r => r.Category).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Response).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.ResponsePlan).HasMaxLength(2000);
        b.Property(r => r.CostImpact).HasPrecision(18, 2);
        b.Property(r => r.OwnerEmail).HasMaxLength(320);
        b.Property(r => r.PartRefs).HasMaxLength(500);
        b.Property(r => r.RequirementRefs).HasMaxLength(500);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.Property(r => r.UpdatedBy).HasMaxLength(320);
        b.HasIndex(r => new { r.ProjectId, r.Number }).IsUnique();
        b.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}
