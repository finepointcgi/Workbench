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
/// Development sample data for billing: billing terms for the Army contract (15% fee withhold, net 30), a travel WBS
/// element on CLIN 0002 (cost, no fee) holding last period's site-survey trip, and an approved, sent cost-plus invoice for
/// last period (CLIN 0001 at cost plus 7% fee, CLIN 0002 at cost) with a partial payment. Generated and posted through
/// the real billing services, at direct cost (the seed runs before any indirect rates exist). Runs once, after the
/// contract-detail seed.
/// </summary>
internal static class BillingSeed
{
    private const string Actor = "pm@example.com";

    public static async Task SeedAsync(WorkbenchDbContext db, IDbContextFactory<WorkbenchDbContext> contexts, CancellationToken cancellationToken)
    {
        if (await db.Set<Invoice>().AnyAsync(cancellationToken) || await db.Set<ContractBillingSettings>().AnyAsync(cancellationToken))
        {
            return;
        }

        var contract = await db.Set<Contract>().FirstOrDefaultAsync(c => c.ContractNumber == "W56KGU-26-C-0001", cancellationToken);
        var travelClin = contract is null ? null : await db.Set<Clin>().FirstOrDefaultAsync(c => c.ContractId == contract.Id && c.Number == "0002", cancellationToken);
        var project = await db.Set<Project>().FirstOrDefaultAsync(p => p.Code == "1001", cancellationToken);
        var root = await db.Set<WbsElement>().FirstOrDefaultAsync(w => w.Code == "1001.01", cancellationToken);
        if (contract is null || travelClin is null || project is null || root is null || !await db.Set<Domain.Accounting.Account>().AnyAsync(cancellationToken))
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var periods = (await db.Set<FiscalPeriod>().AsNoTracking().ToListAsync(cancellationToken)).OrderBy(p => p.Start).ToList();
        var current = periods.LastOrDefault(p => p.Start <= today);
        var previous = current is null ? null : periods.LastOrDefault(p => p.End < current.Start);
        if (current is null || previous is null)
        {
            return;
        }

        db.Add(new ContractBillingSettings
        {
            ContractId = contract.Id,
            InvoicePrefix = InvoiceService.DefaultPrefix(contract.ContractNumber),
            FeeWithholdRate = 0.15m,
            FeeWithholdCap = 100_000m,
            PayingOffice = "DFAS Columbus, HQ0338",
            AdminOffice = "DCMA Manassas, S2404A",
            CageCode = "1WB01",
            UpdatedBy = Actor,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        db.Add(new CustomerBillingProfile
        {
            CustomerId = contract.CustomerId,
            PaymentTermsDays = 30,
            BillingAddress = "Attn: Contracting Officer\nFort Belvoir, VA 22060",
            UpdatedBy = Actor,
            UpdatedAtUtc = DateTime.UtcNow,
        });

        // Travel belongs on the travel CLIN: give it its own charge code and move last period's trip there.
        var travel = new WbsElement
        {
            ProjectId = project.Id,
            ParentId = root.Id,
            Code = "1001.01.05",
            Name = "Travel and other direct costs",
            RequiresAssignment = false,
            Order = 4,
            ClinId = travelClin.Id,
        };
        db.Add(travel);
        foreach (var trip in await db.Set<OdcEntry>().Where(o => o.ProjectId == project.Id && o.Type == OdcType.Travel && !o.IsUnallowable).ToListAsync(cancellationToken))
        {
            trip.WbsElementId = travel.Id;
        }

        await db.SaveChangesAsync(cancellationToken);

        // The real services, run as the seeded Finance user.
        var clock = TimeProvider.System;
        var factory = new WorkbenchDbContextFactory(contexts);
        var audit = new AuditRecorder(new SeedUser(), clock);
        var ledger = new BillingLedger(new GeneralLedger(audit, clock));
        var generator = new InvoiceGenerator(new BillingCostSource([]));
        var invoices = new InvoiceService(factory, generator, ledger, audit, clock, new InvoiceDocuments(Options.Create(new BillingOptions())),
            new UnbilledAccruals(generator, ledger, audit));
        var payments = new PaymentService(factory, ledger, audit, clock);

        var invoiceDate = Min(current.Start.AddDays(4), today);
        var draft = await invoices.GenerateDraftAsync(new GenerateInvoiceRequest(contract.Id, InvoiceKind.CostPlus, previous.Start, previous.End, invoiceDate,
            Notes: "Cost voucher for last period (seeded)."), cancellationToken);
        await invoices.ApproveAsync(draft.Summary.Id, new ApproveInvoiceRequest(), cancellationToken);
        var sent = await invoices.MarkSentAsync(draft.Summary.Id, cancellationToken);
        var partial = Math.Round(sent.Summary.Total / 2, 2, MidpointRounding.AwayFromZero);
        await payments.RecordAsync(new RecordPaymentRequest(contract.CustomerId, Min(invoiceDate.AddDays(10), today), partial, CashReceiptMethod.Eft,
            [new PaymentApplicationInput(draft.Summary.Id, partial)], Reference: "EFT 20261015-ARMY", Notes: "Partial payment (seeded)"), cancellationToken);
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private sealed class SeedUser : ICurrentUser
    {
        public string Email => Actor;
    }
}
