using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Accounting;
using Workbench.Application.Billing;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Collaboration;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for government billing, run through the real services as Pat Morgan (Finance):
/// <list type="bullet">
/// <item>W56HZV-26-C-0042 (Army, armor kits, FFP, project 4101) uses progress payments at 80% on paid costs. Its kit
/// materials and subcontract ODCs back SF 1443 request no. 1 (approved), and the first lot delivered is billed with 80%
/// liquidated, leaving the rest outstanding. It prints on SF 1034 and goes to PIEE as a Combo.</item>
/// <item>N00019-26-C-0117 (NAVAIR, avionics test set, FFP, project 4201) uses performance-based payments: four events,
/// the first billed and approved, the second completed and ready to bill, two still to come.</item>
/// </list>
/// Every invoice is dated in the current fiscal period (earlier periods may be closed). Runs once (skipped when any
/// government billing settings exist), after the billing seed.
/// </summary>
internal static class GovernmentBillingSeed
{
    private const string Actor = "pm@example.com";

    public static async Task SeedAsync(WorkbenchDbContext db, IDbContextFactory<WorkbenchDbContext> contexts, CancellationToken cancellationToken)
    {
        if (await db.Set<ContractGovernmentBilling>().AnyAsync(cancellationToken) || !await db.Set<Domain.Accounting.Account>().AnyAsync(cancellationToken))
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var periods = (await db.Set<FiscalPeriod>().AsNoTracking().ToListAsync(cancellationToken)).OrderBy(p => p.Start).ToList();
        var current = periods.LastOrDefault(p => p.Start <= today);
        var previous = current is null ? null : periods.LastOrDefault(p => p.End < current.Start);
        var before = previous is null ? null : periods.LastOrDefault(p => p.End < previous.Start);
        if (current is null || previous is null || before is null)
        {
            return;
        }

        var pm = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Email == Actor, cancellationToken);
        var now = DateTime.UtcNow;
        var start = before.Start;

        // ---- Progress payments: Army armor kits ----
        var army = new Customer { Code = "ACC-DTA", Name = "Army Contracting Command – Detroit Arsenal", Category = "Federal" };
        var kits = new Contract
        {
            CustomerId = army.Id,
            ContractNumber = "W56HZV-26-C-0042",
            Name = "Armor kit production",
            Type = ContractType.FirmFixedPrice,
            PeriodStart = start,
            PeriodEnd = start.AddYears(2).AddDays(-1),
            CeilingValue = 2_400_000m,
            FundedValue = 1_200_000m,
        };
        var kitClin = new Clin
        {
            ContractId = kits.Id, Number = "0001", Description = "Armor kits, 40 each", Pricing = ClinPricing.FirmFixedPrice,
            PeriodStart = kits.PeriodStart, PeriodEnd = kits.PeriodEnd, CeilingValue = 2_400_000m, FundedValue = 1_200_000m,
        };
        var kitProject = new Project { Code = "4101", Name = "Armor kit production", ContractId = kits.Id, ProjectManagerId = pm?.Id, Start = start, Finish = kits.PeriodEnd };
        var kitWbs = new WbsElement { ProjectId = kitProject.Id, Code = "4101.01", Name = "Kit fabrication", ClinId = kitClin.Id };
        db.AddRange(army, kits, kitClin, kitProject, kitWbs);
        db.Add(new ContractBillingSettings
        {
            ContractId = kits.Id, InvoicePrefix = InvoiceService.DefaultPrefix(kits.ContractNumber), PayingOffice = "DFAS Columbus, HQ0337",
            AdminOffice = "DCMA Detroit, S3605A", CageCode = "1WB01", UpdatedBy = Actor, UpdatedAtUtc = now,
        });
        db.Add(new CustomerBillingProfile { CustomerId = army.Id, PaymentTermsDays = 30, BillingAddress = "6501 E. 11 Mile Road\nWarren, MI 48397", UpdatedBy = Actor, UpdatedAtUtc = now });
        db.AddRange(
            Odc(kitProject, kitWbs, OdcType.Materials, before.Start.AddDays(9), 380_000m, "Armor steel plate, lot 1", "Keystone Metals"),
            Odc(kitProject, kitWbs, OdcType.Subcontract, previous.Start.AddDays(11), 220_000m, "Kit welding and assembly (subcontract)", "Great Lakes Fabrication"));

