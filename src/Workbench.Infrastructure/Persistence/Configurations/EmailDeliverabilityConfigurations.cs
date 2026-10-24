using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Notifications;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class EmailAddressIssueConfiguration : IEntityTypeConfiguration<EmailAddressIssue>
{
    public void Configure(EntityTypeBuilder<EmailAddressIssue> b)
    {
        b.Property(i => i.Address).HasMaxLength(320);
        b.Property(i => i.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(i => i.Provider).HasMaxLength(16);
        b.Property(i => i.Detail).HasMaxLength(500);
        b.Property(i => i.ClearedBy).HasMaxLength(320);
        b.HasIndex(i => i.Address).IsUnique();
    }
}
