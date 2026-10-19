using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Assistant;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class AssistantSettingsConfiguration : IEntityTypeConfiguration<AssistantSettings>
{
    public void Configure(EntityTypeBuilder<AssistantSettings> b)
    {
        b.Property(s => s.Connection).HasMaxLength(64);
        b.Property(s => s.Model).HasMaxLength(128);
        b.Property(s => s.NoticeAcceptedBy).HasMaxLength(320);
        b.Property(s => s.NoticeVersion).HasMaxLength(32);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
    }
}

internal sealed class AssistantInteractionConfiguration : IEntityTypeConfiguration<AssistantInteraction>
{
    public void Configure(EntityTypeBuilder<AssistantInteraction> b)
    {
        b.Property(i => i.Kind).HasConversion<string>().HasMaxLength(24);
        b.Property(i => i.Outcome).HasConversion<string>().HasMaxLength(16);
        b.Property(i => i.UserEmail).HasMaxLength(320);
        b.Property(i => i.PagePath).HasMaxLength(500);
        b.Property(i => i.Prompt).HasMaxLength(8_000);
        b.Property(i => i.Answer).HasMaxLength(32_000);
        b.Property(i => i.OutcomeDetail).HasMaxLength(500);
        b.Property(i => i.Provider).HasMaxLength(32);
        b.Property(i => i.Model).HasMaxLength(128);
        b.Ignore(i => i.TotalTokens);
        b.HasIndex(i => i.StartedAtUtc);
        b.HasIndex(i => new { i.UserEmail, i.StartedAtUtc });
        b.HasIndex(i => i.ConversationId);
    }
}

internal sealed class AssistantToolCallConfiguration : IEntityTypeConfiguration<AssistantToolCall>
{
    public void Configure(EntityTypeBuilder<AssistantToolCall> b)
    {
        b.Property(c => c.ToolName).HasMaxLength(64);
        b.Property(c => c.Arguments).HasMaxLength(2_000);
        b.Property(c => c.Outcome).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.RecordsRead).HasMaxLength(4_000);
        b.Property(c => c.Summary).HasMaxLength(500);
        b.HasOne<AssistantInteraction>().WithMany().HasForeignKey(c => c.InteractionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(c => new { c.InteractionId, c.Sequence });
    }
}

internal sealed class ProjectNarrativeConfiguration : IEntityTypeConfiguration<ProjectNarrative>
{
    public void Configure(EntityTypeBuilder<ProjectNarrative> b)
    {
        b.Property(n => n.PeriodName).HasMaxLength(64);
        foreach (var text in new[]
                 {
                     nameof(ProjectNarrative.Summary), nameof(ProjectNarrative.CostVariance), nameof(ProjectNarrative.ScheduleVariance),
                     nameof(ProjectNarrative.EacChanges), nameof(ProjectNarrative.Accomplishments), nameof(ProjectNarrative.UpcomingMilestones),
                     nameof(ProjectNarrative.Risks),
                 })
        {
            b.Property<string?>(text).HasMaxLength(8_000);
        }

        b.Property(n => n.DraftSource).HasMaxLength(16);
        b.Property(n => n.CreatedBy).HasMaxLength(320);
        b.Property(n => n.UpdatedBy).HasMaxLength(320);
        b.HasOne<Domain.ProjectAccounting.Project>().WithMany().HasForeignKey(n => n.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Domain.ProjectAccounting.FiscalPeriod>().WithMany().HasForeignKey(n => n.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(n => new { n.ProjectId, n.FiscalPeriodId }).IsUnique();
    }
}

internal sealed class TimesheetFlagExplanationConfiguration : IEntityTypeConfiguration<TimesheetFlagExplanation>
{
    public void Configure(EntityTypeBuilder<TimesheetFlagExplanation> b)
    {
        b.Property(e => e.RuleKey).HasMaxLength(48);
        b.Property(e => e.FactsHash).HasMaxLength(64);
        b.Property(e => e.Text).HasMaxLength(4_000);
        b.Property(e => e.CreatedBy).HasMaxLength(320);
        b.HasIndex(e => new { e.TimesheetId, e.RuleKey, e.FactsHash });
    }
}
