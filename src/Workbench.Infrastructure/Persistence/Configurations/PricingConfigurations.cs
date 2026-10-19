using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Pricing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Proposal pricing. A price model owns its structure and lines (cascade); lines point at setup data (labor categories,
// employees) with restricting keys. Proposal.OpportunityId has no foreign key: the CRM module owns opportunities.

internal sealed class ProposalConfiguration : IEntityTypeConfiguration<Proposal>
{
    public void Configure(EntityTypeBuilder<Proposal> b)
    {
        b.Property(p => p.Number).HasMaxLength(32);
        b.Property(p => p.Title).HasMaxLength(200);
        b.Property(p => p.Customer).HasMaxLength(200);
        b.Property(p => p.OpportunityName).HasMaxLength(200);
        b.Property(p => p.ContractType).HasConversion<string>().HasMaxLength(32);
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.TargetPrice).HasPrecision(18, 2);
        b.Property(p => p.Notes).HasMaxLength(2000);
        b.Property(p => p.CreatedBy).HasMaxLength(320);
        b.Property(p => p.HandedOffBy).HasMaxLength(320);
        b.HasIndex(p => p.Number).IsUnique();
        b.HasIndex(p => p.OpportunityId);
    }
}

internal sealed class PriceModelConfiguration : IEntityTypeConfiguration<PriceModel>
{
    public void Configure(EntityTypeBuilder<PriceModel> b)
    {
        b.Property(m => m.Name).HasMaxLength(100);
        b.Property(m => m.Description).HasMaxLength(1000);
        b.Property(m => m.RateBasis).HasConversion<string>().HasMaxLength(16);
        b.Property(m => m.FringeOverride).HasPrecision(18, 6);
        b.Property(m => m.OverheadOverride).HasPrecision(18, 6);
        b.Property(m => m.GaOverride).HasPrecision(18, 6);
        b.Property(m => m.DefaultRateMethod).HasConversion<string>().HasMaxLength(24);
        b.Property(m => m.DefaultEscalation).HasPrecision(9, 6);
        b.Property(m => m.FeeMethod).HasConversion<string>().HasMaxLength(24);
        b.Property(m => m.FeeRate).HasPrecision(9, 6);
        b.Property(m => m.WgTechnicalRisk).HasPrecision(9, 6);
        b.Property(m => m.WgManagementRisk).HasPrecision(9, 6);
        b.Property(m => m.WgContractTypeRisk).HasPrecision(9, 6);
        b.Property(m => m.WgCostEfficiency).HasPrecision(9, 6);
        b.Property(m => m.LockedBy).HasMaxLength(320);
        b.Property(m => m.LockNote).HasMaxLength(1000);
        b.Property(m => m.LockedSnapshot).HasMaxLength(4_000_000);
        b.Property(m => m.CreatedBy).HasMaxLength(320);
        b.HasIndex(m => m.ProposalId);
        b.HasOne<Proposal>().WithMany().HasForeignKey(m => m.ProposalId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(m => m.Periods).WithOne().HasForeignKey(p => p.ModelId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(m => m.Clins).WithOne().HasForeignKey(c => c.ModelId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(m => m.Wbs).WithOne().HasForeignKey(w => w.ModelId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(m => m.Escalations).WithOne().HasForeignKey(e => e.ModelId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(m => m.LaborLines).WithOne().HasForeignKey(l => l.ModelId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(m => m.CostLines).WithOne().HasForeignKey(l => l.ModelId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PricePeriodConfiguration : IEntityTypeConfiguration<PricePeriod>
{
    public void Configure(EntityTypeBuilder<PricePeriod> b)
    {
        b.Property(p => p.Name).HasMaxLength(60);
    }
}

internal sealed class PriceClinConfiguration : IEntityTypeConfiguration<PriceClin>
{
    public void Configure(EntityTypeBuilder<PriceClin> b)
    {
        b.Property(c => c.Number).HasMaxLength(10);
        b.Property(c => c.Description).HasMaxLength(200);
    }
}

internal sealed class PriceWbsConfiguration : IEntityTypeConfiguration<PriceWbs>
{
    public void Configure(EntityTypeBuilder<PriceWbs> b)
    {
        b.Property(w => w.Code).HasMaxLength(40);
        b.Property(w => w.Name).HasMaxLength(200);
        b.Property(w => w.BasisOfEstimate).HasMaxLength(4000);
    }
}

internal sealed class PriceEscalationConfiguration : IEntityTypeConfiguration<PriceEscalation>
{
    public void Configure(EntityTypeBuilder<PriceEscalation> b)
    {
        b.Property(e => e.Rate).HasPrecision(9, 6);
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(e => e.LaborCategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PriceLaborLineConfiguration : IEntityTypeConfiguration<PriceLaborLine>
{
    public void Configure(EntityTypeBuilder<PriceLaborLine> b)
    {
        b.Property(l => l.StaffName).HasMaxLength(100);
        b.Property(l => l.RateMethod).HasConversion<string>().HasMaxLength(24);
        b.Property(l => l.SelectedEmployeeIds).HasMaxLength(2000);
        b.Property(l => l.ManualRate).HasPrecision(18, 4);
        b.Property(l => l.Note).HasMaxLength(500);
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(l => l.LaborCategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(l => l.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(l => l.Hours).WithOne().HasForeignKey(h => h.LineId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PriceLaborHoursConfiguration : IEntityTypeConfiguration<PriceLaborHours>
{
    public void Configure(EntityTypeBuilder<PriceLaborHours> b)
    {
        b.Property(h => h.Hours).Hours();
        b.HasIndex(h => new { h.LineId, h.PeriodId }).IsUnique();
    }
}

internal sealed class PriceCostLineConfiguration : IEntityTypeConfiguration<PriceCostLine>
{
    public void Configure(EntityTypeBuilder<PriceCostLine> b)
    {
        b.Property(l => l.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(l => l.Description).HasMaxLength(200);
        b.Property(l => l.Quantity).HasPrecision(18, 4);
        b.Property(l => l.UnitCost).Rate();
        b.Property(l => l.Vendor).HasMaxLength(200);
        b.Property(l => l.QuoteReference).HasMaxLength(100);
        b.Property(l => l.Destination).HasMaxLength(120);
        b.Property(l => l.State).HasMaxLength(60);
        b.Property(l => l.AirfarePerTrip).Money();
        b.Property(l => l.OtherPerTrip).Money();
    }
}
