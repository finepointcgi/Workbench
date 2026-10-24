using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Reports;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Reports;

public sealed partial class StandardReportService
{
    // Indirect rate variance (Finance/Admin, enforced by IIndirectRateService): for each pool, the fiscal year to date
    // and then each period — pool cost, allocation base, the actual rate from the GL (month and year to date), the
    // provisional, target and final actual rates in force, the variances and the under/over-applied burden.

    private async Task IndirectRateVarianceAsync(ReportBuilder b, ReportParameters p, CancellationToken ct)
    {
        var year = p.FiscalYear ?? (await calendar.GetPeriodForDateAsync(clock.Today, ct))?.FiscalYear
            ?? (clock.Today.Month >= 10 ? clock.Today.Year + 1 : clock.Today.Year);
        var periods = (await calendar.ListPeriodsAsync(year, ct)).OrderBy(x => x.Start).ToList();
        b.Subtitle = $"Fiscal year {year}";
        b.Column("pool", "Pool").Column("period", "Period")
            .Column("pool_cost", "Pool cost", ReportColumnType.Money).Column("base", "Allocation base", ReportColumnType.Money)
            .Column("month_rate", "Actual (month)", ReportColumnType.Rate).Column("ytd_rate", "Actual (YTD)", ReportColumnType.Rate)
            .Column("provisional", "Provisional", ReportColumnType.Rate).Column("target", "Target", ReportColumnType.Rate).Column("final", "Final actual", ReportColumnType.Rate)
            .Column("variance_provisional", "YTD − provisional", ReportColumnType.Rate).Column("variance_target", "YTD − target", ReportColumnType.Rate)
            .Column("under_applied", "Under (over) applied", ReportColumnType.Money);
        var rates = await indirectRates.ListRatesAsync(year, cancellationToken: ct);
        if (periods.Count == 0)
        {
            b.Note($"Fiscal year {year} has no periods in the calendar.");
            return;
        }

        // Through the period containing today (later periods have no cost yet).
        var through = periods.LastOrDefault(x => x.Start <= clock.Today) ?? periods[0];
        var calc = await indirectRates.GetRateCalculationAsync(periods[0].Id, through.Id, ct);
        decimal? Rate(CostPool pool, RateBasis basis) => rates.Where(r => r.Pool == pool && r.Basis == basis).OrderByDescending(r => r.EffectiveFrom).Select(r => (decimal?)r.Rate).FirstOrDefault();
        foreach (var pool in calc.Pools)
        {
            var target = Rate(pool.Pool, RateBasis.Target);
            var final = Rate(pool.Pool, RateBasis.Actual);
            b.Add(ReportRowKind.Subtotal, 0, null, "accounting/indirect-rates", pool.Name, $"FY{year} to {calc.ToPeriodName}", pool.PoolCost, pool.BaseAmount, null, pool.ActualRate,
                pool.ProvisionalRate, target, final, pool.Variance, pool.ActualRate - target, pool.UnderApplied);
            foreach (var point in calc.Trend.Where(x => x.Pool == pool.Pool))
            {
                b.Add(ReportRowKind.Detail, 1, DrillKey.Of(("period", point.FiscalPeriodId), ("pool", pool.Pool)), null,
                    pool.Name, point.PeriodName, point.PoolCost, point.BaseAmount, point.MonthRate, point.YtdRate, point.ProvisionalRate ?? pool.ProvisionalRate, target, final,
                    point.YtdRate - (point.ProvisionalRate ?? pool.ProvisionalRate), point.YtdRate - target, null);
            }
        }

        b.Notes(calc.Notes);
        b.Note("Rates are fractions of the allocation base. Positive variances mean actual rates above provisional or target; under-applied is pool cost not yet absorbed by allocations.");
    }

