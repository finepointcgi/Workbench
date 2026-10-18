using Microsoft.EntityFrameworkCore;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for contract detail and purchasing: two CLINs on the Army contract with their funding
/// history (award plus one executed modification), direct WBS elements mapped to CLIN 0001, two vendors, a
/// subcontract, and the seeded vendor ODCs linked to their vendor records. Runs once, after the accounting seed.
/// </summary>
internal static class ContractDetailSeed
{
    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<Clin>().AnyAsync(cancellationToken))
        {
            return;
        }

        var contract = await db.Set<Contract>().FirstOrDefaultAsync(c => c.ContractNumber == "W56KGU-26-C-0001", cancellationToken);
        var project = await db.Set<Project>().FirstOrDefaultAsync(p => p.Code == "1001", cancellationToken);
        if (contract is null || project is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var labor = new Clin
        {
            ContractId = contract.Id,
            Number = "0001",
            Description = "Program management and engineering support",
            Pricing = ClinPricing.CostPlusFixedFee,
            FeeRate = 0.07m,
            PeriodStart = contract.PeriodStart,
            PeriodEnd = contract.PeriodEnd,
            CeilingValue = 2_200_000m,
            FundedValue = 560_000m,
            Order = 0,
        };
        var travel = new Clin
        {
            ContractId = contract.Id,
            Number = "0002",
            Description = "Travel and other direct costs",
            Pricing = ClinPricing.CostNoFee,
            PeriodStart = contract.PeriodStart,
            PeriodEnd = contract.PeriodEnd,
            CeilingValue = 200_000m,
            FundedValue = 40_000m,
            Order = 1,
        };
        db.AddRange(labor, travel);

        // Funding history: the award obligated 535,000; P00001 added 65,000 of incremental funding.
        var mod = new ContractModification
        {
            ContractId = contract.Id,
            Number = "P00001",
            EffectiveDate = contract.PeriodStart.AddMonths(1),
            Description = "Incremental funding",
            Status = ModificationStatus.Executed,
            ExecutedAtUtc = now.AddDays(-20),
            ExecutedBy = "pm@example.com",
        };
        db.Add(mod);
        db.AddRange(
            new FundingAction { ContractId = contract.Id, ClinId = labor.Id, EffectiveDate = contract.PeriodStart, Amount = 500_000m, Description = "Initial award", CreatedAtUtc = now.AddDays(-40), CreatedBy = "pm@example.com" },
            new FundingAction { ContractId = contract.Id, ClinId = travel.Id, EffectiveDate = contract.PeriodStart, Amount = 35_000m, Description = "Initial award", CreatedAtUtc = now.AddDays(-40), CreatedBy = "pm@example.com" },
            new FundingAction { ContractId = contract.Id, ClinId = labor.Id, ModificationId = mod.Id, EffectiveDate = mod.EffectiveDate, Amount = 60_000m, Description = "P00001 incremental funding", CreatedAtUtc = now.AddDays(-20), CreatedBy = "pm@example.com" },
            new FundingAction { ContractId = contract.Id, ClinId = travel.Id, ModificationId = mod.Id, EffectiveDate = mod.EffectiveDate, Amount = 5_000m, Description = "P00001 incremental funding", CreatedAtUtc = now.AddDays(-20), CreatedBy = "pm@example.com" });

        var direct = await db.Set<WbsElement>().Where(w => w.ProjectId == project.Id && w.Kind == ChargeKind.Direct).ToListAsync(cancellationToken);
        foreach (var element in direct)
        {
            element.ClinId = labor.Id;
        }

        var acme = new Vendor
        {
            Code = "ACME",
            Name = "Acme Federal",
            BusinessSize = "Small",
            Uei = "ACMEFED00001",
            TaxIdLast4 = "4821",
            PaymentTermsDays = 30,
            ContactEmail = "billing@acmefederal.example",
        };
        var primavera = new Vendor
        {
            Code = "PRIMA",
            Name = "Primavera Reseller LLC",
            BusinessSize = "Small",
            Is1099 = true,
            TaxIdLast4 = "1177",
            PaymentTermsDays = 15,
        };
        db.AddRange(acme, primavera);
        var subcontract = new Subcontract
        {
            VendorId = acme.Id,
            PrimeContractId = contract.Id,
            ProjectId = project.Id,
            Number = "SUB-1001-01",
            Description = "Data migration engineering",
            Pricing = ClinPricing.TimeAndMaterials,
            Status = SubcontractStatus.Active,
            PeriodStart = contract.PeriodStart,
            PeriodEnd = contract.PeriodEnd,
            CeilingValue = 150_000m,
            FundedValue = 40_000m,
        };
        db.Add(subcontract);

        var vendorOdcs = await db.Set<OdcEntry>().Where(o => o.Vendor != null).ToListAsync(cancellationToken);
        foreach (var odc in vendorOdcs)
        {
            if (odc.Vendor!.StartsWith("Acme", StringComparison.OrdinalIgnoreCase))
            {
                odc.VendorId = acme.Id;
                odc.SubcontractId = odc.Type == OdcType.Subcontract ? subcontract.Id : null;
            }
            else if (odc.Vendor.StartsWith("Primavera", StringComparison.OrdinalIgnoreCase))
            {
                odc.VendorId = primavera.Id;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
