using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Kitting, backflushing, landed cost, physical inventory and finite capacity scheduling (Domain/Materials).

internal sealed class KitConfiguration : IEntityTypeConfiguration<Kit>
{
    public void Configure(EntityTypeBuilder<Kit> b)
    {
        b.Property(k => k.Number).HasMaxLength(16);
        b.Property(k => k.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(k => k.Notes).HasMaxLength(1000);
        b.Property(k => k.CreatedBy).HasMaxLength(320);
        b.Ignore(k => k.IsOpen);
        b.HasIndex(k => k.Number).IsUnique();
        b.HasIndex(k => new { k.Status, k.StagingBinId });
        b.HasIndex(k => k.WorkOrderId);
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(k => k.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(k => k.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(k => k.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(k => k.StagingWarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bin>().WithMany().HasForeignKey(k => k.StagingBinId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(k => k.Lines).WithOne().HasForeignKey(l => l.KitId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class KitLineConfiguration : IEntityTypeConfiguration<KitLine>
{
    public void Configure(EntityTypeBuilder<KitLine> b)
    {
        b.Property(l => l.QuantityRequired).Quantity();
        b.Property(l => l.QuantityStaged).Quantity();
        b.Property(l => l.QuantityIssued).Quantity();
        b.Ignore(l => l.QuantityShort);
        b.HasOne<Part>().WithMany().HasForeignKey(l => l.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkOrderComponent>().WithMany().HasForeignKey(l => l.WorkOrderComponentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProjectMaterialRequirement>().WithMany().HasForeignKey(l => l.RequirementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(l => l.WbsElementId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class KitPickConfiguration : IEntityTypeConfiguration<KitPick>
{
    public void Configure(EntityTypeBuilder<KitPick> b)
    {
        b.Property(p => p.Lot).HasMaxLength(64);
        b.Property(p => p.Serial).HasMaxLength(64);
        b.Property(p => p.Ownership).HasConversion<string>().HasMaxLength(24);
        b.Property(p => p.Quantity).Quantity();
        b.HasIndex(p => p.KitId);
        b.HasOne<Kit>().WithMany().HasForeignKey(p => p.KitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<KitLine>().WithMany().HasForeignKey(p => p.KitLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(p => p.FromWarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bin>().WithMany().HasForeignKey(p => p.FromBinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(p => p.OwnerProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<InventoryTransaction>().WithMany().HasForeignKey(p => p.TransferInTransactionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<InventoryTransaction>().WithMany().HasForeignKey(p => p.IssueTransactionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BackflushSettingConfiguration : IEntityTypeConfiguration<BackflushSetting>
{
    public void Configure(EntityTypeBuilder<BackflushSetting> b)
    {
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.HasIndex(s => new { s.PartId, s.BomId }).IsUnique();
        b.HasOne<Part>().WithMany().HasForeignKey(s => s.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BillOfMaterial>().WithMany().HasForeignKey(s => s.BomId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(s => s.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bin>().WithMany().HasForeignKey(s => s.BinId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BackflushShortageConfiguration : IEntityTypeConfiguration<BackflushShortage>
{
    public void Configure(EntityTypeBuilder<BackflushShortage> b)
    {
        b.Property(s => s.QuantityShort).Quantity();
        b.Property(s => s.Trigger).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.Message).HasMaxLength(500);
        b.Ignore(s => s.IsOpen);
        b.HasIndex(s => new { s.WorkOrderId, s.WorkOrderComponentId });
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(s => s.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkOrderComponent>().WithMany().HasForeignKey(s => s.WorkOrderComponentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Part>().WithMany().HasForeignKey(s => s.PartId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LandedChargeConfiguration : IEntityTypeConfiguration<LandedCharge>
{
    public void Configure(EntityTypeBuilder<LandedCharge> b)
    {
        b.Property(c => c.Number).HasMaxLength(16);
        b.Property(c => c.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.Method).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.Description).HasMaxLength(500);
        b.Property(c => c.Amount).Money();
        b.Property(c => c.CreatedBy).HasMaxLength(320);
        b.Property(c => c.PostedBy).HasMaxLength(320);
        b.HasIndex(c => c.Number).IsUnique();
        b.HasIndex(c => c.ApBillLineId);
        b.HasOne<ApBillLine>().WithMany().HasForeignKey(c => c.ApBillLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(c => c.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(c => c.CreditAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(c => c.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(c => c.Lines).WithOne().HasForeignKey(l => l.LandedChargeId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LandedChargeLineConfiguration : IEntityTypeConfiguration<LandedChargeLine>
{
    public void Configure(EntityTypeBuilder<LandedChargeLine> b)
    {
        b.Property(l => l.Weight).Quantity();
        b.Property(l => l.Amount).Money();
        b.Property(l => l.ToInventory).Money();
        b.Property(l => l.ToIssued).Money();
        b.Property(l => l.ToVariance).Money();
        b.HasIndex(l => l.PurchaseReceiptId);
        b.HasIndex(l => l.ReceiptTransactionId);
        b.HasOne<InventoryTransaction>().WithMany().HasForeignKey(l => l.ReceiptTransactionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseReceipt>().WithMany().HasForeignKey(l => l.PurchaseReceiptId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Part>().WithMany().HasForeignKey(l => l.PartId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PhysicalInventoryConfiguration : IEntityTypeConfiguration<PhysicalInventory>
{
    public void Configure(EntityTypeBuilder<PhysicalInventory> b)
    {
        b.Property(p => p.Number).HasMaxLength(16);
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.RecountPercent).HasPrecision(9, 2);
        b.Property(p => p.RecountValue).Money();
        b.Property(p => p.Notes).HasMaxLength(1000);
        b.Property(p => p.CreatedBy).HasMaxLength(320);
        b.Property(p => p.PostedBy).HasMaxLength(320);
        b.Ignore(p => p.IsFrozen);
        b.HasIndex(p => p.Number).IsUnique();
        b.HasIndex(p => new { p.Status, p.WarehouseId });
        b.HasOne<Warehouse>().WithMany().HasForeignKey(p => p.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(p => p.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PhysicalInventoryBinConfiguration : IEntityTypeConfiguration<PhysicalInventoryBin>
{
    public void Configure(EntityTypeBuilder<PhysicalInventoryBin> b)
    {
        b.HasIndex(x => new { x.PhysicalInventoryId, x.BinId }).IsUnique();
        b.HasOne<PhysicalInventory>().WithMany().HasForeignKey(x => x.PhysicalInventoryId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Bin>().WithMany().HasForeignKey(x => x.BinId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CountTagConfiguration : IEntityTypeConfiguration<CountTag>
{
    public void Configure(EntityTypeBuilder<CountTag> b)
    {
        b.Property(t => t.Lot).HasMaxLength(64);
        b.Property(t => t.Serial).HasMaxLength(64);
        b.Property(t => t.Ownership).HasConversion<string>().HasMaxLength(24);
        b.Property(t => t.StockStatus).HasConversion<string>().HasMaxLength(16).HasDefaultValue(InventoryStockStatus.Available);
        b.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(t => t.FrozenQuantity).Quantity();
        b.Property(t => t.UnitCost).Rate();
        b.Property(t => t.CountedQuantity).Quantity();
        b.Property(t => t.RecountQuantity).Quantity();
        b.Property(t => t.CountedBy).HasMaxLength(320);
        b.Property(t => t.RecountedBy).HasMaxLength(320);
        b.Property(t => t.Note).HasMaxLength(500);
        b.Ignore(t => t.FinalQuantity);
        b.HasIndex(t => new { t.PhysicalInventoryId, t.TagNumber }).IsUnique();
        b.HasOne<PhysicalInventory>().WithMany().HasForeignKey(t => t.PhysicalInventoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bin>().WithMany().HasForeignKey(t => t.BinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Part>().WithMany().HasForeignKey(t => t.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(t => t.OwnerProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<InventoryTransaction>().WithMany().HasForeignKey(t => t.AdjustmentTransactionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkCenterCalendarConfiguration : IEntityTypeConfiguration<WorkCenterCalendar>
{
    public void Configure(EntityTypeBuilder<WorkCenterCalendar> b)
    {
        b.Property(c => c.WorkingDays).HasMaxLength(7);
        b.Property(c => c.HoursPerDay).Hours();
        b.Property(c => c.UpdatedBy).HasMaxLength(320);
        b.HasIndex(c => c.WorkCenterId).IsUnique();
        b.HasOne<WorkCenter>().WithMany().HasForeignKey(c => c.WorkCenterId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkCenterCapacityExceptionConfiguration : IEntityTypeConfiguration<WorkCenterCapacityException>
{
    public void Configure(EntityTypeBuilder<WorkCenterCapacityException> b)
    {
        b.Property(e => e.Hours).Hours();
        b.Property(e => e.Reason).HasMaxLength(200);
        b.HasIndex(e => new { e.WorkCenterId, e.Date }).IsUnique();
        b.HasOne<WorkCenter>().WithMany().HasForeignKey(e => e.WorkCenterId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OperationScheduleConfiguration : IEntityTypeConfiguration<OperationSchedule>
{
    public void Configure(EntityTypeBuilder<OperationSchedule> b)
    {
        b.Property(s => s.Hours).Hours();
        b.Property(s => s.Problem).HasMaxLength(500);
        b.HasIndex(s => s.WorkOrderOperationId).IsUnique();
        b.HasIndex(s => new { s.WorkCenterId, s.QueuePosition });
        b.HasOne<WorkOrderOperation>().WithMany().HasForeignKey(s => s.WorkOrderOperationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(s => s.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkCenter>().WithMany().HasForeignKey(s => s.WorkCenterId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OperationLoadConfiguration : IEntityTypeConfiguration<OperationLoad>
{
    public void Configure(EntityTypeBuilder<OperationLoad> b)
    {
        b.Property(l => l.Hours).Hours();
        b.HasIndex(l => new { l.WorkCenterId, l.Date });
        b.HasIndex(l => l.OperationScheduleId);
        b.HasOne<OperationSchedule>().WithMany().HasForeignKey(l => l.OperationScheduleId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<WorkCenter>().WithMany().HasForeignKey(l => l.WorkCenterId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CapacityScheduleRunConfiguration : IEntityTypeConfiguration<CapacityScheduleRun>
{
    public void Configure(EntityTypeBuilder<CapacityScheduleRun> b)
    {
        b.Property(r => r.Direction).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Messages).HasMaxLength(4000);
        b.Property(r => r.RunBy).HasMaxLength(320);
        b.HasIndex(r => r.RunAtUtc);
    }
}
