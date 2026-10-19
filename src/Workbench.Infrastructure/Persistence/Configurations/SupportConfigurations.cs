using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Support;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class SupportRequestConfiguration : IEntityTypeConfiguration<SupportRequest>
{
    public void Configure(EntityTypeBuilder<SupportRequest> b)
    {
        b.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Summary).HasMaxLength(200);
        b.Property(r => r.Details).HasMaxLength(4_000);
        b.Property(r => r.PagePath).HasMaxLength(500);
        b.Property(r => r.AppVersion).HasMaxLength(64);
        b.Property(r => r.ReporterEmail).HasMaxLength(320);
        b.Property(r => r.ReporterName).HasMaxLength(200);
        b.Property(r => r.ScreenshotKey).HasMaxLength(200);
        b.Property(r => r.ScreenshotFileName).HasMaxLength(255);
        b.Property(r => r.ScreenshotContentType).HasMaxLength(100);
        b.Property(r => r.Resolution).HasMaxLength(2_000);
        b.Property(r => r.HandledBy).HasMaxLength(320);
        b.HasIndex(r => r.Number).IsUnique();
        b.HasIndex(r => r.ReporterEmail);
        b.HasIndex(r => r.Status);
    }
}

internal sealed class UserHelpStateConfiguration : IEntityTypeConfiguration<UserHelpState>
{
    public void Configure(EntityTypeBuilder<UserHelpState> b)
    {
        b.Property(s => s.UserEmail).HasMaxLength(320);
        b.Property(s => s.CompletedTours).HasMaxLength(500);
        b.Property(s => s.WhatsNewSeen).HasMaxLength(32);
        b.HasIndex(s => s.UserEmail).IsUnique();
    }
}