    /// <summary>The allocation lines for one period: where the pool's burden was applied.</summary>
    private async Task IndirectRateDrillAsync(ReportBuilder b, DrillKey key, CancellationToken ct)
    {
        var preview = await indirectRates.PreviewAllocationAsync(key.RequireId("period"), ct);
        var pool = Enum.TryParse<CostPool>(key.Text("pool"), out var parsed) ? parsed : (CostPool?)null;
        b.Subtitle = $"{preview.PeriodName}{(pool is null ? "" : $" · {ReportValues.Words(pool.Value.ToString())}")} · {(preview.Posted is null ? "not posted" : $"posted {preview.Posted.EntryNumber}")}";
        b.Column("project", "Project").Column("wbs", "WBS").Column("direct_labor", "Direct labor", ReportColumnType.Money).Column("direct_odc", "Direct ODC", ReportColumnType.Money)
            .Column("burden", pool is null ? "Burden" : $"{ReportValues.Words(pool.Value.ToString())} burden", ReportColumnType.Money);
        decimal Burden(Contracts.IndirectRates.AllocationLineDto l) => pool switch
        {
            CostPool.Fringe => l.Fringe,
            CostPool.Overhead => l.Overhead,
            CostPool.GeneralAndAdministrative => l.GeneralAndAdministrative,
            _ => l.Burden,
        };

        foreach (var line in preview.Lines.OrderBy(l => l.WbsCode, StringComparer.Ordinal))
        {
            b.Add(ReportRowKind.Detail, 0, null, $"projects/{line.ProjectId}/financials", line.ProjectCode, line.WbsCode, line.DirectLabor, line.DirectOdc, Burden(line));
        }

        b.Add(ReportRowKind.Total, 0, null, null, "Total", null, preview.Lines.Sum(l => l.DirectLabor), preview.Lines.Sum(l => l.DirectOdc), preview.Lines.Sum(Burden));
        b.Notes(preview.Notes);
    }

    // Contract funding and ceiling status: every contract the user may read (Finance/Admin all; PMs the contracts their
    // projects are on), one row per leaf CLIN under a contract total; contracts without CLINs show contract-level figures.

    private async Task FundingStatusAsync(ReportBuilder b, ReportAccessContext context, ReportParameters p, CancellationToken ct)
    {
        b.Subtitle = $"As of {clock.Today:yyyy-MM-dd}";
        b.Column("contract", "Contract").Column("clin", "CLIN").Column("description", "Description").Column("period_end", "Period end", ReportColumnType.Date)
            .Column("ceiling", "Ceiling", ReportColumnType.Money).Column("funded", "Funded", ReportColumnType.Money)
            .Column("incurred", "Incurred", ReportColumnType.Money).Column("committed", "Committed", ReportColumnType.Money)
            .Column("remaining", "Funded remaining", ReportColumnType.Money).Column("unfunded", "Unfunded ceiling", ReportColumnType.Money)
            .Column("percent", "% of funded incurred", ReportColumnType.Percent).Column("exhaustion", "Projected exhaustion", ReportColumnType.Date)
            .Column("status", "Status");
        var contracts = (await contractService.ListContractsAsync(ct))
            .Where(c => context.CanReadContract(c.Id))
            .Where(c => p.ContractId is { } id ? c.Id == id : c.Status != Contracts.ProjectAccounting.ContractState.Closed)
            .OrderBy(c => c.ContractNumber, StringComparer.Ordinal)
            .ToList();
        if (p.ContractId is not null && contracts.Count == 0)
        {
            throw new WorkbenchForbiddenException("Only Finance, administrators and the managers of its projects can see a contract's funding.");
        }

        decimal tCeiling = 0, tFunded = 0, tIncurred = 0, tCommitted = 0;
        foreach (var contract in contracts)
        {
            var status = await funding.GetFundingStatusAsync(contract.Id, ct);
            if (status.Clins.Count == 0)
            {
                var level = await financials.GetContractFundingAsync(contract.Id, ct);
                b.Add(ReportRowKind.Subtotal, 0, null, $"setup/contracts/{contract.Id}", contract.ContractNumber, null, contract.Name, level.PeriodEnd,
                    level.CeilingValue, level.FundedValue, level.Spent, 0m, level.FundedRemaining, level.CeilingValue - level.FundedValue,
                    level.PercentOfFundedSpent, level.FundingExhaustionDate, level.Alerts.Count == 0 ? "OK" : ReportValues.Words(level.Alerts.Max(a => a.Level).ToString()));
                (tCeiling, tFunded, tIncurred) = (tCeiling + level.CeilingValue, tFunded + level.FundedValue, tIncurred + level.Spent);
                continue;
            }

            var clins = status.Clins;
            var ceiling = clins.Sum(c => c.CeilingValue);
            var funded = clins.Sum(c => c.FundedValue);
            var incurred = clins.Sum(c => c.Incurred);
            var committed = clins.Sum(c => c.Committed);
            // Unfunded sorts after Exhausted in the enum, so it only shows when nothing is worse.
            var worst = clins.Max(c => c.Level is FundingLevel.NotActive or FundingLevel.Unfunded ? FundingLevel.Ok : c.Level);
            if (worst == FundingLevel.Ok && clins.Any(c => c.Level == FundingLevel.Unfunded))
            {
                worst = FundingLevel.Unfunded;
            }

            b.Add(ReportRowKind.Subtotal, 0, null, $"setup/contracts/{contract.Id}", contract.ContractNumber, null, contract.Name, contract.PeriodEnd,
                ceiling, funded, incurred, committed, funded - incurred - committed, ceiling - funded, ReportValues.Ratio(incurred, funded),
                clins.Where(c => c.ProjectedExhaustion is not null).Min(c => c.ProjectedExhaustion), ReportValues.Words(worst.ToString()));
            foreach (var clin in clins)
            {
                b.Add(ReportRowKind.Detail, 1, DrillKey.Of(("contract", contract.Id), ("clin", clin.ClinId)), null,
                    contract.ContractNumber, clin.Number, clin.Description, clin.PeriodEnd, clin.CeilingValue, clin.FundedValue, clin.Incurred, clin.Committed,
                    clin.Remaining, clin.CeilingValue - clin.FundedValue, clin.PercentIncurred, clin.ProjectedExhaustion, ReportValues.Words(clin.Level.ToString()));
            }

            if (!status.IsBurdened)
            {
                b.Note("Some contracts' incurred cost is direct cost only (no indirect rates for the year).");
            }

            (tCeiling, tFunded, tIncurred, tCommitted) = (tCeiling + ceiling, tFunded + funded, tIncurred + incurred, tCommitted + committed);
        }

        if (contracts.Count > 1)
        {
            b.Add(ReportRowKind.Total, 0, null, null, "Total", null, $"{contracts.Count} contracts", null, tCeiling, tFunded, tIncurred, tCommitted,
                tFunded - tIncurred - tCommitted, tCeiling - tFunded, ReportValues.Ratio(tIncurred, tFunded), null, null);
        }

        b.Note("Committed is submitted time and ODCs awaiting approval. Funded remaining = funded − incurred − committed.");
    }

