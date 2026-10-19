using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Planning;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Recurring journals, corporate GL budgets and planning cycles. Foreign keys to the ledger and setup restrict deletes.

internal sealed class RecurringJournalTemplateConfiguration : IEntityTypeConfiguration<RecurringJournalTemplate>
{
    public void Configure(EntityTypeBuilder<RecurringJournalTemplate> b)
    {
        b.ToTable("GlRecurringTemplates");
        b.Property(t => t.Name).HasMaxLength(200);
        b.Property(t => t.Description).HasMaxLength(500);
        b.Property(t => t.Frequency).HasConversion<string>().HasMaxLength(16);
        b.Property(t => t.PostingDay).HasConversion<string>().HasMaxLength(16);
        b.Property(t => t.AmountMode).HasConversion<string>().HasMaxLength(16);
        b.Property(t => t.BaseAmount).Money();
        b.Property(t => t.CreatedBy).HasMaxLength(320);
        b.Property(t => t.UpdatedBy).HasMaxLength(320);
        b.HasIndex(t => t.Name).IsUnique();
        b.HasMany(t => t.Lines).WithOne().HasForeignKey(l => l.TemplateId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RecurringJournalTemplateLineConfiguration : IEntityTypeConfiguration<RecurringJournalTemplateLine>
{
    public void Configure(EntityTypeBuilder<RecurringJournalTemplateLine> b)
    {
        b.ToTable("GlRecurringTemplateLines");
        b.Property(l => l.Side).HasConversion<string>().HasMaxLength(8);
        b.Property(l => l.Amount).Money();
        b.Property(l => l.Percent).Rate();
        b.Property(l => l.Memo).HasMaxLength(500);
        b.HasOne<Account>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(l => l.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(l => l.WbsElementId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RecurringJournalRunConfiguration : IEntityTypeConfiguration<RecurringJournalRun>
{
    public void Configure(EntityTypeBuilder<RecurringJournalRun> b)
    {
        b.ToTable("GlRecurringRuns");
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Message).HasMaxLength(1000);
        b.Property(r => r.Amount).Money();
        b.Property(r => r.GeneratedBy).HasMaxLength(320);

        // One run per occurrence: a second generator racing on the same occurrence fails here instead of double-posting.
        b.HasIndex(r => new { r.TemplateId, r.Occurrence }).IsUnique();
        b.HasIndex(r => r.JournalEntryId);
        b.HasOne<RecurringJournalTemplate>().WithMany().HasForeignKey(r => r.TemplateId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(r => r.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.JournalEntryId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.ReversalEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RecurringJournalSettingsConfiguration : IEntityTypeConfiguration<RecurringJournalSettings>
{
    public void Configure(EntityTypeBuilder<RecurringJournalSettings> b)
    {
        b.ToTable("GlRecurringSettings");
        b.Property(s => s.LastAutoRunSummary).HasMaxLength(1000);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
    }
}

internal sealed class GlBudgetVersionConfiguration : IEntityTypeConfiguration<GlBudgetVersion>
{
    public void Configure(EntityTypeBuilder<GlBudgetVersion> b)
    {
        b.ToTable("GlBudgetVersions");
        b.Property(v => v.Name).HasMaxLength(200);
        b.Property(v => v.Description).HasMaxLength(1000);
        b.Property(v => v.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(v => v.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(v => v.CreatedBy).HasMaxLength(320);
        b.Property(v => v.AdoptedBy).HasMaxLength(320);
        b.Ignore(v => v.IsLocked);
        b.HasIndex(v => new { v.FiscalYearId, v.Name }).IsUnique();
        b.HasOne<FiscalYear>().WithMany().HasForeignKey(v => v.FiscalYearId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<GlBudgetVersion>().WithMany().HasForeignKey(v => v.SourceVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GlBudgetLineConfiguration : IEntityTypeConfiguration<GlBudgetLine>
{
    public void Configure(EntityTypeBuilder<GlBudgetLine> b)
    {
        b.ToTable("GlBudgetLines");
        b.Property(l => l.Amount).Money();
        b.Property(l => l.UpdatedBy).HasMaxLength(320);
        b.HasIndex(l => new { l.VersionId, l.AccountId, l.OrgUnitId, l.FiscalPeriodId }).IsUnique();
        b.HasIndex(l => new { l.VersionId, l.FiscalPeriodId });
        b.HasOne<GlBudgetVersion>().WithMany().HasForeignKey(l => l.VersionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Account>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(l => l.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(l => l.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PlanningCycleConfiguration : IEntityTypeConfiguration<PlanningCycle>
{
    public void Configure(EntityTypeBuilder<PlanningCycle> b)
    {
        b.ToTable("GlPlanningCycles");
        b.Property(c => c.Name).HasMaxLength(200);
        b.Property(c => c.Instructions).HasMaxLength(4000);
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.OpenedBy).HasMaxLength(320);
        b.Property(c => c.AdoptedBy).HasMaxLength(320);
        b.HasIndex(c => c.FiscalYearId);
        b.HasOne<FiscalYear>().WithMany().HasForeignKey(c => c.FiscalYearId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<GlBudgetVersion>().WithMany().HasForeignKey(c => c.VersionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BudgetSubmissionConfiguration : IEntityTypeConfiguration<BudgetSubmission>
{
    public void Configure(EntityTypeBuilder<BudgetSubmission> b)
    {
        b.ToTable("GlBudgetSubmissions");
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.SubmittedBy).HasMaxLength(320);
        b.Property(s => s.DecidedBy).HasMaxLength(320);
        b.Property(s => s.DecisionComment).HasMaxLength(1000);
        b.Ignore(s => s.IsEditable);
        b.HasIndex(s => new { s.CycleId, s.OrgUnitId }).IsUnique();
        b.HasIndex(s => s.OwnerEmployeeId);
        b.HasOne<PlanningCycle>().WithMany().HasForeignKey(s => s.CycleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(s => s.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(s => s.OwnerEmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.SubmissionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BudgetSubmissionLineConfiguration : IEntityTypeConfiguration<BudgetSubmissionLine>
{
    public void Configure(EntityTypeBuilder<BudgetSubmissionLine> b)
    {
        b.ToTable("GlBudgetSubmissionLines");
        b.Property(l => l.Amount).Money();
        b.HasIndex(l => new { l.SubmissionId, l.AccountId, l.FiscalPeriodId }).IsUnique();
        b.HasOne<Account>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(l => l.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
    }
}
