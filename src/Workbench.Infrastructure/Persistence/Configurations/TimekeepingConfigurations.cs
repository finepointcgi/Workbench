using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class TimeDayRecordConfiguration : IEntityTypeConfiguration<TimeDayRecord>
{
    public void Configure(EntityTypeBuilder<TimeDayRecord> b)
    {
        b.HasIndex(d => new { d.EmployeeId, d.WorkDate }).IsUnique();
        b.HasOne<Employee>().WithMany().HasForeignKey(d => d.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Timesheet>().WithMany().HasForeignKey(d => d.TimesheetId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RunningTimerConfiguration : IEntityTypeConfiguration<RunningTimer>
{
    public void Configure(EntityTypeBuilder<RunningTimer> b)
    {
        b.HasIndex(t => t.EmployeeId).IsUnique(); // one running timer per person
        b.Property(t => t.Note).HasMaxLength(500);
        b.HasOne<Employee>().WithMany().HasForeignKey(t => t.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(t => t.WbsElementId).OnDelete(DeleteBehavior.Cascade);
    }
}
