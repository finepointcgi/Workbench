using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Automations;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Admin › Automation (cross-module automations). Versions and runs are history: they reference the automation by id
// without a cascading foreign key, and automations are soft-deleted.

internal sealed class AutomationRecordConfiguration : IEntityTypeConfiguration<AutomationRecord>
{
    public void Configure(EntityTypeBuilder<AutomationRecord> b)
    {
        b.ToTable("Automations");
        b.Property(a => a.Name).HasMaxLength(200);
        b.Property(a => a.Description).HasMaxLength(1000);
        b.Property(a => a.TriggerKind).HasMaxLength(32);
        b.Property(a => a.EntityType).HasMaxLength(64);
        b.Property(a => a.TriggerKeys).HasMaxLength(500);
        b.Property(a => a.TemplateKey).HasMaxLength(64);
        b.Property(a => a.CreatedBy).HasMaxLength(320);
        b.Property(a => a.UpdatedBy).HasMaxLength(320);
        b.Property(a => a.DeletedBy).HasMaxLength(320);
        b.HasIndex(a => new { a.IsEnabled, a.DeletedAtUtc });
        b.HasIndex(a => a.SheetId);
    }
}

internal sealed class AutomationVersionConfiguration : IEntityTypeConfiguration<AutomationVersion>
{
    public void Configure(EntityTypeBuilder<AutomationVersion> b)
    {
        b.ToTable("AutomationVersions");
        b.Property(v => v.Name).HasMaxLength(200);
        b.Property(v => v.CreatedBy).HasMaxLength(320);
        b.Property(v => v.Note).HasMaxLength(1000);
        b.HasIndex(v => new { v.AutomationId, v.Version }).IsUnique();
        b.HasOne<AutomationRecord>().WithMany().HasForeignKey(v => v.AutomationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AutomationQueueItemConfiguration : IEntityTypeConfiguration<AutomationQueueItem>
{
    public void Configure(EntityTypeBuilder<AutomationQueueItem> b)
    {
        b.ToTable("AutomationQueue");
        b.Property(q => q.TriggerKind).HasMaxLength(32);
        b.Property(q => q.TriggerDetail).HasMaxLength(200);
        b.Property(q => q.EntityType).HasMaxLength(64);
        b.Property(q => q.Actor).HasMaxLength(320);
        b.Property(q => q.PrincipalOverride).HasMaxLength(320);
        b.Property(q => q.Attempts).IsConcurrencyToken();
        b.Property(q => q.DedupKey).HasMaxLength(200);
        b.HasIndex(q => q.DedupKey).IsUnique();
        b.HasIndex(q => new { q.ProcessedAtUtc, q.CreatedAtUtc });
    }
}

internal sealed class AutomationRunConfiguration : IEntityTypeConfiguration<AutomationRun>
{
    public void Configure(EntityTypeBuilder<AutomationRun> b)
    {
        b.ToTable("AutomationRuns");
        b.Property(r => r.AutomationName).HasMaxLength(200);
        b.Property(r => r.TriggerKind).HasMaxLength(32);
        b.Property(r => r.TriggerDetail).HasMaxLength(200);
        b.Property(r => r.EntityType).HasMaxLength(64);
        b.Property(r => r.RecordLabel).HasMaxLength(300);
        b.Property(r => r.Actor).HasMaxLength(320);
        b.Property(r => r.Principal).HasMaxLength(320);
        b.Property(r => r.Status).HasMaxLength(32);
        b.Property(r => r.Error).HasMaxLength(2000);
        b.HasIndex(r => new { r.AutomationId, r.StartedAtUtc });
        b.HasIndex(r => new { r.AutomationId, r.RecordId, r.StartedAtUtc });
        b.HasIndex(r => r.StartedAtUtc);
    }
}

internal sealed class AutomationTaskConfiguration : IEntityTypeConfiguration<AutomationTask>
{
    public void Configure(EntityTypeBuilder<AutomationTask> b)
    {
        b.ToTable("AutomationTasks");
        b.Property(t => t.AssigneeEmail).HasMaxLength(320);
        b.Property(t => t.Title).HasMaxLength(300);
        b.Property(t => t.Notes).HasMaxLength(2000);
        b.Property(t => t.EntityType).HasMaxLength(64);
        b.Property(t => t.Link).HasMaxLength(300);
        b.Property(t => t.CompletedBy).HasMaxLength(320);
        b.HasIndex(t => new { t.AssigneeEmail, t.CompletedAtUtc });
    }
}

internal sealed class AutomationApprovalRequestConfiguration : IEntityTypeConfiguration<AutomationApprovalRequest>
{
    public void Configure(EntityTypeBuilder<AutomationApprovalRequest> b)
    {
        b.ToTable("AutomationApprovalRequests");
        b.Property(r => r.DocumentType).HasMaxLength(64);
        b.Property(r => r.EntityType).HasMaxLength(64);
        b.Property(r => r.Outcome).HasMaxLength(32);
        b.HasIndex(r => r.ApprovalInstanceId).IsUnique();
        b.HasIndex(r => new { r.DocumentType, r.RecordId });
    }
}

internal sealed class AutomationTriggerStateConfiguration : IEntityTypeConfiguration<AutomationTriggerState>
{
    public void Configure(EntityTypeBuilder<AutomationTriggerState> b)
    {
        b.ToTable("AutomationTriggerStates");
        b.Property(s => s.Key).HasMaxLength(200);
        b.HasIndex(s => new { s.AutomationId, s.Key }).IsUnique();
    }
}

internal sealed class AutomationSettingsConfiguration : IEntityTypeConfiguration<AutomationSettings>
{
    public void Configure(EntityTypeBuilder<AutomationSettings> b)
    {
        b.ToTable("AutomationSettings");
        b.Property(s => s.PausedBy).HasMaxLength(320);
    }
}
