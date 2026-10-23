using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Sheets;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class SheetReferenceConfiguration : IEntityTypeConfiguration<SheetReference>
{
    public void Configure(EntityTypeBuilder<SheetReference> b)
    {
        b.Property(r => r.Name).HasMaxLength(200);
        b.Property(r => r.Fields).HasJsonConversion();
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.HasIndex(r => r.SheetId);
        b.HasIndex(r => r.SourceSheetId);
    }
}

internal sealed class SheetTemplateConfiguration : IEntityTypeConfiguration<SheetTemplate>
{
    public void Configure(EntityTypeBuilder<SheetTemplate> b)
    {
        b.Property(t => t.Name).HasMaxLength(200);
        b.Property(t => t.Description).HasMaxLength(2000);
        b.Property(t => t.CreatedBy).HasMaxLength(320);
        b.Property(t => t.Content).HasJsonConversion();
        b.HasIndex(t => t.WorkspaceId);
    }
}

internal sealed class SheetReportConfiguration : IEntityTypeConfiguration<SheetReport>
{
    public void Configure(EntityTypeBuilder<SheetReport> b)
    {
        b.Property(r => r.Name).HasMaxLength(200);
        b.Property(r => r.OwnerEmail).HasMaxLength(320);
        b.Property(r => r.SheetIds).HasJsonConversion();
        b.Property(r => r.Columns).HasJsonConversion();
        b.Property(r => r.Filters).HasJsonConversion();
        b.Property(r => r.SortBy).HasMaxLength(200);
        b.HasIndex(r => r.OwnerEmail);
        b.HasIndex(r => r.WorkspaceId);
    }
}
