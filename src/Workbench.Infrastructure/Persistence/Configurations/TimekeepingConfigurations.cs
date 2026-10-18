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
