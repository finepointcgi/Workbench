using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Licensing;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class LicenseSettingsConfiguration : IEntityTypeConfiguration<LicenseSettings>
{
    public void Configure(EntityTypeBuilder<LicenseSettings> b)
    {
        b.Property(s => s.Key).HasMaxLength(4000);
        b.Property(s => s.ActivatedBy).HasMaxLength(320);
    }
}
