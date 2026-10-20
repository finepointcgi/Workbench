using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Files;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Domain.Quality;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class InspectionPlanConfiguration : IEntityTypeConfiguration<InspectionPlan>
{
    public void Configure(EntityTypeBuilder<InspectionPlan> b)
    {
        b.ToTable("QualityInspectionPlans");
        b.Property(p => p.Code).HasMaxLength(64);
        b.Property(p => p.Name).HasMaxLength(200);
        b.Property(p => p.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.Sampling).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.Aql).HasPrecision(9, 3);
        b.Property(p => p.RequiredDocuments).HasMaxLength(1000);
        b.Property(p => p.Instructions).HasMaxLength(2000);
        b.Property(p => p.CreatedBy).HasMaxLength(320);
        b.Ignore(p => p.RequiredDocumentList);
        b.HasIndex(p => p.Code).IsUnique();
        b.HasIndex(p => new { p.PartId, p.Kind });
        b.HasOne<Part>().WithMany().HasForeignKey(p => p.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(p => p.Characteristics).WithOne().HasForeignKey(c => c.PlanId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class InspectionCharacteristicConfiguration : IEntityTypeConfiguration<InspectionCharacteristic>
{
    public void Configure(EntityTypeBuilder<InspectionCharacteristic> b)
    {
        b.ToTable("QualityInspectionCharacteristics");
        b.Property(c => c.Name).HasMaxLength(200);
        b.Property(c => c.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.Nominal).HasPrecision(18, 6);
        b.Property(c => c.LowerLimit).HasPrecision(18, 6);
        b.Property(c => c.UpperLimit).HasPrecision(18, 6);
        b.Property(c => c.Unit).HasMaxLength(16);
    }
}

internal sealed class InspectionConfiguration : IEntityTypeConfiguration<Inspection>
{
    public void Configure(EntityTypeBuilder<Inspection> b)
    {
        b.ToTable("QualityInspections");
        b.Property(i => i.Number).HasMaxLength(16);
        b.Property(i => i.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(i => i.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(i => i.Quantity).Quantity();
        b.Property(i => i.QuantityAccepted).Quantity();
        b.Property(i => i.QuantityRejected).Quantity();
        b.Property(i => i.InspectorEmail).HasMaxLength(320);
        b.Property(i => i.Notes).HasMaxLength(2000);
        b.Property(i => i.CreatedBy).HasMaxLength(320);
        b.HasIndex(i => i.Number).IsUnique();
        b.HasIndex(i => new { i.Status, i.Kind });
        b.HasIndex(i => i.PartId);
        b.HasIndex(i => i.WorkOrderId);
        b.HasIndex(i => i.VendorId);
        b.HasOne<InspectionPlan>().WithMany().HasForeignKey(i => i.PlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Part>().WithMany().HasForeignKey(i => i.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(i => i.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(i => i.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(i => i.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseReceipt>().WithMany().HasForeignKey(i => i.PurchaseReceiptId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(i => i.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Nonconformance>().WithMany().HasForeignKey(i => i.NonconformanceId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(i => i.Items).WithOne().HasForeignKey(x => x.InspectionId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(i => i.Results).WithOne().HasForeignKey(x => x.InspectionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class InspectionItemConfiguration : IEntityTypeConfiguration<InspectionItem>
{
    public void Configure(EntityTypeBuilder<InspectionItem> b)
    {
        b.ToTable("QualityInspectionItems");
        b.Property(x => x.Lot).HasMaxLength(64);
        b.Property(x => x.Serial).HasMaxLength(64);
        b.Property(x => x.Ownership).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.Quantity).Quantity();
        b.Property(x => x.QuantityAccepted).Quantity();
        b.Property(x => x.QuantityRejected).Quantity();
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bin>().WithMany().HasForeignKey(x => x.BinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.OwnerProjectId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InspectionResultConfiguration : IEntityTypeConfiguration<InspectionResult>
{
    public void Configure(EntityTypeBuilder<InspectionResult> b)
    {
        b.ToTable("QualityInspectionResults");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.MeasuredValue).HasPrecision(18, 6);
        b.Property(x => x.Note).HasMaxLength(500);
        b.HasOne<InspectionCharacteristic>().WithMany().HasForeignKey(x => x.CharacteristicId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class QualityAttachmentConfiguration : IEntityTypeConfiguration<QualityAttachment>
{
    public void Configure(EntityTypeBuilder<QualityAttachment> b)
    {
        b.ToTable("QualityAttachments");
        b.Property(a => a.OwnerType).HasMaxLength(32);
        b.Property(a => a.Kind).HasMaxLength(100);
        b.Property(a => a.FileName).HasMaxLength(255);
        b.Property(a => a.UploadedBy).HasMaxLength(320);
        b.HasIndex(a => new { a.OwnerType, a.OwnerId });
        b.HasOne<StoredFile>().WithMany().HasForeignKey(a => a.FileId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DefectCodeConfiguration : IEntityTypeConfiguration<DefectCode>
{
    public void Configure(EntityTypeBuilder<DefectCode> b)
    {
        b.ToTable("QualityDefectCodes");
        b.Property(d => d.Code).HasMaxLength(16);
        b.Property(d => d.Description).HasMaxLength(200);
        b.HasIndex(d => d.Code).IsUnique();
    }
}

internal sealed class NonconformanceConfiguration : IEntityTypeConfiguration<Nonconformance>
{
    public void Configure(EntityTypeBuilder<Nonconformance> b)
    {
        b.ToTable("QualityNonconformances");
        b.Property(n => n.Number).HasMaxLength(16);
        b.Property(n => n.Lot).HasMaxLength(64);
        b.Property(n => n.Serial).HasMaxLength(64);
        b.Property(n => n.Quantity).Quantity();
        b.Property(n => n.Source).HasConversion<string>().HasMaxLength(24);
        b.Property(n => n.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(n => n.DefectCodes).HasMaxLength(200);
        b.Property(n => n.Description).HasMaxLength(2000);
        b.Property(n => n.Disposition).HasConversion<string>().HasMaxLength(16);
        b.Property(n => n.DispositionNote).HasMaxLength(2000);
        b.Property(n => n.ValueAtCost).Money();
        b.Property(n => n.ReturnAuthorization).HasMaxLength(64);
        b.Property(n => n.SubmittedBy).HasMaxLength(320);
        b.Property(n => n.DecidedBy).HasMaxLength(320);
        b.Property(n => n.DecisionComment).HasMaxLength(1000);
        b.Property(n => n.CreatedBy).HasMaxLength(320);
        b.HasIndex(n => n.Number).IsUnique();
        b.HasIndex(n => n.Status);
        b.HasIndex(n => n.VendorId);
        b.HasIndex(n => n.PartId);
        b.HasOne<Part>().WithMany().HasForeignKey(n => n.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Inspection>().WithMany().HasForeignKey(n => n.InspectionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(n => n.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(n => n.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(n => n.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(n => n.ReworkWorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(n => n.ScrapAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(n => n.ScrapWbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(n => n.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(n => n.Items).WithOne().HasForeignKey(x => x.NonconformanceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class NonconformanceItemConfiguration : IEntityTypeConfiguration<NonconformanceItem>
{
    public void Configure(EntityTypeBuilder<NonconformanceItem> b)
    {
        b.ToTable("QualityNonconformanceItems");
        b.Property(x => x.Lot).HasMaxLength(64);
        b.Property(x => x.Serial).HasMaxLength(64);
        b.Property(x => x.Ownership).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.Quantity).Quantity();
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bin>().WithMany().HasForeignKey(x => x.BinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.OwnerProjectId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class VendorDebitMemoConfiguration : IEntityTypeConfiguration<VendorDebitMemo>
{
    public void Configure(EntityTypeBuilder<VendorDebitMemo> b)
    {
        b.ToTable("QualityVendorDebitMemos");
        b.Property(m => m.Number).HasMaxLength(16);
        b.Property(m => m.Quantity).Quantity();
        b.Property(m => m.UnitPrice).Rate();
        b.Property(m => m.Amount).Money();
        b.Property(m => m.ReturnAuthorization).HasMaxLength(64);
        b.Property(m => m.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(m => m.CreatedBy).HasMaxLength(320);
        b.Property(m => m.AppliedBy).HasMaxLength(320);
        b.Property(m => m.AppliedReference).HasMaxLength(100);
        b.HasIndex(m => m.Number).IsUnique();
        b.HasIndex(m => new { m.VendorId, m.Status });
        b.HasOne<Vendor>().WithMany().HasForeignKey(m => m.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Nonconformance>().WithMany().HasForeignKey(m => m.NonconformanceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(m => m.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Part>().WithMany().HasForeignKey(m => m.PartId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CorrectiveActionConfiguration : IEntityTypeConfiguration<CorrectiveAction>
{
    public void Configure(EntityTypeBuilder<CorrectiveAction> b)
    {
        b.ToTable("QualityCorrectiveActions");
        b.Property(c => c.Number).HasMaxLength(16);
        b.Property(c => c.Title).HasMaxLength(200);
        b.Property(c => c.Problem).HasMaxLength(2000);
        b.Property(c => c.RootCauseCategory).HasConversion<string>().HasMaxLength(24);
        b.Property(c => c.RootCause).HasMaxLength(2000);
        b.Property(c => c.Action).HasMaxLength(2000);
        b.Property(c => c.PreventiveAction).HasMaxLength(2000);
        b.Property(c => c.OwnerEmail).HasMaxLength(320);
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.VerifiedBy).HasMaxLength(320);
        b.Property(c => c.VerificationNote).HasMaxLength(1000);
        b.Property(c => c.CreatedBy).HasMaxLength(320);
        b.HasIndex(c => c.Number).IsUnique();
        b.HasIndex(c => new { c.Status, c.DueDate });
        b.HasOne<Nonconformance>().WithMany().HasForeignKey(c => c.NonconformanceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(c => c.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Part>().WithMany().HasForeignKey(c => c.PartId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EngineeringChangeConfiguration : IEntityTypeConfiguration<EngineeringChange>
{
    public void Configure(EntityTypeBuilder<EngineeringChange> b)
    {
        b.ToTable("EngineeringChanges");
        b.Property(e => e.Number).HasMaxLength(16);
        b.Property(e => e.Title).HasMaxLength(200);
        b.Property(e => e.Reason).HasConversion<string>().HasMaxLength(24);
        b.Property(e => e.Description).HasMaxLength(4000);
        b.Property(e => e.EffectivityKind).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.EffectiveSerialOrLot).HasMaxLength(64);
        b.Property(e => e.StockDisposition).HasConversion<string>().HasMaxLength(24);
        b.Property(e => e.WorkOrderDisposition).HasConversion<string>().HasMaxLength(24);
        b.Property(e => e.CostImpact).Money();
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.SubmittedBy).HasMaxLength(320);
        b.Property(e => e.DecidedBy).HasMaxLength(320);
        b.Property(e => e.DecisionComment).HasMaxLength(1000);
        b.Property(e => e.CreatedBy).HasMaxLength(320);
        b.HasIndex(e => e.Number).IsUnique();
        b.HasIndex(e => e.Status);
        b.HasMany(e => e.Items).WithOne().HasForeignKey(i => i.ChangeId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EngineeringChangeItemConfiguration : IEntityTypeConfiguration<EngineeringChangeItem>
{
    public void Configure(EntityTypeBuilder<EngineeringChangeItem> b)
    {
        b.ToTable("EngineeringChangeItems");
        b.Property(i => i.Note).HasMaxLength(500);
        b.HasIndex(i => i.PartId);
        b.HasOne<Part>().WithMany().HasForeignKey(i => i.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Part>().WithMany().HasForeignKey(i => i.NewPartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BillOfMaterial>().WithMany().HasForeignKey(i => i.FromBomId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BillOfMaterial>().WithMany().HasForeignKey(i => i.ToBomId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EngineeringChangeWorkOrderReviewConfiguration : IEntityTypeConfiguration<EngineeringChangeWorkOrderReview>
{
    public void Configure(EntityTypeBuilder<EngineeringChangeWorkOrderReview> b)
    {
        b.ToTable("EngineeringChangeWorkOrderReviews");
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.ReviewedBy).HasMaxLength(320);
        b.Property(r => r.Note).HasMaxLength(1000);
        b.HasIndex(r => new { r.ChangeId, r.WorkOrderId }).IsUnique();
        b.HasIndex(r => new { r.WorkOrderId, r.Status });
        b.HasOne<EngineeringChange>().WithMany().HasForeignKey(r => r.ChangeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(r => r.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
    }
}
