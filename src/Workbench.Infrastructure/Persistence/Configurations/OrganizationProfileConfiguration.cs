using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Company;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class OrganizationProfileConfiguration : IEntityTypeConfiguration<OrganizationProfile>
{
    public void Configure(EntityTypeBuilder<OrganizationProfile> b)
    {
        b.Property(p => p.CompanyType).HasMaxLength(40).IsRequired();
        b.Property(p => p.UpdatedBy).HasMaxLength(320);
    }
}
