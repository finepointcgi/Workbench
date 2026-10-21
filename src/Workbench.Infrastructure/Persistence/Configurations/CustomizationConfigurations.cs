using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Customization;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class CustomFieldDefinitionConfiguration : IEntityTypeConfiguration<CustomFieldDefinition>
{
    public void Configure(EntityTypeBuilder<CustomFieldDefinition> b)
    {
        b.ToTable("CustomFieldDefinitions");
        b.Property(f => f.EntityType).HasMaxLength(64);
        b.Property(f => f.Key).HasMaxLength(64);
        b.Property(f => f.Label).HasMaxLength(100);
        b.Property(f => f.HelpText).HasMaxLength(500);
        b.Property(f => f.FieldType).HasConversion<string>().HasMaxLength(20);
        b.Property(f => f.DefaultValue).HasMaxLength(1000);
        b.Property(f => f.ValueListKey).HasMaxLength(64);
        b.Property(f => f.LinkEntityType).HasMaxLength(64);
        b.Property(f => f.UpdatedBy).HasMaxLength(320);
        b.HasIndex(f => new { f.EntityType, f.Key }).IsUnique();
    }
}

internal sealed class CustomFieldValueConfiguration : IEntityTypeConfiguration<CustomFieldValue>
{
    public void Configure(EntityTypeBuilder<CustomFieldValue> b)
    {
        b.ToTable("CustomFieldValues");
        b.Property(v => v.EntityType).HasMaxLength(64);
        b.Property(v => v.TextValue).HasMaxLength(4000);
        b.Property(v => v.NumberValue).HasPrecision(18, 4);
        b.Property(v => v.SearchText).HasMaxLength(400);
        b.Property(v => v.UpdatedBy).HasMaxLength(320);
        b.HasOne<CustomFieldDefinition>().WithMany().HasForeignKey(v => v.FieldId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(v => new { v.FieldId, v.RecordId }).IsUnique();
        b.HasIndex(v => new { v.EntityType, v.RecordId });
        b.HasIndex(v => new { v.FieldId, v.SearchText });
    }
}

internal sealed class ValueListConfiguration : IEntityTypeConfiguration<ValueList>
{
    public void Configure(EntityTypeBuilder<ValueList> b)
    {
        b.ToTable("ValueLists");
        b.Property(l => l.Key).HasMaxLength(64);
        b.Property(l => l.Name).HasMaxLength(100);
        b.Property(l => l.Description).HasMaxLength(500);
        b.Property(l => l.UpdatedBy).HasMaxLength(320);
        b.HasIndex(l => l.Key).IsUnique();
        b.HasMany(l => l.Items).WithOne().HasForeignKey(i => i.ValueListId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ValueListItemConfiguration : IEntityTypeConfiguration<ValueListItem>
{
    public void Configure(EntityTypeBuilder<ValueListItem> b)
    {
        b.ToTable("ValueListItems");
        b.Property(i => i.Code).HasMaxLength(100);
        b.Property(i => i.Label).HasMaxLength(200);
        b.HasIndex(i => new { i.ValueListId, i.Code }).IsUnique();
    }
}

internal sealed class StatusLabelConfiguration : IEntityTypeConfiguration<StatusLabel>
{
    public void Configure(EntityTypeBuilder<StatusLabel> b)
    {
        b.ToTable("StatusLabels");
        b.Property(s => s.SetKey).HasMaxLength(64);
        b.Property(s => s.Value).HasMaxLength(64);
        b.Property(s => s.Label).HasMaxLength(100);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.HasIndex(s => new { s.SetKey, s.Value }).IsUnique();
    }
}

internal sealed class SubStatusConfiguration : IEntityTypeConfiguration<SubStatus>
{
    public void Configure(EntityTypeBuilder<SubStatus> b)
    {
        b.ToTable("SubStatuses");
        b.Property(s => s.SetKey).HasMaxLength(64);
        b.Property(s => s.ParentValue).HasMaxLength(64);
        b.Property(s => s.Code).HasMaxLength(64);
        b.Property(s => s.Label).HasMaxLength(100);
        b.HasIndex(s => new { s.SetKey, s.Code }).IsUnique();
    }
}

internal sealed class RecordSubStatusConfiguration : IEntityTypeConfiguration<RecordSubStatus>
{
    public void Configure(EntityTypeBuilder<RecordSubStatus> b)
    {
        b.ToTable("RecordSubStatuses");
        b.Property(s => s.SetKey).HasMaxLength(64);
        b.Property(s => s.ParentValue).HasMaxLength(64);
        b.Property(s => s.Code).HasMaxLength(64);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.HasIndex(s => new { s.SetKey, s.RecordId }).IsUnique();
    }
}

internal sealed class NumberingSchemeConfiguration : IEntityTypeConfiguration<NumberingScheme>
{
    public void Configure(EntityTypeBuilder<NumberingScheme> b)
    {
        b.ToTable("NumberingSchemes");
        b.Property(s => s.DocumentType).HasMaxLength(64);
        b.Property(s => s.Pattern).HasMaxLength(100);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.HasIndex(s => s.DocumentType).IsUnique();
    }
}

internal sealed class NumberSequenceConfiguration : IEntityTypeConfiguration<NumberSequence>
{
    public void Configure(EntityTypeBuilder<NumberSequence> b)
    {
        b.ToTable("NumberSequences");
        b.Property(s => s.DocumentType).HasMaxLength(64);
        b.Property(s => s.ScopeKey).HasMaxLength(200);
        b.Property(s => s.Version).IsConcurrencyToken();
        b.HasIndex(s => new { s.DocumentType, s.ScopeKey }).IsUnique();
    }
}
