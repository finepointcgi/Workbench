using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Costpoint;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class CpProjectConfiguration : IEntityTypeConfiguration<CpProject>
{
    public void Configure(EntityTypeBuilder<CpProject> b)
    {
        b.ToTable("CpProjects");
        b.Property(p => p.ProjectId).HasMaxLength(64);
        b.Property(p => p.Name).HasMaxLength(200);
        b.Property(p => p.OrganizationId).HasMaxLength(64);
        b.HasIndex(p => p.ProjectId).IsUnique();
    }
}

internal sealed class CpWbsElementConfiguration : IEntityTypeConfiguration<CpWbsElement>
{
    public void Configure(EntityTypeBuilder<CpWbsElement> b)
    {
        b.ToTable("CpWbsElements");
        b.Property(w => w.ProjectId).HasMaxLength(64);
        b.Property(w => w.WbsId).HasMaxLength(64);
        b.Property(w => w.ParentWbsId).HasMaxLength(64);
        b.Property(w => w.Name).HasMaxLength(200);
        b.HasIndex(w => new { w.ProjectId, w.WbsId }).IsUnique();
    }
}

internal sealed class CpEmployeeConfiguration : IEntityTypeConfiguration<CpEmployee>
{
    public void Configure(EntityTypeBuilder<CpEmployee> b)
    {
        b.ToTable("CpEmployees");
        b.Property(e => e.EmployeeId).HasMaxLength(64);
        b.Property(e => e.Name).HasMaxLength(200);
        b.Property(e => e.Email).HasMaxLength(320);
        b.Property(e => e.OrganizationId).HasMaxLength(64);
        b.Property(e => e.LaborCategoryId).HasMaxLength(64);
        b.HasIndex(e => e.EmployeeId).IsUnique();
    }
}

internal sealed class CpLaborCategoryConfiguration : IEntityTypeConfiguration<CpLaborCategory>
{
    public void Configure(EntityTypeBuilder<CpLaborCategory> b)
    {
        b.ToTable("CpLaborCategories");
        b.Property(l => l.LaborCategoryId).HasMaxLength(64);
        b.Property(l => l.Name).HasMaxLength(200);
        b.HasIndex(l => l.LaborCategoryId).IsUnique();
    }
}

internal sealed class CpBudgetConfiguration : IEntityTypeConfiguration<CpBudget>
{
    public void Configure(EntityTypeBuilder<CpBudget> b)
    {
        b.ToTable("CpBudgets");
        b.Property(x => x.ProjectId).HasMaxLength(64);
        b.Property(x => x.WbsId).HasMaxLength(64);
        b.Property(x => x.FiscalPeriod).HasMaxLength(7);
        b.Property(x => x.Hours).HasPrecision(18, 2);
        b.Property(x => x.Amount).HasPrecision(18, 2);
        b.HasIndex(x => new { x.ProjectId, x.WbsId, x.FiscalPeriod }).IsUnique();
    }
}

internal sealed class CpActualConfiguration : IEntityTypeConfiguration<CpActual>
{
    public void Configure(EntityTypeBuilder<CpActual> b)
    {
        b.ToTable("CpActuals");
        b.Property(x => x.ProjectId).HasMaxLength(64);
        b.Property(x => x.WbsId).HasMaxLength(64);
        b.Property(x => x.FiscalPeriod).HasMaxLength(7);
        b.Property(x => x.Hours).HasPrecision(18, 2);
        b.Property(x => x.Cost).HasPrecision(18, 2);
        b.HasIndex(x => new { x.ProjectId, x.WbsId, x.FiscalPeriod }).IsUnique();
    }
}

internal sealed class CpSyncRunConfiguration : IEntityTypeConfiguration<CpSyncRun>
{
    public void Configure(EntityTypeBuilder<CpSyncRun> b)
    {
        b.ToTable("CpSyncRuns");
        b.Property(r => r.Source).HasMaxLength(32);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Error).HasMaxLength(4000);
        b.HasIndex(r => r.StartedAtUtc);
    }
}
