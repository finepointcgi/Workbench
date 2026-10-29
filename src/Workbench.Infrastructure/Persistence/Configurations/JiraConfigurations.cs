using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Integrations.Jira;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Jira sync: the organization's connection, each project's link, the task <-> issue links with their last-agreed values,
// sync notes and the webhook replay ledger. Unlinking a project removes its rows; plan tasks are never removed by the sync.

internal sealed class JiraConnectionConfiguration : IEntityTypeConfiguration<JiraConnection>
{
    public void Configure(EntityTypeBuilder<JiraConnection> b)
    {
        b.ToTable("JiraConnections");
        b.Property(c => c.Flavor).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.SiteUrl).HasMaxLength(500);
        b.Property(c => c.AccountEmail).HasMaxLength(320);
        b.Property(c => c.TokenProtected).HasMaxLength(4000);
        b.Property(c => c.TokenHint).HasMaxLength(8);
        b.Property(c => c.ConnectedAsName).HasMaxLength(255);
        b.Property(c => c.ConnectedAsAccountId).HasMaxLength(128);
        b.Property(c => c.LastTestMessage).HasMaxLength(1000);
        b.Property(c => c.CreatedBy).HasMaxLength(320);
        b.Property(c => c.UpdatedBy).HasMaxLength(320);
    }
}

internal sealed class JiraProjectLinkConfiguration : IEntityTypeConfiguration<JiraProjectLink>
{
    public void Configure(EntityTypeBuilder<JiraProjectLink> b)
    {
        b.ToTable("JiraProjectLinks");
        b.Property(l => l.JiraProjectKey).HasMaxLength(32);
        b.Property(l => l.JiraProjectName).HasMaxLength(255);
        b.Property(l => l.JiraProjectUrl).HasMaxLength(1000);
        b.Property(l => l.EpicKey).HasMaxLength(64);
        b.Property(l => l.Jql).HasMaxLength(2000);
        b.Property(l => l.DateWinner).HasConversion<string>().HasMaxLength(16);
        b.Property(l => l.IssueType).HasMaxLength(64);
        b.Property(l => l.Status).HasConversion<string>().HasMaxLength(24);
        b.Property(l => l.WebhookSecretProtected).HasMaxLength(2000);
        b.Property(l => l.LastError).HasMaxLength(1000);
        b.Property(l => l.LastErrorHint).HasMaxLength(1000);
        b.Property(l => l.CreatedBy).HasMaxLength(320);
        b.Property(l => l.UpdatedBy).HasMaxLength(320);
        b.Property(l => l.Version).IsConcurrencyToken();
        b.HasIndex(l => l.ProjectId).IsUnique();
        b.HasIndex(l => l.SheetId);
        b.HasOne<Domain.ProjectAccounting.Project>().WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class JiraItemLinkConfiguration : IEntityTypeConfiguration<JiraItemLink>
{
    public void Configure(EntityTypeBuilder<JiraItemLink> b)
    {
        b.ToTable("JiraItemLinks");
        b.Property(l => l.IssueId).HasMaxLength(32);
        b.Property(l => l.IssueKey).HasMaxLength(64);
        b.Property(l => l.WebUrl).HasMaxLength(1000);
        b.Property(l => l.BaseTitle).HasMaxLength(500);
        b.Property(l => l.BaseAssigneeAccountId).HasMaxLength(128);
        b.Property(l => l.BaseAssigneeEmail).HasMaxLength(320);
        b.Property(l => l.JiraStatus).HasMaxLength(128);
        b.Property(l => l.EstimateHours).HasPrecision(12, 2);
        b.Property(l => l.LastError).HasMaxLength(1000);
        b.Property(l => l.Version).IsConcurrencyToken();
        b.HasIndex(l => l.ItemId).IsUnique();
        b.HasIndex(l => new { l.LinkId, l.IssueId }).IsUnique();
        b.HasOne<JiraProjectLink>().WithMany().HasForeignKey(l => l.LinkId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class JiraSyncNoteConfiguration : IEntityTypeConfiguration<JiraSyncNote>
{
    public void Configure(EntityTypeBuilder<JiraSyncNote> b)
    {
        b.ToTable("JiraSyncNotes");
        b.Property(n => n.IssueKey).HasMaxLength(64);
        b.Property(n => n.Field).HasMaxLength(32);
        b.Property(n => n.Message).HasMaxLength(1000);
        b.Property(n => n.Hint).HasMaxLength(1000);
        b.HasIndex(n => new { n.LinkId, n.IsOpen, n.CreatedAtUtc });
        b.HasIndex(n => new { n.ItemId, n.Field, n.IsOpen });
        b.HasOne<JiraProjectLink>().WithMany().HasForeignKey(n => n.LinkId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class JiraWebhookReceiptConfiguration : IEntityTypeConfiguration<JiraWebhookReceipt>
{
    public void Configure(EntityTypeBuilder<JiraWebhookReceipt> b)
    {
        b.ToTable("JiraWebhookReceipts");
        b.Property(r => r.DedupKey).HasMaxLength(128);
        b.Property(r => r.EventKind).HasMaxLength(64);
        b.Property(r => r.Outcome).HasMaxLength(500);
        b.HasIndex(r => new { r.LinkId, r.DedupKey }).IsUnique();
        b.HasIndex(r => r.ReceivedAtUtc);
        b.HasOne<JiraProjectLink>().WithMany().HasForeignKey(r => r.LinkId).OnDelete(DeleteBehavior.Cascade);
    }
}