    private async Task FundingDrillAsync(ReportBuilder b, ReportAccessContext context, DrillKey key, CancellationToken ct)
    {
        var contractId = key.RequireId("contract");
        if (!context.CanReadContract(contractId))
        {
            throw new WorkbenchForbiddenException("Only Finance, administrators and the managers of its projects can see a contract's funding.");
        }

        var history = await funding.ListFundingHistoryAsync(contractId, key.Id("clin"), ct);
        b.Subtitle = history.Count == 0 ? "No funding actions" : $"CLIN {history[0].ClinNumber} funding history";
        b.Column("date", "Effective", ReportColumnType.Date).Column("clin", "CLIN").Column("modification", "Modification").Column("description", "Description")
            .Column("amount", "Amount", ReportColumnType.Money).Column("running", "Funded after", ReportColumnType.Money).Column("by", "By");
        foreach (var a in history)
        {
            b.Add(ReportRowKind.Detail, 0, null, $"setup/contracts/{contractId}", a.EffectiveDate, a.ClinNumber, a.ModificationNumber, a.Description, a.Amount, a.RunningTotal, a.CreatedBy);
        }

        b.Add(ReportRowKind.Total, 0, null, null, null, "Total", null, null, history.Sum(a => a.Amount), null, null);
    }

    // AR and AP aging summaries: the existing aging services (Finance/Admin), one row per customer or vendor, drilling to
    // the open invoices or bills, each linking to its page.

    private async Task ArAgingAsync(ReportBuilder b, ReportParameters p, CancellationToken ct)
    {
        var aging = await arReports.GetAgingAsync(p.AsOf ?? clock.Today, ct);
        b.Subtitle = $"As of {aging.AsOf:yyyy-MM-dd}";
        AgingColumns(b, "Customer");
        foreach (var row in aging.Customers.OrderBy(r => r.CustomerName, StringComparer.OrdinalIgnoreCase))
        {
            var x = row.Buckets;
            b.Add(ReportRowKind.Detail, 0, DrillKey.Of(("customer", row.CustomerId)), "billing/aging", row.CustomerName, row.InvoiceCount, x.Current, x.Days1To30, x.Days31To60, x.Days61To90, x.Over90, x.Total);
        }

        var t = aging.Totals;
        b.Add(ReportRowKind.Total, 0, null, "billing/aging", "Total", aging.Customers.Sum(r => r.InvoiceCount), t.Current, t.Days1To30, t.Days31To60, t.Days61To90, t.Over90, t.Total);
    }

