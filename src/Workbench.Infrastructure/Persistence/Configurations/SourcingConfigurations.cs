using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Files;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence.Configurations;

// RFQs and vendor quotes, the supplier portal and EDI (procurement parity).

internal sealed class RfqConfiguration : IEntityTypeConfiguration<Rfq>
{
    public void Configure(EntityTypeBuilder<Rfq> b)
    {
        b.Property(r => r.Number).HasMaxLength(16);
        b.Property(r => r.Title).HasMaxLength(200);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(r => r.Terms).HasMaxLength(300);
        b.Property(r => r.ShipTo).HasMaxLength(500);
        b.Property(r => r.Instructions).HasMaxLength(2000);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.Property(r => r.IssuedBy).HasMaxLength(320);
        b.Property(r => r.CancelReason).HasMaxLength(1000);
        b.HasIndex(r => r.Number).IsUnique();
        b.HasIndex(r => r.Status);
        b.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.RfqId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RfqLineConfiguration : IEntityTypeConfiguration<RfqLine>
{
    public void Configure(EntityTypeBuilder<RfqLine> b)
    {
        b.Property(l => l.Description).HasMaxLength(500);
        b.Property(l => l.Quantity).Quantity();
        b.Property(l => l.UnitOfMeasure).HasMaxLength(8);
        b.Property(l => l.EstimatedUnitCost).HasPrecision(18, 4);
        b.Property(l => l.OdcType).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(l => l.RequisitionLineId);
        b.HasIndex(l => l.PurchaseOrderLineId);
        b.HasOne<Part>().WithMany().HasForeignKey(l => l.PartId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(l => l.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(l => l.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseRequisitionLine>().WithMany().HasForeignKey(l => l.RequisitionLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(l => l.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RfqAward>().WithMany().HasForeignKey(l => l.AwardId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RfqVendorConfiguration : IEntityTypeConfiguration<RfqVendor>
{
    public void Configure(EntityTypeBuilder<RfqVendor> b)
    {
        b.Property(v => v.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(v => v.DeclineReason).HasMaxLength(1000);
        b.HasIndex(v => new { v.RfqId, v.VendorId }).IsUnique();
        b.HasIndex(v => v.VendorId);
        b.HasOne<Rfq>().WithMany().HasForeignKey(v => v.RfqId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Vendor>().WithMany().HasForeignKey(v => v.VendorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class VendorQuoteConfiguration : IEntityTypeConfiguration<VendorQuote>
{
    public void Configure(EntityTypeBuilder<VendorQuote> b)
    {
        b.Property(q => q.Reference).HasMaxLength(64);
        b.Property(q => q.Terms).HasMaxLength(300);
        b.Property(q => q.Notes).HasMaxLength(2000);
        b.Property(q => q.Source).HasConversion<string>().HasMaxLength(16);
        b.Property(q => q.SubmittedBy).HasMaxLength(320);
        b.HasIndex(q => new { q.RfqId, q.VendorId }).IsUnique();
        b.HasOne<Rfq>().WithMany().HasForeignKey(q => q.RfqId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(q => q.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StoredFile>().WithMany().HasForeignKey(q => q.AttachmentFileId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(q => q.Lines).WithOne().HasForeignKey(l => l.QuoteId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class VendorQuoteLineConfiguration : IEntityTypeConfiguration<VendorQuoteLine>
{
    public void Configure(EntityTypeBuilder<VendorQuoteLine> b)
    {
        b.Property(l => l.UnitPrice).Rate();
        b.Property(l => l.Note).HasMaxLength(500);
        b.HasIndex(l => new { l.QuoteId, l.RfqLineId }).IsUnique();
        b.HasOne<RfqLine>().WithMany().HasForeignKey(l => l.RfqLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(l => l.PriceBreaks).WithOne().HasForeignKey(p => p.QuoteLineId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class VendorQuotePriceBreakConfiguration : IEntityTypeConfiguration<VendorQuotePriceBreak>
{
    public void Configure(EntityTypeBuilder<VendorQuotePriceBreak> b)
    {
        b.Property(p => p.MinimumQuantity).Quantity();
        b.Property(p => p.UnitPrice).Rate();
    }
}

internal sealed class RfqAwardConfiguration : IEntityTypeConfiguration<RfqAward>
{
    public void Configure(EntityTypeBuilder<RfqAward> b)
    {
        b.Property(a => a.AwardedBy).HasMaxLength(320);
        b.Property(a => a.SoleSourceJustification).HasMaxLength(4000);
        b.Property(a => a.PriceReasonableness).HasMaxLength(4000);
        b.Property(a => a.Rationale).HasMaxLength(4000);
        b.Property(a => a.AwardedTotal).Money();
        b.Property(a => a.ComparisonJson).HasMaxLength(400_000);
        b.Property(a => a.PurchaseOrderNumbers).HasMaxLength(1000);
        b.HasIndex(a => a.RfqId);
        b.HasOne<Rfq>().WithMany().HasForeignKey(a => a.RfqId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SupplierContactConfiguration : IEntityTypeConfiguration<SupplierContact>
{
    public void Configure(EntityTypeBuilder<SupplierContact> b)
    {
        b.Property(c => c.Email).HasMaxLength(320);
        b.Property(c => c.Name).HasMaxLength(200);
        b.Property(c => c.PasswordHash).HasMaxLength(500);
        b.Property(c => c.InvitedBy).HasMaxLength(320);
        b.Property(c => c.DeactivatedBy).HasMaxLength(320);
        b.HasIndex(c => c.Email).IsUnique();
        b.HasIndex(c => c.VendorId);
        b.HasOne<Vendor>().WithMany().HasForeignKey(c => c.VendorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SupplierSignInTokenConfiguration : IEntityTypeConfiguration<SupplierSignInToken>
{
    public void Configure(EntityTypeBuilder<SupplierSignInToken> b)
    {
        b.Property(t => t.TokenHash).HasMaxLength(64);
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => t.ContactId);
        b.HasOne<SupplierContact>().WithMany().HasForeignKey(t => t.ContactId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SupplierPortalEventConfiguration : IEntityTypeConfiguration<SupplierPortalEvent>
{
    public void Configure(EntityTypeBuilder<SupplierPortalEvent> b)
    {
        b.Property(e => e.Actor).HasMaxLength(320);
        b.Property(e => e.Action).HasMaxLength(64);
        b.Property(e => e.Target).HasMaxLength(64);
        b.Property(e => e.Outcome).HasMaxLength(16);
        b.Property(e => e.Detail).HasMaxLength(1000);
        b.Property(e => e.IpAddress).HasMaxLength(64);
        b.HasIndex(e => new { e.VendorId, e.OccurredAtUtc });
        b.HasIndex(e => e.OccurredAtUtc);
    }
}

internal sealed class PurchaseOrderAcknowledgementConfiguration : IEntityTypeConfiguration<PurchaseOrderAcknowledgement>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderAcknowledgement> b)
    {
        b.Property(a => a.Status).HasConversion<string>().HasMaxLength(24);
        b.Property(a => a.Source).HasConversion<string>().HasMaxLength(8);
        b.Property(a => a.Note).HasMaxLength(2000);
        b.Property(a => a.AcknowledgedBy).HasMaxLength(320);
        b.Property(a => a.ReviewedBy).HasMaxLength(320);
        b.HasIndex(a => a.PurchaseOrderId);
        b.HasIndex(a => a.VendorId);
        b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(a => a.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(a => a.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(a => a.Lines).WithOne().HasForeignKey(l => l.AcknowledgementId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PurchaseOrderAcknowledgementLineConfiguration : IEntityTypeConfiguration<PurchaseOrderAcknowledgementLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderAcknowledgementLine> b)
    {
        b.Property(l => l.Quantity).Quantity();
        b.Property(l => l.UnitPrice).HasPrecision(18, 4);
        b.Property(l => l.Note).HasMaxLength(500);
        b.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(l => l.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ShipNoticeConfiguration : IEntityTypeConfiguration<ShipNotice>
{
    public void Configure(EntityTypeBuilder<ShipNotice> b)
    {
        b.Property(s => s.Number).HasMaxLength(16);
        b.Property(s => s.ShipmentReference).HasMaxLength(64);
        b.Property(s => s.Carrier).HasMaxLength(100);
        b.Property(s => s.TrackingNumber).HasMaxLength(100);
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.Source).HasConversion<string>().HasMaxLength(8);
        b.Property(s => s.SubmittedBy).HasMaxLength(320);
        b.HasIndex(s => s.Number).IsUnique();
        b.HasIndex(s => s.PurchaseOrderId);
        b.HasIndex(s => new { s.VendorId, s.Status });
        b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(s => s.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(s => s.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseReceipt>().WithMany().HasForeignKey(s => s.ReceiptId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.ShipNoticeId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ShipNoticeLineConfiguration : IEntityTypeConfiguration<ShipNoticeLine>
{
    public void Configure(EntityTypeBuilder<ShipNoticeLine> b)
    {
        b.Property(l => l.Quantity).Quantity();
        b.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(l => l.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SupplierInvoiceConfiguration : IEntityTypeConfiguration<SupplierInvoice>
{
    public void Configure(EntityTypeBuilder<SupplierInvoice> b)
    {
        b.Property(i => i.InvoiceNumber).HasMaxLength(64);
        b.Property(i => i.StatedTotal).Money();
        b.Property(i => i.Source).HasConversion<string>().HasMaxLength(8);
        b.Property(i => i.SubmittedBy).HasMaxLength(320);
        b.HasIndex(i => i.BillId).IsUnique();
        b.HasIndex(i => i.VendorId);
        b.HasOne<Vendor>().WithMany().HasForeignKey(i => i.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(i => i.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);

        // A draft bill can be deleted by AP; the record of what the vendor sent goes with it.
        b.HasOne<ApBill>().WithMany().HasForeignKey(i => i.BillId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EdiPartnerConfiguration : IEntityTypeConfiguration<EdiPartner>
{
    public void Configure(EntityTypeBuilder<EdiPartner> b)
    {
        b.Property(p => p.InterchangeQualifier).HasMaxLength(2);
        b.Property(p => p.InterchangeId).HasMaxLength(15);
        b.Property(p => p.ApplicationCode).HasMaxLength(15);
        b.Property(p => p.Transport).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.Address).HasMaxLength(300);
        b.Property(p => p.UpdatedBy).HasMaxLength(320);
        b.HasIndex(p => p.VendorId).IsUnique();
        b.HasIndex(p => new { p.InterchangeQualifier, p.InterchangeId }).IsUnique();
        b.HasOne<Vendor>().WithMany().HasForeignKey(p => p.VendorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EdiMessageConfiguration : IEntityTypeConfiguration<EdiMessage>
{
    public void Configure(EntityTypeBuilder<EdiMessage> b)
    {
        b.Property(m => m.Direction).HasConversion<string>().HasMaxLength(10);
        b.Property(m => m.TransactionSet).HasMaxLength(3);
        b.Property(m => m.ControlNumber).HasMaxLength(20);
        b.Property(m => m.FileName).HasMaxLength(255);
        b.Property(m => m.Status).HasConversion<string>().HasMaxLength(10);
        b.Property(m => m.Error).HasMaxLength(2000);
        b.Property(m => m.DocumentNumber).HasMaxLength(64);
        b.Property(m => m.Content).HasMaxLength(EdiMessage.MaxContentLength);
        b.Property(m => m.CreatedBy).HasMaxLength(320);
        b.HasIndex(m => m.CreatedAtUtc);
        b.HasIndex(m => new { m.PartnerId, m.Direction, m.ControlNumber });
        b.HasOne<EdiPartner>().WithMany().HasForeignKey(m => m.PartnerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(m => m.VendorId).OnDelete(DeleteBehavior.Restrict);
    }
}
