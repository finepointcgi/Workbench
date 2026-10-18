using Microsoft.EntityFrameworkCore;
using Workbench.Application.IncurredCost;
using Workbench.Contracts.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.IndirectRates;

namespace Workbench.Tests.IncurredCost;

/// <summary>
/// A hand-checkable FY2026 ledger on top of <see cref="IndirectRatesKit"/> (contract W91-26-C-0001, made CPFF, project 2001):
/// direct labor 10,000 and materials 2,000 on 2001.01; fringe 3,000, overhead 2,600, G&amp;A 1,760; an unallowable account
/// charge of 500 and a flagged unallowable ODC of 200 posted by the engine (to 7010). Claimed rates: fringe 3,000 / 10,000 = 30%;
/// overhead 2,600 / 13,000 = 20%; G&amp;A 1,760 / 17,600 = 10%. Total expense 20,060; claimed 19,360; unallowable 700.
/// </summary>
internal sealed class IncurredCostKit
{
    public static readonly DateOnly March = new(2026, 3, 16);

    private IncurredCostKit(IndirectRatesKit rates)
    {
        Rates = rates;
        Service = new IncurredCostService(rates.Factory, rates.Audit, rates.Clock);
    }

    public IndirectRatesKit Rates { get; }
    public IncurredCostService Service { get; }

    public static async Task<IncurredCostKit> CreateAsync(bool ledger = true)
    {
        var kit = new IncurredCostKit(await IndirectRatesKit.CreateAsync());
        await using (var db = await kit.Rates.Factory.CreateAsync())
        {
            var contract = await db.Set<Contract>().SingleAsync(c => c.Id == kit.Rates.Contract.Id);
            contract.Type = ContractType.CostPlusFixedFee;
            contract.CeilingValue = 50_000m;
            await db.SaveChangesAsync();
        }

        if (ledger)
        {
            await kit.SeedLedgerAsync();
        }

        return kit;
    }

    public Task PostAsync(string debit, decimal amount, string credit, bool onProject = true, DateOnly? date = null) =>
        Rates.Gl.Journal.CreateAndPostAsync(new SaveJournalEntryRequest(date ?? March, $"Test {debit}",
        [
            new JournalLineInput(Rates.Gl[debit], amount, 0, ProjectId: onProject ? Rates.Project.Id : null, WbsElementId: onProject ? Rates.Task1.Id : null),
            new JournalLineInput(Rates.Gl[credit], 0, amount),
        ]));

    private async Task SeedLedgerAsync()
    {
        await PostAsync("5010", 10_000m, "2100");
        await PostAsync("5120", 2_000m, "2010");
        await PostAsync("6020", 3_000m, "2130", onProject: false);
        await PostAsync("6120", 2_600m, "2010", onProject: false);
        await PostAsync("6220", 1_760m, "2010", onProject: false);
        await PostAsync("7010", 500m, "2010", onProject: false);
        var odc = await AddFlaggedOdcAsync(200m);
        var outcome = await Rates.Gl.Engine.PostOdcAsync(odc);
        Assert.Equal(1, outcome.Posted);
    }

    public async Task<Guid> AddFlaggedOdcAsync(decimal amount)
    {
        await Rates.AddOdcAsync(Rates.Task1, March.AddDays(4), amount, unallowable: true);
        await using var db = await Rates.Factory.CreateAsync();
        return (await db.Set<OdcEntry>().Where(o => o.IsUnallowable && o.Amount == amount).ToListAsync()).Last().Id;
    }

    /// <summary>An approved cost-plus invoice in FY2026: cost lines and fee.</summary>
    public async Task AddInvoiceAsync(decimal cost, decimal fee, InvoiceStatus status = InvoiceStatus.Approved)
    {
        await using var db = await Rates.Factory.CreateAsync();
        var invoice = new Invoice
        {
            ContractId = Rates.Contract.Id,
            CustomerId = Rates.Contract.CustomerId,
            Number = $"INV-{Guid.NewGuid():N}"[..12],
            Type = InvoiceType.CostPlus,
            Status = status,
            PeriodFrom = new DateOnly(2026, 3, 1),
            PeriodTo = new DateOnly(2026, 3, 31),
            InvoiceDate = new DateOnly(2026, 4, 5),
            DueDate = new DateOnly(2026, 5, 5),
            CreatedBy = "finance@example.com",
        };
        invoice.Lines.Add(new InvoiceLine { InvoiceId = invoice.Id, LineNumber = 1, Kind = InvoiceLineKind.Labor, Description = "Direct labor", Amount = cost, Quantity = 1, Rate = cost });
        invoice.Lines.Add(new InvoiceLine { InvoiceId = invoice.Id, LineNumber = 2, Kind = InvoiceLineKind.Fee, Description = "Fixed fee", Amount = fee, Quantity = 1, Rate = fee });
        invoice.Recalculate();
        db.Set<Invoice>().Add(invoice);
        await db.SaveChangesAsync();
    }
}
