using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain;
using Workbench.Domain.Scheduling;

namespace Workbench.Infrastructure.Persistence.Configurations;

/// <summary>
/// Both item foreign keys cascade, so deleting an item removes its links. SQLite and PostgreSQL
/// allow multiple cascade paths to the same table (SQL Server would not).
/// </summary>
internal sealed class DependencyConfiguration : IEntityTypeConfiguration<Dependency>
{
    public void Configure(EntityTypeBuilder<Dependency> b)
    {
        b.Property(d => d.Type).HasConversion<string>().HasMaxLength(32);
        b.HasOne<Sheet>().WithMany().HasForeignKey(d => d.SheetId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Item>().WithMany().HasForeignKey(d => d.PredecessorId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Item>().WithMany().HasForeignKey(d => d.SuccessorId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(d => new { d.PredecessorId, d.SuccessorId }).IsUnique();
        b.HasIndex(d => d.SuccessorId);
    }
}

internal sealed class BaselineConfiguration : IEntityTypeConfiguration<Baseline>
{
    public void Configure(EntityTypeBuilder<Baseline> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.HasOne<Sheet>().WithMany().HasForeignKey(x => x.SheetId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Entries).WithOne().HasForeignKey(e => e.BaselineId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.SheetId, x.CapturedAtUtc });
    }
}

internal sealed class BaselineEntryConfiguration : IEntityTypeConfiguration<BaselineEntry>
{
    public void Configure(EntityTypeBuilder<BaselineEntry> b)
    {
        b.HasOne<Item>().WithMany().HasForeignKey(e => e.ItemId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(e => new { e.BaselineId, e.ItemId }).IsUnique();
    }
}

internal sealed class PlanCalendarConfiguration : IEntityTypeConfiguration<PlanCalendar>
{
    public void Configure(EntityTypeBuilder<PlanCalendar> b)
    {
        b.HasOne<Sheet>().WithMany().HasForeignKey(x => x.SheetId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.SheetId).IsUnique();
        b.HasMany(x => x.Exceptions).WithOne().HasForeignKey(e => e.CalendarId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PlanCalendarExceptionConfiguration : IEntityTypeConfiguration<PlanCalendarException>
{
    public void Configure(EntityTypeBuilder<PlanCalendarException> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.HasIndex(x => new { x.CalendarId, x.Date }).IsUnique();
    }
}

/// <summary>A constraint goes with its task (and with its sheet).</summary>
internal sealed class TaskConstraintConfiguration : IEntityTypeConfiguration<TaskConstraint>
{
    public void Configure(EntityTypeBuilder<TaskConstraint> b)
    {
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
        b.HasOne<Sheet>().WithMany().HasForeignKey(x => x.SheetId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.ItemId).IsUnique();
        b.HasIndex(x => x.SheetId);
    }
}
