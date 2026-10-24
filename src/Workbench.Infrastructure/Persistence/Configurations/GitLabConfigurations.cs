using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Integrations.GitLab;

namespace Workbench.Infrastructure.Persistence.Configurations;

// GitLab sync: the organization's connection, each project's link, the task <-> issue/epic/milestone links with their
// last-agreed values, people matches, conflicts, issues waiting for approval and the webhook replay ledger.
// Unlinking a project removes its rows; plan tasks are never removed by the sync.

internal sealed class GitLabConnectionConfiguration : IEntityTypeConfiguration<GitLabConnection>
{
    public void Configure(EntityTypeBuilder<GitLabConnection> b)
    {
        b.ToTable("GitLabConnections");
        b.Property(c => c.BaseUrl).HasMaxLength(500);
        b.Property(c => c.TokenProtected).HasMaxLength(4000);
        b.Property(c => c.TokenHint).HasMaxLength(8);
        b.Property(c => c.CaCertificatePem).HasMaxLength(64000);
        b.Property(c => c.ConnectedAsUsername).HasMaxLength(255);
        b.Property(c => c.ConnectedAsName).HasMaxLength(255);
        b.Property(c => c.ServerVersion).HasMaxLength(64);
        b.Property(c => c.LastTestMessage).HasMaxLength(1000);
        b.Property(c => c.CreatedBy).HasMaxLength(320);
        b.Property(c => c.UpdatedBy).HasMaxLength(320);
    }
}

internal sealed class GitLabProjectLinkConfiguration : IEntityTypeConfiguration<GitLabProjectLink>
{
    public void Configure(EntityTypeBuilder<GitLabProjectLink> b)
    {
        b.ToTable("GitLabProjectLinks");
        b.Property(l => l.GitLabProjectPath).HasMaxLength(500);
        b.Property(l => l.GitLabProjectUrl).HasMaxLength(1000);
        b.Property(l => l.GitLabGroupPath).HasMaxLength(500);
        b.Property(l => l.PhaseMode).HasConversion<string>().HasMaxLength(16);
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

internal sealed class GitLabItemLinkConfiguration : IEntityTypeConfiguration<GitLabItemLink>
{
    public void Configure(EntityTypeBuilder<GitLabItemLink> b)
    {
        b.ToTable("GitLabItemLinks");
        b.Property(l => l.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(l => l.WebUrl).HasMaxLength(1000);
        b.Property(l => l.BaseTitle).HasMaxLength(500);
        b.Property(l => l.BaseDescription).HasMaxLength(64000);
        b.Property(l => l.BaseAssignees).HasMaxLength(1000);
        b.Property(l => l.BaseState).HasMaxLength(16);
        b.Property(l => l.BaseWbsCode).HasMaxLength(100);
        b.Property(l => l.BaseEstimateHours).HasPrecision(12, 2);
        b.Property(l => l.BaseBlocks).HasMaxLength(4000);
        b.Property(l => l.PercentBeforeClose).HasPrecision(5, 1);
        b.Property(l => l.BasePercent).HasPrecision(5, 1);
        b.Property(l => l.LastError).HasMaxLength(1000);
        b.Property(l => l.Version).IsConcurrencyToken();
        b.HasIndex(l => l.ItemId).IsUnique();
        b.HasIndex(l => new { l.LinkId, l.Kind, l.GitLabId }).IsUnique();
        b.HasOne<GitLabProjectLink>().WithMany().HasForeignKey(l => l.LinkId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class GitLabUserMapConfiguration : IEntityTypeConfiguration<GitLabUserMap>
{
    public void Configure(EntityTypeBuilder<GitLabUserMap> b)
    {
        b.ToTable("GitLabUserMaps");
        b.Property(m => m.Email).HasMaxLength(320);
        b.Property(m => m.GitLabUsername).HasMaxLength(255);
        b.Property(m => m.GitLabName).HasMaxLength(255);
        b.Property(m => m.MatchedBy).HasMaxLength(16);
        b.Property(m => m.CreatedBy).HasMaxLength(320);
        b.HasIndex(m => m.Email).IsUnique();
        b.HasIndex(m => m.GitLabUserId).IsUnique();
    }
}

internal sealed class GitLabSyncConflictConfiguration : IEntityTypeConfiguration<GitLabSyncConflict>
{
    public void Configure(EntityTypeBuilder<GitLabSyncConflict> b)
    {
        b.ToTable("GitLabSyncConflicts");
        b.Property(c => c.ObjectKind).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.Kind).HasConversion<string>().HasMaxLength(24);
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.Field).HasMaxLength(32);
        b.Property(c => c.WorkbenchValue).HasMaxLength(2000);
        b.Property(c => c.GitLabValue).HasMaxLength(2000);
        b.Property(c => c.Outcome).HasMaxLength(1000);
        b.Property(c => c.Hint).HasMaxLength(1000);
        b.Property(c => c.ResolvedBy).HasMaxLength(320);
        b.Property(c => c.Resolution).HasMaxLength(500);
        b.HasIndex(c => new { c.LinkId, c.Status, c.CreatedAtUtc });
        b.HasIndex(c => new { c.ItemId, c.Field, c.Status });
        b.HasOne<GitLabProjectLink>().WithMany().HasForeignKey(c => c.LinkId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class GitLabPendingIssueConfiguration : IEntityTypeConfiguration<GitLabPendingIssue>
{
    public void Configure(EntityTypeBuilder<GitLabPendingIssue> b)
    {
        b.ToTable("GitLabPendingIssues");
        b.Property(p => p.Title).HasMaxLength(500);
        b.Property(p => p.WebUrl).HasMaxLength(1000);
        b.Property(p => p.Author).HasMaxLength(255);
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.DecidedBy).HasMaxLength(320);
        b.HasIndex(p => new { p.LinkId, p.IssueId }).IsUnique();
        b.HasOne<GitLabProjectLink>().WithMany().HasForeignKey(p => p.LinkId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class GitLabWebhookReceiptConfiguration : IEntityTypeConfiguration<GitLabWebhookReceipt>
{
    public void Configure(EntityTypeBuilder<GitLabWebhookReceipt> b)
    {
        b.ToTable("GitLabWebhookReceipts");
        b.Property(r => r.DedupKey).HasMaxLength(128);
        b.Property(r => r.EventKind).HasMaxLength(64);
        b.Property(r => r.Outcome).HasMaxLength(500);
        b.HasIndex(r => new { r.LinkId, r.DedupKey }).IsUnique();
        b.HasIndex(r => r.ReceivedAtUtc);
        b.HasOne<GitLabProjectLink>().WithMany().HasForeignKey(r => r.LinkId).OnDelete(DeleteBehavior.Cascade);
    }
}
