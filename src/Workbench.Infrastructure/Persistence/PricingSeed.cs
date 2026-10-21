using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Identity;
using Workbench.Application.Pricing;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Pricing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Identity;
using Workbench.Domain.Pricing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample for proposal pricing: Riley Quinn (pricing@example.com, Pricing role only), forward pricing rates
/// for this fiscal year and the next two (a little below provisional), and proposal PRP-2027-014, a cost-plus-fixed-fee
/// bid for three years (base + 2 options) priced from the seeded people and rates: named key personnel, category
/// averages and medians, a TBD hire, travel to Washington DC at GSA per diem, licenses, hardware, a subcontractor quote
/// and an unallowable dinner. It has a lower-fee scenario and its working price locked as version 1. Runs once.
/// </summary>
internal static class PricingSeed
{
    public const string PricerEmail = "pricing@example.com";

    private static readonly (IndirectPool Pool, decimal Rate, decimal Step)[] ForwardPricing =
    [
        (IndirectPool.Fringe, 0.31m, 0.005m),
        (IndirectPool.Overhead, 0.17m, 0.005m),
        (IndirectPool.GeneralAndAdministrative, 0.105m, 0m),
    ];

    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<Proposal>().AnyAsync(cancellationToken))
        {
            return;
        }

        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Code, cancellationToken);
        var pat = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Email == "pm@example.com", cancellationToken);
        if (!categories.ContainsKey("PM3") || !categories.ContainsKey("SE2") || !categories.ContainsKey("BA2") || !categories.ContainsKey("ADM1") || pat is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        if (!await db.Set<AppUser>().AnyAsync(u => u.Email == PricerEmail, cancellationToken))
        {
            await RoleStore.AddUserAsync(db, new AppUser { Email = PricerEmail, DisplayName = "Riley Quinn", CreatedAtUtc = now }, OrgRoles.Pricing, cancellationToken);
        }

        // Forward pricing rates: this fiscal year and the next two.
        if (await db.Set<IndirectPoolSetup>().AnyAsync(cancellationToken)
            && !await db.Set<IndirectRate>().AnyAsync(r => r.Basis == IndirectRateBasis.ForwardPricing, cancellationToken))
        {
            var current = await db.Set<FiscalYear>().AsNoTracking().FirstOrDefaultAsync(y => y.Start <= today && y.End >= today, cancellationToken);
            if (current is not null)
            {
                for (var i = 0; i < 3; i++)
                {
                    db.Set<IndirectRate>().AddRange(ForwardPricing.Select(p => new IndirectRate
                    {
                        Pool = p.Pool,
                        FiscalYear = current.Year + i,
                        Basis = IndirectRateBasis.ForwardPricing,
                        Rate = p.Rate + i * p.Step,
                        EffectiveFrom = current.Start.AddYears(i),
                        Note = "Forward pricing rate proposal (FPRP) submitted to DCMA",
                        ApprovedBy = PricerEmail,
                        ApprovedAtUtc = now,
                        CreatedBy = PricerEmail,
                        CreatedAtUtc = now,
                    }));
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var factory = services.GetRequiredService<Application.IWorkbenchDbContextFactory>();
        var pricing = new PricingService(factory, new AuditRecorder(new SeedUser(), TimeProvider.System), TimeProvider.System);
        var start = new DateOnly(today.Year, today.Month, 1).AddMonths(2);
        var proposal = await pricing.CreateProposalAsync(new SaveProposalRequest("PRP-2027-014", "Enterprise Cloud Operations Support (ECOS)", PricingContractKind.CostPlusFixedFee,
            Customer: "U.S. Army PEO EIS", OpportunityName: "ECOS recompete (W91QV1-27-R-0042)", DueDate: today.AddDays(30),
            Notes: "Price volume due with the technical volume. Pink team on the 12th.", FirstPeriodStart: start, OptionYears: 2), cancellationToken);
        var modelId = proposal.Models.Single(m => m.IsBase).Id;
        var model = await pricing.GetModelAsync(modelId, cancellationToken);
        var periods = model.Periods.Select(p => p.Id).ToArray();

        model = await pricing.SaveClinsAsync(modelId,
        [
            new SavePriceClinRequest(model.Clins[0].Id, "0001", "Cloud operations labor (CPFF)"),
            new SavePriceClinRequest(null, "0002", "Travel and other direct costs (cost)"),
        ], cancellationToken);
        var labor = model.Clins.First(c => c.Number == "0001").Id;
        var odcs = model.Clins.First(c => c.Number == "0002").Id;

        model = await pricing.SaveWbsAsync(modelId,
        [
            new SavePriceWbsRequest(model.Wbs[0].Id, "01", "Program management",
                "One half-time program manager (Pat Morgan, key personnel) for the full period, based on the incumbent's level of effort and the PWS 3.1 reporting cadence. Quarterly program reviews at the customer site in Washington DC."),
            new SavePriceWbsRequest(null, "02", "Cloud engineering"),
            new SavePriceWbsRequest(null, "02.01", "Workload migration",
                "Two systems engineers for the base year to migrate the 46 workloads in Attachment J-3 at 80 hours per workload (ECOS-1 actuals), tapering to half an FTE in option 1 for the stragglers. Hardware refresh for the two on-premises racks per the vendor quote.",
                ParentCode: "02"),
            new SavePriceWbsRequest(null, "02.02", "Operations and sustainment",
                "Steady-state operations staffed at one engineer per 120 workloads (industry benchmark), growing to two in the option years as migrated workloads land. A TBD cloud engineer is priced at the market rate from the 2026 salary survey. Monitoring licenses at list; the cyber assessment is subcontracted per Primavera's quote.",
                ParentCode: "02"),
            new SavePriceWbsRequest(null, "03", "Service desk",
                "Tier 1/2 service desk for 2,400 users: one business analyst (median category rate) plus a half-time administrative assistant, from the incumbent ticket volume (1,150 tickets a month)."),
        ], cancellationToken);
        Guid Wbs(string code) => model.Wbs.First(w => w.Code == code).Id;
        Dictionary<Guid, decimal> Hours(params decimal[] hours) => periods.Zip(hours).ToDictionary(x => x.First, x => x.Second);

        model = await pricing.SaveLaborLinesAsync(modelId,
        [
            new SavePriceLaborLineRequest(null, Wbs("01"), labor, categories["PM3"].Id, LaborRateSource.NamedEmployee, Hours(960, 960, 960), EmployeeId: pat.Id, Note: "Key personnel"),
            new SavePriceLaborLineRequest(null, Wbs("02.01"), labor, categories["SE2"].Id, LaborRateSource.CategoryAverage, Hours(3760, 940, 0)),
            new SavePriceLaborLineRequest(null, Wbs("02.02"), labor, categories["SE2"].Id, LaborRateSource.CategoryMedian, Hours(1880, 3760, 3760)),
            new SavePriceLaborLineRequest(null, Wbs("02.02"), labor, categories["SE2"].Id, LaborRateSource.Manual, Hours(1880, 1880, 1880),
                StaffName: "Cloud engineer (TBD hire)", ManualRate: 72m, Note: "2026 salary survey, 50th percentile"),
            new SavePriceLaborLineRequest(null, Wbs("03"), labor, categories["BA2"].Id, LaborRateSource.CategoryMedian, Hours(1880, 1880, 1880)),
            new SavePriceLaborLineRequest(null, Wbs("03"), labor, categories["ADM1"].Id, LaborRateSource.CategoryAverage, Hours(940, 940, 940), StaffName: "TBD"),
        ], cancellationToken);

        var costs = new List<SavePriceCostLineRequest>();
        foreach (var period in periods)
        {
            costs.Add(new SavePriceCostLineRequest(null, Wbs("01"), odcs, period, PriceCostType.Travel, "Quarterly program reviews", Destination: "District of Columbia",
                State: "DC", Trips: 4, Travelers: 2, Nights: 3, AirfarePerTrip: 450m, OtherPerTrip: 120m));
            costs.Add(new SavePriceCostLineRequest(null, Wbs("02.02"), odcs, period, PriceCostType.Odc, "Cloud monitoring licenses (per month)", Quantity: 12, UnitCost: 1450m,
                Vendor: "Acme Federal"));
        }

        costs.Add(new SavePriceCostLineRequest(null, Wbs("02.01"), odcs, periods[0], PriceCostType.Materials, "Rack hardware refresh", Quantity: 2, UnitCost: 8500m, Vendor: "Acme Federal",
            QuoteReference: "AF-7731"));
        costs.Add(new SavePriceCostLineRequest(null, Wbs("02.02"), labor, periods[0], PriceCostType.Subcontract, "Cyber assessment (ATO package)", UnitCost: 96000m,
            Vendor: "Primavera Reseller LLC", QuoteReference: "Q-2291"));
        costs.Add(new SavePriceCostLineRequest(null, Wbs("02.02"), labor, periods[1], PriceCostType.Subcontract, "Cyber assessment (annual review)", UnitCost: 48000m,
            Vendor: "Primavera Reseller LLC", QuoteReference: "Q-2291"));
        costs.Add(new SavePriceCostLineRequest(null, Wbs("01"), odcs, periods[0], PriceCostType.Odc, "Kickoff team dinner", UnitCost: 1200m, IsUnallowable: true));
        model = await pricing.SaveCostLinesAsync(modelId, costs, cancellationToken);

        model = await pricing.SaveSettingsAsync(modelId, new SavePriceSettingsRequest(model.Settings with
        {
            RateBasis = RateBasis.ForwardPricing,
            DefaultEscalation = 0.03m,
            FeeMethod = FeeMethodKind.FlatPercent,
            FeeRate = 0.07m,
        }), cancellationToken);
        model = await pricing.SaveEscalationsAsync(modelId, [new PriceEscalationDto(categories["SE2"].Id, 0.035m)], cancellationToken);

        // Price-to-win: the capture team's target sits just under the working price.
        var price = (await pricing.CalculateAsync(modelId, cancellationToken)).Totals.Price;
        var saved = await pricing.GetProposalAsync(proposal.Id, cancellationToken);
        await pricing.UpdateProposalAsync(proposal.Id, new SaveProposalRequest(saved.Number, saved.Title, saved.ContractType, saved.Customer, saved.OpportunityId,
            saved.OpportunityName, Math.Round(price * 0.97m / 1000m, MidpointRounding.AwayFromZero) * 1000m, saved.DueDate, saved.Notes), cancellationToken);

        // A what-if: weighted guidelines fee and a leaner service desk.
        var scenario = await pricing.CopyModelAsync(modelId, new CopyPriceModelRequest("Leaner desk, weighted guidelines fee", "Analyst at 80%; fee from DFARS weighted guidelines"),
            cancellationToken);
        await pricing.SaveSettingsAsync(scenario.Id, new SavePriceSettingsRequest(scenario.Settings with
        {
            FeeMethod = FeeMethodKind.WeightedGuidelines,
            WgTechnicalRisk = 0.04m,
            WgManagementRisk = 0.04m,
            WgContractTypeRisk = 0.005m,
            WgCostEfficiency = 0m,
        }), cancellationToken);
        await pricing.SaveLaborLinesAsync(scenario.Id, scenario.LaborLines.Select(l => new SavePriceLaborLineRequest(l.Id, l.WbsId, l.ClinId, l.LaborCategoryId, l.RateMethod,
            l.LaborCategoryId == categories["BA2"].Id ? l.HoursByPeriod.ToDictionary(h => h.Key, h => h.Value * 0.8m) : l.HoursByPeriod,
            l.EmployeeId, l.StaffName, l.SelectedEmployeeIds, l.ManualRate, l.Note)).ToList(), cancellationToken);

        await pricing.LockModelAsync(modelId, new LockPriceModelRequest("Submitted with the initial proposal"), cancellationToken);
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email => PricerEmail;
    }
}