        // ---- Performance-based payments: NAVAIR avionics test set ----
        var navair = new Customer { Code = "NAVAIR", Name = "Naval Air Systems Command", Category = "Federal" };
        var tester = new Contract
        {
            CustomerId = navair.Id,
            ContractNumber = "N00019-26-C-0117",
            Name = "Avionics test set development",
            Type = ContractType.FirmFixedPrice,
            PeriodStart = start,
            PeriodEnd = start.AddYears(2).AddDays(-1),
            CeilingValue = 1_800_000m,
            FundedValue = 900_000m,
        };
        var testerClin = new Clin
        {
            ContractId = tester.Id, Number = "0001", Description = "Avionics test set, development and first article", Pricing = ClinPricing.FirmFixedPrice,
            PeriodStart = tester.PeriodStart, PeriodEnd = tester.PeriodEnd, CeilingValue = 1_800_000m, FundedValue = 900_000m,
        };
        var testerProject = new Project { Code = "4201", Name = "Avionics test set", ContractId = tester.Id, ProjectManagerId = pm?.Id, Start = start, Finish = tester.PeriodEnd };
        var testerWbs = new WbsElement { ProjectId = testerProject.Id, Code = "4201.01", Name = "Test set engineering", ClinId = testerClin.Id };
        db.AddRange(navair, tester, testerClin, testerProject, testerWbs);

        // Charge codes need an assignment, so only the program manager (and Finance) sees these projects (ProjectVisibility).
        if (pm is not null)
        {
            db.AddRange(new ChargeAssignment { WbsElementId = kitWbs.Id, EmployeeId = pm.Id }, new ChargeAssignment { WbsElementId = testerWbs.Id, EmployeeId = pm.Id });
        }
        db.Add(new ContractBillingSettings
        {
            ContractId = tester.Id, InvoicePrefix = InvoiceService.DefaultPrefix(tester.ContractNumber), PayingOffice = "DFAS Columbus, N68732",
            AdminOffice = "DCMA Baltimore, S2101A", CageCode = "1WB01", UpdatedBy = Actor, UpdatedAtUtc = now,
        });
        db.Add(new CustomerBillingProfile { CustomerId = navair.Id, PaymentTermsDays = 30, BillingAddress = "47123 Buse Road\nPatuxent River, MD 20670", UpdatedBy = Actor, UpdatedAtUtc = now });
        await db.SaveChangesAsync(cancellationToken);

        // The real services, as the seeded Finance user, at direct cost (no indirect rates provider at startup).
        var clock = TimeProvider.System;
        var factory = new WorkbenchDbContextFactory(contexts);
        var audit = new AuditRecorder(new SeedUser(), clock);
        var ledger = new BillingLedger(new GeneralLedger(audit, clock));
        var costs = new BillingCostSource([]);
        var generator = new InvoiceGenerator(costs);
        var options = Options.Create(new BillingOptions { CompanyName = "Workbench Demo Corp", CageCode = "1WB01" });
        var invoices = new InvoiceService(factory, generator, ledger, audit, clock, new InvoiceDocuments(options), new UnbilledAccruals(generator, ledger, audit));
        var progress = new ProgressPaymentService(factory, costs, audit, clock, options);
        var events = new PerformanceBasedPaymentService(factory, audit);
        var milestones = new BillingMilestoneService(factory, audit);
        var government = new GovernmentBillingService(factory, invoices, progress, [new ManualPieeSubmitter()], audit, clock, options);

