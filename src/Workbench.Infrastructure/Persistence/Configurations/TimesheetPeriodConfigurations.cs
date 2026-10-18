using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class TimesheetPeriodRuleConfiguration : IEntityTypeConfiguration<TimesheetPeriodRule>
{
    public void Configure(EntityTypeBuilder<TimesheetPeriodRule> b)
    {
        b.Property(r => r.Cadence).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.WeekStartDay).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.ChangedBy).HasMaxLength(320);
        b.Property(r => r.Reason).HasMaxLength(500);
        b.HasIndex(r => r.EffectiveFrom);
    }
}
