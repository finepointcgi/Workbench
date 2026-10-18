using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

// General ledger. Accounts and journal history are referenced by later accounting modules (indirect rates, billing,
// AP), so foreign keys restrict deletes: accounts are deactivated and posted entries are reversed, never removed.

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        b.ToTable("GlAccounts");
        b.Property(a => a.Number).HasMaxLength(32);
        b.Property(a => a.Name).HasMaxLength(200);
        b.Property(a => a.Description).HasMaxLength(1000);
        b.Property(a => a.Type).HasConversion<string>().HasMaxLength(16);
        b.Property(a => a.NormalBalance).HasConversion<string>().HasMaxLength(8);
        b.Property(a => a.CostClass).HasConversion<string>().HasMaxLength(32);
        b.HasIndex(a => a.Number).IsUnique();
        b.HasOne<Account>().WithMany().HasForeignKey(a => a.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> b)
    {
        b.ToTable("GlJournalEntries");
        b.Property(e => e.Number).HasMaxLength(16);
        b.Property(e => e.Description).HasMaxLength(500);
        b.Property(e => e.Source).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.CreatedBy).HasMaxLength(320);
        b.Property(e => e.PostedBy).HasMaxLength(320);
        b.Property(e => e.ReversalReason).HasMaxLength(1000);
        b.Ignore(e => e.TotalDebit);
        b.Ignore(e => e.TotalCredit);
        b.Ignore(e => e.IsBalanced);

        // Posted entries are numbered without gaps or duplicates; drafts all have sequence 0.
        b.HasIndex(e => e.Sequence).IsUnique().HasFilter("\"Sequence\" > 0");

        // Idempotent subledger posting: one entry per source record, revision and period. A retry that loses the race
        // fails on this index instead of double-posting. Manual entries have no source id (NULLs never collide).
        b.HasIndex(e => new { e.Source, e.SourceId, e.SourceRevision, e.FiscalPeriodId }).IsUnique();
        b.HasIndex(e => new { e.FiscalPeriodId, e.Status });
        b.HasIndex(e => e.Date);

        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(e => e.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(e => e.ReversalOfId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(e => e.Lines).WithOne().HasForeignKey(l => l.JournalEntryId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class JournalLineConfiguration : IEntityTypeConfiguration<JournalLine>
{
    public void Configure(EntityTypeBuilder<JournalLine> b)
    {
        b.ToTable("GlJournalLines");
        b.Property(l => l.Debit).Money();
        b.Property(l => l.Credit).Money();
        b.Property(l => l.Memo).HasMaxLength(500);
        b.HasIndex(l => l.AccountId);
        b.HasIndex(l => new { l.ProjectId, l.WbsElementId });
        b.HasOne<Account>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(l => l.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(l => l.WbsElementId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PostingRuleConfiguration : IEntityTypeConfiguration<PostingRule>
{
    public void Configure(EntityTypeBuilder<PostingRule> b)
    {
        b.ToTable("GlPostingRules");
        b.Property(r => r.Kind).HasConversion<string>().HasMaxLength(32);
        b.Property(r => r.Key).HasMaxLength(64);
        b.Property(r => r.UpdatedBy).HasMaxLength(320);
        b.HasIndex(r => new { r.Kind, r.Key }).IsUnique();
        b.HasOne<Account>().WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WbsCostPoolConfiguration : IEntityTypeConfiguration<WbsCostPool>
{
    public void Configure(EntityTypeBuilder<WbsCostPool> b)
    {
        b.ToTable("GlWbsCostPools");
        b.Property(p => p.Pool).HasConversion<string>().HasMaxLength(32);
        b.Property(p => p.UpdatedBy).HasMaxLength(320);
        b.HasIndex(p => p.WbsElementId).IsUnique();
        b.HasOne<WbsElement>().WithMany().HasForeignKey(p => p.WbsElementId).OnDelete(DeleteBehavior.Restrict);
    }
}
