using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class PartConfiguration : IEntityTypeConfiguration<Part>
{
    public void Configure(EntityTypeBuilder<Part> b)
    {
        b.Property(p => p.Number).HasMaxLength(64);
        b.Property(p => p.Revision).HasMaxLength(16);
        b.Property(p => p.Description).HasMaxLength(500);
        b.Property(p => p.UnitOfMeasure).HasMaxLength(16);
        b.Property(p => p.Source).HasConversion<string>().HasMaxLength(8);
        b.Property(p => p.Tracking).HasConversion<string>().HasMaxLength(8);
        b.Property(p => p.CostMethod).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.StandardCost).Rate();
        b.Property(p => p.AverageCost).Rate();
        b.Property(p => p.InventoryValue).Money();
        b.Property(p => p.SafetyStock).Quantity();
        b.Property(p => p.MinimumOrderQuantity).Quantity();
        b.Property(p => p.OrderMultiple).Quantity();
        b.Property(p => p.Notes).HasMaxLength(2000);
        b.Ignore(p => p.CurrentUnitCost);
        b.Ignore(p => p.Display);
        b.HasIndex(p => new { p.Number, p.Revision }).IsUnique();
        b.HasOne<Vendor>().WithMany().HasForeignKey(p => p.PreferredVendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(p => p.DefaultWarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bin>().WithMany().HasForeignKey(p => p.DefaultBinId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> b)
    {
        b.Property(w => w.Code).HasMaxLength(32);
        b.Property(w => w.Name).HasMaxLength(200);
        b.Property(w => w.Address).HasMaxLength(500);
        b.HasIndex(w => w.Code).IsUnique();
    }
}

internal sealed class BinConfiguration : IEntityTypeConfiguration<Bin>
{
    public void Configure(EntityTypeBuilder<Bin> b)
    {
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Description).HasMaxLength(200);
        b.HasIndex(x => new { x.WarehouseId, x.Code }).IsUnique();
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PartForecastConfiguration : IEntityTypeConfiguration<PartForecast>
{
    public void Configure(EntityTypeBuilder<PartForecast> b)
    {
        b.Property(f => f.Quantity).Quantity();
        b.Property(f => f.Note).HasMaxLength(500);
        b.Property(f => f.CreatedBy).HasMaxLength(320);
        b.HasIndex(f => new { f.PartId, f.Date });
        b.HasOne<Part>().WithMany().HasForeignKey(f => f.PartId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProjectMaterialRequirementConfiguration : IEntityTypeConfiguration<ProjectMaterialRequirement>
{
    public void Configure(EntityTypeBuilder<ProjectMaterialRequirement> b)
    {
        b.Property(r => r.Quantity).Quantity();
        b.Property(r => r.QuantityIssued).Quantity();
        b.Property(r => r.Note).HasMaxLength(500);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.Ignore(r => r.OpenQuantity);
        b.HasIndex(r => new { r.PartId, r.IsClosed });
        b.HasIndex(r => r.ProjectId);
        b.HasOne<Part>().WithMany().HasForeignKey(r => r.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(r => r.WbsElementId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InventoryBalanceConfiguration : IEntityTypeConfiguration<InventoryBalance>
{
    public void Configure(EntityTypeBuilder<InventoryBalance> b)
    {
        b.Property(x => x.Lot).HasMaxLength(64);
        b.Property(x => x.Serial).HasMaxLength(64);
        b.Property(x => x.Ownership).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.Quantity).Quantity();
        b.Property(x => x.GovernmentReference).HasMaxLength(100);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).HasDefaultValue(InventoryStockStatus.Available); // Existing rows are available stock.
        b.HasIndex(x => new { x.PartId, x.BinId });
        b.HasIndex(x => x.Status);
        b.HasIndex(x => new { x.Lot, x.Serial });
        b.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bin>().WithMany().HasForeignKey(x => x.BinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.OwnerProjectId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InventoryTransactionConfiguration : IEntityTypeConfiguration<InventoryTransaction>
{
    public void Configure(EntityTypeBuilder<InventoryTransaction> b)
    {
        b.Property(t => t.Number).HasMaxLength(16);
        b.Property(t => t.Type).HasConversion<string>().HasMaxLength(24);
        b.Property(t => t.Lot).HasMaxLength(64);
        b.Property(t => t.Serial).HasMaxLength(64);
        b.Property(t => t.Ownership).HasConversion<string>().HasMaxLength(24);
        b.Property(t => t.Quantity).Quantity();
        b.Property(t => t.UnitCost).Rate();
        b.Property(t => t.Amount).Money();
        b.Property(t => t.Variance).Money();
        b.Property(t => t.Reason).HasMaxLength(500);
        b.Property(t => t.Reference).HasMaxLength(100);
        b.Property(t => t.Status).HasConversion<string>().HasMaxLength(16).HasDefaultValue(InventoryStockStatus.Available);
        b.Property(t => t.CreatedBy).HasMaxLength(320);
        b.HasIndex(t => t.Number).IsUnique();
        b.HasIndex(t => new { t.PartId, t.Date });
        b.HasIndex(t => new { t.Lot, t.Serial });
        b.HasIndex(t => t.WorkOrderId);
        b.HasIndex(t => new { t.ProjectId, t.WbsElementId });
        b.HasOne<Part>().WithMany().HasForeignKey(t => t.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(t => t.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bin>().WithMany().HasForeignKey(t => t.BinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(t => t.OwnerProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(t => t.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(t => t.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(t => t.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseReceipt>().WithMany().HasForeignKey(t => t.PurchaseReceiptId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(t => t.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MaterialPurchaseLinkConfiguration : IEntityTypeConfiguration<MaterialPurchaseLink>
{
    public void Configure(EntityTypeBuilder<MaterialPurchaseLink> b)
    {
        b.Property(l => l.Ownership).HasConversion<string>().HasMaxLength(24);
        b.Property(l => l.CreatedBy).HasMaxLength(320);
        b.HasIndex(l => l.RequisitionLineId);
        b.HasIndex(l => l.PurchaseOrderLineId);
        b.HasOne<Part>().WithMany().HasForeignKey(l => l.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseRequisitionLine>().WithMany().HasForeignKey(l => l.RequisitionLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(l => l.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PlannedOrder>().WithMany().HasForeignKey(l => l.PlannedOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(l => l.OwnerProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(l => l.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bin>().WithMany().HasForeignKey(l => l.BinId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BillOfMaterialConfiguration : IEntityTypeConfiguration<BillOfMaterial>
{
    public void Configure(EntityTypeBuilder<BillOfMaterial> b)
    {
        b.Property(x => x.Revision).HasMaxLength(16);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.CreatedBy).HasMaxLength(320);
        b.HasIndex(x => new { x.PartId, x.Revision }).IsUnique();
        b.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Components).WithOne().HasForeignKey(c => c.BomId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BomComponentConfiguration : IEntityTypeConfiguration<BomComponent>
{
    public void Configure(EntityTypeBuilder<BomComponent> b)
    {
        b.Property(c => c.QuantityPer).HasPrecision(18, 6);
        b.Property(c => c.ScrapPercent).HasPrecision(9, 4);
        b.Property(c => c.ReferenceDesignators).HasMaxLength(200);
        b.HasIndex(c => c.ComponentPartId);
        b.HasOne<Part>().WithMany().HasForeignKey(c => c.ComponentPartId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkCenterConfiguration : IEntityTypeConfiguration<WorkCenter>
{
    public void Configure(EntityTypeBuilder<WorkCenter> b)
    {
        b.Property(w => w.Code).HasMaxLength(32);
        b.Property(w => w.Name).HasMaxLength(200);
        b.Property(w => w.HoursPerDay).Hours();
        b.HasIndex(w => w.Code).IsUnique();
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(w => w.LaborCategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RoutingConfiguration : IEntityTypeConfiguration<Routing>
{
    public void Configure(EntityTypeBuilder<Routing> b)
    {
        b.Property(r => r.Revision).HasMaxLength(16);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.HasIndex(r => new { r.PartId, r.Revision }).IsUnique();
        b.HasOne<Part>().WithMany().HasForeignKey(r => r.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(r => r.Operations).WithOne().HasForeignKey(o => o.RoutingId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RoutingOperationConfiguration : IEntityTypeConfiguration<RoutingOperation>
{
    public void Configure(EntityTypeBuilder<RoutingOperation> b)
    {
        b.Property(o => o.Description).HasMaxLength(200);
        b.Property(o => o.SetupHours).Hours();
        b.Property(o => o.RunHoursPerUnit).HasPrecision(9, 4);
        b.HasOne<WorkCenter>().WithMany().HasForeignKey(o => o.WorkCenterId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(o => o.LaborCategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkOrderConfiguration : IEntityTypeConfiguration<WorkOrder>
{
    public void Configure(EntityTypeBuilder<WorkOrder> b)
    {
        b.Property(w => w.Number).HasMaxLength(16);
        b.Property(w => w.Quantity).Quantity();
        b.Property(w => w.QuantityCompleted).Quantity();
        b.Property(w => w.QuantityScrapped).Quantity();
        b.Property(w => w.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(w => w.Notes).HasMaxLength(2000);
        b.Property(w => w.MaterialCost).Money();
        b.Property(w => w.LaborCost).Money();
        b.Property(w => w.ScrapCost).Money();
        b.Property(w => w.CompletedCost).Money();
        b.Property(w => w.VarianceCost).Money();
        b.Property(w => w.CreatedBy).HasMaxLength(320);
        b.Ignore(w => w.IsForProject);
        b.Ignore(w => w.QuantityOpen);
        b.Ignore(w => w.WipBalance);
        b.Ignore(w => w.IsOpen);
        b.HasIndex(w => w.Number).IsUnique();
        b.HasIndex(w => new { w.Status, w.PartId });
        b.HasOne<Part>().WithMany().HasForeignKey(w => w.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(w => w.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(w => w.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(w => w.LaborChargeWbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BillOfMaterial>().WithMany().HasForeignKey(w => w.BomId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Routing>().WithMany().HasForeignKey(w => w.RoutingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(w => w.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bin>().WithMany().HasForeignKey(w => w.BinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PlannedOrder>().WithMany().HasForeignKey(w => w.PlannedOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(w => w.Components).WithOne().HasForeignKey(c => c.WorkOrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(w => w.Operations).WithOne().HasForeignKey(o => o.WorkOrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WorkOrderComponentConfiguration : IEntityTypeConfiguration<WorkOrderComponent>
{
    public void Configure(EntityTypeBuilder<WorkOrderComponent> b)
    {
        b.Property(c => c.QuantityPer).HasPrecision(18, 6);
        b.Property(c => c.QuantityRequired).Quantity();
        b.Property(c => c.QuantityIssued).Quantity();
        b.Ignore(c => c.QuantityOpen);
        b.HasIndex(c => c.PartId);
        b.HasOne<Part>().WithMany().HasForeignKey(c => c.PartId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkOrderOperationConfiguration : IEntityTypeConfiguration<WorkOrderOperation>
{
    public void Configure(EntityTypeBuilder<WorkOrderOperation> b)
    {
        b.Property(o => o.Description).HasMaxLength(200);
        b.Property(o => o.SetupHours).Hours();
        b.Property(o => o.RunHoursPerUnit).HasPrecision(9, 4);
        b.Property(o => o.QuantityCompleted).Quantity();
        b.Property(o => o.HoursReported).Hours();
        b.HasOne<WorkCenter>().WithMany().HasForeignKey(o => o.WorkCenterId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(o => o.LaborCategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkOrderLaborReportConfiguration : IEntityTypeConfiguration<WorkOrderLaborReport>
{
    public void Configure(EntityTypeBuilder<WorkOrderLaborReport> b)
    {
        b.Property(r => r.Hours).Hours();
        b.Property(r => r.QuantityCompleted).Quantity();
        b.Property(r => r.Cost).Money();
        b.Property(r => r.Note).HasMaxLength(500);
        b.Property(r => r.ReportedBy).HasMaxLength(320);
        b.HasIndex(r => r.WorkOrderId);
        b.HasIndex(r => new { r.EmployeeId, r.WorkDate });
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(r => r.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkOrderOperation>().WithMany().HasForeignKey(r => r.OperationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(r => r.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkOrderGenealogyConfiguration : IEntityTypeConfiguration<WorkOrderGenealogy>
{
    public void Configure(EntityTypeBuilder<WorkOrderGenealogy> b)
    {
        b.HasIndex(g => g.CompletionTransactionId);
        b.HasIndex(g => g.ComponentTransactionId);
        b.HasOne<WorkOrder>().WithMany().HasForeignKey(g => g.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<InventoryTransaction>().WithMany().HasForeignKey(g => g.CompletionTransactionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<InventoryTransaction>().WithMany().HasForeignKey(g => g.ComponentTransactionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MrpRunConfiguration : IEntityTypeConfiguration<MrpRun>
{
    public void Configure(EntityTypeBuilder<MrpRun> b)
    {
        b.Property(r => r.Messages).HasMaxLength(8000);
        b.Property(r => r.RunBy).HasMaxLength(320);
        b.HasIndex(r => r.RunAtUtc);
    }
}

internal sealed class PlannedOrderConfiguration : IEntityTypeConfiguration<PlannedOrder>
{
    public void Configure(EntityTypeBuilder<PlannedOrder> b)
    {
        b.Property(p => p.Kind).HasConversion<string>().HasMaxLength(8);
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.Quantity).Quantity();
        b.Property(p => p.Pegging).HasMaxLength(2000);
        b.Property(p => p.FirmedBy).HasMaxLength(320);
        b.HasIndex(p => new { p.RunId, p.Status });
        b.HasIndex(p => p.PartId);
        b.HasOne<MrpRun>().WithMany().HasForeignKey(p => p.RunId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Part>().WithMany().HasForeignKey(p => p.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(p => p.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseRequisition>().WithMany().HasForeignKey(p => p.RequisitionId).OnDelete(DeleteBehavior.Restrict);
    }
}