    private async Task ArDrillAsync(ReportBuilder b, ReportParameters p, DrillKey key, CancellationToken ct)
    {
        var customerId = key.RequireId("customer");
        var aging = await arReports.GetAgingAsync(p.AsOf ?? clock.Today, ct);
        var invoices = aging.Invoices.Where(i => i.CustomerId == customerId).OrderBy(i => i.DueDate).ToList();
        b.Subtitle = $"{invoices.FirstOrDefault()?.CustomerName ?? "Customer"} · open invoices as of {aging.AsOf:yyyy-MM-dd}";
        b.Column("invoice", "Invoice").Column("contract", "Contract").Column("date", "Invoice date", ReportColumnType.Date).Column("due", "Due", ReportColumnType.Date)
            .Column("days", "Days past due", ReportColumnType.Number).Column("total", "Total", ReportColumnType.Money).Column("balance", "Balance", ReportColumnType.Money);
        foreach (var i in invoices)
        {
            b.Add(ReportRowKind.Detail, 0, null, $"billing/invoices/{i.Id}", i.Number, i.ContractNumber, i.InvoiceDate, i.DueDate, Math.Max(0, i.DaysPastDue), i.Total, i.Balance);
        }

        b.Add(ReportRowKind.Total, 0, null, null, "Total", null, null, null, null, invoices.Sum(i => i.Total), invoices.Sum(i => i.Balance));
    }

    private async Task ApAgingAsync(ReportBuilder b, ReportParameters p, CancellationToken ct)
    {
        var aging = await apReports.GetAgingAsync(p.AsOf ?? clock.Today, ct);
        b.Subtitle = $"As of {aging.AsOf:yyyy-MM-dd} · excluding retainage";
        AgingColumns(b, "Vendor", withCount: false);
        b.Column("retainage", "Retainage", ReportColumnType.Money);
        foreach (var r in aging.Rows.OrderBy(r => r.VendorName, StringComparer.OrdinalIgnoreCase))
        {
            b.Add(ReportRowKind.Detail, 0, DrillKey.Of(("vendor", r.VendorId)), "purchasing/reports", $"{r.VendorCode} {r.VendorName}", r.Current, r.Days1To30, r.Days31To60, r.Days61To90, r.Over90, r.Total, r.Retainage);
        }

        b.Add(ReportRowKind.Total, 0, null, "purchasing/reports", "Total", aging.Rows.Sum(r => r.Current), aging.Rows.Sum(r => r.Days1To30), aging.Rows.Sum(r => r.Days31To60),
            aging.Rows.Sum(r => r.Days61To90), aging.Rows.Sum(r => r.Over90), aging.Total, aging.Rows.Sum(r => r.Retainage));
    }

    private async Task ApDrillAsync(ReportBuilder b, ReportParameters p, DrillKey key, CancellationToken ct)
    {
        var vendorId = key.RequireId("vendor");
        var asOf = p.AsOf ?? clock.Today;
        // The aging call enforces Finance/Admin before any bill is read.
        var aging = await apReports.GetAgingAsync(asOf, ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var bills = await db.Set<ApBill>().AsNoTracking()
            .Where(x => x.VendorId == vendorId && x.Status == ApBillStatus.Posted && x.InvoiceDate <= asOf)
            .OrderBy(x => x.DueDate)
            .ToListAsync(ct);
        bills = bills.Where(x => x.Balance != 0).ToList(); // Open bills and open credits (shown negative).
        var vendor = aging.Rows.FirstOrDefault(r => r.VendorId == vendorId);
        b.Subtitle = $"{vendor?.VendorName ?? "Vendor"} · open bills as of {asOf:yyyy-MM-dd}";
        b.Column("bill", "Bill").Column("invoice", "Vendor invoice").Column("date", "Invoice date", ReportColumnType.Date).Column("due", "Due", ReportColumnType.Date)
            .Column("days", "Days past due", ReportColumnType.Number).Column("amount", "Amount", ReportColumnType.Money).Column("open", "Open", ReportColumnType.Money);
        foreach (var x in bills)
        {
            b.Add(ReportRowKind.Detail, 0, null, $"purchasing/bills/{x.Id}", x.Number, x.InvoiceNumber, x.InvoiceDate, x.DueDate, Math.Max(0, asOf.DayNumber - x.DueDate.DayNumber),
                x.IsCredit ? -x.Amount : x.Amount, x.SignedBalance);
        }

        b.Add(ReportRowKind.Total, 0, null, null, "Total", null, null, null, null, bills.Sum(x => x.IsCredit ? -x.Amount : x.Amount), bills.Sum(x => x.SignedBalance));
    }

    private static void AgingColumns(ReportBuilder b, string party, bool withCount = true)
    {
        b.Column("party", party);
        if (withCount)
        {
            b.Column("count", "Invoices", ReportColumnType.Number);
        }

        b.Column("current", "Current", ReportColumnType.Money).Column("d30", "1–30", ReportColumnType.Money).Column("d60", "31–60", ReportColumnType.Money)
            .Column("d90", "61–90", ReportColumnType.Money).Column("over90", "Over 90", ReportColumnType.Money).Column("total", "Total", ReportColumnType.Money);
    }
}
