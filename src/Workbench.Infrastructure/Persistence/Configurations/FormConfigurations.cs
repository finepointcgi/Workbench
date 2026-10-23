using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Forms;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class SheetFormConfiguration : IEntityTypeConfiguration<SheetForm>
{
    public void Configure(EntityTypeBuilder<SheetForm> b)
    {
        b.Property(f => f.Title).HasMaxLength(200);
        b.Property(f => f.Description).HasMaxLength(4000);
        b.Property(f => f.ConfirmationMessage).HasMaxLength(2000);
        b.Property(f => f.Fields).HasJsonConversion();
        b.Property(f => f.Audience).HasConversion<string>().HasMaxLength(32);
        b.Property(f => f.PublicToken).HasMaxLength(64);
        b.Property(f => f.CreatedBy).HasMaxLength(320);
        b.HasIndex(f => f.SheetId);
        b.HasIndex(f => f.PublicToken).IsUnique();
    }
}

internal sealed class FormSubmissionConfiguration : IEntityTypeConfiguration<FormSubmission>
{
    public void Configure(EntityTypeBuilder<FormSubmission> b)
    {
        b.Property(s => s.SubmittedBy).HasMaxLength(320);
        b.Property(s => s.ClientKey).HasMaxLength(64);
        b.Property(s => s.RejectedReason).HasMaxLength(64);
        b.HasIndex(s => new { s.FormId, s.SubmittedAtUtc });
    }
}
