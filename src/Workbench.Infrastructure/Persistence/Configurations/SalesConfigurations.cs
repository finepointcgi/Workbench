using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class SalesOrderConfiguration : IEntityTypeConfiguration<SalesOrder>
{
    public void Configure(EntityTypeBuilder<SalesOrder> b)
    {
        b.Property(o => o.Number).HasMaxLength(16);
        b.Property(o => o.CustomerReference).HasMaxLength(64);
        b.Property(o => o.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(o => o.HoldReason).HasMaxLength(500);
        b.Property(o => o.ShipToName).HasMaxLength(200);
        b.Property(o => o.ShipToAddress).HasMaxLength(500);
        b.Property(o => o.ShipToCode).HasMaxLength(16);
        b.Property(o => o.MarkForCode).HasMaxLength(16);
        b.Property(o => o.IssuedByCode).HasMaxLength(16);
        b.Property(o => o.InspectByCode).HasMaxLength(16);
        b.Property(o => o.AcceptByCode).HasMaxLength(16);
        b.Property(o => o.InspectionPoint).HasConversion<string>().HasMaxLength(16);
        b.Property(o => o.AcceptancePoint).HasConversion<string>().HasMaxLength(16);
        b.Property(o => o.Notes).HasMaxLength(2000);
        b.Property(o => o.CreatedBy).HasMaxLength(320);
        b.Property(o => o.ReleasedBy).HasMaxLength(320);
        b.Property(o => o.CancelReason).HasMaxLength(500);
        b.Ignore(o => o.IsHeld);
        b.HasIndex(o => o.Number).IsUnique();
        b.HasIndex(o => new { o.Status, o.OrderDate });
        b.HasIndex(o => o.ContractId);
        b.HasOne<Customer>().WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Contract>().WithMany().HasForeignKey(o => o.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(o => o.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(o => o.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.SalesOrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SalesOrderCompanyConfiguration : CompanyColumnConfiguration<SalesOrder>;

internal sealed class ShipmentCompanyConfiguration : CompanyColumnConfiguration<Shipment>;

internal sealed class SalesOrderLineConfiguration : IEntityTypeConfiguration<SalesOrderLine>
{
    public void Configure(EntityTypeBuilder<SalesOrderLine> b)
    {
        b.Property(l => l.ItemNumber).HasMaxLength(16);
        b.Property(l => l.Description).HasMaxLength(500);
        b.Property(l => l.QuantityOrdered).Quantity();
        b.Property(l => l.QuantityShipped).Quantity();
        b.Property(l => l.UnitPrice).Rate();
        b.Property(l => l.ShipToName).HasMaxLength(200);
        b.Property(l => l.ShipToAddress).HasMaxLength(500);
        b.Property(l => l.ShipToCode).HasMaxLength(16);
        b.Property(l => l.StockNumber).HasMaxLength(32);
        b.Ignore(l => l.QuantityOpen);
        b.Ignore(l => l.Amount);
        b.HasIndex(l => new { l.SalesOrderId, l.LineNumber }).IsUnique();
        b.HasIndex(l => l.PartId);
        b.HasOne<Part>().WithMany().HasForeignKey(l => l.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Clin>().WithMany().HasForeignKey(l => l.ClinId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PickListConfiguration : IEntityTypeConfiguration<PickList>
{
    public void Configure(EntityTypeBuilder<PickList> b)
    {
        b.Property(p => p.Number).HasMaxLength(16);
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.CreatedBy).HasMaxLength(320);
        b.HasIndex(p => p.Number).IsUnique();
        b.HasIndex(p => new { p.SalesOrderId, p.Status });
        b.HasOne<SalesOrder>().WithMany().HasForeignKey(p => p.SalesOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(p => p.Lines).WithOne().HasForeignKey(l => l.PickListId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PickListLineConfiguration : IEntityTypeConfiguration<PickListLine>
{
    public void Configure(EntityTypeBuilder<PickListLine> b)
    {
        b.Property(l => l.Lot).HasMaxLength(64);
        b.Property(l => l.Serial).HasMaxLength(64);
        b.Property(l => l.Ownership).HasConversion<string>().HasMaxLength(24);
        b.Property(l => l.Quantity).Quantity();
        b.Property(l => l.QuantityPicked).Quantity();
        b.Property(l => l.PickedBy).HasMaxLength(320);
        b.HasIndex(l => l.SalesOrderLineId);
        b.HasIndex(l => new { l.PartId, l.BinId });
        b.HasOne<SalesOrderLine>().WithMany().HasForeignKey(l => l.SalesOrderLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Part>().WithMany().HasForeignKey(l => l.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(l => l.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bin>().WithMany().HasForeignKey(l => l.BinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(l => l.OwnerProjectId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ShipmentConfiguration : IEntityTypeConfiguration<Shipment>
{
    public void Configure(EntityTypeBuilder<Shipment> b)
    {
        b.Property(s => s.Number).HasMaxLength(16);
        b.Property(s => s.ShipmentNumber).HasMaxLength(8);
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.Carrier).HasMaxLength(100);
        b.Property(s => s.BillOfLading).HasMaxLength(64);
        b.Property(s => s.TrackingNumber).HasMaxLength(64);
        b.Property(s => s.ShipToName).HasMaxLength(200);
        b.Property(s => s.ShipToAddress).HasMaxLength(500);
        b.Property(s => s.ShipToCode).HasMaxLength(16);
        b.Property(s => s.Notes).HasMaxLength(2000);
        b.Property(s => s.CreatedBy).HasMaxLength(320);
        b.Property(s => s.ConfirmedBy).HasMaxLength(320);
        b.HasIndex(s => s.Number).IsUnique();
        b.HasIndex(s => new { s.SalesOrderId, s.Status });
        b.HasIndex(s => s.ShipDate);
        b.HasOne<SalesOrder>().WithMany().HasForeignKey(s => s.SalesOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Invoice>().WithMany().HasForeignKey(s => s.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(s => s.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.ShipmentId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(s => s.Packages).WithOne().HasForeignKey(p => p.ShipmentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ShipmentLineConfiguration : IEntityTypeConfiguration<ShipmentLine>
{
    public void Configure(EntityTypeBuilder<ShipmentLine> b)
    {
        b.Property(l => l.Lot).HasMaxLength(64);
        b.Property(l => l.Serial).HasMaxLength(64);
        b.Property(l => l.Ownership).HasConversion<string>().HasMaxLength(24);
        b.Property(l => l.Quantity).Quantity();
        b.Property(l => l.UnitPrice).Rate();
        b.Property(l => l.UnitCost).Rate();
        b.Property(l => l.CostAmount).Money();
        b.Ignore(l => l.Amount);
        b.HasIndex(l => l.SalesOrderLineId);
        b.HasOne<SalesOrderLine>().WithMany().HasForeignKey(l => l.SalesOrderLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PickListLine>().WithMany().HasForeignKey(l => l.PickListLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Part>().WithMany().HasForeignKey(l => l.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(l => l.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bin>().WithMany().HasForeignKey(l => l.BinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(l => l.OwnerProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<InventoryTransaction>().WithMany().HasForeignKey(l => l.InventoryTransactionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ShipmentPackageConfiguration : IEntityTypeConfiguration<ShipmentPackage>
{
    public void Configure(EntityTypeBuilder<ShipmentPackage> b)
    {
        b.Property(p => p.Weight).Quantity();
        b.Property(p => p.Length).Quantity();
        b.Property(p => p.Width).Quantity();
        b.Property(p => p.Height).Quantity();
        b.Property(p => p.TrackingNumber).HasMaxLength(64);
        b.HasIndex(p => new { p.ShipmentId, p.Number }).IsUnique();
    }
}

internal sealed class WawfReceivingReportConfiguration : IEntityTypeConfiguration<WawfReceivingReport>
{
    public void Configure(EntityTypeBuilder<WawfReceivingReport> b)
    {
        b.Property(r => r.DocumentType).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.FileName).HasMaxLength(200);
        b.Property(r => r.Reference).HasMaxLength(100);
        b.Property(r => r.Message).HasMaxLength(1000);
        b.Property(r => r.ExportedBy).HasMaxLength(320);
        b.Property(r => r.SubmittedBy).HasMaxLength(320);
        b.HasIndex(r => r.ShipmentId).IsUnique();
        b.HasOne<Shipment>().WithMany().HasForeignKey(r => r.ShipmentId).OnDelete(DeleteBehavior.Restrict);
    }
}
