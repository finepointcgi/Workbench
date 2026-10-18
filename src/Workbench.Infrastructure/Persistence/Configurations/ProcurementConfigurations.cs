using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal static class QuantityPrecision
{
    public static PropertyBuilder<decimal> Quantity(this PropertyBuilder<decimal> p) => p.HasPrecision(18, 4);

    public static PropertyBuilder<decimal?> Quantity(this PropertyBuilder<decimal?> p) => p.HasPrecision(18, 4);
}

internal sealed class PurchaseRequisitionConfiguration : IEntityTypeConfiguration<PurchaseRequisition>
{
    public void Configure(EntityTypeBuilder<PurchaseRequisition> b)
    {
        b.Property(r => r.Number).HasMaxLength(16);
        b.Property(r => r.RequesterEmail).HasMaxLength(320);
        b.Property(r => r.Justification).HasMaxLength(2000);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Stage).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Total).Money();
        b.Property(r => r.RejectedBy).HasMaxLength(320);
        b.Property(r => r.RejectionReason).HasMaxLength(1000);
        b.HasIndex(r => r.Number).IsUnique();
        b.HasIndex(r => new { r.Status, r.RequesterEmail });
        b.HasOne<Employee>().WithMany().HasForeignKey(r => r.RequesterEmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(r => r.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.RequisitionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PurchaseRequisitionLineConfiguration : IEntityTypeConfiguration<PurchaseRequisitionLine>
{
    public void Configure(EntityTypeBuilder<PurchaseRequisitionLine> b)
    {
        b.Property(l => l.Description).HasMaxLength(500);
        b.Property(l => l.Quantity).Quantity();
        b.Property(l => l.UnitCost).Rate();
        b.Property(l => l.Amount).Money();
        b.Property(l => l.OdcType).HasConversion<string>().HasMaxLength(16);
        b.Property(l => l.ApproverEmail).HasMaxLength(320);
        b.Property(l => l.ApprovedBy).HasMaxLength(320);
        b.HasIndex(l => l.PurchaseOrderLineId);
        b.HasOne<Project>().WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(l => l.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(l => l.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(l => l.SuggestedVendorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RequisitionApprovalConfiguration : IEntityTypeConfiguration<RequisitionApproval>
{
    public void Configure(EntityTypeBuilder<RequisitionApproval> b)
    {
        b.Property(a => a.Stage).HasConversion<string>().HasMaxLength(16);
        b.Property(a => a.Decision).HasMaxLength(16);
        b.Property(a => a.DecidedBy).HasMaxLength(320);
        b.Property(a => a.Comment).HasMaxLength(1000);
        b.HasIndex(a => a.RequisitionId);
        b.HasOne<PurchaseRequisition>().WithMany().HasForeignKey(a => a.RequisitionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> b)
    {
        b.Property(o => o.Number).HasMaxLength(16);
        b.Property(o => o.Terms).HasMaxLength(300);
        b.Property(o => o.ShipTo).HasMaxLength(500);
        b.Property(o => o.Notes).HasMaxLength(2000);
        b.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(o => o.Total).Money();
        b.Property(o => o.ReceiverEmails).HasMaxLength(1000);
        b.Property(o => o.CreatedBy).HasMaxLength(320);
        b.Property(o => o.IssuedBy).HasMaxLength(320);
        b.Property(o => o.CancelReason).HasMaxLength(1000);
        b.Ignore(o => o.IsOpen);
        b.HasIndex(o => o.Number).IsUnique();
        b.HasIndex(o => new { o.VendorId, o.Status });
        b.HasOne<Vendor>().WithMany().HasForeignKey(o => o.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> b)
    {
        b.Property(l => l.Description).HasMaxLength(500);
        b.Property(l => l.Quantity).Quantity();
        b.Property(l => l.UnitPrice).Rate();
        b.Property(l => l.Amount).Money();
        b.Property(l => l.QuantityReceived).Quantity();
        b.Property(l => l.OdcType).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(l => l.WbsElementId);
        b.HasIndex(l => l.RequisitionLineId);
        b.HasOne<Project>().WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(l => l.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(l => l.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PurchaseOrderRevisionConfiguration : IEntityTypeConfiguration<PurchaseOrderRevision>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderRevision> b)
    {
        b.Property(r => r.Reason).HasMaxLength(1000);
        b.Property(r => r.Changes).HasMaxLength(8000);
        b.Property(r => r.TotalBefore).Money();
        b.Property(r => r.TotalAfter).Money();
        b.Property(r => r.ChangedBy).HasMaxLength(320);
        b.HasIndex(r => new { r.PurchaseOrderId, r.Revision }).IsUnique();
        b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(r => r.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PurchaseReceiptConfiguration : IEntityTypeConfiguration<PurchaseReceipt>
{
    public void Configure(EntityTypeBuilder<PurchaseReceipt> b)
    {
        b.Property(r => r.Number).HasMaxLength(16);
        b.Property(r => r.ReceivedBy).HasMaxLength(320);
        b.Property(r => r.Note).HasMaxLength(1000);
        b.HasIndex(r => r.Number).IsUnique();
        b.HasIndex(r => r.PurchaseOrderId);
        b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(r => r.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.ReceiptId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PurchaseReceiptLineConfiguration : IEntityTypeConfiguration<PurchaseReceiptLine>
{
    public void Configure(EntityTypeBuilder<PurchaseReceiptLine> b)
    {
        b.Property(l => l.Quantity).Quantity();
        b.HasIndex(l => l.PurchaseOrderLineId);
        b.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(l => l.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>The bill line columns procure-to-pay added for the three-way match (the base mapping is in PurchasingConfigurations).</summary>
internal sealed class ApBillLineMatchConfiguration : IEntityTypeConfiguration<ApBillLine>
{
    public void Configure(EntityTypeBuilder<ApBillLine> b)
    {
        b.Property(l => l.Quantity).Quantity();
        b.Property(l => l.UnitPrice).HasPrecision(18, 4);
        b.Property(l => l.MatchProblem).HasMaxLength(1000);
        b.Property(l => l.MatchOverrideBy).HasMaxLength(320);
        b.Property(l => l.MatchOverrideReason).HasMaxLength(1000);
        b.HasIndex(l => l.PurchaseOrderLineId);
        b.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(l => l.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
    }
}
