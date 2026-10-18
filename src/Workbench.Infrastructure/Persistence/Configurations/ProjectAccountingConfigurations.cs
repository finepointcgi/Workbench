using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Project-accounting records are referenced by time, cost and audit history, so foreign keys
// restrict deletes: setup data is closed or deactivated, never removed out from under its history.

internal static class Precision
{
    public static PropertyBuilder<decimal> Money(this PropertyBuilder<decimal> p) => p.HasPrecision(18, 2);
    public static PropertyBuilder<decimal> Rate(this PropertyBuilder<decimal> p) => p.HasPrecision(18, 4);
    public static PropertyBuilder<decimal> Hours(this PropertyBuilder<decimal> p) => p.HasPrecision(9, 2);
}

internal sealed class OrgUnitConfiguration : IEntityTypeConfiguration<OrgUnit>
{
    public void Configure(EntityTypeBuilder<OrgUnit> b)
    {
        b.Property(o => o.Code).HasMaxLength(32);
        b.Property(o => o.Name).HasMaxLength(200);
        b.HasIndex(o => o.Code).IsUnique();
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(o => o.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> b)
    {
        b.Property(e => e.EmployeeNumber).HasMaxLength(32);
        b.Property(e => e.Email).HasMaxLength(320);
        b.Property(e => e.DisplayName).HasMaxLength(200);
        b.Property(e => e.StandardHoursPerWeek).Hours();
        b.HasIndex(e => e.EmployeeNumber).IsUnique();
        b.HasIndex(e => e.Email).IsUnique();
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(e => e.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(e => e.SupervisorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(e => e.DefaultLaborCategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LaborCategoryConfiguration : IEntityTypeConfiguration<LaborCategory>
{
    public void Configure(EntityTypeBuilder<LaborCategory> b)
    {
        b.Property(l => l.Code).HasMaxLength(32);
        b.Property(l => l.Name).HasMaxLength(200);
        b.HasIndex(l => l.Code).IsUnique();
    }
}

internal sealed class EmployeeCostRateConfiguration : IEntityTypeConfiguration<EmployeeCostRate>
{
    public void Configure(EntityTypeBuilder<EmployeeCostRate> b)
    {
        b.Property(r => r.HourlyCost).Rate();
        b.HasIndex(r => new { r.EmployeeId, r.EffectiveFrom }).IsUnique();
        b.HasOne<Employee>().WithMany().HasForeignKey(r => r.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.Property(c => c.Code).HasMaxLength(32);
        b.Property(c => c.Name).HasMaxLength(200);
        b.Property(c => c.Category).HasMaxLength(64);
        b.HasIndex(c => c.Code).IsUnique();
    }
}

internal sealed class ContractConfiguration : IEntityTypeConfiguration<Contract>
{
    public void Configure(EntityTypeBuilder<Contract> b)
    {
        b.Property(c => c.ContractNumber).HasMaxLength(64);
        b.Property(c => c.Name).HasMaxLength(200);
        b.Property(c => c.Type).HasConversion<string>().HasMaxLength(32);
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.CeilingValue).Money();
        b.Property(c => c.FundedValue).Money();
        b.HasIndex(c => c.ContractNumber).IsUnique();
        b.HasOne<Customer>().WithMany().HasForeignKey(c => c.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContractLaborRateConfiguration : IEntityTypeConfiguration<ContractLaborRate>
{
    public void Configure(EntityTypeBuilder<ContractLaborRate> b)
    {
        b.Property(r => r.HourlyBillRate).Rate();
        b.HasIndex(r => new { r.ContractId, r.LaborCategoryId, r.EffectiveFrom }).IsUnique();
        b.HasOne<Contract>().WithMany().HasForeignKey(r => r.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(r => r.LaborCategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> b)
    {
        b.Property(p => p.Code).HasMaxLength(64);
        b.Property(p => p.Name).HasMaxLength(200);
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(p => p.Code).IsUnique();
        b.HasOne<Contract>().WithMany().HasForeignKey(p => p.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(p => p.ProjectManagerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Sheet>().WithMany().HasForeignKey(p => p.SheetId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class WbsElementConfiguration : IEntityTypeConfiguration<WbsElement>
{
    public void Configure(EntityTypeBuilder<WbsElement> b)
    {
        b.Property(w => w.Code).HasMaxLength(64);
        b.Property(w => w.Name).HasMaxLength(200);
        b.Property(w => w.Kind).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(w => w.Code).IsUnique();
        b.HasIndex(w => new { w.ProjectId, w.ParentId, w.Order });
        b.HasOne<Project>().WithMany().HasForeignKey(w => w.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(w => w.ParentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Item>().WithMany().HasForeignKey(w => w.ItemId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class ChargeAssignmentConfiguration : IEntityTypeConfiguration<ChargeAssignment>
{
    public void Configure(EntityTypeBuilder<ChargeAssignment> b)
    {
        b.HasIndex(a => new { a.WbsElementId, a.EmployeeId });
        b.HasOne<WbsElement>().WithMany().HasForeignKey(a => a.WbsElementId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(a => a.LaborCategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FiscalYearConfiguration : IEntityTypeConfiguration<FiscalYear>
{
    public void Configure(EntityTypeBuilder<FiscalYear> b) => b.HasIndex(y => y.Year).IsUnique();
}

internal sealed class FiscalPeriodConfiguration : IEntityTypeConfiguration<FiscalPeriod>
{
    public void Configure(EntityTypeBuilder<FiscalPeriod> b)
    {
        b.Property(p => p.Name).HasMaxLength(16);
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(p => p.Name).IsUnique();
        b.HasIndex(p => p.Start);
        b.HasOne<FiscalYear>().WithMany().HasForeignKey(p => p.FiscalYearId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TimesheetConfiguration : IEntityTypeConfiguration<Timesheet>
{
    public void Configure(EntityTypeBuilder<Timesheet> b)
    {
        b.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(t => t.RejectionReason).HasMaxLength(1000);
        b.HasIndex(t => new { t.EmployeeId, t.PeriodStart }).IsUnique();
        b.HasIndex(t => t.Status);
        b.HasOne<Employee>().WithMany().HasForeignKey(t => t.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(t => t.ApprovedById).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(t => t.Entries).WithOne().HasForeignKey(e => e.TimesheetId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TimeEntryConfiguration : IEntityTypeConfiguration<TimeEntry>
{
    public void Configure(EntityTypeBuilder<TimeEntry> b)
    {
        b.Property(e => e.Hours).Hours();
        b.Property(e => e.Comment).HasMaxLength(1000);
        b.HasIndex(e => new { e.WbsElementId, e.WorkDate });
        b.HasOne<WbsElement>().WithMany().HasForeignKey(e => e.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(e => e.LaborCategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TimeEntryRevisionConfiguration : IEntityTypeConfiguration<TimeEntryRevision>
{
    public void Configure(EntityTypeBuilder<TimeEntryRevision> b)
    {
        b.Property(r => r.OldHours).HasPrecision(9, 2);
        b.Property(r => r.NewHours).HasPrecision(9, 2);
        b.Property(r => r.ChangedBy).HasMaxLength(320);
        b.Property(r => r.Reason).HasMaxLength(1000);
        b.HasIndex(r => r.TimesheetId);
        b.HasIndex(r => r.TimeEntryId);
    }
}

internal sealed class OdcEntryConfiguration : IEntityTypeConfiguration<OdcEntry>
{
    public void Configure(EntityTypeBuilder<OdcEntry> b)
    {
        b.Property(o => o.Type).HasConversion<string>().HasMaxLength(16);
        b.Property(o => o.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(o => o.Amount).Money();
        b.Property(o => o.Description).HasMaxLength(500);
        b.Property(o => o.Vendor).HasMaxLength(200);
        b.Property(o => o.InvoiceNumber).HasMaxLength(64);
        b.Property(o => o.SubmittedBy).HasMaxLength(320);
        b.Property(o => o.ApprovedBy).HasMaxLength(320);
        b.Property(o => o.RejectionReason).HasMaxLength(1000);
        b.HasIndex(o => new { o.ProjectId, o.IncurredOn });
        b.HasIndex(o => o.Status);
        b.HasOne<Project>().WithMany().HasForeignKey(o => o.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(o => o.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(o => o.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BudgetVersionConfiguration : IEntityTypeConfiguration<BudgetVersion>
{
    public void Configure(EntityTypeBuilder<BudgetVersion> b)
    {
        b.Property(v => v.Name).HasMaxLength(200);
        b.Property(v => v.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(v => v.CreatedBy).HasMaxLength(320);
        b.Property(v => v.Description).HasMaxLength(2000);
        b.Property(v => v.LockedBy).HasMaxLength(320);
        b.HasIndex(v => new { v.ProjectId, v.Kind });
        b.HasOne<Project>().WithMany().HasForeignKey(v => v.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(v => v.Lines).WithOne().HasForeignKey(l => l.BudgetVersionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BudgetLineConfiguration : IEntityTypeConfiguration<BudgetLine>
{
    public void Configure(EntityTypeBuilder<BudgetLine> b)
    {
        b.Property(l => l.CostType).HasConversion<string>().HasMaxLength(16);
        b.Property(l => l.OdcType).HasConversion<string>().HasMaxLength(16);
        b.Property(l => l.Hours).Hours();
        b.Property(l => l.PlanningRate).Rate();
        b.Property(l => l.Amount).Money();
        b.HasIndex(l => new { l.BudgetVersionId, l.WbsElementId });
        b.HasOne<WbsElement>().WithMany().HasForeignKey(l => l.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(l => l.LaborCategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.Property(a => a.EntityType).HasMaxLength(64);
        b.Property(a => a.Action).HasMaxLength(32);
        b.Property(a => a.Actor).HasMaxLength(320);
        b.Property(a => a.Reason).HasMaxLength(1000);
        b.HasIndex(a => new { a.EntityType, a.EntityId });
        b.HasIndex(a => a.OccurredAtUtc);
    }
}