        var requestDate = Min(current.Start.AddDays(3), today);
        await government.SaveSettingsAsync(kits.Id, new SaveGovernmentBillingSettingsRequest(VoucherFormat.Sf1034, FinancingKind.ProgressPayments, false, 0.80m, 0.80m,
            ProgressPaymentBasis.PaidCosts, null, "W56HZV", "S3605A", "HQ0337", "W25G1U", PieeDocumentKind.Combo), cancellationToken);
        var request = await progress.CreateAsync(new ProgressPaymentPreviewRequest(kits.Id, previous.End, requestDate,
            Notes: "Request no. 1 (seeded): kit materials and the welding subcontract."), cancellationToken);
        await invoices.ApproveAsync(request.InvoiceId!.Value, new ApproveInvoiceRequest(), cancellationToken);

        var lot = await milestones.CreateAsync(kits.Id, new SaveMilestoneRequest(kitClin.Id, "Lot 1: 4 kits delivered and accepted", 240_000m, previous.Start.AddDays(20)), cancellationToken);
        await milestones.CompleteAsync(lot.Id, new CompleteMilestoneRequest(Min(previous.Start.AddDays(22), previous.End)), cancellationToken);
        await milestones.CreateAsync(kits.Id, new SaveMilestoneRequest(kitClin.Id, "Lot 2: 12 kits delivered and accepted", 720_000m, current.End), cancellationToken);
        var delivery = await invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kits.Id, InvoiceKind.FixedPriceMilestone, previous.Start, previous.End, requestDate,
            Notes: "Lot 1 delivery, less 80% liquidation of progress payments (seeded)."), cancellationToken);
        await invoices.ApproveAsync(delivery.Summary.Id, new ApproveInvoiceRequest(), cancellationToken);

        await government.SaveSettingsAsync(tester.Id, new SaveGovernmentBillingSettingsRequest(VoucherFormat.Sf1034, FinancingKind.PerformanceBased, false, 0, 0,
            ProgressPaymentBasis.PaidCosts, null, "N00019", "S2101A", "N68732", null, PieeDocumentKind.TwoInOne), cancellationToken);
        var srr = await events.CreateAsync(tester.Id, new SavePerformanceEventRequest("System requirements review complete", 270_000m, before.Start.AddDays(20),
            "SRR held; requirements baseline approved by the NAVAIR program office"), cancellationToken);
        var pdr = await events.CreateAsync(tester.Id, new SavePerformanceEventRequest("Preliminary design review complete", 360_000m, previous.Start.AddDays(15),
            "PDR held; action items closed or accepted by the government"), cancellationToken);
        await events.CreateAsync(tester.Id, new SavePerformanceEventRequest("Critical design review complete", 450_000m, current.End.AddMonths(2),
            "CDR held; design released to fabrication"), cancellationToken);
        await events.CreateAsync(tester.Id, new SavePerformanceEventRequest("First article test passed", 360_000m, current.End.AddMonths(6),
            "First article test report accepted by the COR"), cancellationToken);
        await events.CompleteAsync(srr.Id, new CompletePerformanceEventRequest(before.Start.AddDays(22), "SRR minutes signed by the COR", "NAVAIR-SRR-0117"), cancellationToken);
        var pbp = await invoices.GenerateDraftAsync(new GenerateInvoiceRequest(tester.Id, InvoiceKind.PerformanceBased, before.Start, before.End, requestDate,
            Notes: "Performance-based payment for event 1 (seeded)."), cancellationToken);
        await invoices.ApproveAsync(pbp.Summary.Id, new ApproveInvoiceRequest(), cancellationToken);
        await events.CompleteAsync(pdr.Id, new CompletePerformanceEventRequest(Min(previous.Start.AddDays(18), today), "PDR report and government action item log", "NAVAIR-PDR-0117"), cancellationToken);
    }

    private static OdcEntry Odc(Project project, WbsElement wbs, OdcType type, DateOnly on, decimal amount, string description, string vendor) => new()
    {
        ProjectId = project.Id,
        WbsElementId = wbs.Id,
        Type = type,
        IncurredOn = on,
        Amount = amount,
        Description = description,
        Vendor = vendor,
        Status = OdcStatus.Approved,
        SubmittedBy = Actor,
        CreatedAtUtc = DateTime.UtcNow,
        ApprovedBy = Actor,
        ApprovedAtUtc = DateTime.UtcNow,
    };

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private sealed class SeedUser : ICurrentUser
    {
        public string Email => Actor;
    }
}
